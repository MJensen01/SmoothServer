using System;
using System.Reflection.Emit;
using HarmonyLib;

namespace SmoothServer
{
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
    }
}
