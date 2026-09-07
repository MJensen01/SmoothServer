using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SmoothServerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Nosferatu.SmoothServer";
        public const string PluginName = "SmoothServer";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigFile Cfg;
        internal static readonly List<FeatureModule> Modules = new List<FeatureModule>();

        internal static ConfigEntry<bool> HotReloadCfg;

        private Harmony _bootstrap;
        private static bool _summaryLogged;
        private ConfigWatcher _configWatcher;

        private void Awake()
        {
            Log = Logger;
            Cfg = Config;

            HotReloadCfg = Config.Bind("General", "HotReload", true,
                "Watch this plugin's own cfg file on disk and reload it automatically when it " +
                "changes, so edits take effect without a server restart.");

            Modules.Add(new TelemetryModule());
            Modules.Add(new SendCadenceModule());
            Modules.Add(new SendBudgetModule());
            Modules.Add(new CreateBudgetModule());
            Modules.Add(new FrameRateModule());

            foreach (var m in Modules)
            {
                try { m.Configure(Config); }
                catch (Exception e) { Log.LogError("[" + m.Name + "] config bind failed: " + e); }
            }

            // Patches are installed now (ZNet.Start -> ServerLoadWorld happens too late for
            // some hooks), but every patch body gates on ZNet.instance.IsServer() at runtime.
            foreach (var m in Modules) m.TryEnable(PluginGuid);

            _bootstrap = new Harmony(PluginGuid + ".bootstrap");
            var znetStart = AccessTools.Method(typeof(ZNet), "Start");
            if (znetStart == null)
                Log.LogError("SmoothServer: ZNet.Start not found - cannot detect server/client mode");
            else
                _bootstrap.Patch(znetStart,
                    postfix: new HarmonyMethod(typeof(SmoothServerPlugin), nameof(ZNetStartPostfix)));

            // Live config reload: watches Nosferatu.SmoothServer.cfg on disk and calls
            // Config.Reload() (debounced, on the main thread via Update()) so edits on a
            // running server take effect without a restart. See ConfigWatcher.cs.
            _configWatcher = new ConfigWatcher(Cfg, Log, "[Config]");

            Log.LogInfo("SmoothServer " + PluginVersion + " loaded, " + Modules.Count + " modules");
        }

        private static void ZNetStartPostfix()
        {
            if (_summaryLogged) return;
            _summaryLogged = true;

            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                foreach (var m in Modules) m.Disable();
                Log.LogInfo("SmoothServer: client mode, no patches");
                return;
            }

            var parts = new List<string>();
            foreach (var m in Modules) parts.Add(m.Name + "=" + m.Status);
            Log.LogInfo("SmoothServer module summary: " + string.Join(", ", parts.ToArray()));

            FrameRateModule.OnZNetStart();
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            TelemetryModule.Tick(dt);
            FrameRateModule.Tick(dt);

            if (HotReloadCfg != null && HotReloadCfg.Value) _configWatcher?.Pump();
        }

        private void OnDestroy()
        {
            foreach (var m in Modules) m.Disable();
            _configWatcher?.Dispose();
            try { if (_bootstrap != null) _bootstrap.UnpatchSelf(); }
            catch { /* shutting down */ }
        }
    }
}
