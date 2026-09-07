using BepInEx.Configuration;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// Raise (or leave alone) the dedicated server's frame cap.
    ///
    /// No Harmony patch: nothing in assembly_valheim writes Application.targetFrameRate on a
    /// dedicated server. The only writer is PresentManager (UpdatePresentSettingsWithVsyncCount),
    /// which is owned by GraphicsSettingsManager - client GUI only, and its
    /// GetCurrentFrameRateTarget cannot even produce 30 unless the window is unfocused. So the
    /// idle 30 fps is Unity's own headless/-batchmode default, not a Valheim setting.
    ///
    /// We therefore just assign it after ZNet.Start (server only), and assign it a second time
    /// ~5 s later in case something in the engine or another mod resets it during world load.
    /// A live config edit re-applies it immediately (Reapply()) and restarts that 5s safety
    /// re-assert window from the moment of the edit.
    /// </summary>
    internal sealed class FrameRateModule : FeatureModule
    {
        public override string Name => "FrameRate";

        private ConfigEntry<int> _target;

        internal static bool Active;
        internal static int TargetFrameRate;

        private static float _sinceStart = -1f;
        private static bool _reasserted;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("FrameRate", "Enabled", true,
                "Allow SmoothServer to set the server's frame cap. Does nothing while " +
                "TargetFrameRate is 0.");
            _target = cfg.Bind("FrameRate", "TargetFrameRate", 0,
                "Server frame rate cap. 0 = leave vanilla (Unity's headless default, ~30). " +
                "Set 60 or 120 to raise it. -1 = uncapped (burns a whole core).");
            Watch(_target);
        }

        protected override void ApplyPatches()
        {
            TargetFrameRate = _target.Value;
            _sinceStart = -1f;
            _reasserted = false;
            Active = true;
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != _target) return;
            TargetFrameRate = _target.Value;
            Reapply();
        }

        /// <summary>Called from the plugin's ZNet.Start postfix, server branch only.</summary>
        internal static void OnZNetStart()
        {
            if (!Active) return;
            Reapply();
        }

        /// <summary>
        /// Applies TargetFrameRate to the running engine right now. Used both at ZNet.Start
        /// and from a live config edit, so the two produce identical log lines.
        /// </summary>
        private static void Reapply()
        {
            if (!Active) return;
            if (!ServerActive())
            {
                SmoothServerPlugin.Log.LogInfo("[FrameRate] config changed to " + TargetFrameRate +
                    " but server not active yet, will apply at ZNet.Start");
                return;
            }

            int oldTarget = Application.targetFrameRate;
            int oldVSync = QualitySettings.vSyncCount;

            if (TargetFrameRate == 0)
            {
                SmoothServerPlugin.Log.LogInfo("[FrameRate] TargetFrameRate=0, leaving vanilla " +
                    "(Application.targetFrameRate=" + oldTarget + ", vSyncCount=" + oldVSync + ")");
                return;
            }

            Application.targetFrameRate = TargetFrameRate;
            QualitySettings.vSyncCount = 0;
            _sinceStart = 0f;
            _reasserted = false;

            SmoothServerPlugin.Log.LogInfo("[FrameRate] targetFrameRate " + oldTarget + " -> " +
                Application.targetFrameRate + ", vSyncCount " + oldVSync + " -> " +
                QualitySettings.vSyncCount);
        }

        internal static void Tick(float dt)
        {
            if (!Active || _reasserted || _sinceStart < 0f) return;

            _sinceStart += dt;
            if (_sinceStart < 5f) return;

            _reasserted = true;
            int before = Application.targetFrameRate;
            Application.targetFrameRate = TargetFrameRate;
            QualitySettings.vSyncCount = 0;
            SmoothServerPlugin.Log.LogInfo("[FrameRate] re-assert after 5s: was " + before +
                ", now " + Application.targetFrameRate +
                (before == TargetFrameRate ? " (held)" : " (SOMETHING RESET IT)"));
        }
    }
}
