using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>
    /// Raised by a module that finds another mod has already transpiled the method it wants.
    /// This is NOT a failure: it means the method is somebody else's now, so we stand down
    /// rather than fight over the same IL. <see cref="FeatureModule.TryEnable(string, ModuleSide)"/>
    /// turns it into the status <c>disabled(conflict)</c> and a single warning line, where any
    /// other exception becomes <c>FAILED(reason)</c> and an error.
    /// </summary>
    internal sealed class TranspilerConflictException : Exception
    {
        internal TranspilerConflictException(string message) : base(message) { }
    }

    /// <summary>
    /// Small Harmony transpiler helpers shared by the modules that swap vanilla int literals for
    /// a configurable call (SendBudget, CreateBudget, ...). Not a module itself — no side, no
    /// config section, nothing to enable/disable.
    ///
    /// <b>Transpiler coexistence (0.5.2, issue #2).</b> Harmony chains transpilers: the second
    /// one to be registered on a method is handed the FIRST one's output, not the game's IL. Our
    /// literal-swapping modules therefore cannot tell "Iron Gate changed the method" from
    /// "another mod already rewrote these exact constants" by counting literals alone - both look
    /// like `found 0`. <see cref="OtherTranspilersOn"/> asks Harmony who else is on the method and
    /// lets the module say which of the two it is: a foreign transpiler means stand down quietly
    /// (<see cref="RequireSolePatcher"/> / <see cref="StandDown"/>), no foreign transpiler means
    /// the strict assertion still fires exactly as before.
    /// </summary>
    internal static class ILUtil
    {
        private static readonly string[] NoOwners = new string[0];

        // ---- transpiler coexistence -------------------------------------------------------

        /// <summary>
        /// Harmony ids of every OTHER owner with a transpiler already registered on
        /// <paramref name="target"/>. Empty when we are the only one, which is the normal case.
        /// Never throws: if Harmony cannot be asked, we report "nobody else" and the caller's
        /// strict assertion keeps its old behaviour.
        /// </summary>
        internal static string[] OtherTranspilersOn(MethodBase target, string ownId)
        {
            if (target == null) return NoOwners;
            List<string> found = null;
            try
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null || info.Transpilers == null) return NoOwners;
                foreach (var p in info.Transpilers)
                {
                    if (p == null) continue;
                    var owner = p.owner;
                    if (string.IsNullOrEmpty(owner) || owner == ownId) continue;
                    if (found == null) found = new List<string>();
                    if (!found.Contains(owner)) found.Add(owner);
                }
            }
            catch (Exception e)
            {
                SmoothServerPlugin.Log.LogWarning("could not read Harmony patch info for " +
                                                  Describe(target) + ": " + e.Message);
                return NoOwners;
            }

            if (found == null) return NoOwners;
            found.Sort(StringComparer.Ordinal);
            return found.ToArray();
        }

        /// <summary>"ZDOMan.SendZDOs" - for log lines, not for reflection.</summary>
        internal static string Describe(MethodBase target)
        {
            if (target == null) return "<unknown method>";
            var t = target.DeclaringType;
            return (t == null ? "?" : t.Name) + "." + target.Name;
        }

        /// <summary>The one wording every module uses when it stands down. Kept in one place so
        /// the log line is identical whichever module hits it.</summary>
        internal static string ConflictMessage(string moduleName, string section, MethodBase target,
                                               string[] owners)
        {
            var who = (owners == null || owners.Length == 0) ? "unknown" : string.Join(", ", owners);
            return "[" + moduleName + "] another mod already changed " + Describe(target) +
                   " (owners: " + who + ") - " + moduleName +
                   " left off so the two do not fight; set [" + section +
                   "] Enabled=false to silence this";
        }

        /// <summary>
        /// Call from ApplyPatches BEFORE Harmony.Patch. Throws
        /// <see cref="TranspilerConflictException"/> - i.e. status <c>disabled(conflict)</c> - when
        /// somebody else already transpiles this method.
        /// </summary>
        internal static void RequireSolePatcher(MethodBase target, string ownId, string moduleName,
                                                string section)
        {
            var owners = OtherTranspilersOn(target, ownId);
            if (owners.Length == 0) return;
            throw new TranspilerConflictException(ConflictMessage(moduleName, section, target, owners));
        }

        /// <summary>
        /// Call from INSIDE a transpiler that found its literals missing while another mod's
        /// transpiler is on the method - the late case, where a third mod patched the method
        /// after us and Harmony re-ran the whole chain over their output. Logs the one warning and
        /// flips the module to <c>disabled(conflict)</c>; the caller must then hand the
        /// instructions back completely unmodified.
        /// </summary>
        internal static void StandDown(string moduleName, string section, MethodBase target,
                                       string[] owners)
        {
            SmoothServerPlugin.Log.LogWarning(ConflictMessage(moduleName, section, target, owners));
            SmoothServerPlugin.MarkConflict(moduleName);
        }

        /// <summary>
        /// Digs a <see cref="TranspilerConflictException"/> out of whatever Harmony wrapped it in
        /// (HarmonyException -> TargetInvocationException -> ours). Null when this really is a
        /// failure.
        /// </summary>
        internal static TranspilerConflictException FindConflict(Exception e)
        {
            for (int depth = 0; e != null && depth < 8; depth++)
            {
                var c = e as TranspilerConflictException;
                if (c != null) return c;
                e = e.InnerException;
            }
            return null;
        }

        /// <summary>True if the instruction pushes an int32 constant; yields its value.</summary>
        internal static bool TryGetI4(CodeInstruction ci, out int value)
        {
            value = 0;
            if (ci == null || ci.operand == null) return false;
            if (ci.opcode == OpCodes.Ldc_I4 || ci.opcode == OpCodes.Ldc_I4_S)
            {
                try { value = Convert.ToInt32(ci.operand); return true; }
                catch { return false; }
            }
            return false;
        }

        /// <summary>
        /// Turn a constant-load into a call to a static int-returning method, IN PLACE so the
        /// instruction keeps its labels and exception blocks.
        /// </summary>
        internal static void ReplaceWithCall(CodeInstruction ci, Type owner, string method)
        {
            var mi = AccessTools.Method(owner, method);
            if (mi == null)
                throw new Exception("SmoothServer: replacement method " + owner.Name + "." + method + " not found");
            ci.opcode = OpCodes.Call;
            ci.operand = mi;
        }

        /// <summary>True if the instruction pushes a float32 constant; yields its value.</summary>
        internal static bool TryGetR4(CodeInstruction ci, out float value)
        {
            value = 0f;
            if (ci == null || ci.operand == null) return false;
            if (ci.opcode != OpCodes.Ldc_R4) return false;
            try { value = Convert.ToSingle(ci.operand); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Turn the constant-load at <paramref name="index"/> into <c>this</c> + a call to a
        /// static one-argument method, i.e. <c>ldc.r4 0.2</c> becomes
        /// <c>ldarg.0; call Hook(ZSyncTransform)</c>. A <c>ldarg.0</c> is INSERTED, so callers
        /// must walk their list backwards to keep the indices they have not visited valid.
        ///
        /// Any labels on the original instruction move to the inserted one - otherwise a
        /// branch to that label would jump straight to the call and leave the stack one
        /// argument short. An instruction that also carries an exception-block marker is
        /// refused rather than guessed at.
        /// </summary>
        internal static void ReplaceWithThisCall(List<CodeInstruction> list, int index,
                                                 Type owner, string method)
        {
            var ci = list[index];
            if (ci.blocks != null && ci.blocks.Count > 0)
                throw new Exception("SmoothServer: constant at IL index " + index +
                                    " starts an exception block - refusing to rewrite it");

            var mi = AccessTools.Method(owner, method);
            if (mi == null)
                throw new Exception("SmoothServer: replacement method " + owner.Name + "." +
                                    method + " not found");

            var ldarg0 = new CodeInstruction(OpCodes.Ldarg_0);
            ldarg0.labels.AddRange(ci.labels);
            ci.labels.Clear();

            ci.opcode = OpCodes.Call;
            ci.operand = mi;
            list.Insert(index, ldarg0);
        }
    }
}
