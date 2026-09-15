using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using ZstdSharp;

namespace SmoothServer.Net
{
    /// <summary>
    /// M4 - compression on the Steam-direct transport, done properly.
    ///
    /// Vanilla Valheim does not compress ZDOData on the Steam socket path at all
    /// (ZPackage.WriteCompressed exists but is only used for world/map blobs). BetterNetworking
    /// showed the win is real (40-60% off ZDOData); this is the same idea without its three
    /// defects, and it runs on BOTH ends out of one DLL so the framing can never disagree:
    ///
    ///   (a) 1-BYTE FRAME TAG instead of exception-driven detection. BN try/catches a zstd
    ///       decompress on every inbound message and uses the throw as its "was it compressed"
    ///       signal - one .NET exception per message per uncompressed peer, on the main thread.
    ///       Once our handshake completes, EVERY message carries a tag byte:
    ///         0x00 = raw, 0x01 = zstd + BN's dict/small, 0x02 = zstd + BN's dict/big.
    ///       A receiver never guesses, and a payload that does not compress costs 1 byte.
    ///   (b) NEVER COMPRESS TWICE. ZSteamSocket.SendQueuedPackages breaks its drain loop when
    ///       SendMessageToConnection fails, leaving already-processed arrays in m_sendQueue.
    ///       BN's prefix then compresses them a second time and the peer, unwrapping once, gets
    ///       a zstd frame where a ZPackage should be - a corrupted stream, not a dropped packet.
    ///       We track the exact byte[] instances we framed (reference identity) and skip them.
    ///   (c) EXPLICIT PER-PEER HANDSHAKE with a dictionary hash, so a dictionary change can
    ///       never produce silent corruption, and a vanilla peer is simply never framed.
    ///
    /// Steam sockets only. ZPlayFabSocket already compresses (zlib, via PlayFabZLibWorkQueue)
    /// and its queue accounting differs; crossplay is left exactly as vanilla.
    ///
    /// HANDSHAKE (routed RPCs "SS_Caps", "SS_Ready" and "SS_Off", both directions, ordered
    /// because they ride the same reliable socket as the data):
    ///   client  -> server : SS_Caps {proto, dictHash, flags}
    ///   server: learns the client's caps, answers SS_Caps + SS_Ready, and only THEN starts
    ///           framing what it sends
    ///   client: learns the server's caps, sends SS_Ready, and only THEN starts framing what
    ///           it sends; on the server's SS_Ready it starts unframing what it receives
    ///   server: on the client's SS_Ready it starts unframing what it receives
    ///
    /// SS_Ready is the in-band switch marker: "everything I send after this message is framed".
    /// Both flips therefore key off the SAME byte position in the stream, which the socket's
    /// reliable FIFO makes exact - a side never unframes a message the peer sent before its own
    /// Ready, and never receives a framed message before it has seen that Ready. (0.5.0 set
    /// recvFramed as soon as the peer's SS_Caps arrived, one round trip before that peer began
    /// framing: every plain packet in the gap was mis-read as a frame - issue #1.)
    ///
    /// SS_Off is the same trick in reverse. If a message ever fails to unframe, that side
    /// poisons the peer: it sends SS_Off (still framed, so the peer can read it), then goes
    /// plain in both directions and never re-arms for that socket. The peer stops unframing at
    /// the Off and stops framing too, so the disable is two-sided and no half-framed state can
    /// survive.
    ///
    /// Dictionaries: dict/small (110 KB) and dict/big (512 KB) are BetterNetworking's
    /// `zstd --train` outputs over a capture of real Valheim traffic, reused under its MIT
    /// licence (CW_Jesse). Credited in THIRD_PARTY.md.
    /// </summary>
    internal sealed class CompressionModule : FeatureModule
    {
        public override string Name => "Compression";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "Compression";

        protected override string EnabledDescription =>
            "zstd-compress every ZSteamSocket payload, using a dictionary trained on real "
            + "Valheim traffic. Both ends must run SmoothServer for a peer to be framed; a "
            + "vanilla joiner silently stays uncompressed, so this is safe to leave on."
            + Profiles.Note;

        /// <summary>
        /// Wire protocol version. Bump on any framing change. 2 = the SS_Ready switch point
        /// (0.5.1); 1 = 0.5.0, whose switch point was one round trip early. A peer that
        /// advertises a different Proto is never framed, in either direction.
        /// </summary>
        private const int Proto = 2;

        internal const byte TagRaw = 0x00;
        internal const byte TagSmall = 0x01;
        internal const byte TagBig = 0x02;

        internal const string RpcCaps = "SS_Caps";
        internal const string RpcReady = "SS_Ready";
        internal const string RpcOff = "SS_Off";

        private const string ResSmall = "SmoothServer.dict.small";
        private const string ResBig = "SmoothServer.dict.big";

        // ---- config ---------------------------------------------------------------------

        private ConfigEntry<int> _minBytes;
        private ConfigEntry<int> _level;
        private ConfigEntry<bool> _useBigDict;
        private ConfigEntry<bool> _selfTest;

        internal static bool Active2;                 // module applied AND enabled
        internal static int MinBytes = 256;
        internal static byte SendTag = TagSmall;      // which dictionary WE compress with

        // ---- codecs ---------------------------------------------------------------------

        private static Compressor _cSmall, _cBig;
        private static Decompressor _dSmall, _dBig;
        private static int _dictHash;
        private static bool _codecsReady;

        // ---- per-socket state ------------------------------------------------------------

