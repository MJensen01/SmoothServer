using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// M2 - per-peer adaptive send budget.
    ///
    /// SendBudget replaces vanilla's two 10240 literals in ZDOMan.SendZDOs with a call to
    /// SendBudgetModule.GetHighWaterBytes(), which reads the static field
    /// SendBudgetModule.HighWaterBytes. That is ONE number for every peer, which is exactly
    /// BetterNetworking's weakness: the vanilla budget is a bandwidth-delay product cap
    /// (GetSendQueueSize includes m_cbSentUnackedReliable), so the right value is per-peer and
    /// depends on that peer's RTT and achieved throughput.
    ///
    /// This module computes, per peer and once per second from PeerTelemetry:
    ///
    ///     bdp    = OutBytesPerSec * clamp(ping, PingFloorMs, PingCeilMs)/1000
    ///     target = clamp(K * bdp, FloorBytes, CeilingBytes)
    ///
    /// and backs off (halves toward the floor) while the peer's *pending* bytes - the bytes
    /// Steam has accepted but not yet put on the wire - exceed PendingBackoffBytes. Pending
    /// growth is real congestion; in-flight (unacked) growth is just the pipe being full, which
    /// is what we are trying to achieve, so we deliberately do NOT back off on that.
    ///
    /// HOW IT IS APPLIED (since 0.3.0): SendBudgetModule.GetHighWaterBytes() - the target of
    /// SendBudget's transpiler - ends with `return AdaptiveBudgetModule.HighWaterFor(HighWaterBytes)`,
    /// so the per-peer number is read fresh on every call with no mutation of anybody's state.
    ///
    /// The legacy mechanism is still here but OFF by default ([AdaptiveBudget] UseCallSiteSwap,
    /// default false): a prefix on ZDOMan.SendZDOs wrote the peer's target into
    /// SendBudgetModule.HighWaterBytes and a Harmony finalizer put the configured value back.
    /// It is kept only for the case where a future SendBudget rewrite drops the hook call;
    /// turning it on while the hook is in place is harmless but pointless (the swap writes the
    /// same number HighWaterFor would return). See NOTES.md §18-Integration.
    /// </summary>
    internal sealed class AdaptiveBudgetModule : FeatureModule
    {
        public override string Name => "AdaptiveBudget";

        private ConfigEntry<int> _floor;
        private ConfigEntry<int> _ceiling;
        private ConfigEntry<float> _k;
        private ConfigEntry<int> _pendingBackoff;
        private ConfigEntry<float> _smoothing;
        private ConfigEntry<float> _logInterval;
        private ConfigEntry<bool> _callSiteSwap;

        internal static bool Active;
        internal static int FloorBytes = 16384;
        internal static int CeilingBytes = 131072;
        internal static float K = 2.0f;
        internal static int PendingBackoffBytes = 8192;
        internal static float Smoothing = 0.3f;
        internal static bool UseCallSiteSwap = false;

        private const int PingFloorMs = 5;
        private const int PingCeilMs = 250;

        private sealed class PeerBudget
        {
            public int Target;
            public bool Congested;
            public int Samples;
        }

        private static readonly Dictionary<long, PeerBudget> Budgets = new Dictionary<long, PeerBudget>();
        private static float _logAcc;
        private static float LogIntervalSec = 10f;

        // set by the SendZDOs prefix, restored by the finalizer
        private static int _savedHighWater;
        private static bool _swapped;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("AdaptiveBudget", "Enabled", true,
                "Replace SendBudget's single HighWaterBytes number with a per-peer target derived " +
                "from that peer's live RTT and throughput, backing off on real congestion.");
            _floor = cfg.Bind("AdaptiveBudget", "FloorBytes", 16384,
                "Never budget a peer below this many bytes. Vanilla is 10240 for everyone." + Profiles.Note);
            _ceiling = cfg.Bind("AdaptiveBudget", "CeilingBytes", 131072,
                "Never budget a peer above this many bytes. Steam's own send queue starts erroring " +
                "well above this; 128KB is a deliberate safety margin." + Profiles.Note);
            _k = cfg.Bind("AdaptiveBudget", "K", 2.0f,
                "Multiplier on the bandwidth-delay product (outBytesPerSec * RTT). 1.0 exactly fills " +
                "the pipe; 2.0 leaves headroom for bursts. Clamped to 0.5-8.");
            _pendingBackoff = cfg.Bind("AdaptiveBudget", "PendingBackoffBytes", 8192,
                "Back off while a peer's Steam-side PENDING bytes (queued, not yet on the wire) " +
                "exceed this. In-flight/unacked bytes deliberately do NOT trigger a back-off.");
            _smoothing = cfg.Bind("AdaptiveBudget", "Smoothing", 0.3f,
                "EMA factor for the per-peer target. 1.0 = react instantly, 0.1 = very smooth.");
            _logInterval = cfg.Bind("AdaptiveBudget", "LogIntervalSec", 10f,
                "Seconds between per-peer budget lines. 0 disables the log line.");
            _callSiteSwap = cfg.Bind("AdaptiveBudget", "UseCallSiteSwap", false,
                "Legacy application path, OFF by default. Since 0.3.0 SendBudget's " +
                "GetHighWaterBytes() calls AdaptiveBudgetModule.HighWaterFor() directly, so the " +
                "per-peer budget already applies. true additionally swaps " +
                "SendBudgetModule.HighWaterBytes around each ZDOMan.SendZDOs call - only useful " +
                "if that hook call is ever removed.");
            Watch(_floor); Watch(_ceiling); Watch(_k); Watch(_pendingBackoff);
            Watch(_smoothing); Watch(_logInterval); Watch(_callSiteSwap);
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var target = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
            if (target == null)
                throw new Exception("SmoothServer AdaptiveBudget: ZDOMan.SendZDOs not found");

            var pars = target.GetParameters();
            if (pars.Length != 2 || pars[0].Name != "peer" || pars[1].ParameterType != typeof(bool))
                throw new Exception("SmoothServer AdaptiveBudget: ZDOMan.SendZDOs signature changed (expected (ZDOPeer peer, bool flush), got " +
                                    pars.Length + " params) - refusing to patch");

            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(AdaptiveBudgetModule), nameof(Prefix)) { priority = Priority.Low },
                finalizer: new HarmonyMethod(typeof(AdaptiveBudgetModule), nameof(Finalizer)));

            Budgets.Clear();
            Active = true;
            Log.LogInfo("[AdaptiveBudget] floor=" + FloorBytes + "B ceiling=" + CeilingBytes +
                        "B K=" + K.ToString("F1") + " pendingBackoff=" + PendingBackoffBytes +
                        "B smoothing=" + Smoothing.ToString("F2") +
                        " apply=" + (UseCallSiteSwap ? "call-site swap of SendBudget.HighWaterBytes" : "HighWaterFor() hook"));
        }

        public override void Disable()
        {
            Active = false;
            Budgets.Clear();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[AdaptiveBudget] floor=" + FloorBytes + "B ceiling=" + CeilingBytes +
                        "B K=" + K.ToString("F1") + " pendingBackoff=" + PendingBackoffBytes + "B");
        }

        private void ReadConfig()
        {
            FloorBytes = Math.Max(2048, _floor.Value);
            CeilingBytes = Math.Max(FloorBytes, _ceiling.Value);
            K = Mathf.Clamp(_k.Value, 0.5f, 8f);
            PendingBackoffBytes = Math.Max(512, _pendingBackoff.Value);
            Smoothing = Mathf.Clamp(_smoothing.Value, 0.05f, 1f);
            LogIntervalSec = _logInterval == null ? 10f : Mathf.Max(0f, _logInterval.Value);
            UseCallSiteSwap = _callSiteSwap.Value;
        }

        // ---- the hook SendBudget can be pointed at (one line) ---------------------------

        /// <summary>
        /// Per-peer override of SendBudget's high-water mark. <paramref name="configured"/> is
        /// SendBudgetModule.HighWaterBytes; returned unchanged when this module is off or the
        /// peer has no telemetry yet.
        /// </summary>
        internal static int HighWaterFor(int configured)
        {
            if (!Active || !ServerActive()) return configured;
            if (_currentPeerUid == 0L) return configured;
            PeerBudget b;
            if (!Budgets.TryGetValue(_currentPeerUid, out b) || b.Samples == 0) return configured;
            return b.Target;
        }

        private static long _currentPeerUid;

        /// <summary>
        /// Read-only snapshot for StatsLog: this peer's current smoothed target and whether it
        /// is presently backing off. False when the module is off or the peer has no samples yet.
        /// </summary>
        internal static bool TryGetBudget(long uid, out int target, out bool congested)
        {
            PeerBudget b;
            if (Active && Budgets.TryGetValue(uid, out b) && b.Samples > 0)
            {
                target = b.Target; congested = b.Congested; return true;
            }
            target = 0; congested = false; return false;
        }

        // ---- per-call application -------------------------------------------------------

        private static void Prefix(ZDOMan.ZDOPeer peer)
        {
            _currentPeerUid = 0L;
            _swapped = false;
            if (!Active || !ServerActive()) return;
            if (peer == null || peer.m_peer == null) return;

            _currentPeerUid = peer.m_peer.m_uid;
            if (!UseCallSiteSwap) return;

            PeerBudget b;
            if (!Budgets.TryGetValue(_currentPeerUid, out b) || b.Samples == 0) return;

            _savedHighWater = SendBudgetModule.HighWaterBytes;
            SendBudgetModule.HighWaterBytes = b.Target;
            _swapped = true;
        }

        private static void Finalizer()
        {
            if (_swapped)
            {
                SendBudgetModule.HighWaterBytes = _savedHighWater;
                _swapped = false;
            }
            _currentPeerUid = 0L;
        }

        // ---- the control loop -----------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Active || !ServerActive()) return;

            var stats = PeerTelemetryModule.Snapshot();
            if (stats.Length == 0)
            {
                if (Budgets.Count > 0) Budgets.Clear();
                return;
            }

            int configured = SendBudgetModule.HighWaterBytes;

            for (int i = 0; i < stats.Length; i++)
            {
                var s = stats[i];
                PeerBudget b;
                if (!Budgets.TryGetValue(s.Uid, out b))
                {
                    b = new PeerBudget { Target = configured };
                    Budgets[s.Uid] = b;
                }

                if (!s.Valid)
                {
                    // no Steam status for this socket (PlayFab peer, or Steam refused):
                    // fall back to SendBudget's configured number, i.e. current behaviour.
                    b.Target = configured;
                    b.Samples = 0;
                    b.Congested = false;
                    continue;
                }

                float rtt = Mathf.Clamp(s.Ping, PingFloorMs, PingCeilMs) / 1000f;
                float bdp = s.OutBytesPerSec * rtt;
                float want = K * bdp;

                int pending = s.PendingReliable + s.PendingUnreliable;
                b.Congested = pending > PendingBackoffBytes;
                if (b.Congested) want = Mathf.Min(want, b.Target * 0.5f);

                want = Mathf.Clamp(want, FloorBytes, CeilingBytes);
                b.Target = Mathf.RoundToInt(Mathf.Lerp(b.Target, want, Smoothing));
                b.Target = Mathf.Clamp(b.Target, FloorBytes, CeilingBytes);
                b.Samples++;
            }

            // prune departed peers
            if (Budgets.Count > stats.Length)
            {
                var live = new HashSet<long>();
                for (int i = 0; i < stats.Length; i++) live.Add(stats[i].Uid);
                var stale = new List<long>();
                foreach (var kv in Budgets) if (!live.Contains(kv.Key)) stale.Add(kv.Key);
                foreach (var id in stale) Budgets.Remove(id);
            }

            if (LogIntervalSec <= 0f) return;
            _logAcc += dt;
            if (_logAcc < LogIntervalSec) return;
            _logAcc = 0f;

            foreach (var kv in Budgets)
            {
                var b = kv.Value;
                SmoothServerPlugin.Log.LogInfo(string.Format(
                    "[AdaptiveBudget] uid={0} target={1}B ({2}) samples={3} base={4}B",
                    kv.Key, b.Target, b.Congested ? "backing off" : "steady", b.Samples, configured));
            }
        }
    }
}
