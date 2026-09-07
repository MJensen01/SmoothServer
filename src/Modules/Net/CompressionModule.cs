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
    /// HANDSHAKE (routed RPCs "SS_Caps" and "SS_Ready", both directions, ordered because they
    /// ride the same reliable socket as the data):
    ///   client  -> server : SS_Caps {proto, flags, dictHash}
    ///   server: learns the client's caps, sets recvFramed=true FIRST, then answers
    ///           SS_Caps + SS_Ready
    ///   client: learns the server's caps, sets recvFramed=true FIRST, then sends SS_Ready;
    ///           on the server's SS_Ready it sets sendFramed=true
    ///   server: on the client's SS_Ready it sets sendFramed=true
    /// Each side therefore enables *receiving* frames strictly before it tells the peer it may
    /// start *sending* them. There is no window in which a framed message can arrive at a peer
    /// that is not yet expecting one.
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

        /// <summary>Wire protocol version. Bump on any framing change.</summary>
        private const int Proto = 1;

        internal const byte TagRaw = 0x00;
        internal const byte TagSmall = 0x01;
        internal const byte TagBig = 0x02;

        internal const string RpcCaps = "SS_Caps";
        internal const string RpcReady = "SS_Ready";

        private const string ResSmall = "SmoothServer.dict.small";
        private const string ResBig = "SmoothServer.dict.big";

        // ---- config ---------------------------------------------------------------------

        private ConfigEntry<int> _minBytes;
        private ConfigEntry<int> _level;
        private ConfigEntry<bool> _useBigDict;

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
            public int TheirProto;
            public int TheirDictHash;
            // byte[] instances we have already framed and left in m_sendQueue. Reference
            // identity (byte[] does not override Equals), so a failed send cannot double-frame.
            public readonly HashSet<byte[]> Framed = new HashSet<byte[]>();
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
            if (framed.Length < 1) return framed;
            byte tag = framed[0];
            var payload = new byte[framed.Length - 1];
            Buffer.BlockCopy(framed, 1, payload, 0, payload.Length);

            byte[] raw;
            if (tag == TagRaw) raw = payload;
            else if (tag == TagSmall || tag == TagBig) raw = Decompress(payload, tag);
            else throw new Exception("unknown frame tag 0x" + tag.ToString("x2"));

            WireIn += framed.Length;
            RawIn += raw.Length;
            return raw;
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
            foreach (var pkt in ___m_sendQueue)
            {
                if (pkt == null) continue;
                // Already framed on an earlier call whose send failed - pass it through
                // untouched. This is BetterNetworking's double-compression bug, fixed.
                if (st.Framed.Contains(pkt)) { outq.Enqueue(pkt); stillFramed.Add(pkt); continue; }
                byte[] framed;
                try { framed = Frame(pkt); }
                catch (Exception e) { Log.LogError("[Compression] framing failed: " + e); outq.Enqueue(pkt); continue; }
                outq.Enqueue(framed);
                stillFramed.Add(framed);
            }

            st.Framed.Clear();
            foreach (var b in stillFramed) st.Framed.Add(b);
            ___m_sendQueue = outq;
        }

        private static void RecvPostfix(ZSteamSocket __instance, ref ZPackage __result)
        {
            if (!Active2 || !_codecsReady || __result == null) return;
            var st = Get(__instance, false);
            if (st == null || !st.RecvFramed) return;

            try
            {
                __result = new ZPackage(Unframe(__result.GetArray()));
            }
            catch (Exception e)
            {
                // Never throw on the receive path. A stream we cannot unframe means the peer
                // disagrees with us about framing - stop framing this peer and say so loudly.
                st.RecvFramed = false;
                st.SendFramed = false;
                Log.LogError("[Compression] unframing failed for " + __instance.GetHostName() +
                             " - compression disabled for this peer: " + e.Message);
            }
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
                _rpcsRegistered = true;
                Log.LogInfo("[Compression] routed RPCs '" + RpcCaps + "' / '" + RpcReady + "' registered");
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

        private static void SendCaps(long peerId)
        {
            var pkg = new ZPackage();
            pkg.Write(Proto);
            pkg.Write(_dictHash);
            pkg.Write(Active2 ? 1 : 0);
            ZRoutedRpc.instance.InvokeRoutedRPC(peerId, RpcCaps, pkg);
        }

        private static void OnCaps(long sender, ZPackage pkg)
        {
            if (!Active2 || !_codecsReady) return;
            var sock = SocketOf(sender);
            if (sock == null) return;                    // PlayFab peer, or gone
            var st = Get(sock, true);

            int proto = pkg.ReadInt();
            int dictHash = pkg.ReadInt();
            int flags = pkg.ReadInt();
            st.CapsSeen = true;
            st.TheirProto = proto;
            st.TheirDictHash = dictHash;

            bool compatible = proto == Proto && dictHash == _dictHash && flags != 0;
            if (!compatible)
            {
                Log.LogWarning("[Compression] " + sock.GetHostName() + " advertises proto=" + proto +
                               " dict=" + dictHash.ToString("x8") + " enabled=" + flags +
                               " (ours proto=" + Proto + " dict=" + _dictHash.ToString("x8") +
                               ") - staying uncompressed with this peer");
                if (!st.SentCaps) { st.SentCaps = true; SendCaps(sender); }
                return;
            }

            // Enable RECEIVING before telling them they may start SENDING. This ordering is the
            // whole safety argument: a framed message can never reach a peer that is not ready.
            st.RecvFramed = true;
            if (!st.SentCaps) { st.SentCaps = true; SendCaps(sender); }
            if (!st.SentReady)
            {
                st.SentReady = true;
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReady, new object[0]);
            }
        }

        private static void OnReady(long sender)
        {
            if (!Active2 || !_codecsReady) return;
            var sock = SocketOf(sender);
            if (sock == null) return;
            var st = Get(sock, true);
            if (st.SendFramed) return;
            st.SendFramed = true;
            RecountFramed();
            Log.LogInfo("[Compression] " + sock.GetHostName() + " negotiated: framing on (dict=" +
                        DictName(SendTag) + ", proto " + Proto + ")");
        }

        private static void RecountFramed()
        {
            int n = 0;
            foreach (var kv in States) if (kv.Value.SendFramed) n++;
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
                        var st = Get(sock, true);
                        if (st.SentCaps || st.CapsSeen) continue;
                        st.SentCaps = true;
                        try { SendCaps(peer.m_uid); }
                        catch (Exception e) { Log.LogWarning("[Compression] caps send failed: " + e.Message); }
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