        private sealed class PeerState
        {
            public bool CapsSeen;
            public bool RecvFramed;
            public bool SendFramed;
            public bool SentCaps;
            public bool SentReady;
            /// <summary>An unframe failed on this socket: stay plain for the rest of its life.</summary>
            public bool Poisoned;
            public int TheirProto;
            public int TheirDictHash;
            /// <summary>The peer's ZNet uid. 0 until ZNet.RPC_PeerInfo lands - see OfferCapsForPeer.</summary>
            public long PeerId;
            /// <summary>Host name of the peer, for the handshake log lines.</summary>
            public string Who;
            // byte[] instances we have already framed and left in m_sendQueue. Reference
            // identity (byte[] does not override Equals), so a failed send cannot double-frame.
            public readonly HashSet<byte[]> Framed = new HashSet<byte[]>();
            // byte[] instances that were queued BEFORE the switch to framing (up to and
            // including our own SS_Ready) and must go out untouched even though SendFramed is
            // now true. Same reference identity; disjoint from Framed.
            public readonly HashSet<byte[]> Plain = new HashSet<byte[]>();

            // Transport hooks. The Harmony patches wire these to the real socket and routed
            // RPC; the handshake self-test wires them to an in-process peer, so both drive
            // exactly the same state machine.
            public Action<string, object[]> SendRpc;
            public Action ProtectQueue;   // mark everything queued right now "do not frame"
            public Action RestoreQueue;   // unframe anything we framed that is still queued
        }

        private static readonly Dictionary<ZSteamSocket, PeerState> States =
            new Dictionary<ZSteamSocket, PeerState>();

        private static bool _rpcsRegistered;
        private static float _handshakeTimer;
        private static float _statsTimer;
        private static bool _selfTestLogged;

        // ---- stats -----------------------------------------------------------------------

        internal static long RawOut, WireOut, RawIn, WireIn;
        internal static int FramedPeers;

        /// <summary>StatsLog: whether this peer (by uid) currently has framing negotiated on.</summary>
        internal static bool IsFramedFor(long peerUid)
        {
            if (!Active2) return false;
            var net = ZNet.instance;
            if (net == null) return false;
            var peer = net.GetPeer(peerUid);
            var sock = peer != null ? peer.m_socket as ZSteamSocket : null;
            if (sock == null) return false;
            PeerState st;
            return States.TryGetValue(sock, out st) && st.SendFramed;
        }

        // ---- lifecycle -------------------------------------------------------------------

        protected override void Bind()
        {
            _minBytes = BindSynced("MinBytes", 256,
                "Payloads smaller than this are sent raw (tag 0x00). Small packets do not " +
                "compress usefully and the CPU is better spent elsewhere. Vanilla-equivalent: infinity.");
            _level = BindSynced("Level", 1,
                "zstd compression level (1-9). 1 is what BetterNetworking used and is the right " +
                "answer for a real-time transport: almost all of the ratio, almost none of the CPU.");
            _useBigDict = BindSynced("UseBigDictionary", false,
                "Compress with the 512 KB trained dictionary instead of the 110 KB one. Slightly " +
                "better ratio, 512 KB more resident memory per process. Both dictionaries are always " +
                "loaded for DEcompression, so peers may disagree on this without any loss of " +
                "compatibility - the frame tag says which one each message used.");
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic: at load, run the two-peer handshake through an in-process simulation " +
                "(caps/ready ordering, a two-sided disable after a bad frame, a proto mismatch) and " +
                "log one PASS/FAIL line per case. Touches no sockets and changes no behaviour. " +
                "Machine-local, never synced. Leave off.");
        }

