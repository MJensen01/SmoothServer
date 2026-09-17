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
    /// Since 0.5.3 (issue #2, part two) a binary whose two high-water literals were already
    /// raised - the host's assembly_valheim.dll hex-patched by one of the old "network fix"
    /// guides (10240 -> 30720 is the common one; the reporter's file was byte-for-byte Steam's
    /// 1.0.14 except those two operands) - is recognised and taken over: the raised value is
    /// swapped for our call exactly like vanilla's, remembered in DetectedVanillaHighWater and
    /// named in the log and the module summary. IL that is neither vanilla nor that shape, with
    /// nobody else on the method, no longer throws out of the transpiler (which failed the
    /// whole Harmony patch and printed a wall of stack): the method is handed back untouched,
    /// the module reports disabled(IL mismatch), and one error block lists every int literal it
    /// saw plus the game version, assembly MVID, size and md5 so the report can be answered.
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

            if (Status == "disabled(IL mismatch)")
            {
                // The transpiler ran inside Harmony.Patch and found IL it does not know; it has
                // already logged the dump and left the method untouched. Not a failure of ours.
                Active = false;
                return;
            }
            Active = true;
            Log.LogInfo("[SendBudget] highWater=" + HighWaterBytes + "B minChunk=" + MinChunkBytes +
                        "B (binary literal " + DetectedVanillaHighWater + ")");
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

        /// <summary>What <see cref="Rewrite"/> did with the instruction list it was given.</summary>
        internal enum Outcome
        {
            /// <summary>Both high-water loads and the min-chunk load were swapped for our calls.</summary>
            Rewritten,
            /// <summary>Another mod's transpiler owns the method and its numbers are in it: left untouched, disabled(conflict).</summary>
            StoodDown,
            /// <summary>Nobody else is on the method and the IL is still not one we recognise: left untouched, disabled(IL mismatch).</summary>
            Mismatch
        }

        /// <summary>
        /// The high-water literal this game binary actually carries in ZDOMan.SendZDOs. 10240 on
        /// every build Iron Gate has shipped; something else (30720, 61440...) when the host's
        /// assembly_valheim.dll was hex-patched by one of the old "network fix" guides. Set by the
        /// transpiler; read by the status line so the summary names it.
        /// </summary>
        internal static int DetectedVanillaHighWater = VanillaHighWater;

        /// <summary>One line for the module summary: what the transpiler found, when it was not plain vanilla.</summary>
        internal static string Detail;

        private static bool _prepatchedLogged;

        /// <summary>ILSelfTest runs Rewrite() on synthetic lists at startup; while set, nothing is logged and the log-once flag is untouched.</summary>
        internal static bool Quiet;

        public override string StatusDetail() { return Detail; }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            try
            {
                var owners = ILUtil.OtherTranspilersOn(_target, _ownId);
                Outcome outcome;
                var result = Rewrite(list, owners.Length > 0, out outcome);
                switch (outcome)
                {
                    case Outcome.StoodDown:
                        ILUtil.StandDown(ModuleName, ModuleName, _target, owners);
                        break;
                    case Outcome.Mismatch:
                        MarkMismatch();
                        break;
                    default:
                        SmoothServerPlugin.Log.LogInfo("[SendBudget] transpiler OK: 2x highWater (binary literal " +
                                                       DetectedVanillaHighWater + "), 1x minChunk replaced (assertion 2/1 passed)");
                        break;
                }
                return result;
            }
            catch (Exception e)
            {
                // A transpiler that throws takes the whole Harmony patch down with a wall of
                // stack trace and the module ends up FAILED. Nothing in here is worth that:
                // hand vanilla's instructions back untouched and say why.
                Detail = "transpiler threw: " + e.Message;
                SmoothServerPlugin.Log.LogError("[SendBudget] transpiler threw, leaving ZDOMan.SendZDOs untouched: " + e);
                MarkMismatch();
                return list;
            }
        }

        private static void MarkMismatch()
        {
            Active = false;
            SmoothServerPlugin.MarkStatus(ModuleName, "disabled(IL mismatch)");
        }

        /// <summary>
        /// The transpiler body, separated from Harmony so ILSelfTest can run it on a synthetic
        /// instruction list. COUNTS first and only rewrites once it knows what it is looking at,
        /// so every non-rewrite path hands the caller's own list back byte-for-byte untouched -
        /// on the stand-down path the other mod's rewrite must survive intact.
        ///
        /// Three shapes are recognised:
        ///   vanilla      2x 10240 + 1x 2048               -> rewritten (the normal case)
        ///   pre-patched  2x V + 1x 2048, nothing else, V > 2048 and V != 10240
        ///                                                  -> rewritten, V remembered and logged
        ///                (issue #2: a host's assembly_valheim.dll hex-patched by an old "network
        ///                fix" guide - byte-for-byte Steam's build except the two 10240 operands)
        ///   anything else, with another mod's transpiler on the method -> StoodDown
        ///   anything else, nobody else on the method      -> Mismatch, with a dump of what we saw
        /// Never throws for an IL shape; the Harmony wrapper above catches anything unexpected.
        /// </summary>
        internal static List<CodeInstruction> Rewrite(List<CodeInstruction> list, bool foreignTranspiler,
                                                      out Outcome outcome)
        {
            int hiMatches = 0, minMatches = 0;
            var others = new Dictionary<int, int>();     // literal value -> occurrences
            var literals = new List<string>();           // "ldc.i4 30720@3" for the dump
            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;
                literals.Add(list[i].opcode + " " + v + "@" + i);
                if (v == VanillaHighWater) hiMatches++;
                else if (v == VanillaMinChunk) minMatches++;
                else
                {
                    int n;
                    others.TryGetValue(v, out n);
                    others[v] = n + 1;
                }
            }

            int highWater = VanillaHighWater;
            if (hiMatches == 2 && minMatches == 1)
            {
                // Vanilla. Unchanged since 0.1: other literals in the method are none of our business.
            }
            else if (foreignTranspiler)
            {
                outcome = Outcome.StoodDown;
                return list;
            }
            else if (hiMatches == 0 && minMatches == 1 && others.Count == 1)
            {
                int v = 0, n = 0;
                foreach (var kv in others) { v = kv.Key; n = kv.Value; }
                if (n == 2 && v > VanillaMinChunk)
                {
                    highWater = v;
                }
                else
                {
                    outcome = Outcome.Mismatch;
                    ReportMismatch(hiMatches, minMatches, literals, list.Count);
                    return list;
                }
            }
            else
            {
                outcome = Outcome.Mismatch;
                ReportMismatch(hiMatches, minMatches, literals, list.Count);
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;
                if (v == highWater)
                    ILUtil.ReplaceWithCall(list[i], typeof(SendBudgetModule), nameof(GetHighWaterBytes));
                else if (v == VanillaMinChunk)
                    ILUtil.ReplaceWithCall(list[i], typeof(SendBudgetModule), nameof(GetMinChunkBytes));
            }

            DetectedVanillaHighWater = highWater;
            if (highWater != VanillaHighWater)
            {
                Detail = "binary's send-queue high-water literal is " + highWater + " (vanilla 10240; hex-patched assembly_valheim.dll) - taken over";
                if (!_prepatchedLogged && !Quiet)
                {
                    _prepatchedLogged = true;
                    SmoothServerPlugin.Log.LogWarning("[SendBudget] this game binary's send-queue high-water literal is " + highWater +
                        ", not vanilla's 10240 - assembly_valheim.dll was hex-patched (the old \"network fix\"?). " +
                        "Taking it over: [SendBudget] HighWaterBytes=" + HighWaterBytes + " applies from here on, the patched value no longer matters");
                }
            }
            else Detail = null;

            outcome = Outcome.Rewritten;
            return list;
        }

        private static void ReportMismatch(int hi, int min, List<string> literals, int count)
        {
            Detail = "found " + hi + "x 10240 and " + min + "x 2048 in ZDOMan.SendZDOs (" + count + " instructions; literals: " +
                     (literals.Count == 0 ? "none" : string.Join(", ", literals.ToArray())) + ")";
            if (Quiet) return;
            SmoothServerPlugin.Log.LogError("[SendBudget] ZDOMan.SendZDOs is not IL this version knows: expected 2x " + VanillaHighWater +
                " and 1x " + VanillaMinChunk + " (or 2x one raised value and 1x " + VanillaMinChunk + "), " + Detail +
                ". SendBudget left off (disabled(IL mismatch)); everything else keeps running. " +
                "Please paste this block into a GitHub issue. " + DescribeGameAssembly());
        }

        /// <summary>Which assembly_valheim.dll this is, for the mismatch report. Never throws.</summary>
        internal static string DescribeGameAssembly()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var asm = typeof(ZDOMan).Assembly;
                sb.Append("game ");
                try { sb.Append(Version.GetVersionString()); } catch { sb.Append("(version n/a)"); }
                sb.Append("; assembly_valheim mvid=").Append(asm.ManifestModule.ModuleVersionId);
                string path = null;
                try { path = asm.Location; } catch { }
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    var fi = new System.IO.FileInfo(path);
                    sb.Append(" size=").Append(fi.Length).Append("B modified=").Append(fi.LastWriteTimeUtc.ToString("u"));
                    try
                    {
                        using (var md5 = System.Security.Cryptography.MD5.Create())
                        using (var fs = System.IO.File.OpenRead(path))
                            sb.Append(" md5=").Append(BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant());
                    }
                    catch (Exception e) { sb.Append(" md5=(").Append(e.GetType().Name).Append(")"); }
                }
                else sb.Append(" (no file location)");
            }
            catch (Exception e) { sb.Append("(describe failed: ").Append(e.Message).Append(")"); }
            return sb.ToString();
        }
    }
}
