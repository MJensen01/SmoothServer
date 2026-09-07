using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// M1 - per-peer instrumentation. No Harmony patches: it polls the live sockets.
    ///
    /// Every SampleIntervalSec it walks ZDOMan.m_peers and records, per peer:
    ///   * ZSteamSocket.GetConnectionQuality  -> ping, local/remote quality, out/in B/s
    ///   * SteamNetworkingSockets.GetConnectionRealTimeStatus(m_con) -> m_cbPendingReliable,
    ///     m_cbPendingUnreliable (queued but not yet on the wire), m_cbSentUnackedReliable
    ///     (in flight), m_nSendRateBytesPerSecond (Steam's own bandwidth estimate)
    ///   * ISocket.GetSendQueueSize() - exactly the number ZDOMan.SendZDOs budgets against
    ///   * the ZDOMan-side per-peer state: peer.m_zdos.Count (ZDOs this peer is known to have),
    ///     peer.m_forceSend.Count, peer.m_invalidSector.Count
    /// plus the global ZDOMan.m_zdosSentLastSec / m_zdosRecvLastSec.
    ///
    /// ZSteamSocket in 0.221.12 talks to the *user* interface (SteamNetworkingSockets) even on a
    /// dedicated server - there is no SteamGameServerNetworkingSockets call anywhere in the type.
    /// We mirror that, and fall back to the game-server interface if the user one refuses, then
    /// log once which one actually answered (open question 1 of SMOOTHSERVER-PHASE2 §7).
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
            public float SampledAt;
        }

        private ConfigEntry<float> _interval;
        private ConfigEntry<float> _sampleInterval;

        internal static bool Active;
        internal static float IntervalSec = 10f;
        internal static float SampleIntervalSec = 1f;

        private static readonly Dictionary<long, PeerStat> Stats = new Dictionary<long, PeerStat>();
        private static readonly List<long> TempIds = new List<long>();
        private static float _sampleAcc;
        private static float _logAcc;
        private static int _steamIface;     // 0 = unknown, 1 = user, 2 = gameserver, -1 = none
        private static bool _ifaceLogged;

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
            _sampleAcc = 0f; _logAcc = 0f;
            Active = true;
            Log.LogInfo("[PeerTelemetry] sampling every " + SampleIntervalSec.ToString("F1") +
                        "s, logging every " + IntervalSec.ToString("F1") + "s (no patches)");
        }

        public override void Disable()
        {
            Active = false;
            Stats.Clear();
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
                if (sock != null)
                {
                    try { stat.SocketQueueBytes = sock.GetSendQueueSize(); }
                    catch { stat.SocketQueueBytes = -1; }
                }

                var zs = sock as ZSteamSocket;
                if (zs != null)
                {
                    try
                    {
                        float ql, qr, ob, ib; int ping;
                        zs.GetConnectionQuality(out ql, out qr, out ping, out ob, out ib);
                        stat.QualityLocal = ql; stat.QualityRemote = qr; stat.Ping = ping;
                        stat.OutBytesPerSec = ob; stat.InBytesPerSec = ib;
                    }
                    catch { /* socket closing */ }

                    SteamNetConnectionRealTimeStatus_t st;
                    if (TryRealTimeStatus(zs, out st))
                    {
                        stat.Valid = true;
                        stat.PendingReliable = st.m_cbPendingReliable;
                        stat.PendingUnreliable = st.m_cbPendingUnreliable;
                        stat.SentUnackedReliable = st.m_cbSentUnackedReliable;
                        stat.SendRateBytesPerSec = st.m_nSendRateBytesPerSecond;
                        if (stat.Ping == 0) stat.Ping = st.m_nPing;
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
                foreach (var id in stale) Stats.Remove(id);
            }
        }

        private static bool TryRealTimeStatus(ZSteamSocket zs, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = default(SteamNetConnectionRealTimeStatus_t);
            SteamNetConnectionRealTimeLaneStatus_t lanes = default(SteamNetConnectionRealTimeLaneStatus_t);

            if (_steamIface == -1) return false;

            if (_steamIface == 0 || _steamIface == 1)
            {
                try
                {
                    if (SteamNetworkingSockets.GetConnectionRealTimeStatus(zs.m_con, ref status, 0, ref lanes) == EResult.k_EResultOK)
                    {
                        SetIface(1, "SteamNetworkingSockets (user interface)");
                        return true;
                    }
                }
                catch { /* not initialised on this build */ }
            }

            if (_steamIface == 0 || _steamIface == 2)
            {
                try
                {
                    if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(zs.m_con, ref status, 0, ref lanes) == EResult.k_EResultOK)
                    {
                        SetIface(2, "SteamGameServerNetworkingSockets (game-server interface)");
                        return true;
                    }
                }
                catch { /* not initialised on this build */ }
            }

            if (_steamIface == 0) SetIface(-1, "neither interface answered GetConnectionRealTimeStatus");
            return false;
        }

        private static void SetIface(int iface, string what)
        {
            _steamIface = iface;
            if (_ifaceLogged) return;
            _ifaceLogged = true;
            SmoothServerPlugin.Log.LogInfo("[PeerTelemetry] Steam real-time status source: " + what);
        }

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
                    "pending={7}B(r)+{8}B(u) inflight={9}B steamRate={10}B/s socketQueue={11}B zdos={12} force={13} invalid={14}{15}",
                    s.PlayerName, s.Uid, s.Ping, s.QualityLocal, s.QualityRemote,
                    s.OutBytesPerSec / 1024f, s.InBytesPerSec / 1024f,
                    s.PendingReliable, s.PendingUnreliable, s.SentUnackedReliable,
                    s.SendRateBytesPerSec, s.SocketQueueBytes, s.ZdoQueue, s.ForceSend, s.InvalidSector,
                    s.Valid ? "" : " (steam status unavailable)"));
            }
        }
    }
}
