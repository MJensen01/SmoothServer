using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// C1 - send cadence.
    ///
    /// Vanilla ZDOMan.SendZDOToPeers2(float dt) (0.221.12, verified against the decompile):
    ///   m_sendTimer += dt;
    ///   if (m_nextSendPeer &lt; 0) { if (m_sendTimer &gt; 0.05f) { m_nextSendPeer = 0; m_sendTimer = 0; } return; }
    ///   if (m_nextSendPeer &lt; m_peers.Count) SendZDOs(m_peers[m_nextSendPeer], false);
    ///   m_nextSendPeer++; if (m_nextSendPeer &gt;= m_peers.Count) m_nextSendPeer = -1;
    /// i.e. ONE peer per frame, and the 0.05s gate only restarts the round-robin.
    /// With N peers a full sweep costs one 50ms gate + N frames.
    ///
    /// Ours: every 1/SendHz seconds, send to EVERY peer in the same frame.
    /// Priority.High so we run before BetterNetworking's prefix on the same method -
    /// while this module is on, BetterNetworking's "Update Rate" setting is INERT
    /// (its prefix never runs; its Steamworks send-rate tuning is unaffected).
    /// </summary>
    internal sealed class SendCadenceModule : FeatureModule
    {
        public override string Name => "SendCadence";

        private ConfigEntry<float> _sendHz;

        internal static bool Active;
        internal static float SendHz = 20f;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("SendCadence", "Enabled", true,
                "Replace vanilla's one-peer-per-frame ZDO send round-robin with a fixed-rate " +
                "sweep over all peers. While on, BetterNetworking's Update Rate option is inert.");
            _sendHz = cfg.Bind("SendCadence", "SendHz", 20f,
                "Times per second the server pushes ZDO updates to every peer. Vanilla is " +
                "effectively 20Hz divided by peer count. Clamped to 1-60.");
            Watch(_sendHz);
        }

        protected override void ApplyPatches()
        {
            SendHz = Mathf.Clamp(_sendHz.Value, 1f, 60f);

            var target = AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers2", new[] { typeof(float) });
            if (target == null)
                throw new Exception("SmoothServer SendCadence: ZDOMan.SendZDOToPeers2(float) not found");

            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SendCadenceModule), nameof(Prefix))
                {
                    priority = Priority.High
                });

            Active = true;
            Log.LogInfo("[SendCadence] SendHz=" + SendHz.ToString("F1") +
                        " (interval " + (1000f / SendHz).ToString("F1") + "ms), priority=High");
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != _sendHz) return;
            SendHz = Mathf.Clamp(_sendHz.Value, 1f, 60f);
            Log.LogInfo("[SendCadence] SendHz -> " + SendHz.ToString("F1") +
                        " (interval " + (1000f / SendHz).ToString("F1") + "ms)");
        }

        private static bool Prefix(ZDOMan __instance, float dt)
        {
            if (!Active) return true;
            if (!ServerActive()) return true;

            var peers = __instance.m_peers;
            if (peers.Count == 0)
            {
                __instance.m_nextSendPeer = -1;
                return false;
            }

            __instance.m_sendTimer += dt;
            if (__instance.m_sendTimer >= 1f / SendHz)
            {
                __instance.m_sendTimer = 0f;
                for (int i = 0; i < peers.Count; i++)
                    __instance.SendZDOs(peers[i], false);
            }

            // vanilla's round-robin cursor stays parked so nothing else half-drives it
            __instance.m_nextSendPeer = -1;
            return false;
        }
    }
}
