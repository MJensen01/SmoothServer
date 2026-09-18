using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>
    /// Headless unit tests for the literal-swapping transpilers, in MapSelfTest's spirit: no
    /// game, no Harmony, no network - each module's transpiler body is run against a synthetic
    /// instruction list and the result is asserted.
    ///
    /// What it exists to prove (issue #2). Harmony CHAINS transpilers: the second one registered
    /// on a method is handed the first one's output. Several networking mods rewrite exactly the
    /// two 10240 literals in ZDOMan.SendZDOs, so after one of them runs our scan finds
    /// "0 and 1" - the same numbers a game update would produce. Until 0.5.2 that threw, the
    /// module reported FAILED, and the log blamed Iron Gate. The cases below pin the new
    /// behaviour down in both directions:
    ///
    ///   * foreign transpiler present + literals gone -> hand the IL BACK UNCHANGED (their
    ///     rewrite must survive us byte for byte) and report the conflict. Never throw.
    ///   * no foreign transpiler + literals gone      -> still throw. A game IL change must stay
    ///     loud; this is the assertion that has caught every Valheim patch so far.
    ///   * foreign transpiler present + literals intact -> patch normally. Somebody else being
    ///     on the method is not, by itself, a reason to skip work we can still do correctly.
    ///
    /// The startup gate for the same thing (ILUtil.RequireSolePatcher, which stands the module
    /// down BEFORE it ever patches) needs a live Harmony instance and is not covered here.
    /// </summary>
    internal sealed class ILSelfTestModule : FeatureModule
    {
        public override string Name => "ILSelfTest";
        public override ModuleSide Side => ModuleSide.Both;
        public override bool DefaultEnabled => true;
        protected override string EnabledDescription =>
            "Run the transpiler-coexistence unit tests at startup and log one PASS/FAIL line. " +
            "Pure data, no patches, no gameplay effect.";

        private string _result = "not run";

        protected override void ApplyPatches()
        {
            var fails = new List<string>();
            int cases = 0;

            SendBudgetModule.Quiet = true;   // synthetic lists: no error/warning lines, no log-once flag consumed
            try
            {
                // --- 1. the reporter's case: another mod took both 10240s, the 2048 remains ----
                cases++;
                var mangled = ForeignRewriteOfSendZDOs();
                var before = Snapshot(mangled);
                SendBudgetModule.Outcome o1;
                var after = SendBudgetModule.Rewrite(mangled, true, out o1);
                if (o1 != SendBudgetModule.Outcome.StoodDown) fails.Add("SendBudget: foreign IL did not report a conflict");
                if (!ReferenceEquals(after, mangled)) fails.Add("SendBudget: conflict path returned a different list");
                if (!Same(before, Snapshot(after)))
                    fails.Add("SendBudget: conflict path MODIFIED the other mod's IL");

                // --- 2. same IL, nobody else on the method: IL mismatch, untouched, no throw ---
                cases++;
                var mangled2 = ForeignRewriteOfSendZDOs();
                var before2 = Snapshot(mangled2);
                SendBudgetModule.Outcome o2;
                var after2 = SendBudgetModule.Rewrite(mangled2, false, out o2);
                if (o2 != SendBudgetModule.Outcome.Mismatch) fails.Add("SendBudget: unrecognised IL was not reported as a mismatch (" + o2 + ")");
                if (!ReferenceEquals(after2, mangled2) || !Same(before2, Snapshot(after2)))
                    fails.Add("SendBudget: mismatch path MODIFIED the IL");
                if (SendBudgetModule.Detail == null || SendBudgetModule.Detail.IndexOf("found 0x 10240 and 1x 2048", StringComparison.Ordinal) < 0)
                    fails.Add("SendBudget: mismatch detail wrong: " + SendBudgetModule.Detail);

                // --- 3. vanilla IL, nobody else: the normal 2/1 swap -------------------------
                cases++;
                var vanilla = VanillaSendZDOs();
                SendBudgetModule.Outcome o3;
                SendBudgetModule.Rewrite(vanilla, false, out o3);
                if (o3 != SendBudgetModule.Outcome.Rewritten) fails.Add("SendBudget: clean vanilla IL not rewritten (" + o3 + ")");
                if (SendBudgetModule.DetectedVanillaHighWater != 10240) fails.Add("SendBudget: vanilla literal misdetected as " + SendBudgetModule.DetectedVanillaHighWater);
                int hi = CountCalls(vanilla, typeof(SendBudgetModule), "GetHighWaterBytes");
                int min = CountCalls(vanilla, typeof(SendBudgetModule), "GetMinChunkBytes");
                if (hi != 2 || min != 1) fails.Add("SendBudget: swapped " + hi + "/" + min + ", wanted 2/1");

                // --- 4. foreign transpiler elsewhere, our literals intact: patch anyway -------
                cases++;
                var vanilla2 = VanillaSendZDOs();
                SendBudgetModule.Outcome o4;
                SendBudgetModule.Rewrite(vanilla2, true, out o4);
                if (o4 != SendBudgetModule.Outcome.Rewritten) fails.Add("SendBudget: stood down although its literals were all present");
                if (CountCalls(vanilla2, typeof(SendBudgetModule), "GetHighWaterBytes") != 2)
                    fails.Add("SendBudget: did not patch with a foreign transpiler present");

                // --- 4b. hex-patched binary (issue #2, the G-Portal file): 2x 30720 + 1x 2048 ---
                // Taken over exactly like vanilla, and the raised literal is remembered.
                foreach (int raised in new[] { 30720, 61440 })
                {
                    cases++;
                    var patched = PrePatchedSendZDOs(raised);
                    SendBudgetModule.Outcome op;
                    SendBudgetModule.Rewrite(patched, false, out op);
                    if (op != SendBudgetModule.Outcome.Rewritten) fails.Add("SendBudget: pre-patched " + raised + " not taken over (" + op + ")");
                    if (CountCalls(patched, typeof(SendBudgetModule), "GetHighWaterBytes") != 2 ||
                        CountCalls(patched, typeof(SendBudgetModule), "GetMinChunkBytes") != 1)
                        fails.Add("SendBudget: pre-patched " + raised + " swapped wrong counts");
                    if (SendBudgetModule.DetectedVanillaHighWater != raised)
                        fails.Add("SendBudget: pre-patched literal recorded as " + SendBudgetModule.DetectedVanillaHighWater + ", wanted " + raised);
                }

                // --- 4c. shapes that must NOT be taken over: one 10240 only; extra literal ----
                cases++;
                var oneOnly = VanillaSendZDOs();
                oneOnly.RemoveAt(3);                       // drop the second 10240
                var oneBefore = Snapshot(oneOnly);
                SendBudgetModule.Outcome o4c;
                SendBudgetModule.Rewrite(oneOnly, false, out o4c);
                if (o4c != SendBudgetModule.Outcome.Mismatch || !Same(oneBefore, Snapshot(oneOnly)))
                    fails.Add("SendBudget: 1x 10240 + 1x 2048 was not a clean mismatch (" + o4c + ")");

                cases++;
                var extra = PrePatchedSendZDOs(30720);
                extra.Insert(1, new CodeInstruction(OpCodes.Ldc_I4, 4096));
                var extraBefore = Snapshot(extra);
                SendBudgetModule.Outcome o4d;
                SendBudgetModule.Rewrite(extra, false, out o4d);
                if (o4d != SendBudgetModule.Outcome.Mismatch || !Same(extraBefore, Snapshot(extra)))
                    fails.Add("SendBudget: 2x 30720 + 2048 + a stray 4096 was taken over (" + o4d + ")");

                // --- 4d. the same wrong shape with a foreign transpiler present: conflict, not mismatch
                cases++;
                var foreignWrong = PrePatchedSendZDOs(30720);
                foreignWrong.Insert(1, new CodeInstruction(OpCodes.Ldc_I4, 4096));
                SendBudgetModule.Outcome o4e;
                SendBudgetModule.Rewrite(foreignWrong, true, out o4e);
                if (o4e != SendBudgetModule.Outcome.StoodDown)
                    fails.Add("SendBudget: wrong shape + foreign transpiler did not stand down (" + o4e + ")");

                // the real patch runs after this module (alphabetical order) and sets these itself;
                // leave them as vanilla so nothing above leaks into the summary if it does not.
                SendBudgetModule.DetectedVanillaHighWater = 10240;
                SendBudgetModule.Detail = null;
                SendBudgetModule.Quiet = false;

                // --- 5. the same contract in a second module ---------------------------------
                cases++;
                var create = ForeignRewriteOfCreateObjects();
                var createBefore = Snapshot(create);
                bool sd5;
                var createAfter = CreateBudgetModule.Rewrite(create, true, out sd5);
                if (!sd5) fails.Add("CreateBudget: foreign IL did not report a conflict");
                if (!Same(createBefore, Snapshot(createAfter)))
                    fails.Add("CreateBudget: conflict path MODIFIED the other mod's IL");

                cases++;
                var create2 = VanillaCreateObjects();
                bool sd6;
                CreateBudgetModule.Rewrite(create2, false, out sd6);
                if (sd6 || CountCalls(create2, typeof(CreateBudgetModule), "GetMaxCreatedPerFrame") != 1)
                    fails.Add("CreateBudget: clean vanilla IL was not patched");

                // --- 6. the hit-latency modules: packet classification, drain ordering and the
                //        CombatOwnership guards, all as pure functions over synthetic inputs -----
                cases += HitLatencySelfTest.Run(fails);

                // --- 7. the live Harmony half: does GetPatchInfo really see a rival? ----------
                // Everything above is arithmetic on a synthetic list. This one patches a dummy
                // method of our own with a second Harmony id and asks ILUtil the same question
                // the modules ask at startup, so the detection itself is exercised for real.
                cases++;
                fails.AddRange(LiveDetectionCase());
            }
            catch (Exception e)
            {
                fails.Add("threw: " + e.Message);
            }
            finally { SendBudgetModule.Quiet = false; }

            if (fails.Count == 0)
            {
                _result = "PASS (" + cases + " cases)";
                Log.LogInfo("[ILSelfTest] PASS - " + cases + " transpiler-coexistence and hit-latency cases");
            }
            else
            {
                _result = "FAIL: " + string.Join("; ", fails.ToArray());
                Log.LogError("[ILSelfTest] FAIL - " + _result);
            }
        }

        public override string StatusDetail() { return _result; }

        // ---- live Harmony detection ------------------------------------------------------------

        private const string RivalId = "SmoothServer.ILSelfTest.pretend-rival";

        /// <summary>
        /// Stand-in for a foreign mod's transpiler: patches ProbeTarget() under a DIFFERENT
        /// Harmony id, then checks that ILUtil sees it from anybody else's point of view, does not
        /// see it from its own, and that RequireSolePatcher turns it into a conflict rather than a
        /// failure. Always unpatches itself.
        /// </summary>
        private static List<string> LiveDetectionCase()
        {
            var fails = new List<string>();
            var target = AccessTools.Method(typeof(ILSelfTestModule), nameof(ProbeTarget));
            if (target == null) { fails.Add("live: ProbeTarget not found"); return fails; }

            var rival = new Harmony(RivalId);
            try
            {
                if (ILUtil.OtherTranspilersOn(target, "whoever").Length != 0)
                    fails.Add("live: reported a transpiler on an unpatched method");

                rival.Patch(target, transpiler: new HarmonyMethod(typeof(ILSelfTestModule), nameof(NoOpTranspiler)));

                var seen = ILUtil.OtherTranspilersOn(target, "Nosferatu.SmoothServer.SendBudget");
                if (Array.IndexOf(seen, RivalId) < 0)
                    fails.Add("live: did not see the rival transpiler (saw " + seen.Length + " owners)");
                if (ILUtil.OtherTranspilersOn(target, RivalId).Length != 0)
                    fails.Add("live: counted our own transpiler as a rival");

                bool conflicted = false;
                try { ILUtil.RequireSolePatcher(target, "Nosferatu.SmoothServer.SendBudget", "SendBudget", "SendBudget"); }
                catch (TranspilerConflictException e)
                {
                    conflicted = true;
                    if (e.Message.IndexOf(RivalId, StringComparison.Ordinal) < 0)
                        fails.Add("live: conflict message does not name the owner: " + e.Message);
                    if (ILUtil.FindConflict(new Exception("wrapped", e)) == null)
                        fails.Add("live: FindConflict could not unwrap a nested conflict");
                }
                catch (Exception e) { fails.Add("live: wrong exception type " + e.GetType().Name); }
                if (!conflicted) fails.Add("live: RequireSolePatcher did not raise a conflict");
            }
            catch (Exception e)
            {
                fails.Add("live: threw " + e.Message);
            }
            finally
            {
                try { rival.UnpatchSelf(); } catch { /* best effort - it is a dummy method */ }
            }
            return fails;
        }

        /// <summary>Dummy patch target. Exists only so the case above has something to patch.</summary>
        internal static int ProbeTarget() { return 1; }

        private static IEnumerable<CodeInstruction> NoOpTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions;
        }

        // ---- fixtures ------------------------------------------------------------------------

        /// <summary>ZDOMan.SendZDOs' shape as far as our scan cares: 2x 10240, 1x 2048.</summary>
        private static List<CodeInstruction> VanillaSendZDOs()
        {
            return new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldc_I4, 10240),
                new CodeInstruction(OpCodes.Bgt),
                new CodeInstruction(OpCodes.Ldc_I4, 10240),
                new CodeInstruction(OpCodes.Sub),
                new CodeInstruction(OpCodes.Ldc_I4, 2048),
                new CodeInstruction(OpCodes.Blt),
                new CodeInstruction(OpCodes.Ret)
            };
        }

        /// <summary>Vanilla's method after the old "network fix" hex patch: both 10240 operands raised, 2048 untouched.</summary>
        private static List<CodeInstruction> PrePatchedSendZDOs(int raised)
        {
            var list = VanillaSendZDOs();
            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (ILUtil.TryGetI4(list[i], out v) && v == 10240) list[i].operand = raised;
            }
            return list;
        }

        /// <summary>
        /// The same method after another networking mod's transpiler replaced both high-water
        /// literals with its own call and left vanilla's 2048 alone - i.e. exactly the
        /// "found 0 and 1" of issue #2.
        /// </summary>
        private static List<CodeInstruction> ForeignRewriteOfSendZDOs()
        {
            var foreign = AccessTools.Method(typeof(ILSelfTestModule), nameof(PretendOtherModsBudget));
            var list = VanillaSendZDOs();
            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v) || v != 10240) continue;
                list[i].opcode = OpCodes.Call;
                list[i].operand = foreign;
            }
            return list;
        }

        private static List<CodeInstruction> VanillaCreateObjects()
        {
            return new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)10),
                new CodeInstruction(OpCodes.Stloc_0),
                new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)100),
                new CodeInstruction(OpCodes.Ret)
            };
        }

        private static List<CodeInstruction> ForeignRewriteOfCreateObjects()
        {
            var foreign = AccessTools.Method(typeof(ILSelfTestModule), nameof(PretendOtherModsBudget));
            var list = VanillaCreateObjects();
            list[0].opcode = OpCodes.Call;
            list[0].operand = foreign;
            return list;
        }

        /// <summary>Stands in for the other mod's replacement call in the fixtures above.</summary>
        internal static int PretendOtherModsBudget() { return 32768; }

        // ---- assertions ----------------------------------------------------------------------

        private static string[] Snapshot(List<CodeInstruction> list)
        {
            var a = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                a[i] = list[i].opcode + "|" + (list[i].operand == null ? "<null>" : list[i].operand.ToString());
            return a;
        }

        private static bool Same(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Compared by name + declaring type, not by MethodInfo identity: two AccessTools.Method
        // lookups of the same method are not guaranteed to hand back the same object.
        private static int CountCalls(List<CodeInstruction> list, Type owner, string method)
        {
            int n = 0;
            foreach (var ci in list)
            {
                if (ci.opcode != OpCodes.Call) continue;
                var mi = ci.operand as MethodInfo;
                if (mi != null && mi.DeclaringType == owner && mi.Name == method) n++;
            }
            return n;
        }
    }
}
