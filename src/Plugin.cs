using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using SmoothServer.Map;
using SmoothServer.Net;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>How the plugin decides which half of itself to run.</summary>
    public enum RunMode
    {
        /// <summary>Dedicated server (-batchmode) = server half, anything else = client half.</summary>
        Auto,
        /// <summary>Force the server half.</summary>
        Server,
        /// <summary>Force the client half.</summary>
        Client
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SmoothServerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Nosferatu.SmoothServer";
        public const string PluginName = "SmoothServer";
        public const string PluginVersion = "0.4.0";

        internal static ManualLogSource Log;
        internal static ConfigFile Cfg;
        internal static ConfigSync ConfigSync;

        internal static readonly List<FeatureModule> Modules = new List<FeatureModule>();

        internal static ConfigEntry<RunMode> ModeCfg;
        internal static ConfigEntry<bool> EnforceClientMod;
        internal static ConfigEntry<bool> HotReloadCfg;
        internal static ConfigEntry<bool> SteamSelfTestCfg;

        /// <summary>The half of the mod this process is running.</summary>
        internal static ModuleSide RunningSide = ModuleSide.Server;

        internal static bool IsServerSide => RunningSide == ModuleSide.Server;

        /// <summary>True when CW_Jesse's BetterNetworking is loaded alongside us.</summary>
        internal static bool BetterNetworkingPresent;

        private Harmony _bootstrap;
        private static bool _summaryLogged;
        private ConfigWatcher _configWatcher;

        private void Awake()
        {
            Log = Logger;
            Cfg = Config;

            ConfigSync = new ConfigSync(PluginGuid)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };

            ModeCfg = BindLocal("General", "Mode", RunMode.Auto,
                "Which half of the mod to run. Auto = a dedicated server (-batchmode) runs the " +
                "server half, everything else runs the client half. Machine-local, never synced.",
                null);

            SteamSelfTestCfg = BindLocal("General", "SteamSelfTest", false,
                "Diagnostic, off by default: at load, log which half of Steamworks is initialised " +
                "in this process (SteamGameServer* vs Steam*) and which interfaces this build of " +
                "ZSteamSocket actually calls, read straight out of its IL. The client and " +
                "dedicated-server builds of assembly_valheim.dll differ here, which is what broke " +
                "0.3.0 - turn this on once after a game update to re-prove it. Machine-local.",
                null);

            HotReloadCfg = BindLocal("General", "HotReload", true,
                "Watch this plugin's own cfg file on disk and reload it automatically when it " +
                "changes, so edits take effect without a server restart. Machine-local, never synced.",
                null);

            // NOTE the default is FALSE, unlike NoVikingLeftBehind. SmoothServer's server half is
            // valuable with zero client installs - that is its market position - so it must keep
            // working as a pure server-side mod. Groups that do install everywhere opt in here.
            EnforceClientMod = BindSynced("General", "EnforceClientMod", false,
                "Server: require every connecting client to run SmoothServer " + PluginVersion +
                " or newer. Vanilla clients and clients with an older version are disconnected " +
                "with an explanatory message, and the synced config is locked so only the server " +
                "(and admins) can change it. Default OFF: SmoothServer's server half works with " +
                "no client installs at all - the client-side features (compression, client send " +
                "budget, shared map) simply do not exist for a vanilla joiner.",
                null);
            ConfigSync.AddLockingConfigEntry(EnforceClientMod);
            EnforceClientMod.SettingChanged += (s, a) => ApplyEnforcement();
            ApplyEnforcement();

            // 0.4.0: the tuning preset. Bound here so it sits with the other plugin-level
            // entries; applied further down, once every module has bound its own config.
            Profiles.Bind();

            Log.LogInfo("ServerSync initialised: id=" + ConfigSync.Name +
                        " display=" + ConfigSync.DisplayName +
                        " CurrentVersion=" + ConfigSync.CurrentVersion +
                        " MinimumRequiredVersion=" + ConfigSync.MinimumRequiredVersion +
                        " ModRequired=" + ConfigSync.ModRequired +
                        " EnforceClientMod=" + EnforceClientMod.Value);

            RunningSide = ResolveSide();
            Log.LogInfo(PluginName + " " + PluginVersion + ": mode=" + ModeCfg.Value +
                        " -> running the " + (IsServerSide ? "SERVER" : "CLIENT") + " half" +
                        " (isBatchMode=" + Application.isBatchMode + ")");

            if (SteamSelfTestCfg.Value)
            {
                try { SteamSelfTest.Run(); }
                catch (Exception e) { Log.LogWarning("[SteamSelfTest] failed: " + e); }
            }

            BetterNetworkingPresent = DetectBetterNetworking();
            if (BetterNetworkingPresent)
                Log.LogWarning("BetterNetworking is installed alongside SmoothServer. Both wrap " +
                               "ZSteamSocket's send queue and they CANNOT coexist - SmoothServer's " +
                               "Compression module will refuse to patch. Uninstall BetterNetworking " +
                               "to use SmoothServer compression.");

            DiscoverModules();

            foreach (var m in Modules)
            {
                try { m.Configure(Config); }
                catch (Exception e) { Log.LogError("[" + m.Name + "] config bind failed: " + e); }
            }

            // Presets, before ANY module installs a patch: a profile writes its values into
            // the entries that are already bound, so every module below reads - and logs -
            // the number it is actually going to run with. See Profiles.cs.
            Profiles.Apply("startup");

            // Patches are installed now (ZNet.Start -> ServerLoadWorld happens too late for
            // some hooks), but every patch body gates on its side at runtime.
            foreach (var m in Modules) m.TryEnable(PluginGuid, RunningSide);

            _bootstrap = new Harmony(PluginGuid + ".bootstrap");
            var znetStart = AccessTools.Method(typeof(ZNet), "Start");
            if (znetStart == null)
                Log.LogError("SmoothServer: ZNet.Start not found - cannot log the module summary");
            else
                _bootstrap.Patch(znetStart,
                    postfix: new HarmonyMethod(typeof(SmoothServerPlugin), nameof(ZNetStartPostfix)));

            // Live config reload: watches Nosferatu.SmoothServer.cfg on disk and calls
            // Config.Reload() (debounced, on the main thread via Update()) so edits on a
            // running server take effect without a restart. See ConfigWatcher.cs.
            _configWatcher = new ConfigWatcher(Cfg, Log, "[Config]");
            ConfigWatcher.AfterReload = () => Profiles.Apply("cfg reload");

            Log.LogInfo("SmoothServer " + PluginVersion + " loaded, " + Modules.Count + " modules");
        }

        // ---- side resolution -------------------------------------------------------------

        private static ModuleSide ResolveSide()
        {
            switch (ModeCfg.Value)
            {
                case RunMode.Server: return ModuleSide.Server;
                case RunMode.Client: return ModuleSide.Client;
                default:
                    // ZNet.IsDedicated() is an instance method and ZNet does not exist yet at
                    // Awake, where the patches have to be installed - so the dedicated server is
                    // identified by Unity's headless flag instead.
                    return Application.isBatchMode ? ModuleSide.Server : ModuleSide.Client;
            }
        }

        private static void ApplyEnforcement()
        {
            bool on = EnforceClientMod != null && EnforceClientMod.Value;
            ConfigSync.ModRequired = on;
            ConfigSync.MinimumRequiredVersion = on ? PluginVersion : "0.0.0";
        }

        private static bool DetectBetterNetworking()
        {
            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    if (kv.Key != null && kv.Key.IndexOf("BetterNetworking", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("could not enumerate loaded plugins: " + e.Message);
            }
            return false;
        }

        // ---- module auto-discovery ---------------------------------------------------------

        private static void DiscoverModules()
        {
            var found = new List<FeatureModule>();
            Type[] types;
            try { types = Assembly.GetExecutingAssembly().GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.IsAbstract || !typeof(FeatureModule).IsAssignableFrom(t)) continue;
                try { found.Add((FeatureModule)Activator.CreateInstance(t, true)); }
                catch (Exception e) { Log.LogError("module discovery: cannot instantiate " + t.Name + ": " + e); }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            Modules.Clear();
            Modules.AddRange(found);
        }

        // ---- config helpers ----------------------------------------------------------------

        internal static ConfigEntry<T> BindSynced<T>(string section, string key, T defaultValue,
                                                     string description, FeatureModule owner)
        {
            var entry = Cfg.Bind(section, key, defaultValue, description);
            ConfigSync.AddConfigEntry(entry).SynchronizedConfig = true;
            Wire(entry, owner);
            return entry;
        }

        internal static ConfigEntry<T> BindLocal<T>(string section, string key, T defaultValue,
                                                    string description, FeatureModule owner)
        {
            // Deliberately NOT registered with ConfigSync: a local entry stays editable on the
            // client even when the synced config is locked.
            var entry = Cfg.Bind(section, key, defaultValue, description);
            Wire(entry, owner);
            return entry;
        }

        private static void Wire<T>(ConfigEntry<T> entry, FeatureModule owner)
        {
            entry.SettingChanged += (s, a) =>
            {
                try { if (owner != null) owner.OnConfigChanged(entry); }
                catch (Exception e) { Log.LogError("OnConfigChanged(" + entry.Definition + ") threw: " + e); }
            };
        }

        // ---- summary -------------------------------------------------------------------------

        private static void ZNetStartPostfix()
        {
            if (_summaryLogged) return;
            _summaryLogged = true;

            var parts = new List<string>();
            foreach (var m in Modules) parts.Add(m.Name + "=" + m.Status);
            Log.LogInfo("SmoothServer module summary: " + string.Join(", ", parts.ToArray()));
            Log.LogInfo(Profiles.Summary());

            Log.LogInfo("SmoothServer " + PluginVersion + " (" + (IsServerSide ? "server" : "client") + " half)" +
                        "  EnforceClientMod=" + EnforceClientMod.Value +
                        "  configLocked=" + ConfigSync.IsLocked +
                        "  sourceOfTruth=" + ConfigSync.IsSourceOfTruth);

            foreach (var m in Modules)
            {
                string detail = null;
                try { detail = m.StatusDetail(); }
                catch (Exception e) { detail = "status detail threw: " + e.Message; }
                Log.LogInfo("  " + m.Name + " [" + m.Side + "] = " + m.Status +
                            (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
            }

            FrameRateModule.OnZNetStart();
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            TelemetryModule.Tick(dt);
            FrameRateModule.Tick(dt);
            CompressionModule.Tick(dt);
            SharedMapModule.Tick(dt);
            ServerModules.Tick(dt);

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
