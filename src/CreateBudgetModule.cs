using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>
    /// C5 - object creation budget.
    ///
    /// Vanilla ZNetScene.CreateObjects(List&lt;ZDO&gt;, List&lt;ZDO&gt;) (0.221.12):
    ///   int maxCreatedPerFrame = 10;                 // ldc.i4.s 10  <-- ours
    ///   if (InLoadingScreen()) maxCreatedPerFrame = 100;   // ldc.i4.s 100
    ///   int created = 0;                             // ldc.i4.0
    /// Exactly one literal 10 in the method. Default config value is 10, so enabling the
    /// module changes nothing until MaxCreatedPerFrame is raised.
    /// </summary>
    internal sealed class CreateBudgetModule : FeatureModule
    {
        public override string Name => "CreateBudget";

        private const int VanillaMax = 10;

        private ConfigEntry<int> _max;

        internal static bool Active;
        internal static int MaxCreatedPerFrame = VanillaMax;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("CreateBudget", "Enabled", true,
                "Make ZNetScene's per-frame object instantiation budget configurable.");
            _max = cfg.Bind("CreateBudget", "MaxCreatedPerFrame", 10,
                "Objects ZNetScene may instantiate per frame outside the loading screen. " +
                "Vanilla 10 - the default is deliberately vanilla, raise it to test." + Profiles.Note);
            Watch(_max);
        }

        internal static int GetMaxCreatedPerFrame()
        {
            if (!Active || !ServerActive()) return VanillaMax;
            return MaxCreatedPerFrame;
        }

        protected override void ApplyPatches()
        {
            MaxCreatedPerFrame = Math.Max(1, _max.Value);

            var target = AccessTools.Method(typeof(ZNetScene), "CreateObjects");
            if (target == null)
                throw new Exception("SmoothServer CreateBudget: ZNetScene.CreateObjects not found");

            Harmony.Patch(target,
                transpiler: new HarmonyMethod(typeof(CreateBudgetModule), nameof(Transpiler)));

            Active = true;
            Log.LogInfo("[CreateBudget] maxCreatedPerFrame=" + MaxCreatedPerFrame +
                        (MaxCreatedPerFrame == VanillaMax ? " (vanilla value, no behaviour change)" : ""));
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != _max) return;
            MaxCreatedPerFrame = Math.Max(1, _max.Value);
            Log.LogInfo("[CreateBudget] maxCreatedPerFrame=" + MaxCreatedPerFrame +
                        (MaxCreatedPerFrame == VanillaMax ? " (vanilla value, no behaviour change)" : ""));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int matches = 0;

            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;
                if (v != VanillaMax) continue;

                ILUtil.ReplaceWithCall(list[i], typeof(CreateBudgetModule), nameof(GetMaxCreatedPerFrame));
                matches++;
            }

            if (matches != 1)
            {
                var msg = "SmoothServer CreateBudget transpiler: expected exactly 1x " + VanillaMax +
                          " in ZNetScene.CreateObjects, found " + matches +
                          " - game IL changed, refusing to patch";
                SmoothServerPlugin.Log.LogError(msg);
                throw new Exception(msg);
            }

            SmoothServerPlugin.Log.LogInfo("[CreateBudget] transpiler OK: 1x maxCreatedPerFrame replaced (assertion 1 passed)");
            return list;
        }
    }
}
