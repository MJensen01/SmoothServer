using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// The SmoothSave idea, re-implemented (SmoothSave itself states no licence - nothing copied).
    ///
    /// FIRST FINDING, and it changes the shape of the fix: vanilla 0.221.12 ALREADY writes the
    /// world off the main thread. ZNet.SaveWorld(bool) does
    ///
    ///     m_zdoMan.PrepareSave();              // main thread
    ///     ZoneSystem.instance.PrepareSave();   // main thread
    ///     RandEventSystem.instance.PrepareSave();
    ///     m_saveThread = new Thread(SaveWorldThread); m_saveThread.Start();
    ///
    /// and SaveWorldThread does all of the serialisation and file IO. So the 440-615ms autosave
    /// stall PERF-REPORT measured is NOT the file write - it is PrepareSave, and the bulk of that
    /// is ZDOMan.GetSaveClone(), which MemberwiseClones every persistent ZDO into a fresh
    ///     List&lt;ZDO&gt; list = new List&lt;ZDO&gt;();
    /// with no capacity. At ~100k ZDOs that list doubles ~17 times and copies ~200k references on
    /// the way, on the main thread, inside the stall we are trying to remove.
    ///
    /// This module therefore does two things:
    ///   * measures the main-thread half of every save (prefix/postfix stopwatch on SaveWorld)
    ///     and the background half (Stopwatch handed to a postfix on SaveWorldThread), so the
    ///     stall is a number in the log rather than a guess;
    ///   * pre-sizes the clone list from ZDOMan.m_objectsByID.Count (PreSizeClone, default on).
    ///
    /// Moving the clone itself off-thread is NOT safe: every other ZDO write on the main thread
    /// would race the walk of m_objectsBySector. That is left as future work behind a real design.
    ///
    /// SelfTestSeconds &gt; 0 forces two saves on an idle server (0 peers) - the first with the
    /// optimisation off, the second with it on - and logs both stalls plus the delta, which is the
    /// headless proof line.
    /// </summary>
    internal sealed class AsyncSaveModule : FeatureModule
    {
        public override string Name => "AsyncSave";

        private ConfigEntry<bool> _preSize;
        private ConfigEntry<bool> _logStalls;
        private ConfigEntry<float> _selfTestSeconds;

        internal static bool Active;
        internal static bool PreSizeClone = true;
        internal static bool LogStalls = true;

        private static readonly Stopwatch MainThreadWatch = new Stopwatch();
        private static readonly Stopwatch ThreadWatch = new Stopwatch();
        private static int _lastZdoCount;
        private static double _lastStallMs = -1.0;

        // StatsLog accumulators: fed by SaveWorldPostfix, drained by ConsumeSaveStats.
        private static int _statSaveCount;
        private static double _statMaxStallMs;

        // self test
        private static float _selfTestAt = -1f;
        private static int _selfTestPhase;          // 0 idle, 1 waiting to fire A, 2 waiting for A, 3 waiting to fire B, 4 waiting for B
        private static float _selfTestTimer;
        private static double _stallVanilla = -1.0;
        private static double _stallOptimised = -1.0;
        private static bool _savedPreSize;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("AsyncSave", "Enabled", true,
                "Measure the main-thread world-save stall and shrink it. Vanilla already writes the " +
                "file on a background thread; the stall is ZDOMan.PrepareSave's ZDO clone.");
            _preSize = cfg.Bind("AsyncSave", "PreSizeClone", true,
                "Pre-size ZDOMan.GetSaveClone()'s list from the live ZDO count instead of letting " +
                "it grow from zero. Same output, no reallocation storm on the main thread.");
            _logStalls = cfg.Bind("AsyncSave", "LogStalls", true,
                "Log one line per world save with the main-thread stall and the background-thread time.");
            _selfTestSeconds = cfg.Bind("AsyncSave", "SelfTestSeconds", 0f,
                "0 = off. Above 0: this many seconds after the world is up AND with 0 peers " +
                "connected, force two saves - one with PreSizeClone off, one on - and log both " +
                "stalls and the delta. Headless proof; leave at 0 in production.");
            Watch(_preSize); Watch(_logStalls); Watch(_selfTestSeconds);
        }

        protected override void ApplyPatches()
        {
            PreSizeClone = _preSize.Value;
            LogStalls = _logStalls.Value;

            var saveWorld = AccessTools.Method(typeof(ZNet), "SaveWorld", new[] { typeof(bool) });
            var saveThread = AccessTools.Method(typeof(ZNet), "SaveWorldThread");
            var getSaveClone = AccessTools.Method(typeof(ZDOMan), "GetSaveClone");

            if (saveWorld == null)
                throw new Exception("SmoothServer AsyncSave: ZNet.SaveWorld(bool) not found");
            if (saveThread == null)
                throw new Exception("SmoothServer AsyncSave: ZNet.SaveWorldThread not found - " +
                                    "vanilla's background save thread is gone, re-check the design");
            if (getSaveClone == null || getSaveClone.ReturnType != typeof(List<ZDO>))
                throw new Exception("SmoothServer AsyncSave: ZDOMan.GetSaveClone() -> List<ZDO> not found");
            if (AccessTools.Field(typeof(ZDOMan), "m_objectsBySector") == null ||
                AccessTools.Field(typeof(ZDOMan), "m_objectsByOutsideSector") == null ||
                AccessTools.Field(typeof(ZDOMan), "m_objectsByID") == null)
                throw new Exception("SmoothServer AsyncSave: ZDOMan sector/ID collections not found");

            Harmony.Patch(saveWorld,
                prefix: new HarmonyMethod(typeof(AsyncSaveModule), nameof(SaveWorldPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(AsyncSaveModule), nameof(SaveWorldPostfix)) { priority = Priority.Last });
            Harmony.Patch(saveThread,
                postfix: new HarmonyMethod(typeof(AsyncSaveModule), nameof(SaveWorldThreadPostfix)));
            Harmony.Patch(getSaveClone,
                prefix: new HarmonyMethod(typeof(AsyncSaveModule), nameof(GetSaveClonePrefix)) { priority = Priority.High });

            _selfTestAt = _selfTestSeconds.Value;
            _selfTestPhase = _selfTestAt > 0f ? 1 : 0;
            _selfTestTimer = 0f;

            Active = true;
            Log.LogInfo("[AsyncSave] vanilla already writes the world on ZNet.SaveWorldThread; " +
                        "instrumenting the MAIN-THREAD half (PrepareSave). preSizeClone=" + PreSizeClone +
                        " logStalls=" + LogStalls +
                        (_selfTestAt > 0f ? " selfTest=in " + _selfTestAt.ToString("F0") + "s" : ""));
        }

        public override void Disable()
        {
            Active = false;
            _selfTestPhase = 0;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _preSize) { PreSizeClone = _preSize.Value; Log.LogInfo("[AsyncSave] preSizeClone -> " + PreSizeClone); }
            else if (entry == _logStalls) LogStalls = _logStalls.Value;
        }

        // ---- measurement -----------------------------------------------------------------

        private static void SaveWorldPrefix()
        {
            if (!Active) return;
            _lastZdoCount = ZDOMan.instance != null ? ZDOMan.instance.m_objectsByID.Count : -1;
            MainThreadWatch.Reset();
            MainThreadWatch.Start();
            ThreadWatch.Reset();
            ThreadWatch.Start();
        }

        private static void SaveWorldPostfix()
        {
            if (!Active) return;
            MainThreadWatch.Stop();
            _lastStallMs = MainThreadWatch.Elapsed.TotalMilliseconds;
            _statSaveCount++;
            if (_lastStallMs > _statMaxStallMs) _statMaxStallMs = _lastStallMs;
            if (LogStalls)
                SmoothServerPlugin.Log.LogInfo(string.Format(
                    "[AsyncSave] world save: main-thread stall {0:F1}ms for {1} ZDOs (preSizeClone={2}); " +
                    "serialisation + file write continue on ZNet.SaveWorldThread",
                    _lastStallMs, _lastZdoCount, PreSizeClone));
        }

        private static void SaveWorldThreadPostfix()
        {
            if (!Active) return;
            ThreadWatch.Stop();
            if (LogStalls)
                SmoothServerPlugin.Log.LogInfo(string.Format(
                    "[AsyncSave] background save thread finished {0:F0}ms after SaveWorld started",
                    ThreadWatch.Elapsed.TotalMilliseconds));
        }

        // ---- the one optimisation ---------------------------------------------------------

        private static bool GetSaveClonePrefix(ZDOMan __instance, ref List<ZDO> __result)
        {
            if (!Active || !PreSizeClone) return true;

            var list = new List<ZDO>(__instance.m_objectsByID.Count + 64);

            var bySector = __instance.m_objectsBySector;
            for (int i = 0; i < bySector.Length; i++)
            {
                var bucket = bySector[i];
                if (bucket == null) continue;
                for (int j = 0; j < bucket.Count; j++)
                {
                    var zdo = bucket[j];
                    if (zdo.Persistent) list.Add(zdo.Clone());
                }
            }

            foreach (var bucket in __instance.m_objectsByOutsideSector.Values)
            {
                for (int j = 0; j < bucket.Count; j++)
                {
                    var zdo = bucket[j];
                    if (zdo.Persistent) list.Add(zdo.Clone());
                }
            }

            __result = list;
            return false;
        }

        /// <summary>StatsLog: save-event count and max stall since the last call, then reset.</summary>
        internal static void ConsumeSaveStats(out int count, out double maxStallMs)
        {
            count = _statSaveCount; maxStallMs = _statMaxStallMs;
            _statSaveCount = 0; _statMaxStallMs = 0.0;
        }

        // ---- headless self test -------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Active || _selfTestPhase == 0) return;
            if (!ServerActive()) return;

            var znet = ZNet.instance;
            var zm = ZDOMan.instance;
            if (znet == null || zm == null) return;

            _selfTestTimer += dt;

            switch (_selfTestPhase)
            {
                case 1:
                    if (_selfTestTimer < _selfTestAt) return;
                    if (zm.m_peers.Count > 0)
                    {
                        SmoothServerPlugin.Log.LogInfo("[AsyncSave] self-test skipped: " +
                            zm.m_peers.Count + " peer(s) connected");
                        _selfTestPhase = 0;
                        return;
                    }
                    _savedPreSize = PreSizeClone;
                    PreSizeClone = false;
                    SmoothServerPlugin.Log.LogInfo("[AsyncSave] self-test A: forcing a save with PreSizeClone=false");
                    _lastStallMs = -1.0;
                    znet.Save(false);
                    _selfTestTimer = 0f;
                    _selfTestPhase = 2;
                    return;

                case 2:
                    if (_lastStallMs < 0.0) return;
                    _stallVanilla = _lastStallMs;
                    if (_selfTestTimer < 5f || znet.IsSaving()) return;
                    PreSizeClone = true;
                    SmoothServerPlugin.Log.LogInfo("[AsyncSave] self-test B: forcing a save with PreSizeClone=true");
                    _lastStallMs = -1.0;
                    znet.Save(false);
                    _selfTestTimer = 0f;
                    _selfTestPhase = 4;
                    return;

                case 4:
                    if (_lastStallMs < 0.0) return;
                    _stallOptimised = _lastStallMs;
                    if (_selfTestTimer < 5f || znet.IsSaving()) return;
                    PreSizeClone = _savedPreSize;
                    SmoothServerPlugin.Log.LogInfo(string.Format(
                        "[AsyncSave] SELF-TEST RESULT: {0} ZDOs, main-thread stall vanilla-clone={1:F1}ms, " +
                        "pre-sized-clone={2:F1}ms, delta={3:F1}ms ({4:F1}%). PreSizeClone restored to {5}.",
                        _lastZdoCount, _stallVanilla, _stallOptimised,
                        _stallVanilla - _stallOptimised,
                        _stallVanilla > 0.0 ? 100.0 * (_stallVanilla - _stallOptimised) / _stallVanilla : 0.0,
                        PreSizeClone));
                    _selfTestPhase = 0;
                    return;
            }
        }
    }
}
