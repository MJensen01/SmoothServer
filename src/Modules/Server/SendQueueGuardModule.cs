using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// The No_More_Crashes idea, re-implemented from the public Steamworks error codes
    /// (the mod itself is deprecated, licence unverified - nothing was copied).
    ///
    /// Vanilla ZSteamSocket.SendQueuedPackages (0.221.12):
    ///
    ///     while (m_sendQueue.Count > 0) {
    ///         byte[] a = m_sendQueue.Peek();
    ///         IntPtr p = Marshal.AllocHGlobal(a.Length);
    ///         Marshal.Copy(a, 0, p, a.Length);
    ///         EResult r = SteamNetworkingSockets.SendMessageToConnection(m_con, p, (uint)a.Length, 8, out _);
    ///         Marshal.FreeHGlobal(p);
    ///         if (r == k_EResultOK) { m_totalSent += a.Length; m_sendQueue.Dequeue(); continue; }
    ///         ZLog.Log("Failed to send data " + r);
    ///         break;
    ///     }
    ///
    /// Two problems, both made worse by SendBudget raising the ZDO high-water mark:
    ///   1. every failed drain logs a line - at 20Hz x 6 peers a persistently full Steam queue is
    ///      a log flood, which is itself a frame-time cost on a headless server;
    ///   2. any exception out of the interop (the k_EResultLimitExceeded / k_EResultInvalidParam
    ///      class this guard exists for) propagates through ZSteamSocket.Send -> ZRpc.Invoke ->
    ///      ZDOMan.SendZDOs and takes the frame (or the socket) with it.
    ///
    /// This replaces the drain with the same loop wrapped in try/catch, plus:
    ///   * a per-socket backoff: after a non-OK result we stop trying for BackoffMs, so a
    ///     saturated peer costs one syscall per backoff window instead of one per frame;
    ///   * one log line per socket per failure *episode* instead of per attempt;
    ///   * an optional hard cap (MaxQueuedBytes, default 0 = never drop). Dropping a queued
    ///     ZDOData package is NOT free - ZDOMan has already recorded it in peer.m_zdos, so the
    ///     ZDO will not be re-sent until it changes again - which is why the default is to defer,
    ///     not drop, and why the drop path logs loudly.
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

        private sealed class GuardState
        {
            public float BlockedUntil;
            public bool InEpisode;
            public int Failures;
            public int Dropped;
            public float LastReport;
            public EResult LastResult;
        }

        private static readonly Dictionary<ZSteamSocket, GuardState> States =
            new Dictionary<ZSteamSocket, GuardState>();

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("SendQueueGuard", "Enabled", true,
                "Guard ZSteamSocket's send drain so a full/erroring Steam send queue backs off " +
                "instead of throwing, and logs once per episode instead of once per frame.");
            _backoffMs = cfg.Bind("SendQueueGuard", "BackoffMs", 50,
                "After a non-OK SendMessageToConnection result, skip this socket's drain for this " +
                "many milliseconds. Clamped to 0-1000.");
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
                AccessTools.Field(typeof(ZSteamSocket), "m_con") == null ||
                AccessTools.Field(typeof(ZSteamSocket), "m_totalSent") == null)
                throw new Exception("SmoothServer SendQueueGuard: ZSteamSocket fields m_sendQueue/m_con/m_totalSent " +
                                    "not all present - refusing to patch");

            // Priority.Low ON PURPOSE. Our prefix returns false (it does the drain itself), and a
            // prefix returning false skips every prefix that has not run yet. BetterNetworking B6
            // is also a prefix on this method: it rewraps m_sendQueue through zstd and returns
            // true. At Low we run AFTER it, so BN still compresses and we still own the drain; at
            // High we would silently disable BN compression for anyone running both mods.
            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SendQueueGuardModule), nameof(Prefix)) { priority = Priority.Low });

            States.Clear();
            Active = true;
            Log.LogInfo("[SendQueueGuard] guarding ZSteamSocket.SendQueuedPackages: backoff=" +
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

        private static bool Prefix(ZSteamSocket __instance)
        {
            if (!Active) return true;

            try
            {
                Drain(__instance);
            }
            catch (Exception e)
            {
                // Never let an interop failure escape into ZRpc/ZDOMan.
                var st = StateFor(__instance);
                if (!st.InEpisode || Time.realtimeSinceStartup - st.LastReport > ReportIntervalSec)
                {
                    st.InEpisode = true;
                    st.LastReport = Time.realtimeSinceStartup;
                    SmoothServerPlugin.Log.LogWarning("[SendQueueGuard] send threw for " +
                        Endpoint(__instance) + ", backing off " + (BackoffSec * 1000f).ToString("F0") +
                        "ms: " + e.Message);
                }
                st.BlockedUntil = Time.realtimeSinceStartup + BackoffSec;
            }

            return false;   // we did the drain
        }

        private static void Drain(ZSteamSocket s)
        {
            if (!s.IsConnected()) return;

            var st = StateFor(s);
            float now = Time.realtimeSinceStartup;
            if (now < st.BlockedUntil) return;

            var queue = s.m_sendQueue;

            if (MaxQueuedBytes > 0)
            {
                int bytes = 0;
                foreach (var b in queue) bytes += b.Length;
                while (bytes > MaxQueuedBytes && queue.Count > 1)
                {
                    var dropped = queue.Dequeue();
                    bytes -= dropped.Length;
                    st.Dropped++;
                }
                if (st.Dropped > 0 && now - st.LastReport > ReportIntervalSec)
                {
                    st.LastReport = now;
                    SmoothServerPlugin.Log.LogWarning("[SendQueueGuard] DROPPED " + st.Dropped +
                        " queued packages for " + Endpoint(s) + " (queue above MaxQueuedBytes=" +
                        MaxQueuedBytes + "B) - those ZDO updates are lost until they change again");
                }
            }

            while (queue.Count > 0)
            {
                byte[] array = queue.Peek();
                IntPtr ptr = Marshal.AllocHGlobal(array.Length);
                EResult result;
                try
                {
                    Marshal.Copy(array, 0, ptr, array.Length);
                    long msgNum;
                    result = SteamNetworkingSockets.SendMessageToConnection(s.m_con, ptr, (uint)array.Length, 8, out msgNum);
                }
                finally { Marshal.FreeHGlobal(ptr); }

                if (result == EResult.k_EResultOK)
                {
                    s.m_totalSent += array.Length;
                    queue.Dequeue();
                    if (st.InEpisode)
                    {
                        st.InEpisode = false;
                        SmoothServerPlugin.Log.LogInfo("[SendQueueGuard] " + Endpoint(s) +
                            " recovered after " + st.Failures + " blocked attempt(s) (last result " +
                            st.LastResult + ")");
                        st.Failures = 0;
                    }
                    continue;
                }

                st.Failures++;
                st.LastResult = result;
                st.BlockedUntil = now + BackoffSec;
                if (!st.InEpisode || now - st.LastReport > ReportIntervalSec)
                {
                    st.InEpisode = true;
                    st.LastReport = now;
                    SmoothServerPlugin.Log.LogWarning("[SendQueueGuard] " + Endpoint(s) +
                        " send blocked (" + result + "), " + queue.Count + " package(s) queued - " +
                        "deferring for " + (BackoffSec * 1000f).ToString("F0") + "ms" +
                        (result == EResult.k_EResultLimitExceeded
                            ? " [k_EResultLimitExceeded: lower SendBudget.HighWaterBytes or AdaptiveBudget.CeilingBytes]"
                            : ""));
                }
                return;
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
