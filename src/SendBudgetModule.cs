using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>
    /// C2 - per-send byte budget.
    ///
    /// Vanilla ZDOMan.SendZDOs(ZDOPeer peer, bool flush) (0.221.12):
    ///   if (!flush &amp;&amp; sendQueueSize &gt; 10240) return false;
    ///   int num = 10240 - sendQueueSize;
    ///   if (num &lt; 2048) return false;
    /// Two 10240 literals (ldc.i4) and one 2048 literal (ldc.i4). We swap each for a call
    /// returning the configured value, and assert the exact match counts at patch time.
    ///
    /// Since 0.5.2 the assertion knows the difference between "Iron Gate changed the method" and
    /// "another mod got here first" (issue #2): several networking mods rewrite these exact two
    /// literals, and Harmony hands the second transpiler the first one's output, so a foreign
    /// rewrite also shows up as `found 0`. If Harmony reports another owner's transpiler on
    /// ZDOMan.SendZDOs we leave the method entirely alone and report disabled(conflict); with no
    /// foreign transpiler the strict 2/1 assertion is unchanged and still refuses to patch.
    ///
    /// GetHighWaterBytes()/GetMinChunkBytes() are invoked fresh from the patched IL on every
    /// call, but they read the static HighWaterBytes/MinChunkBytes fields rather than the
    /// ConfigEntry directly (a transpiled IL call target must be a plain static method with no
    /// captured state) - so a live config edit only takes effect once OnConfigChanged below
    /// refreshes those fields.
    /// </summary>
    internal sealed class SendBudgetModule : FeatureModule
    {
        internal const string ModuleName = "SendBudget";
        public override string Name => ModuleName;

        private const int VanillaHighWater = 10240;
        private const int VanillaMinChunk = 2048;

        // Set at patch time so the static transpiler can ask Harmony who else is on the method.
        private static MethodBase _target;
        private static string _ownId;

        private ConfigEntry<int> _highWater;
        private ConfigEntry<int> _minChunk;

        internal static bool Active;
        internal static int HighWaterBytes = 65536;
        internal static int MinChunkBytes = 2048;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("SendBudget", "Enabled", true,
                "Raise the per-peer ZDO send queue high-water mark from vanilla's 10240 bytes.");
            _highWater = cfg.Bind("SendBudget", "HighWaterBytes", 65536,
                "Bytes of queued data above which the server stops adding ZDOs for a peer. Vanilla 10240.");
            _minChunk = cfg.Bind("SendBudget", "MinChunkBytes", 2048,
                "Minimum remaining budget worth building a packet for. Vanilla 2048.");
            Watch(_highWater);
            Watch(_minChunk);
        }

        // Called from patched IL. Falls back to vanilla numbers off-server so a client
        // running this DLL behaves exactly like vanilla.
        internal static int GetHighWaterBytes()
        {
            if (!Active || !ServerActive()) return VanillaHighWater;
            // Per-peer override (0.3.0): AdaptiveBudget returns `configured` unchanged
            // whenever that module is off or the peer has no telemetry yet, so this is
            // exactly HighWaterBytes when AdaptiveBudget is not running. NOTES 17.3.
            return AdaptiveBudgetModule.HighWaterFor(HighWaterBytes);
        }

        internal static int GetMinChunkBytes()
        {
            if (!Active || !ServerActive()) return VanillaMinChunk;
            return MinChunkBytes;
        }

        protected override void ApplyPatches()
        {
            HighWaterBytes = Math.Max(4096, _highWater.Value);
            MinChunkBytes = Math.Max(256, _minChunk.Value);

            var target = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
            if (target == null)
                throw new Exception("SmoothServer SendBudget: ZDOMan.SendZDOs not found");

            _target = target;
            _ownId = Harmony.Id;
            ILUtil.RequireSolePatcher(target, _ownId, ModuleName, ModuleName);

            Harmony.Patch(target,
                transpiler: new HarmonyMethod(typeof(SendBudgetModule), nameof(Transpiler)));

            Active = true;
            Log.LogInfo("[SendBudget] highWater=" + HighWaterBytes + "B minChunk=" + MinChunkBytes + "B");
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _highWater) HighWaterBytes = Math.Max(4096, _highWater.Value);
            else if (entry == _minChunk) MinChunkBytes = Math.Max(256, _minChunk.Value);
            else return;

            Log.LogInfo("[SendBudget] highWater=" + HighWaterBytes + "B minChunk=" + MinChunkBytes + "B");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var owners = ILUtil.OtherTranspilersOn(_target, _ownId);
            bool stoodDown;
            var result = Rewrite(new List<CodeInstruction>(instructions), owners.Length > 0, out stoodDown);
            if (stoodDown) ILUtil.StandDown(ModuleName, ModuleName, _target, owners);
            else SmoothServerPlugin.Log.LogInfo("[SendBudget] transpiler OK: 2x highWater, 1x minChunk replaced (assertion 2/1 passed)");
            return result;
        }

        /// <summary>
        /// The transpiler body, separated from Harmony so ILSelfTest can run it on a synthetic
        /// instruction list. COUNTS first and only rewrites once the counts are right, so the
        /// stand-down path can hand the caller's own instructions back byte-for-byte untouched -
        /// the other mod's rewrite must survive intact.
        ///
        /// foreignTranspiler = another mod has a transpiler on this method. It is the ONLY thing
        /// that turns the hard assertion into a quiet stand-down; on unmodified IL the 2/1 match
        /// is still mandatory.
        /// </summary>
        internal static List<CodeInstruction> Rewrite(List<CodeInstruction> list, bool foreignTranspiler,
                                                      out bool stoodDown)
        {
            stoodDown = false;
            int hiMatches = 0, minMatches = 0;

            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;
                if (v == VanillaHighWater) hiMatches++;
                else if (v == VanillaMinChunk) minMatches++;
            }

            if (hiMatches != 2 || minMatches != 1)
            {
                if (foreignTranspiler)
                {
                    // Somebody else's numbers are in this method now. Leave every instruction as
                    // we received it and let the caller report the conflict.
                    stoodDown = true;
                    return list;
                }

                var msg = "SmoothServer SendBudget transpiler: expected exactly 2x " + VanillaHighWater +
                          " and 1x " + VanillaMinChunk + " in ZDOMan.SendZDOs, found " +
                          hiMatches + " and " + minMatches +
                          " - game IL changed, refusing to patch";
                throw new Exception(msg);
            }

            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;
                if (v == VanillaHighWater)
                    ILUtil.ReplaceWithCall(list[i], typeof(SendBudgetModule), nameof(GetHighWaterBytes));
                else if (v == VanillaMinChunk)
                    ILUtil.ReplaceWithCall(list[i], typeof(SendBudgetModule), nameof(GetMinChunkBytes));
            }

            return list;
        }
    }
}
