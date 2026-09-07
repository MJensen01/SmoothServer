using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// The ResourceUnloadOptimizer idea, re-implemented (that mod is deprecated and its repo is
    /// gone - only the technique was reused).
    ///
    /// Where the game calls it in 0.221.12 (Game.cs):
    ///   * Game.Start:  InvokeRepeating("CollectResourcesCheckPeriodic", 3600f, 3600f)
    ///                  -> CollectResources() if &gt;3599s since the last one   [the hourly ~200ms
    ///                     GC spike PERF-REPORT saw]
    ///   * Game.CollectResourcesCheck() -> CollectResources() if &gt;1200s since the last one;
    ///     called from the spawn path and from the idle/paused branch of Game.Update
    ///   * ZDOMan.ShutDown() -> Game.instance.CollectResources()
    /// All of them funnel through Game.CollectResources(bool), which is just
    /// Resources.UnloadUnusedAssets() plus a timestamp - a synchronous, main-thread, whole-heap
    /// sweep. On a dedicated server nothing is being unloaded that a player will notice.
    ///
    /// We prefix Game.CollectResources and skip it unless MinIntervalSec has elapsed since the
    /// last sweep we actually let through, and (when OffPeak is on) unless the server is idle -
    /// 0 peers connected and no save in flight. Skipping is safe: the call is a hint to Unity,
    /// not a correctness requirement, and vanilla itself skips it most of the time.
    /// </summary>
    internal sealed class GcThrottleModule : FeatureModule
    {
        public override string Name => "GcThrottle";

        private ConfigEntry<float> _minInterval;
        private ConfigEntry<bool> _offPeak;
        private ConfigEntry<bool> _logSkips;

        internal static bool Active;
        internal static float MinIntervalSec = 3600f;
        internal static bool OffPeak = true;
        internal static bool LogSkips = true;

        private static float _lastRun = -1f;
        private static int _skipped;
        private static bool _deferred;      // a sweep was refused because players were online

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("GcThrottle", "Enabled", true,
                "Throttle Game.CollectResources (Resources.UnloadUnusedAssets) - vanilla's hourly " +
                "whole-heap sweep, measured at ~200ms of main-thread stall.");
            _minInterval = cfg.Bind("GcThrottle", "MinIntervalSec", 3600f,
                "Minimum seconds between sweeps we allow through. Vanilla's own periodic check is " +
                "3600s but CollectResourcesCheck can fire one after only 1200s.");
            _offPeak = cfg.Bind("GcThrottle", "OffPeak", true,
                "Only allow a sweep when the server is idle: 0 peers connected and no world save " +
                "in flight. A sweep refused this way is retried as soon as the server goes idle.");
            _logSkips = cfg.Bind("GcThrottle", "LogSkips", true,
                "Log when a sweep is allowed through or deferred.");
            Watch(_minInterval); Watch(_offPeak); Watch(_logSkips);
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var target = AccessTools.Method(typeof(Game), "CollectResources", new[] { typeof(bool) });
            if (target == null)
                throw new Exception("SmoothServer GcThrottle: Game.CollectResources(bool) not found");

            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(GcThrottleModule), nameof(Prefix)) { priority = Priority.High });

            _lastRun = -1f;
            _skipped = 0;
            _deferred = false;
            Active = true;
            Log.LogInfo("[GcThrottle] Resources.UnloadUnusedAssets throttled to at most once per " +
                        MinIntervalSec.ToString("F0") + "s" + (OffPeak ? ", idle-server only" : ""));
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[GcThrottle] minInterval=" + MinIntervalSec.ToString("F0") + "s offPeak=" + OffPeak);
        }

        private void ReadConfig()
        {
            MinIntervalSec = Mathf.Max(0f, _minInterval.Value);
            OffPeak = _offPeak.Value;
            LogSkips = _logSkips.Value;
        }

        private static bool Prefix()
        {
            if (!Active || !ServerActive()) return true;

            float now = Time.realtimeSinceStartup;

            if (_lastRun >= 0f && now - _lastRun < MinIntervalSec)
            {
                _skipped++;
                if (LogSkips)
                    SmoothServerPlugin.Log.LogInfo("[GcThrottle] skipped UnloadUnusedAssets (" +
                        (now - _lastRun).ToString("F0") + "s since the last one, minimum " +
                        MinIntervalSec.ToString("F0") + "s; " + _skipped + " skipped so far)");
                return false;
            }

            if (OffPeak && !IsIdle())
            {
                _skipped++;
                if (!_deferred)
                {
                    _deferred = true;
                    if (LogSkips)
                        SmoothServerPlugin.Log.LogInfo("[GcThrottle] deferred UnloadUnusedAssets: " +
                            "players online (OffPeak=true); it will run once the server is idle");
                }
                return false;
            }

            _deferred = false;
            _lastRun = now;
            if (LogSkips)
                SmoothServerPlugin.Log.LogInfo("[GcThrottle] allowing UnloadUnusedAssets through" +
                    (_skipped > 0 ? " (" + _skipped + " skipped since the last one)" : ""));
            _skipped = 0;
            return true;
        }

        private static bool IsIdle()
        {
            var znet = ZNet.instance;
            if (znet == null) return true;
            if (znet.IsSaving()) return false;
            var peers = znet.GetConnectedPeers();
            return peers == null || peers.Count == 0;
        }
    }
}
