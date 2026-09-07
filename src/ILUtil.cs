using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace SmoothServer
{
    /// <summary>
    /// Small Harmony transpiler helpers shared by the modules that swap vanilla int literals for
    /// a configurable call (SendBudget, CreateBudget, ...). Not a module itself — no side, no
    /// config section, nothing to enable/disable.
    /// </summary>
    internal static class ILUtil
    {
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