        protected override void ApplyPatches()
        {
            if (SmoothServerPlugin.BetterNetworkingPresent)
                throw new Exception("BetterNetworking is installed - it wraps the same ZSteamSocket " +
                                    "send queue and the two cannot coexist. Uninstall BetterNetworking.");

            MinBytes = Math.Max(0, _minBytes.Value);
            SendTag = _useBigDict.Value ? TagBig : TagSmall;

            InitCodecs(Math.Max(1, Math.Min(9, _level.Value)));

            var send = AccessTools.Method(typeof(ZSteamSocket), "SendQueuedPackages");
            if (send == null) throw new Exception("ZSteamSocket.SendQueuedPackages not found");
            var recv = AccessTools.Method(typeof(ZSteamSocket), "Recv");
            if (recv == null) throw new Exception("ZSteamSocket.Recv not found");
            var rrpcCtor = AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            if (rrpcCtor == null) throw new Exception("ZRoutedRpc(bool) constructor not found");
            var disconnect = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
            if (disconnect == null) throw new Exception("ZNet.Disconnect(ZNetPeer) not found");

            Harmony.Patch(send, prefix: new HarmonyMethod(typeof(CompressionModule), nameof(SendPrefix)));
            Harmony.Patch(recv, postfix: new HarmonyMethod(typeof(CompressionModule), nameof(RecvPostfix)));
            Harmony.Patch(rrpcCtor, postfix: new HarmonyMethod(typeof(CompressionModule), nameof(RoutedRpcCtorPostfix)));
            Harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(CompressionModule), nameof(DisconnectPrefix)));

            Active2 = true;
            SelfTest();
            if (_selfTest.Value) HandshakeSelfTest();
        }

        public override void Disable()
        {
            Active2 = false;
            States.Clear();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == EnabledCfg) { Active2 = Applied && Enabled; if (!Active2) States.Clear(); }
            else if (entry == _minBytes) MinBytes = Math.Max(0, _minBytes.Value);
            else if (entry == _useBigDict) SendTag = _useBigDict.Value ? TagBig : TagSmall;
            else if (entry == _level) InitCodecs(Math.Max(1, Math.Min(9, _level.Value)));
            else return;
            Log.LogInfo("[Compression] minBytes=" + MinBytes + " sendDict=" + DictName(SendTag) +
                        " level=" + _level.Value);
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return "dict=" + DictName(SendTag) + " minBytes=" + MinBytes +
                   " framedPeers=" + FramedPeers +
                   " out=" + RawOut + "->" + WireOut + "B in=" + WireIn + "->" + RawIn + "B" +
                   (FramedPeers == 0 ? "  (waiting for peers)" : "");
        }

        // ---- codecs ----------------------------------------------------------------------

        private static string DictName(byte tag)
        {
            return tag == TagBig ? "big" : tag == TagSmall ? "small" : "none";
        }

        private static byte[] LoadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("embedded resource '" + name + "' missing");
                var buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return buf;
            }
        }

        private static void InitCodecs(int level)
        {
            var small = LoadResource(ResSmall);
            var big = LoadResource(ResBig);

            _cSmall = new Compressor(level); _cSmall.LoadDictionary(small);
            _cBig = new Compressor(level); _cBig.LoadDictionary(big);
            _dSmall = new Decompressor(); _dSmall.LoadDictionary(small);
            _dBig = new Decompressor(); _dBig.LoadDictionary(big);

            _dictHash = unchecked(Fnv(small) * 31 + Fnv(big));
            _codecsReady = true;
        }

        private static int Fnv(byte[] data)
        {
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < data.Length; i++) h = (h ^ data[i]) * 16777619;
                return h;
            }
        }

        private void SelfTest()
        {
            if (_selfTestLogged) return;
            _selfTestLogged = true;

            // A 4 KB payload shaped roughly like a ZDOData package: repeated prefab hashes,
            // small floats and long runs of zeros. Not a benchmark - a proof the codec loaded
            // and that a round trip through each dictionary is byte-exact.
            var pkg = new ZPackage();
            for (int i = 0; i < 170; i++)
            {
                pkg.Write((int)-1234567890);
                pkg.Write((long)(9000000000000000000L + i));
                pkg.Write(1.5f); pkg.Write(0f); pkg.Write(-42.25f);
                pkg.Write("Greydwarf");
            }
            var raw = pkg.GetArray();

            var parts = new List<string>();
            foreach (var tag in new[] { TagSmall, TagBig })
            {
                var wire = Compress(raw, tag);
                var back = Decompress(wire, tag);
                bool ok = back.Length == raw.Length;
                if (ok) for (int i = 0; i < raw.Length; i++) if (raw[i] != back[i]) { ok = false; break; }
                parts.Add(DictName(tag) + "=" + wire.Length + "B (" +
                          (100f * wire.Length / raw.Length).ToString("0.0") + "%) roundtrip=" + (ok ? "OK" : "MISMATCH"));
                if (!ok) throw new Exception("zstd round trip failed with dict/" + DictName(tag));
            }

            Log.LogInfo("[Compression] self-test: " + raw.Length + "B ZPackage -> " +
                        string.Join(", ", parts.ToArray()) + "; ZstdSharp " +
                        typeof(Compressor).Assembly.GetName().Version +
                        " loaded, dictHash=" + _dictHash.ToString("x8") + ", sendDict=" + DictName(SendTag));
        }

        // ---- handshake self-test -----------------------------------------------------------
        //
        // Drives the REAL state machine (CapsReceived / ReadyReceived / OffReceived / Poison /
        // ReceiveBytes) over an in-process ordered channel instead of a socket, so the three
        // things issue #1 turned on can be checked without two running game clients:
        //   1. plain packets arriving between SS_Caps and SS_Ready must not be unframed,
        //   2. one bad frame must disable compression on BOTH ends,
        //   3. a peer on another wire proto must stay plain,
        // plus the one issue #2 turned on:
        //   4. the client must not spend its one offer before the peer has a uid.

        private sealed class SimMsg
        {
            public string Rpc;      // null => a data packet
            public object[] Args;
            public byte[] Wire;     // data packet exactly as it would go out
            public byte[] Expect;   // what the receiver must end up with (null = garbage)
        }

        private sealed class SimPeer
        {
            public string Name;      // who this side is
            public string PeerName;  // who it is talking to - what the real code would log
            public PeerState St;
            public readonly Queue<SimMsg> Inbox = new Queue<SimMsg>();
        }

        /// <summary>A stand-in ZNet uid: non-zero means "ZNet.RPC_PeerInfo has completed".</summary>
        private const long SimPeerId = 0x5111L;

        private static void SimWire(SimPeer from, SimPeer to)
        {
            from.PeerName = to.Name;
            from.St.Who = to.Name;
            from.St.PeerId = SimPeerId;   // ready by default; Case4 walks the not-ready window
            from.St.SendRpc = (rpc, args) => to.Inbox.Enqueue(new SimMsg { Rpc = rpc, Args = args });
            from.St.ProtectQueue = () => { };   // no real send queue in the simulation
            from.St.RestoreQueue = () => { };
        }

        private static void SimSend(SimPeer from, SimPeer to, byte[] raw)
        {
            to.Inbox.Enqueue(new SimMsg { Wire = from.St.SendFramed ? Frame(raw) : raw, Expect = raw });
        }

        private static void SimPump(SimPeer p, List<string> errs)
        {
            while (p.Inbox.Count > 0)
            {
                var m = p.Inbox.Dequeue();
                if (m.Rpc == RpcCaps)
                {
                    var pkg = (ZPackage)m.Args[0];
                    pkg.SetPos(0);
                    CapsReceived(p.St, p.PeerName, pkg.ReadInt(), pkg.ReadInt(), pkg.ReadInt());
                }
                else if (m.Rpc == RpcReady) ReadyReceived(p.St, p.PeerName);
                else if (m.Rpc == RpcOff) OffReceived(p.St, p.PeerName, (string)m.Args[0]);
                else
                {
                    var got = ReceiveBytes(p.St, p.PeerName, m.Wire);
                    if (m.Expect != null && !SameBytes(got, m.Expect))
                        errs.Add(p.Name + " read a corrupt packet");
                }
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] SimPayload(int seed, int len)
        {
            var pkg = new ZPackage();
            while (pkg.Size() < len) { pkg.Write(seed); pkg.Write("Greydwarf"); pkg.Write(0f); }
            return pkg.GetArray();
        }

        private static void SimCheck(List<string> errs, bool ok, string what)
        {
            if (!ok) errs.Add(what);
        }

        private void HandshakeSelfTest()
        {
            long rawOut = RawOut, wireOut = WireOut, rawIn = RawIn, wireIn = WireIn;
            try
            {
                Case1Ordering();
                Case2TwoSidedDisable();
                Case3ProtoMismatch();
                Case4PeerNotReady();
            }
            catch (Exception e)
            {
                Log.LogError("[Compression] self-test/handshake: threw " + e);
            }
            finally
            {
                RawOut = rawOut; WireOut = wireOut; RawIn = rawIn; WireIn = wireIn;
            }
        }

        private static void SimResult(string name, List<string> errs)
        {
            if (errs.Count == 0) Log.LogInfo("[Compression] self-test/handshake: " + name + " PASS");
            else Log.LogError("[Compression] self-test/handshake: " + name + " FAIL - " +
                              string.Join("; ", errs.ToArray()));
        }

        /// <summary>
        /// The issue #1 regression: the client floods plain packets in the window between its
        /// SS_Caps and the SS_Ready round trip. Nothing may be unframed before the peer's Ready.
        /// </summary>
        private static void Case1Ordering()
        {
            var errs = new List<string>();
            var c = new SimPeer { Name = "sim-client", St = new PeerState() };
            var s = new SimPeer { Name = "sim-server", St = new PeerState() };
            SimWire(c, s); SimWire(s, c);

            SimSend(c, s, SimPayload(1, 900));      // plain traffic already in flight
            SendCapsOnce(c.St);                     // client initiates
            SimSend(c, s, SimPayload(2, 900));      // ...and keeps flooding (Jotunn does)

            SimPump(s, errs);                       // 0.5.0 poisoned right here
            SimCheck(errs, !s.St.Poisoned, "server poisoned by plain traffic after SS_Caps");
            SimCheck(errs, s.St.SendFramed, "server did not start framing after answering SS_Ready");
            SimCheck(errs, !s.St.RecvFramed, "server unframes before the client's SS_Ready");

            SimSend(s, c, SimPayload(3, 900));      // server's first framed packet, after its Ready
            SimPump(c, errs);                       // caps, ready, then that packet
            SimCheck(errs, c.St.SendFramed && c.St.RecvFramed, "client did not finish the handshake");

            SimSend(c, s, SimPayload(4, 900));
            SimSend(c, s, SimPayload(5, 40));       // below MinBytes: framed with tag 0x00
            SimPump(s, errs);
            SimCheck(errs, s.St.RecvFramed && s.St.SendFramed, "server did not finish the handshake");
            SimCheck(errs, !s.St.Poisoned && !c.St.Poisoned, "handshake poisoned a peer");
            SimResult("caps/ready ordering", errs);
        }

        /// <summary>One bad frame must take compression down on both ends, not just the reader's.</summary>
        private static void Case2TwoSidedDisable()
        {
            var errs = new List<string>();
            var c = new SimPeer { Name = "sim-client", St = new PeerState() };
            var s = new SimPeer { Name = "sim-server", St = new PeerState() };
            SimWire(c, s); SimWire(s, c);

            SendCapsOnce(c.St);
            SimPump(s, errs);
            SimPump(c, errs);
            SimPump(s, errs);
            SimCheck(errs, c.St.SendFramed && c.St.RecvFramed && s.St.SendFramed && s.St.RecvFramed,
                     "handshake did not complete");

            // A plain RoutedRPC envelope arriving where a frame was expected - the exact shape
            // of issue #1's report, byte 0x48 first.
            s.Inbox.Enqueue(new SimMsg { Wire = new byte[] { 0x48, 0x6f, 0x34, 0xd8, 0x01, 0x02 } });
            SimPump(s, errs);
            SimCheck(errs, s.St.Poisoned && !s.St.SendFramed && !s.St.RecvFramed,
                     "reader did not disable itself");
            SimPump(c, errs);                       // the SS_Off
            SimCheck(errs, c.St.Poisoned && !c.St.SendFramed && !c.St.RecvFramed,
                     "peer kept framing after SS_Off");

            var after = SimPayload(6, 900);
            SimSend(c, s, after); SimSend(s, c, after);
            SimPump(s, errs); SimPump(c, errs);     // both directions plain and intact

            c.St.SentCaps = false; SendCapsOnce(c.St);   // a fresh handshake attempt
            SimPump(s, errs);
            SimCheck(errs, !s.St.SendFramed && !s.St.RecvFramed, "a poisoned peer re-armed");
            SimResult("two-sided disable", errs);
        }

        /// <summary>A 0.5.0 peer (proto 1) must leave both sides exactly as vanilla.</summary>
        private static void Case3ProtoMismatch()
        {
            var errs = new List<string>();
            var c = new SimPeer { Name = "sim-client", St = new PeerState() };
            var s = new SimPeer { Name = "sim-server", St = new PeerState() };
            SimWire(c, s); SimWire(s, c);

            CapsReceived(s.St, s.PeerName, Proto - 1, _dictHash, 1);
            SimCheck(errs, !s.St.SendFramed && !s.St.RecvFramed && !s.St.Poisoned,
                     "framing negotiated with an old-proto peer");
            SimCheck(errs, s.St.SentCaps && !s.St.SentReady, "server answered a proto mismatch with SS_Ready");
            int ready = 0;
            foreach (var m in c.Inbox) if (m.Rpc == RpcReady) ready++;
            SimCheck(errs, ready == 0, "SS_Ready sent to an old-proto peer");

            c.Inbox.Clear();
            SimSend(s, c, SimPayload(7, 900));      // still plain, still readable
            SimPump(c, errs);
            SimResult("proto mismatch", errs);
        }

        /// <summary>
        /// Issue #2: the client offered its caps on the first tick after the socket connected,
        /// which is inside the window where ZNet.RPC_PeerInfo has not yet given the peer a uid.
        /// A routed RPC to uid 0 is a broadcast, RouteRPC forwards a broadcast only to ready
        /// peers, so the offer reached nobody - and SentCaps latched, so it was never retried and
        /// compression silently never negotiated. Nothing may go out before the peer is ready,
        /// and the offer must still happen (exactly once) when it becomes ready.
        /// </summary>
        private static void Case4PeerNotReady()
        {
            var errs = new List<string>();
            var c = new SimPeer { Name = "sim-client", St = new PeerState() };
            var s = new SimPeer { Name = "sim-server", St = new PeerState() };
            SimWire(c, s); SimWire(s, c);

            c.St.PeerId = 0L;                       // ZNetPeer.IsReady() == false
            OfferCapsForPeer(c.St, 0L);             // the two ticks that used to burn the offer
            OfferCapsForPeer(c.St, 0L);
            SimCheck(errs, !c.St.SentCaps, "client offered caps before the peer was ready");
            SimCheck(errs, s.Inbox.Count == 0, "an offer went out while the peer id was 0");

            OfferCapsForPeer(c.St, SimPeerId);      // PeerInfo landed: uid assigned, peer routed
            SimCheck(errs, c.St.SentCaps, "client did not offer caps once the peer was ready");

            SimPump(s, errs); SimPump(c, errs); SimPump(s, errs);
            SimCheck(errs, c.St.SendFramed && c.St.RecvFramed && s.St.SendFramed && s.St.RecvFramed,
                     "handshake did not complete after the peer became ready");

            OfferCapsForPeer(c.St, SimPeerId);      // later ticks must not re-offer
            int caps = 0;
            foreach (var m in s.Inbox) if (m.Rpc == RpcCaps) caps++;
            SimCheck(errs, caps == 0, "client re-offered caps after the handshake");
            SimResult("peer readiness", errs);
        }

        internal static byte[] Compress(byte[] raw, byte tag)
        {
            var c = tag == TagBig ? _cBig : _cSmall;
            return c.Wrap(raw).ToArray();
        }

        internal static byte[] Decompress(byte[] payload, byte tag)
        {
            var d = tag == TagBig ? _dBig : _dSmall;
            return d.Unwrap(payload).ToArray();
        }

        // ---- framing ---------------------------------------------------------------------

        private static byte[] Frame(byte[] raw)
        {
            byte tag = SendTag;
            if (raw.Length < MinBytes) tag = TagRaw;

            byte[] payload = raw;
            if (tag != TagRaw)
            {
                try { payload = Compress(raw, tag); }
                catch (Exception e)
                {
                    Log.LogWarning("[Compression] compress failed, sending raw: " + e.Message);
                    tag = TagRaw; payload = raw;
                }
                // A payload that grew is not worth the CPU on the far end.
                if (payload.Length >= raw.Length) { tag = TagRaw; payload = raw; }
            }

            var framed = new byte[payload.Length + 1];
            framed[0] = tag;
            Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);

            RawOut += raw.Length;
            WireOut += framed.Length;
            return framed;
        }

        private static byte[] Unframe(byte[] framed)
        {
            var raw = UnframeNoStats(framed);
            WireIn += framed.Length;
            RawIn += raw.Length;
            return raw;
        }

        /// <summary>Unframe without touching the counters (used to undo a queued frame).</summary>
        private static byte[] UnframeNoStats(byte[] framed)
        {
            if (framed.Length < 1) return framed;
            byte tag = framed[0];
            var payload = new byte[framed.Length - 1];
            Buffer.BlockCopy(framed, 1, payload, 0, payload.Length);

            if (tag == TagRaw) return payload;
            if (tag == TagSmall || tag == TagBig) return Decompress(payload, tag);
            throw new Exception("unknown frame tag 0x" + tag.ToString("x2"));
        }

        // ---- patches ---------------------------------------------------------------------

        private static PeerState Get(ZSteamSocket s, bool create)
        {
            if (s == null) return null;
            PeerState st;
            if (States.TryGetValue(s, out st)) return st;
            if (!create) return null;
            st = new PeerState();
            States[s] = st;
            return st;
        }

        private static void SendPrefix(ZSteamSocket __instance, ref Queue<byte[]> ___m_sendQueue)
        {
            if (!Active2 || !_codecsReady) return;
            var st = Get(__instance, false);
            if (st == null || !st.SendFramed) return;
            if (___m_sendQueue == null || ___m_sendQueue.Count == 0) return;

            var outq = new Queue<byte[]>(___m_sendQueue.Count);
            var stillFramed = new HashSet<byte[]>();
            var stillPlain = new HashSet<byte[]>();
            foreach (var pkt in ___m_sendQueue)
            {
                if (pkt == null) continue;
                // Already framed on an earlier call whose send failed - pass it through
                // untouched. This is BetterNetworking's double-compression bug, fixed.
                if (st.Framed.Contains(pkt)) { outq.Enqueue(pkt); stillFramed.Add(pkt); continue; }
                // Queued before the switch (our SS_Ready and anything ahead of it): the peer
                // reads these as plain, so they must stay plain.
                if (st.Plain.Contains(pkt)) { outq.Enqueue(pkt); stillPlain.Add(pkt); continue; }
                byte[] framed;
                try { framed = Frame(pkt); }
                catch (Exception e) { Log.LogError("[Compression] framing failed: " + e); outq.Enqueue(pkt); continue; }
                outq.Enqueue(framed);
                stillFramed.Add(framed);
            }

            st.Framed.Clear();
            foreach (var b in stillFramed) st.Framed.Add(b);
            st.Plain.Clear();
            foreach (var b in stillPlain) st.Plain.Add(b);
            ___m_sendQueue = outq;
        }

        /// <summary>
        /// Everything sitting in the send queue right now was produced before we flipped to
        /// framing (our own SS_Ready is the last of them, because ZSteamSocket.Send enqueues and
        /// drains synchronously - the queue is normally already empty here). Mark them so
        /// SendPrefix passes them through plain if a failed send left them behind.
        /// </summary>
        private static void ProtectQueuedPlain(ZSteamSocket sock, PeerState st)
        {
            if (sock == null || sock.m_sendQueue == null) return;
            foreach (var pkt in sock.m_sendQueue) if (pkt != null) st.Plain.Add(pkt);
        }

        /// <summary>
        /// The peer has stopped unframing (it sent SS_Off). Anything we framed that is still
        /// queued would be unreadable to it, so put those packets back to their raw bytes
        /// rather than dropping them.
        /// </summary>
        private static void RestoreQueuedRaw(ZSteamSocket sock, PeerState st)
        {
            if (sock == null || sock.m_sendQueue == null || sock.m_sendQueue.Count == 0) return;
            var outq = new Queue<byte[]>(sock.m_sendQueue.Count);
            foreach (var pkt in sock.m_sendQueue)
            {
                if (pkt == null) continue;
                if (st.Framed.Contains(pkt))
                {
                    try { outq.Enqueue(UnframeNoStats(pkt)); continue; }
                    catch (Exception e) { Log.LogWarning("[Compression] could not restore a queued packet: " + e.Message); }
                }
                outq.Enqueue(pkt);
            }
            sock.m_sendQueue = outq;
        }

        private static void RecvPostfix(ZSteamSocket __instance, ref ZPackage __result)
        {
            if (!Active2 || !_codecsReady || __result == null) return;
            var st = Get(__instance, false);
            if (st == null || !st.RecvFramed) return;

            var wire = __result.GetArray();
            var raw = ReceiveBytes(st, __instance.GetHostName(), wire);
            if (!ReferenceEquals(raw, wire)) __result = new ZPackage(raw);
        }

        /// <summary>
        /// The receive half of the state machine, socket-free so the self-test can drive it.
        /// Returns the raw bytes, or the input untouched when we are not unframing this peer
        /// (or just stopped). Never throws: a stream we cannot unframe means the peer disagrees
        /// with us about framing, which poisons the socket for good.
        /// </summary>
        private static byte[] ReceiveBytes(PeerState st, string who, byte[] wire)
        {
            if (!st.RecvFramed) return wire;
            try { return Unframe(wire); }
            catch (Exception e) { Poison(st, who, e.Message); return wire; }
        }

        private static void DisconnectPrefix(ZNetPeer peer)
        {
            var s = peer != null ? peer.m_socket as ZSteamSocket : null;
            if (s != null && States.Remove(s)) RecountFramed();
        }

        private static void RoutedRpcCtorPostfix(ZRoutedRpc __instance)
        {
            // A fresh ZRoutedRpc = a fresh session: every socket, and every handshake, is gone.
            States.Clear();
            FramedPeers = 0;
            _rpcsRegistered = false;
            TryRegisterRpcs();
        }

        private static void TryRegisterRpcs()
        {
            if (_rpcsRegistered) return;
            var rrpc = ZRoutedRpc.instance;
            if (rrpc == null) return;
            try
            {
                rrpc.Register<ZPackage>(RpcCaps, OnCaps);
                rrpc.Register(RpcReady, OnReady);
                rrpc.Register<string>(RpcOff, OnOff);
                _rpcsRegistered = true;
                Log.LogInfo("[Compression] routed RPCs '" + RpcCaps + "' / '" + RpcReady + "' / '" +
                            RpcOff + "' registered");
            }
            catch (Exception e)
            {
                Log.LogWarning("[Compression] could not register routed RPCs: " + e.Message);
                _rpcsRegistered = true; // do not spin
            }
        }

        // ---- handshake -------------------------------------------------------------------

        private static ZSteamSocket SocketOf(long peerId)
        {
            var net = ZNet.instance;
            if (net == null) return null;
            var peer = net.GetPeer(peerId);
            return peer != null ? peer.m_socket as ZSteamSocket : null;
        }

        /// <summary>
        /// Attach the live transport to a socket's state: the routed-RPC sender and the two
        /// send-queue fix-ups. Idempotent; the self-test substitutes its own hooks instead.
        /// </summary>
        private static PeerState Attach(ZSteamSocket sock, long peerId)
        {
            var st = Get(sock, true);
            st.PeerId = peerId;
            if (st.Who == null) st.Who = sock.GetHostName();
            if (st.SendRpc == null)
            {
                var s = sock;
                var state = st;
                st.SendRpc = (name, args) =>
                {
                    var rrpc = ZRoutedRpc.instance;
                    if (rrpc != null) rrpc.InvokeRoutedRPC(state.PeerId, name, args);
                };
                st.ProtectQueue = () => ProtectQueuedPlain(s, state);
                st.RestoreQueue = () => RestoreQueuedRaw(s, state);
            }
            return st;
        }

        private static void Invoke(PeerState st, string rpc, object[] args)
        {
            if (st.SendRpc == null) return;
            try { st.SendRpc(rpc, args); }
            catch (Exception e) { Log.LogWarning("[Compression] " + rpc + " send failed: " + e.Message); }
        }

        private static void SendCapsOnce(PeerState st)
        {
            if (st.SentCaps) return;
            // Issue #2. ZRoutedRpc reads target id 0 as "everybody", and RouteRPC forwards a
            // broadcast only to peers that are already ready - so an offer addressed to a peer
            // without a uid reaches nobody, while SentCaps would latch and stop us ever trying
            // again. Refuse instead: a state that never gets a peer id simply never offers.
            if (st.PeerId == 0L) return;
            st.SentCaps = true;
            var pkg = new ZPackage();
            pkg.Write(Proto);
            pkg.Write(_dictHash);
            pkg.Write(Active2 ? 1 : 0);
            Invoke(st, RpcCaps, new object[] { pkg });
            Log.LogInfo("[Compression] sent caps to " + (st.Who ?? st.PeerId.ToString()) +
                        " (proto " + Proto + ", dict " + _dictHash.ToString("x8") + ")");
        }

        /// <summary>
        /// One tick's worth of client-side handshake for a single peer. <paramref name="peerUid"/>
        /// is ZNetPeer.m_uid, which stays 0 until ZNet.RPC_PeerInfo assigns it (ZNet.cs:1070) and,
        /// a few lines later in that same method, hands the peer to ZRoutedRpc.AddPeer - so a
        /// non-zero uid (== ZNetPeer.IsReady()) is exactly the point at which a routed RPC can
        /// reach this peer. ZRoutedRpc.GetPeer is private, so there is no public way to confirm
        /// the routed peer list beyond that. Split out of Tick so the self-test drives the same gate.
        /// </summary>
        private static void OfferCapsForPeer(PeerState st, long peerUid)
        {
            if (peerUid == 0L) return;          // not ready: an offer now would go nowhere
            st.PeerId = peerUid;
            if (st.SentCaps || st.CapsSeen || st.Poisoned) return;
            SendCapsOnce(st);
        }

        /// <summary>
        /// Send SS_Ready and, from the very next byte on, frame everything. The Ready itself
        /// goes out plain: ZSteamSocket.Send enqueues and drains inside the Invoke above, while
        /// SendFramed is still false, so SendPrefix cannot see it - and if that send failed and
        /// left it queued, ProtectQueue marks it (and anything ahead of it) untouchable.
        /// </summary>
        private static void SendReadyAndSwitch(PeerState st)
        {
            if (st.SentReady) return;
            st.SentReady = true;
            Invoke(st, RpcReady, new object[0]);
            if (st.ProtectQueue != null) st.ProtectQueue();
            st.SendFramed = true;
        }

        private static void OnCaps(long sender, ZPackage pkg)
        {
            if (!Active2 || !_codecsReady) return;
            var sock = SocketOf(sender);
            if (sock == null) return;                    // PlayFab peer, or gone
            var st = Attach(sock, sender);

            int proto = pkg.ReadInt();
            int dictHash = pkg.ReadInt();
            int flags = pkg.ReadInt();
            CapsReceived(st, sock.GetHostName(), proto, dictHash, flags);
        }

        private static void CapsReceived(PeerState st, string who, int proto, int dictHash, int flags)
        {
            if (st.Poisoned) return;
            bool firstCaps = !st.CapsSeen;
            st.CapsSeen = true;
            st.TheirProto = proto;
            st.TheirDictHash = dictHash;
            // One line per peer per connection - the other half of the handshake, so a server log
            // shows the offer arriving even when the negotiation then declines it.
            if (firstCaps)
                Log.LogInfo("[Compression] caps from " + who + ": proto " + proto +
                            " dict " + dictHash.ToString("x8") + " enabled=" + flags);

            if (proto != Proto)
            {
                // A 0.5.0 peer (proto 1) framed one round trip too early - issue #1. Different
                // proto, no framing, in either direction: both sides stay exactly as vanilla.
                Log.LogInfo("[Compression] " + who + " runs SmoothServer wire proto " + proto +
                            " (ours " + Proto + ") - staying uncompressed with this peer");
                SendCapsOnce(st);
                return;
            }
            if (dictHash != _dictHash || flags == 0)
            {
                Log.LogWarning("[Compression] " + who + " advertises proto=" + proto +
                               " dict=" + dictHash.ToString("x8") + " enabled=" + flags +
                               " (ours proto=" + Proto + " dict=" + _dictHash.ToString("x8") +
                               ") - staying uncompressed with this peer");
                SendCapsOnce(st);
                return;
            }

            SendCapsOnce(st);
            SendReadyAndSwitch(st);
        }

        private static void OnReady(long sender)
        {
            if (!Active2 || !_codecsReady) return;
            var sock = SocketOf(sender);
            if (sock == null) return;
            var st = Attach(sock, sender);
            ReadyReceived(st, sock.GetHostName());
        }

        /// <summary>
        /// The peer's SS_Ready: everything it sends from here on is framed, so start unframing
        /// at exactly this point in its stream.
        /// </summary>
        private static void ReadyReceived(PeerState st, string who)
        {
            if (st.Poisoned || st.RecvFramed) return;
            st.RecvFramed = true;
            RecountFramed();
            Log.LogInfo("[Compression] " + who + " negotiated: framing on (dict=" +
                        DictName(SendTag) + ", proto " + Proto + ")");
        }

        private static void OnOff(long sender, string reason)
        {
            if (!Active2 || !_codecsReady) return;
            var sock = SocketOf(sender);
            if (sock == null) return;
            var st = Get(sock, false);
            if (st == null) return;
            OffReceived(st, sock.GetHostName(), reason);
        }

        /// <summary>
        /// We could not unframe something. Tell the peer while it can still understand us (the
        /// SS_Off goes out framed, because our send direction may well be fine), then drop to
        /// plain in both directions for the rest of this socket's life.
        /// </summary>
        private static void Poison(PeerState st, string who, string reason)
        {
            bool first = !st.Poisoned;
            st.Poisoned = true;
            if (first && st.SendFramed) Invoke(st, RpcOff, new object[] { reason ?? "" });
            // Anything still queued is ahead of that SS_Off and was framed while the peer was
            // still unframing, so it stays as it is; only what comes after goes plain.
            st.RecvFramed = false;
            st.SendFramed = false;
            st.Framed.Clear();
            st.Plain.Clear();
            RecountFramed();
            if (first)
                Log.LogError("[Compression] " + who + " compression disabled (" + reason +
                             ") - running plain");
        }

        /// <summary>
        /// The peer's SS_Off: it stopped unframing at the moment it sent this, so everything
        /// after it is plain in both directions.
        /// </summary>
        private static void OffReceived(PeerState st, string who, string reason)
        {
            bool first = !st.Poisoned;
            st.Poisoned = true;
            st.RecvFramed = false;
            if (st.SendFramed)
            {
                st.SendFramed = false;
                if (st.RestoreQueue != null) st.RestoreQueue();
            }
            st.Framed.Clear();
            st.Plain.Clear();
            RecountFramed();
            if (first)
                Log.LogWarning("[Compression] " + who + " compression disabled (peer reported: " +
                               reason + ") - running plain");
        }

        private static void RecountFramed()
        {
            int n = 0;
            foreach (var kv in States) if (kv.Value.SendFramed && kv.Value.RecvFramed) n++;
            FramedPeers = n;
        }

        // ---- tick ------------------------------------------------------------------------

        /// <summary>Called every frame from the plugin. Cheap when there is nothing to do.</summary>
        internal static void Tick(float dt)
        {
            if (!Active2 || !_codecsReady) return;
            var net = ZNet.instance;
            if (net == null) { if (States.Count > 0) { States.Clear(); FramedPeers = 0; } return; }
            if (ZRoutedRpc.instance == null) return;
            TryRegisterRpcs();
            if (!_rpcsRegistered) return;

            _handshakeTimer += dt;
            if (_handshakeTimer >= 2f)
            {
                _handshakeTimer = 0f;
                // Only the client initiates. The server answers, which keeps the join-time cost
                // at one extra round trip per peer instead of a broadcast.
                if (!net.IsServer())
                {
                    foreach (var peer in net.GetConnectedPeers())
                    {
                        var sock = peer.m_socket as ZSteamSocket;
                        if (sock == null) continue;
                        // A ZNetPeer is in ZNet.m_peers from the moment its socket connects, but
                        // its uid stays 0 until RPC_PeerInfo completes - and the first tick after
                        // a join lands inside that window. Never attach or offer with uid 0
                        // (issue #2): wait for the peer to be ready, then offer exactly once.
                        if (!peer.IsReady()) continue;
                        var st = Attach(sock, peer.m_uid);
                        OfferCapsForPeer(st, peer.m_uid);
                    }
                }

                // prune sockets that went away without a Disconnect call
                List<ZSteamSocket> dead = null;
                foreach (var kv in States)
                    if (kv.Key == null || !kv.Key.IsConnected())
                        (dead ?? (dead = new List<ZSteamSocket>())).Add(kv.Key);
                if (dead != null) { foreach (var s in dead) States.Remove(s); RecountFramed(); }
            }

            _statsTimer += dt;
            if (_statsTimer >= 300f)
            {
                _statsTimer = 0f;
                if (FramedPeers > 0 && RawOut > 0)
                    Log.LogInfo("[Compression] peers=" + FramedPeers +
                                " out " + RawOut + "->" + WireOut + "B (" +
                                (100f * WireOut / Math.Max(1L, RawOut)).ToString("0.0") + "%)" +
                                " in " + WireIn + "->" + RawIn + "B (" +
                                (100f * WireIn / Math.Max(1L, RawIn)).ToString("0.0") + "%)");
            }
        }
    }
}
