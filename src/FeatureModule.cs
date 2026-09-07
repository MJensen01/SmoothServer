using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>Which half of the mod a module belongs to.</summary>
    internal enum ModuleSide
    {
        /// <summary>Dedicated server only (never patched on a player's client).</summary>
        Server,
        /// <summary>Player client only (never patched on a dedicated server).</summary>
        Client,
        /// <summary>Both halves.</summary>
        Both
    }

    /// <summary>
    /// One toggleable feature. Owns its own Harmony instance so a failure in one
    /// module can never take down the others or the plugin.
    ///
    /// Since 0.3.0 SmoothServer is a BOTH-ENDS mod: the same DLL runs on the dedicated
    /// server and in a player's BepInEx profile. A module declares which half it belongs to
    /// with <see cref="Side"/>; a module whose side does not match the running half is never
    /// patched and reports <c>disabled(side)</c>.
    ///
    /// <b>Side defaults to Server</b>, because that is what SmoothServer's existing modules
    /// are — a new server-side module needs no override and behaves exactly as it did in
    /// 0.2.0. Client and both-ends modules must say so explicitly.
    ///
    /// To add a module: drop a file in this project with a non-abstract subclass of
    /// FeatureModule. The plugin reflects over the assembly at Awake and instantiates every
    /// one it finds (a private parameterless ctor is fine) — there is no registry to edit.
    ///
    /// Contract:
    ///   override string Name         - log prefix, module summary, Harmony instance id.
    ///   override ModuleSide Side     - Server (default) / Client / Both.
    ///   override void Bind()         - bind config with BindSynced/BindLocal. [Section] Enabled
    ///                                  is bound for you before Bind() runs. (Legacy modules may
    ///                                  instead override Configure(ConfigFile) and bind by hand.)
    ///   override void ApplyPatches() - install patches on `Harmony`. THROW loudly on any
    ///                                  mismatch; the throw becomes FAILED(reason) in the
    ///                                  summary and disables only this module.
    /// Optional: Section, DefaultEnabled, OnConfigChanged, StatusDetail, Disable.
    ///
    /// Every patch body must gate on <see cref="Active"/> plus the side gate
    /// (ServerActive() / ClientActive()) so the same DLL is vanilla on the other half.
    /// </summary>
    internal abstract class FeatureModule
    {
        public abstract string Name { get; }

        /// <summary>Defaults to Server — SmoothServer's historic modules are all server-side.</summary>
        public virtual ModuleSide Side => ModuleSide.Server;

        public virtual string Section => Name;
        public virtual bool DefaultEnabled => true;
        protected virtual string EnabledDescription => "Enable the " + Name + " module.";

        public ConfigEntry<bool> EnabledCfg;
        protected Harmony Harmony;
        protected ConfigFile Cfg;

        public string Status = "not-run";
        public bool Applied;

        /// <summary>Config says on. Independent of whether the patches installed.</summary>
        public bool Enabled => EnabledCfg != null && EnabledCfg.Value;

        /// <summary>
        /// Patches installed AND config still on. Patch bodies gate on this.
        /// Named IsActive, not Active: SmoothServer's original modules each carry their own
        /// `internal static bool Active` field that the transpiled IL calls into, and a base
        /// member of the same name would shadow it (CS0108) in every one of them.
        /// </summary>
        public bool IsActive => Applied && Enabled;

        protected static ManualLogSource Log => SmoothServerPlugin.Log;

        // ---- lifecycle (called by the plugin, not by modules) --------------------------

        /// <summary>
        /// Binds [Section] Enabled (synced) and calls <see cref="Bind"/>. Legacy modules that
        /// predate ServerSync override this wholesale and bind their entries by hand.
        /// </summary>
        public virtual void Configure(ConfigFile cfg)
        {
            Cfg = cfg;
            EnabledCfg = BindSynced(Section, "Enabled", DefaultEnabled, EnabledDescription);
            Bind();
        }

        /// <summary>
        /// Compatibility overload for callers that predate the Server/Client split (e.g. the
        /// Modules/Server bootstrap): enable against the half this process is actually running.
        /// </summary>
        public void TryEnable(string guidPrefix)
        {
            TryEnable(guidPrefix, SmoothServerPlugin.RunningSide);
        }

        public void TryEnable(string guidPrefix, ModuleSide runningSide)
        {
            if (Side != ModuleSide.Both && Side != runningSide)
            {
                Applied = false;
                Status = "disabled(side)";
                return;
            }

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

        // ---- to implement -------------------------------------------------------------

        /// <summary>Bind this module's config entries. [Section] Enabled is already bound.</summary>
        protected virtual void Bind() { }

        /// <summary>Apply the module's patches. Throw loudly on any mismatch.</summary>
        protected abstract void ApplyPatches();

        /// <summary>
        /// Hot reload: fires for any config entry this module wired with <see cref="Watch"/> or
        /// bound through BindSynced/BindLocal, whether the change came from a local edit,
        /// Config.Reload() picking up an edit on disk, or a ServerSync push from the server.
        /// </summary>
        public virtual void OnConfigChanged(ConfigEntryBase entry) { }

        /// <summary>Extra detail for the startup summary. Return null for none.</summary>
        public virtual string StatusDetail() { return null; }

        /// <summary>Wire an entry's SettingChanged straight into this module's OnConfigChanged.</summary>
        protected void Watch<T>(ConfigEntry<T> entry)
        {
            entry.SettingChanged += (s, a) =>
            {
                try { OnConfigChanged(entry); }
                catch (Exception e) { Log.LogError("[" + Name + "] OnConfigChanged threw: " + e); }
            };
        }

        public virtual void Disable()
        {
            Applied = false;
            try
            {
                if (Harmony != null) Harmony.UnpatchSelf();
            }
            catch (Exception e)
            {
                Log.LogWarning("[" + Name + "] unpatch failed: " + e.Message);
            }
        }

        // ---- config helpers -----------------------------------------------------------

        /// <summary>Bind a server-synced entry: the server's value wins on every client.</summary>
        protected ConfigEntry<T> BindSynced<T>(string section, string key, T defaultValue, string description)
        {
            return SmoothServerPlugin.BindSynced(section, key, defaultValue, description, this);
        }

        protected ConfigEntry<T> BindSynced<T>(string key, T defaultValue, string description)
        {
            return SmoothServerPlugin.BindSynced(Section, key, defaultValue, description, this);
        }

        /// <summary>Bind a machine-local entry: never synced, each install keeps its own value.</summary>
        protected ConfigEntry<T> BindLocal<T>(string section, string key, T defaultValue, string description)
        {
            return SmoothServerPlugin.BindLocal(section, key, defaultValue, description, this);
        }

        protected ConfigEntry<T> BindLocal<T>(string key, T defaultValue, string description)
        {
            return SmoothServerPlugin.BindLocal(Section, key, defaultValue, description, this);
        }

        // ---- runtime gates --------------------------------------------------------------

        /// <summary>Runtime gate for server-side behaviour.</summary>
        protected internal static bool ServerActive()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>Runtime gate for client-side behaviour (a player's game, incl. a host).</summary>
        protected internal static bool ClientActive()
        {
            return ZNet.instance != null && !ZNet.instance.IsDedicated();
        }
    }
}
