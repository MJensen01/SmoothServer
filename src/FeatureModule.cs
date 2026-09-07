using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>
    /// One toggleable feature. Owns its own Harmony instance so a failure in one
    /// module can never take down the others or the plugin.
    /// </summary>
    internal abstract class FeatureModule
    {
        public abstract string Name { get; }

        public ConfigEntry<bool> EnabledCfg;
        protected Harmony Harmony;

        public string Status = "not-run";
        public bool Applied;

        protected static ManualLogSource Log => SmoothServerPlugin.Log;

        public abstract void Configure(ConfigFile cfg);

        /// <summary>Apply the module's patches. Throw loudly on any mismatch.</summary>
        protected abstract void ApplyPatches();

        /// <summary>
        /// Hot reload: fires for any config entry this module wired with <see cref="Watch"/>,
        /// whether the change came from a local edit or Config.Reload() picking up an edit on
        /// disk. Override to re-read the value into whatever static/cached field the module's
        /// patch actually reads at call time.
        /// </summary>
        public virtual void OnConfigChanged(ConfigEntryBase entry) { }

        /// <summary>Wire an entry's SettingChanged straight into this module's OnConfigChanged.</summary>
        protected void Watch<T>(ConfigEntry<T> entry)
        {
            entry.SettingChanged += (s, a) =>
            {
                try { OnConfigChanged(entry); }
                catch (Exception e) { Log.LogError("[" + Name + "] OnConfigChanged threw: " + e); }
            };
        }

        public void TryEnable(string guidPrefix)
        {
            if (EnabledCfg == null || !EnabledCfg.Value)
            {
                Status = "disabled";
                Applied = false;
                return;
            }

            try
            {
                Harmony = new Harmony(guidPrefix + "." + Name);
                ApplyPatches();
                Applied = true;
                Status = "applied";
                Log.LogInfo("[" + Name + "] applied");
            }
            catch (Exception e)
            {
                Applied = false;
                var msg = e.InnerException != null ? e.InnerException.Message : e.Message;
                Status = "FAILED(" + msg + ")";
                Log.LogError("[" + Name + "] FAILED to patch: " + e);
                Disable();
            }
        }

        public virtual void Disable()
        {
            try
            {
                if (Harmony != null) Harmony.UnpatchSelf();
            }
            catch (Exception e)
            {
                Log.LogWarning("[" + Name + "] unpatch failed: " + e.Message);
            }
        }

        /// <summary>Runtime gate: every patch body must call this before changing behaviour.</summary>
        protected internal static bool ServerActive()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }
    }
}
