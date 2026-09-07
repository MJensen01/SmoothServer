using BepInEx.Configuration;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// No patches: accumulates unscaled frame time from the plugin MonoBehaviour's Update
    /// and logs one summary line every IntervalSeconds. Tells us the real dedicated-server
    /// frame rate, which is what decides how much a higher send cadence can buy.
    /// </summary>
    internal sealed class TelemetryModule : FeatureModule
    {
        public override string Name => "Telemetry";

        private ConfigEntry<float> _interval;

        internal static bool Active;
        internal static float IntervalSeconds = 10f;

        private static float _acc;
        private static int _frames;
        private static float _worstDt;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("Telemetry", "Enabled", true,
                "Log a periodic server performance line (frame time, fps, peers, ZDO rates).");
            _interval = cfg.Bind("Telemetry", "IntervalSeconds", 10f,
                "Seconds between telemetry lines.");
            Watch(_interval);
        }

        protected override void ApplyPatches()
        {
            IntervalSeconds = Mathf.Max(1f, _interval.Value);
            _acc = 0f; _frames = 0; _worstDt = 0f;
            Active = true;
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != _interval) return;
            IntervalSeconds = Mathf.Max(1f, _interval.Value);
            // Restart the current window so the new interval takes effect immediately rather
            // than after whatever is left of the old window.
            _acc = 0f; _frames = 0; _worstDt = 0f;
            Log.LogInfo("[Telemetry] IntervalSeconds -> " + IntervalSeconds.ToString("F1"));
        }

        internal static void Tick(float dt)
        {
            if (!Active) return;
            if (!ServerActive()) return;

            _acc += dt;
            _frames++;
            if (dt > _worstDt) _worstDt = dt;

            if (_acc < IntervalSeconds || _frames == 0) return;

            float avgMs = (_acc / _frames) * 1000f;
            float fps = _frames / _acc;
            float worstMs = _worstDt * 1000f;

            int peers = -1, sent = -1, recv = -1, zdos = -1;
            var zm = ZDOMan.instance;
            if (zm != null)
            {
                peers = zm.m_peers.Count;
                sent = zm.m_zdosSentLastSec;
                recv = zm.m_zdosRecvLastSec;
                zdos = zm.m_objectsByID.Count;
            }

            int objs = -1;
            var scene = ZNetScene.instance;
            if (scene != null) objs = scene.m_instances.Count;

            SmoothServerPlugin.Log.LogInfo(string.Format(
                "[Telemetry] fps={0:F1} frame={1:F2}ms worst={2:F1}ms peers={3} zdosSent/s={4} zdosRecv/s={5} zdos={6} sceneObjs={7}",
                fps, avgMs, worstMs, peers, sent, recv, zdos, objs));

            _acc = 0f; _frames = 0; _worstDt = 0f;
        }
    }
}
