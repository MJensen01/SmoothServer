using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// The No_More_Crashes idea, re-implemented from the public Steamworks error codes
    /// (the mod itself is deprecated, licence unverified - nothing was copied).
    ///
    /// Vanilla ZSteamSocket.SendQueuedPackages (0.221.12) drains m_sendQueue through
    /// SendMessageToConnection, logs "Failed to send data &lt;EResult&gt;" and breaks out of the
    /// loop on the first non-OK result. Two problems, both made worse by SendBudget raising the
    /// ZDO high-water mark:
    ///   1. every failed drain logs a line - at 20Hz x 6 peers a persistently full Steam queue is
    ///      a log flood, which is itself a frame-time cost on a headless server;
    ///   2. any exception out of the interop (the k_EResultLimitExceeded / k_EResultInvalidParam
    ///      class this guard exists for) propagates through ZSteamSocket.Send -> ZRpc.Invoke ->
    ///      ZDOMan.SendZDOs and takes the frame (or the socket) with it.
    ///
    /// <b>0.3.1: this module no longer re-implements the drain.</b> It used to copy vanilla's loop
    /// and call <c>SteamNetworkingSockets.SendMessageToConnection</c> - which is correct only on the
    /// CLIENT build. The dedicated-server <c>assembly_valheim.dll</c> is compiled against
    /// <c>SteamGameServerNetworkingSockets</c> throughout ZSteamSocket (verified by decompiling the
    /// server assembly; see NOTES §20), so the copied loop threw
    /// "Steamworks is not initialized." on every send and no client could complete a handshake.
    ///
    /// The guard is now a prefix/finalizer pair around the *vanilla* drain, so vanilla picks the
    /// interface its own build was compiled for and we never name one:
    ///   * <b>prefix</b> - while a socket is inside its back-off window, skip vanilla's drain
    ///     entirely (return false). That is what kills the per-frame log flood: vanilla cannot log
    ///     "Failed to send data" if it does not run. Also enforces the optional MaxQueuedBytes cap.
    ///   * <b>finalizer</b> - swallows any exception out of the interop (it never reaches ZRpc /
    ///     ZDOMan) and applies the back-off. With no exception, progress is measured by
    ///     <c>m_totalSent</c>: unchanged across a call that started with a non-empty queue means
    ///     Steam refused every package, so we back off the same way.
    /// One log line per socket per failure *episode* instead of one per attempt, either way.
    ///
    /// MaxQueuedBytes (default 0 = never drop) is a hard cap. Dropping a queued ZDOData package is
    /// NOT free - ZDOMan has already recorded it in peer.m_zdos, so the ZDO will not be re-sent
    /// until it changes again - which is why the default is to defer, not drop, and why the drop
    /// path logs loudly.
    /// </summary>
    internal sealed class SendQueueGuardModule : FeatureModule
    {
        public override string Name => "SendQueueGuard";

        private ConfigEntry<int> _backoffMs;
        private ConfigEntry<int> _maxQueuedBytes;
        private ConfigEntry<float> _reportIntervalSec;

        internal static bool Active;
        internal static float BackoffSec = 0.05f;
        internal static int MaxQueuedBytes;            // 0 = never drop
        internal static float ReportIntervalSec = 30f;

        /// <summary>Sentinel for __state: "nothing to evaluate in the finalizer".</summary>
        private const int NoCheck = int.MinValue;

        private sealed class GuardState
        {
            public float BlockedUntil;
            public bool InEpisode;
            public int Failures;
            public int Deferrals;
            public int Dropped;
            public float LastReport;
            public string LastReason;
        }

        private static readonly Dictionary<ZSteamSocket, GuardState> States =
            new Dictionary<ZSteamSocket, GuardState>();

        /// <summary>One reported drop episode, for StatsLog's events-*.jsonl.</summary>
        internal struct DropEvent { public string Endpoint; public int Count; }
        private static readonly List<DropEvent> _statDrops = new List<DropEvent>();

        /// <summary>StatsLog: drop episodes since the last call, then reset.</summary>
        internal static List<DropEvent> ConsumeDrops()
        {
            var copy = new List<DropEvent>(_statDrops);
            _statDrops.Clear();
            return copy;
        }

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("SendQueueGuard", "Enabled", true,
                "Guard ZSteamSocket's send drain so a full/erroring Steam send queue backs off " +
                "instead of throwing, and logs once per episode instead of once per frame.");
            _backoffMs = cfg.Bind("SendQueueGuard", "BackoffMs", 50,
                "After a failed drain, skip this socket's send for this many milliseconds. " +
                "Clamped to 0-1000.");
            _maxQueuedBytes = cfg.Bind("SendQueueGuard", "MaxQueuedBytes", 0,
                "0 = never drop (defer only). Above 0: if the socket's own byte queue grows past " +
                "this, drop oldest packages until it fits. DROPPING LOSES ZDO UPDATES - ZDOMan has " +
                "already marked them as sent - so only raise this if a peer is provably wedged.");
            _reportIntervalSec = cfg.Bind("SendQueueGuard", "ReportIntervalSec", 30f,
                "Minimum seconds between repeat warnings for the same socket while it stays blocked.");
            Watch(_backoffMs); Watch(_maxQueuedBytes); Watch(_reportIntervalSec);
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var target = AccessTools.Method(typeof(ZSteamSocket), "SendQueuedPackages");
            if (target == null)
                throw new Exception("SmoothServer SendQueueGuard: ZSteamSocket.SendQueuedPackages not found");
            if (target.GetParameters().Length != 0)
                throw new Exception("SmoothServer SendQueueGuard: ZSteamSocket.SendQueuedPackages signature changed " +
                                    "(expected no parameters) - refusing to patch");
            if (AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue") == null ||
                AccessTools.Field(typeof(ZSteamSocket), "m_totalSent") == null)
                throw new Exception("SmoothServer SendQueueGuard: ZSteamSocket fields m_sendQueue/m_totalSent " +
                                    "not both present - refusing to patch (progress is measured through them)");

            // Priority.Low ON PURPOSE. Our prefix can return false (during a back-off window), and
            // a prefix returning false skips every prefix that has not run yet. Compression (and
            // BetterNetworking B6, if someone runs it) is also a prefix here: it rewraps
            // m_sendQueue and returns true. At Low we run AFTER it, so the queue we measure and
            // cap is the one vanilla is about to put on the wire; at High we would silently
            // disable their rewrap on every deferred frame.
            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SendQueueGuardModule), nameof(Prefix)) { priority = Priority.Low },
                finalizer: new HarmonyMethod(typeof(SendQueueGuardModule), nameof(Finalizer)));

            States.Clear();
            Active = true;
            Log.LogInfo("[SendQueueGuard] wrapping ZSteamSocket.SendQueuedPackages (vanilla still does the " +
                        "send, so the build's own Steam interface is used): backoff=" +
                        (BackoffSec * 1000f).ToString("F0") + "ms maxQueuedBytes=" +
                        (MaxQueuedBytes == 0 ? "unlimited (defer, never drop)" : MaxQueuedBytes + "B (drop above)"));
        }

        public override void Disable()
        {
            Active = false;
            States.Clear();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[SendQueueGuard] backoff=" + (BackoffSec * 1000f).ToString("F0") +
                        "ms maxQueuedBytes=" + MaxQueuedBytes);
        }

        private void ReadConfig()
        {
            BackoffSec = Mathf.Clamp(_backoffMs.Value, 0, 1000) / 1000f;
            MaxQueuedBytes = Math.Max(0, _maxQueuedBytes.Value);
            ReportIntervalSec = Mathf.Max(1f, _reportIntervalSec.Value);
        }

        /// <summary>
        /// Returns false to skip vanilla's drain while the socket is backing off. __state carries
        /// m_totalSent into the finalizer, or NoCheck when there is nothing to judge.
        /// </summary>
        private static bool Prefix(ZSteamSocket __instance, out int __state)
        {
            __state = NoCheck;
            if (!Active) return true;

            try
            {
                var queue = __instance.m_sendQueue;
                if (queue == null || queue.Count == 0) return true;
                if (!__instance.IsConnected()) return true;

                var st = StateFor(__instance);
                float now = Time.realtimeSinceStartup;

                if (now < st.BlockedUntil)
                {
                    st.Deferrals++;
                    return false;           // still inside the back-off window
                }

                if (MaxQueuedBytes > 0) Trim(st, queue, __instance, now);

                if (queue.Count > 0) __state = __instance.m_totalSent;
            }
            catch (Exception e)
            {
                // Bookkeeping must never break the send path.
                SmoothServerPlugin.Log.LogWarning("[SendQueueGuard] guard bookkeeping failed: " + e.Message);
                __state = NoCheck;
            }

            return true;
        }

        /// <summary>
        /// Swallows anything the vanilla drain throws (it must never reach ZRpc/ZDOMan) and turns
        /// "no progress" into the same back-off, without ever naming a Steam interface.
        /// </summary>
        private static Exception Finalizer(ZSteamSocket __instance, Exception __exception, int __state)
        {
            if (!Active) return __exception;

            try
            {
                var st = StateFor(__instance);
                float now = Time.realtimeSinceStartup;

                if (__exception != null)
                {
                    Fail(st, __instance, now, "send threw: " + __exception.Message);
                    return null;            // never propagate
                }

                if (__state == NoCheck) return null;

                if (__instance.m_totalSent == __state)
                {
                    // Vanilla ran with a non-empty queue and sent nothing: Steam refused the first
                    // package (vanilla already logged the EResult once) - defer instead of retrying
                    // every frame.
                    int queued = __instance.m_sendQueue != null ? __instance.m_sendQueue.Count : -1;
                    Fail(st, __instance, now, "send blocked, " + queued + " package(s) queued " +
                         "(if the log above says k_EResultLimitExceeded, lower " +
                         "SendBudget.HighWaterBytes or AdaptiveBudget.CeilingBytes)");
                    return null;
                }

                if (st.InEpisode)
                {
                    st.InEpisode = false;
                    SmoothServerPlugin.Log.LogInfo("[SendQueueGuard] " + Endpoint(__instance) +
                        " recovered after " + st.Failures + " failed drain(s) / " + st.Deferrals +
                        " deferred frame(s) (last reason: " + st.LastReason + ")");
                    st.Failures = 0;
                    st.Deferrals = 0;
                }
            }
            catch { /* a guard must not be able to fail the send */ }

            return null;
        }

        private static void Fail(GuardState st, ZSteamSocket s, float now, string reason)
        {
            st.Failures++;
            st.LastReason = reason;
            st.BlockedUntil = now + BackoffSec;
            if (!st.InEpisode || now - st.LastReport > ReportIntervalSec)
            {
                st.InEpisode = true;
                st.LastReport = now;
                SmoothServerPlugin.Log.LogWarning("[SendQueueGuard] " + Endpoint(s) + " " + reason +
                    " - deferring for " + (BackoffSec * 1000f).ToString("F0") + "ms");
            }
        }

        private static void Trim(GuardState st, Queue<byte[]> queue, ZSteamSocket s, float now)
        {
            int bytes = 0;
            foreach (var b in queue) if (b != null) bytes += b.Length;
            while (bytes > MaxQueuedBytes && queue.Count > 1)
            {
                var dropped = queue.Dequeue();
                if (dropped != null) bytes -= dropped.Length;
                st.Dropped++;
            }
            if (st.Dropped > 0 && now - st.LastReport > ReportIntervalSec)
            {
                st.LastReport = now;
                _statDrops.Add(new DropEvent { Endpoint = Endpoint(s), Count = st.Dropped });
                SmoothServerPlugin.Log.LogWarning("[SendQueueGuard] DROPPED " + st.Dropped +
                    " queued packages for " + Endpoint(s) + " (queue above MaxQueuedBytes=" +
                    MaxQueuedBytes + "B) - those ZDO updates are lost until they change again");
            }
        }

        private static GuardState StateFor(ZSteamSocket s)
        {
            GuardState st;
            if (!States.TryGetValue(s, out st))
            {
                st = new GuardState();
                States[s] = st;
                if (States.Count > 64) Prune();
            }
            return st;
        }

        private static void Prune()
        {
            var dead = new List<ZSteamSocket>();
            foreach (var kv in States)
                if (kv.Key == null || !kv.Key.IsConnected()) dead.Add(kv.Key);
            foreach (var d in dead) States.Remove(d);
        }

        private static string Endpoint(ZSteamSocket s)
        {
            try { return s.GetEndPointString(); }
            catch { return "<socket>"; }
        }
    }
}
