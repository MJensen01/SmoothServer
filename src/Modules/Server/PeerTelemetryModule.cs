using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// M1 - per-peer instrumentation. No Harmony patches: it polls the live sockets.
    ///
    /// Every SampleIntervalSec it walks ZDOMan.m_peers and records, per peer:
    ///   * SteamNetworkingSockets.GetConnectionRealTimeStatus(m_con) -> ping, local/remote
    ///     connection quality, out/in B/s, m_cbPendingReliable / m_cbPendingUnreliable (queued
    ///     but not yet on the wire), m_cbSentUnackedReliable (in flight) and
    ///     m_nSendRateBytesPerSecond (Steam's own bandwidth estimate)
    ///   * ISocket.GetSendQueueSize() - exactly the number ZDOMan.SendZDOs budgets against
    ///   * the ZDOMan-side per-peer state: peer.m_zdos.Count (ZDOs this peer is known to have),
    ///     peer.m_forceSend.Count, peer.m_invalidSector.Count
    ///   * LagProbe's own round-trip numbers (rtt / jitter / loss / the client's frame time),
    ///     which do not depend on Steam answering at all.
    /// plus the global ZDOMan.m_zdosSentLastSec / m_zdosRecvLastSec.
    ///
    /// <b>Which Steam interface (corrected in 0.3.1).</b> The two builds of assembly_valheim.dll
    /// are compiled differently: on the CLIENT, ZSteamSocket calls SteamNetworkingSockets; on the
    /// DEDICATED SERVER it calls SteamGameServerNetworkingSockets throughout (NOTES §20). Only the
    /// matching half of Steamworks is initialised per process, so the other one throws
    /// "Steamworks is not initialized.". We therefore probe the build's own interface FIRST, cache
    /// whichever answers, and log it once.
    ///
    /// Note one vanilla quirk the decompile exposes: even on the server build,
    /// ZSteamSocket.GetConnectionQuality still calls the *client* SteamNetworkingSockets - so it
    /// throws on a dedicated server. It is client-UI-only in vanilla, which is why nobody noticed.
    /// We do not call it at all any more: GetConnectionRealTimeStatus carries the same five
    /// numbers and is the interface the build actually uses.
    ///
    /// <b>The zero-telemetry bug (fixed in 0.5.1).</b> On the live server every peer read
    /// ping=0 / quality=0 / 0 B/s forever, so AdaptiveBudget never got a single sample. The cause
    /// was not the Steam interface: it was <c>peer.m_socket as ZSteamSocket</c> returning null.
    /// ServerSync (vendored by us, by NoVikingLeftBehind AND by third-party mods such as Hugo's
    /// Armory) swaps a decorator socket into <c>ZNetPeer.m_socket</c> during ZNet.RPC_PeerInfo and
    /// restores it from a coroutine afterwards; each copy's restore only recognises *its own*
    /// BufferingSocket type, so with several ServerSync copies loaded the unwind does not
    /// complete and a peer is left holding another mod's decorator (which derives from
    /// ZPlayFabSocket, not ZSteamSocket) for the rest of the session. Every ISocket call still
    /// works - the decorator forwards to <c>Original</c> - which is why socketQueue and zdos
    /// looked healthy while every Steam number was zero.
    ///
    /// So we no longer cast: <see cref="ResolveSteamSocket"/> walks the decorator chain to the
    /// real ZSteamSocket, and every failure now says so in the log exactly once, per
    /// <see cref="NoteStatusFailure"/> / <see cref="NoteNoSteamSocket"/> - a silent zero is not
    /// possible any more.
    ///
    /// The snapshot is static so AdaptiveBudget (and anything later) can read it without
    /// re-polling Steam.
    /// </summary>
    internal sealed class PeerTelemetryModule : FeatureModule
    {
        public override string Name => "PeerTelemetry";

        internal struct PeerStat
        {
            public long Uid;
            public string PlayerName;
            public bool Valid;              // Steam real-time status answered
            public int Ping;                // ms
            public float QualityLocal;
            public float QualityRemote;
            public float OutBytesPerSec;
            public float InBytesPerSec;
            public int PendingReliable;     // queued in Steam, not yet sent
            public int PendingUnreliable;
            public int SentUnackedReliable; // in flight
            public int SendRateBytesPerSec; // Steam's own estimate
            public int SocketQueueBytes;    // ISocket.GetSendQueueSize() - what SendZDOs budgets on
            public int ZdoQueue;            // peer.m_zdos.Count
            public int ForceSend;
            public int InvalidSector;
            public string SocketKind;       // runtime type of ZNetPeer.m_socket (diagnostics)
            public float SampledAt;
        }

        private ConfigEntry<float> _interval;
        private ConfigEntry<float> _sampleInterval;

        internal static bool Active;
        internal static float IntervalSec = 10f;
        internal static float SampleIntervalSec = 1f;

        private static readonly Dictionary<long, PeerStat> Stats = new Dictionary<long, PeerStat>();
        private static readonly List<long> TempIds = new List<long>();
        private static readonly HashSet<long> GoodRead = new HashSet<long>();
        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();
        private static float _sampleAcc;
        private static float _logAcc;
        private static int _steamIface;     // 0 = unknown, 1 = user, 2 = gameserver
        private static bool _ifaceLogged;

        /// <summary>The interface itself is not initialised in this process (it threw).</summary>
        private const string IfaceDead = "interface-not-initialised";

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("PeerTelemetry", "Enabled", true,
                "Log a per-peer network line (ping, pending/in-flight bytes, send rate, ZDO queue). " +
                "Also feeds AdaptiveBudget.");
            _interval = cfg.Bind("PeerTelemetry", "IntervalSec", 10f,
                "Seconds between per-peer telemetry lines.");
            _sampleInterval = cfg.Bind("PeerTelemetry", "SampleIntervalSec", 1f,
                "Seconds between samples of the live Steam connection status. The snapshot other " +
                "modules read is refreshed at this rate; the log line is printed every IntervalSec.");
            Watch(_interval);
            Watch(_sampleInterval);
        }

        protected override void ApplyPatches()
        {
            IntervalSec = Mathf.Max(1f, _interval.Value);
            SampleIntervalSec = Mathf.Clamp(_sampleInterval.Value, 0.1f, 10f);
            Stats.Clear();
            GoodRead.Clear();
            WarnedOnce.Clear();
            _sampleAcc = 0f; _logAcc = 0f;
            Active = true;
            Log.LogInfo("[PeerTelemetry] sampling every " + SampleIntervalSec.ToString("F1") +
                        "s, logging every " + IntervalSec.ToString("F1") + "s (no patches)");
        }

        public override void Disable()
        {
            Active = false;
            Stats.Clear();
            GoodRead.Clear();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _interval) IntervalSec = Mathf.Max(1f, _interval.Value);
            else if (entry == _sampleInterval) SampleIntervalSec = Mathf.Clamp(_sampleInterval.Value, 0.1f, 10f);
            else return;
            _logAcc = 0f;
            Log.LogInfo("[PeerTelemetry] interval=" + IntervalSec.ToString("F1") +
                        "s sample=" + SampleIntervalSec.ToString("F1") + "s");
        }

        // ---- public snapshot ------------------------------------------------------------

        /// <summary>Latest sample for a peer uid. False when telemetry is off or the peer is new.</summary>
        internal static bool TryGet(long uid, out PeerStat stat)
        {
            return Stats.TryGetValue(uid, out stat);
        }

        /// <summary>Copy of every current per-peer sample.</summary>
        internal static PeerStat[] Snapshot()
        {
            var a = new PeerStat[Stats.Count];
            Stats.Values.CopyTo(a, 0);
            return a;
        }

        // ---- socket resolution -----------------------------------------------------------

        private static readonly Dictionary<Type, FieldInfo> InnerFieldCache = new Dictionary<Type, FieldInfo>();

        /// <summary>
        /// The real <see cref="ZSteamSocket"/> behind a peer's ISocket, unwrapping any decorator
        /// sockets in front of it (ServerSync's BufferingSocket and friends: they hold the socket
        /// they wrap in a field called <c>Original</c> and forward every ISocket call to it).
        /// Returns null when there is no Steam socket in the chain - e.g. a genuine PlayFab peer.
        /// </summary>
        internal static ZSteamSocket ResolveSteamSocket(ISocket sock)
        {
            for (int depth = 0; sock != null && depth < 8; depth++)
            {
                var zs = sock as ZSteamSocket;
                if (zs != null) return zs;
                var inner = InnerSocket(sock);
                if (inner == null || ReferenceEquals(inner, sock)) return null;
                sock = inner;
            }
            return null;
        }

        /// <summary>The ISocket a decorator wraps, or null. One reflection pass per socket type.</summary>
        private static ISocket InnerSocket(ISocket sock)
        {
            var t = sock.GetType();
            FieldInfo f;
            if (!InnerFieldCache.TryGetValue(t, out f))
            {
                f = FindInnerField(t);
                InnerFieldCache[t] = f;
            }
            if (f == null) return null;
            try { return f.GetValue(sock) as ISocket; }
            catch { return null; }
        }

        private static FieldInfo FindInnerField(Type t)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo any = null;
                FieldInfo[] fields;
                try { fields = cur.GetFields(Flags); }
                catch { continue; }

                for (int i = 0; i < fields.Length; i++)
                {
                    if (!typeof(ISocket).IsAssignableFrom(fields[i].FieldType)) continue;
                    // ServerSync calls it "Original"; prefer that over any other ISocket field.
                    if (fields[i].Name.IndexOf("Original", StringComparison.OrdinalIgnoreCase) >= 0)
                        return fields[i];
                    if (any == null) any = fields[i];
                }
                if (any != null) return any;
            }
            return null;
        }

        // ---- polling -------------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Active || !ServerActive()) return;

            _sampleAcc += dt;
            _logAcc += dt;

            if (_sampleAcc >= SampleIntervalSec)
            {
                _sampleAcc = 0f;
                Sample();
            }

            if (_logAcc >= IntervalSec)
            {
                _logAcc = 0f;
                Emit();
            }
        }

        private static void Sample()
        {
            var zm = ZDOMan.instance;
            if (zm == null) return;

            float now = Time.realtimeSinceStartup;
            TempIds.Clear();

            for (int i = 0; i < zm.m_peers.Count; i++)
            {
                var zp = zm.m_peers[i];
                if (zp == null || zp.m_peer == null) continue;

                var stat = new PeerStat
                {
                    Uid = zp.m_peer.m_uid,
                    PlayerName = string.IsNullOrEmpty(zp.m_peer.m_playerName) ? "?" : zp.m_peer.m_playerName,
                    ZdoQueue = zp.m_zdos.Count,
                    ForceSend = zp.m_forceSend.Count,
                    InvalidSector = zp.m_invalidSector.Count,
                    SampledAt = now
                };

                var sock = zp.m_peer.m_socket;
                stat.SocketKind = sock == null ? "null" : sock.GetType().Name;
                if (sock != null)
                {
                    try { stat.SocketQueueBytes = sock.GetSendQueueSize(); }
                    catch { stat.SocketQueueBytes = -1; }
                }

                var zs = ResolveSteamSocket(sock);
                if (zs == null)
                {
                    NoteNoSteamSocket(stat);
                }
                else
                {
                    if (!ReferenceEquals(zs, sock)) NoteWrappedSocket(stat);

                    SteamNetConnectionRealTimeStatus_t st;
                    string failure;
                    if (TryRealTimeStatus(zs, out st, out failure))
                    {
                        stat.Valid = true;
                        stat.Ping = st.m_nPing;
                        stat.QualityLocal = st.m_flConnectionQualityLocal;
                        stat.QualityRemote = st.m_flConnectionQualityRemote;
                        stat.OutBytesPerSec = st.m_flOutBytesPerSec;
                        stat.InBytesPerSec = st.m_flInBytesPerSec;
                        stat.PendingReliable = st.m_cbPendingReliable;
                        stat.PendingUnreliable = st.m_cbPendingUnreliable;
                        stat.SentUnackedReliable = st.m_cbSentUnackedReliable;
                        stat.SendRateBytesPerSec = st.m_nSendRateBytesPerSecond;
                        NoteGoodRead(stat);
                    }
                    else
                    {
                        NoteStatusFailure(failure);
                    }
                }

                Stats[stat.Uid] = stat;
                TempIds.Add(stat.Uid);
            }

            // drop peers that went away
            if (Stats.Count != TempIds.Count)
            {
                var stale = new List<long>();
                foreach (var kv in Stats)
                    if (!TempIds.Contains(kv.Key)) stale.Add(kv.Key);
                foreach (var id in stale) { Stats.Remove(id); GoodRead.Remove(id); }
            }
        }

        // ---- Steam real-time status ------------------------------------------------------

        /// <summary>
        /// Read Steam's live status for this connection. Returns false with a reason in
        /// <paramref name="failure"/>; a refused CONNECTION never latches the interface off.
        /// </summary>
        private static bool TryRealTimeStatus(ZSteamSocket zs, out SteamNetConnectionRealTimeStatus_t status,
                                              out string failure)
        {
            status = default(SteamNetConnectionRealTimeStatus_t);
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            failure = null;

            // The build's own interface first: game-server on a dedicated server, user on a client.
            bool serverFirst = SmoothServerPlugin.IsServerSide;
            int first = serverFirst ? 2 : 1;
            int second = serverFirst ? 1 : 2;

            if (_steamIface == 1 || _steamIface == 2)
            {
                if (Call(_steamIface, zs, ref status, ref lanes, out failure)) return true;
                // A live interface that refuses THIS connection (closed, still handshaking) is a
                // per-connection failure, not a reason to stop using the interface: 0.3.1 latched
                // the whole module off on the first such refusal and never read a number again.
                if (failure != IfaceDead) return false;
                _steamIface = 0;   // the interface itself went away - re-probe
            }

            string f1, f2;
            if (Call(first, zs, ref status, ref lanes, out f1)) { SetIface(first); return true; }
            if (f1 != IfaceDead) { failure = f1; return false; }
            if (Call(second, zs, ref status, ref lanes, out f2)) { SetIface(second); return true; }

            failure = f2 == IfaceDead
                ? "neither Steam interface is initialised in this process"
                : f2;
            return false;
        }

        private static bool Call(int iface, ZSteamSocket zs,
                                 ref SteamNetConnectionRealTimeStatus_t status,
                                 ref SteamNetConnectionRealTimeLaneStatus_t lanes,
                                 out string failure)
        {
            failure = null;
            try
            {
                EResult r = iface == 2
                    ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(zs.m_con, ref status, 0, ref lanes)
                    : SteamNetworkingSockets.GetConnectionRealTimeStatus(zs.m_con, ref status, 0, ref lanes);
                if (r == EResult.k_EResultOK) return true;
                failure = r.ToString();
                return false;
            }
            catch (Exception)
            {
                // That half of Steamworks is not initialised in this process (NOTES §20).
                failure = IfaceDead;
                return false;
            }
        }

        private static void SetIface(int iface)
        {
            _steamIface = iface;
            if (_ifaceLogged) return;
            _ifaceLogged = true;
            SmoothServerPlugin.Log.LogInfo("[PeerTelemetry] Steam real-time status source: " +
                (iface == 2 ? "SteamGameServerNetworkingSockets (game-server interface)"
                            : "SteamNetworkingSockets (user interface)"));
        }

        // ---- one-time diagnostics --------------------------------------------------------

        private static bool Once(string key)
        {
            return WarnedOnce.Add(key);
        }

        /// <summary>One line the first time a peer's live stats actually read.</summary>
        private static void NoteGoodRead(PeerStat s)
        {
            if (!GoodRead.Add(s.Uid)) return;
            SmoothServerPlugin.Log.LogInfo("[PeerTelemetry] live stats OK for " + s.PlayerName +
                                           ": ping=" + s.Ping + "ms");
        }

        /// <summary>One warning per distinct failure reason; never silent, never spammy.</summary>
        private static void NoteStatusFailure(string failure)
        {
            if (!Once("status:" + failure)) return;
            SmoothServerPlugin.Log.LogWarning("[PeerTelemetry] GetConnectionRealTimeStatus failed (" +
                                              failure + ") - stats unavailable");
        }

        /// <summary>One warning per distinct socket type with no ZSteamSocket behind it.</summary>
        private static void NoteNoSteamSocket(PeerStat s)
        {
            if (!Once("nosock:" + s.SocketKind)) return;
            SmoothServerPlugin.Log.LogWarning("[PeerTelemetry] peer '" + s.PlayerName + "' socket is " +
                s.SocketKind + " with no ZSteamSocket behind it - Steam stats unavailable for this " +
                "peer (a PlayFab/crossplay peer, or a mod's socket decorator that hides what it wraps). " +
                "LagProbe's rtt/jitter/loss still work.");
        }

        /// <summary>One line the first time a decorator had to be unwrapped.</summary>
        private static void NoteWrappedSocket(PeerStat s)
        {
            if (!Once("wrapped:" + s.SocketKind)) return;
            SmoothServerPlugin.Log.LogInfo("[PeerTelemetry] peer socket is wrapped by " + s.SocketKind +
                " (another mod's ServerSync buffering socket, left in place after its handshake) - " +
                "reading the ZSteamSocket behind it");
        }

        // ---- log line --------------------------------------------------------------------

        private static void Emit()
        {
            var zm = ZDOMan.instance;
            int peers = zm == null ? 0 : zm.m_peers.Count;

            if (peers == 0)
            {
                SmoothServerPlugin.Log.LogInfo("[PeerTelemetry] peers=0 - no peers connected, nothing to measure");
                return;
            }

            SmoothServerPlugin.Log.LogInfo(string.Format(
                "[PeerTelemetry] peers={0} zdosSent/s={1} zdosRecv/s={2}",
                peers, zm.m_zdosSentLastSec, zm.m_zdosRecvLastSec));

            foreach (var kv in Stats)
            {
                var s = kv.Value;
                SmoothServerPlugin.Log.LogInfo(string.Format(
                    "[PeerTelemetry]   '{0}' uid={1} ping={2}ms qual={3:F2}/{4:F2} out={5:F1}kB/s in={6:F1}kB/s " +
                    "pending={7}B(r)+{8}B(u) inflight={9}B steamRate={10}B/s socketQueue={11}B zdos={12} force={13} invalid={14}{15}{16}",
                    s.PlayerName, s.Uid, s.Ping, s.QualityLocal, s.QualityRemote,
                    s.OutBytesPerSec / 1024f, s.InBytesPerSec / 1024f,
                    s.PendingReliable, s.PendingUnreliable, s.SentUnackedReliable,
                    s.SendRateBytesPerSec, s.SocketQueueBytes, s.ZdoQueue, s.ForceSend, s.InvalidSector,
                    LagProbeModule.PeerSuffix(s.Uid),
                    s.Valid ? "" : " (steam status unavailable, socket=" + s.SocketKind + ")"));
            }
        }
    }
}
