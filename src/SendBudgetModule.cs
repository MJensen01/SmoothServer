using System;
using System.Collections.Generic;
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
    /// GetHighWaterBytes()/GetMinChunkBytes() are invoked fresh from the patched IL on every
    /// call, but they read the static HighWaterBytes/MinChunkBytes fields rather than the
    /// ConfigEntry directly (a transpiled IL call target must be a plain static method with no
    /// captured state) - so a live config edit only takes effect once OnConfigChanged below
    /// refreshes those fields.
    /// </summary>
    internal sealed class SendBudgetModule : FeatureModule
    {
        public override string Name => "SendBudget";

        private const int VanillaHighWater = 10240;
        private const int VanillaMinChunk = 2048;

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
            return HighWaterBytes;
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
            var list = new List<CodeInstruction>(instructions);
            int hiMatches = 0, minMatches = 0;

            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;

                if (v == VanillaHighWater)
                {
                    ILUtil.ReplaceWithCall(list[i], typeof(SendBudgetModule), nameof(GetHighWaterBytes));
                    hiMatches++;
                }
                else if (v == VanillaMinChunk)
                {
                    ILUtil.ReplaceWithCall(list[i], typeof(SendBudgetModule), nameof(GetMinChunkBytes));
                    minMatches++;
                }
            }

            if (hiMatches != 2 || minMatches != 1)
            {
                var msg = "SmoothServer SendBudget transpiler: expected exactly 2x " + VanillaHighWater +
                          " and 1x " + VanillaMinChunk + " in ZDOMan.SendZDOs, found " +
                          hiMatches + " and " + minMatches +
                          " - game IL changed, refusing to patch";
                SmoothServerPlugin.Log.LogError(msg);
                throw new Exception(msg);
            }

            SmoothServerPlugin.Log.LogInfo("[SendBudget] transpiler OK: " + hiMatches + "x highWater, " +
                                       minMatches + "x minChunk replaced (assertion 2/1 passed)");
            return list;
        }
    }
}
