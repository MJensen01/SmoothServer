using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// M7 - faster ownership release.
    ///
    /// Vanilla ZDOMan.ReleaseZDOS(float dt) (0.221.12):
    ///     m_releaseZDOTimer += dt;
    ///     if (!(m_releaseZDOTimer > 2f)) return;        // exactly one ldc.r4 2 in the method
    ///     m_releaseZDOTimer = 0f;
    ///     ReleaseNearbyZDOS(ZNet.instance.GetReferencePosition(), m_sessionID);
    ///     foreach (peer) ReleaseNearbyZDOS(peer.m_peer.m_refPos, peer.m_peer.m_uid);
    ///
    /// That 2-second cadence is why a mob "freezes" for up to two seconds after the player who
    /// owned its zone walks away: nobody re-claims the ZDO until the next sweep. Shortening it to
    /// 0.5s makes ownership follow players out of a zone 4x faster.
    ///
    /// The cost is 4x as many ReleaseNearbyZDOS scans - which is exactly the scan VPOServer's
    /// ReleaseScanSpeedup port makes cheaper, so the two are designed to ship together.
    ///
    /// Transpiler on the single ldc.r4 2 literal, asserting the match count, exactly like
    /// SendBudget/CreateBudget.
    /// </summary>
    internal sealed class OwnershipReleaseModule : FeatureModule
    {
        public override string Name => "OwnershipRelease";

        private const float VanillaIntervalSec = 2f;

        private ConfigEntry<float> _interval;

        internal static bool Active;
        internal static float ReleaseIntervalSec = 0.5f;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("OwnershipRelease", "Enabled", true,
                "Shorten ZDOMan's ownership-release sweep interval from vanilla's 2 seconds.");
            _interval = cfg.Bind("OwnershipRelease", "ReleaseIntervalSec", 0.5f,
                "Seconds between ownership-release sweeps. Vanilla 2.0. Lower = mobs are re-claimed " +
                "faster when their owner leaves a zone, at the cost of more scans per second - " +
                "enable VPOServer.ReleaseScanSpeedup with this. Clamped to 0.1-10.");
            Watch(_interval);
        }

        /// <summary>Called from patched IL. Vanilla value off-server / when disabled.</summary>
        internal static float GetReleaseIntervalSec()
        {
            if (!Active || !ServerActive()) return VanillaIntervalSec;
            return ReleaseIntervalSec;
        }

        protected override void ApplyPatches()
        {
            ReleaseIntervalSec = Mathf.Clamp(_interval.Value, 0.1f, 10f);

            var target = AccessTools.Method(typeof(ZDOMan), "ReleaseZDOS");
            if (target == null)
                throw new Exception("SmoothServer OwnershipRelease: ZDOMan.ReleaseZDOS not found");

            Harmony.Patch(target,
                transpiler: new HarmonyMethod(typeof(OwnershipReleaseModule), nameof(Transpiler)));

            Active = true;
            Log.LogInfo("[OwnershipRelease] releaseIntervalSec=" + ReleaseIntervalSec.ToString("F2") +
                        " (vanilla 2.00)");
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != _interval) return;
            ReleaseIntervalSec = Mathf.Clamp(_interval.Value, 0.1f, 10f);
            Log.LogInfo("[OwnershipRelease] releaseIntervalSec -> " + ReleaseIntervalSec.ToString("F2"));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int matches = 0;

            for (int i = 0; i < list.Count; i++)
            {
                float v;
                if (!ServerIL.TryGetR4(list[i], out v)) continue;
                if (Math.Abs(v - VanillaIntervalSec) > 0.0001f) continue;

                ILUtil.ReplaceWithCall(list[i], typeof(OwnershipReleaseModule), nameof(GetReleaseIntervalSec));
                matches++;
            }

            if (matches != 1)
            {
                var msg = "SmoothServer OwnershipRelease transpiler: expected exactly 1x " +
                          VanillaIntervalSec + "f in ZDOMan.ReleaseZDOS, found " + matches +
                          " - game IL changed, refusing to patch";
                SmoothServerPlugin.Log.LogError(msg);
                throw new Exception(msg);
            }

            SmoothServerPlugin.Log.LogInfo("[OwnershipRelease] transpiler OK: 1x releaseZDOTimer " +
                                           "threshold replaced (assertion 1 passed)");
            return list;
        }
    }

    /// <summary>Float-literal helpers for the Modules/Server transpilers (ILUtil only handles i4).</summary>
    internal static class ServerIL
    {
        /// <summary>True if the instruction pushes a float32 constant; yields its value.</summary>
        internal static bool TryGetR4(CodeInstruction ci, out float value)
        {
            value = 0f;
            if (ci == null || ci.operand == null) return false;
            if (ci.opcode != OpCodes.Ldc_R4) return false;
            try { value = Convert.ToSingle(ci.operand); return true; }
            catch { return false; }
        }
    }
}
