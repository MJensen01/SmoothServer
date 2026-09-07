using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>Which non-owned objects SmoothMotion's knobs apply to.</summary>
    public enum MotionTargets
    {
        /// <summary>Only objects that carry a Character component (players, mobs). Default.</summary>
        Characters,

        /// <summary>Every non-owned ZSyncTransform: characters, carts, ships, dropped items...</summary>
        All
    }

    /// <summary>
    /// M13 (client) - remote-object motion, off by default.
    ///
    /// <b>What vanilla actually does</b> (0.221.12, decompiled - the guesses in the brief were
    /// wrong and are worth writing down):
    ///
    /// ZSyncTransform is not a MonoBehaviour that ticks itself. It implements IMonoUpdater and a
    /// single MonoUpdaters component drives every instance: CustomFixedUpdate -> ClientSync(dt)
    /// with dt = Time.fixedDeltaTime for objects we do NOT own, CustomLateUpdate -> OwnerSync()
    /// for the ones we do. ClientSync's first line is `if (zDO.IsOwner()) return;` - that is the
    /// non-owned gate, and it is vanilla's, not ours.
    ///
    /// ClientSync delegates the position maths to the private SyncPosition(zdo, dt, out bool),
    /// whose tail (the path an ordinary character takes) is:
    ///
    ///     if (zdo.HasOwner()) {
    ///         m_targetPosTimer += dt;
    ///         m_targetPosTimer = Mathf.Min(m_targetPosTimer, 2f);          // &lt;-- cap, ours
    ///         position += zdo.GetVec3(ZDOVars.s_velHash) * m_targetPosTimer;
    ///     }
    ///     float num = Vector3.Distance(transform.position, position);
    ///     if (num &gt; 0.001f)
    ///         transform.position = (num &lt; 5f)
    ///             ? Vector3.Lerp(transform.position, position, 0.2f)        // &lt;-- factor, ours
    ///             : position;                                              //     (&gt;5m snaps)
    ///
    /// So two things the brief expected to have to build already exist:
    ///   * dead reckoning IS in vanilla - the owner writes its velocity to the ZDO key "vel"
    ///     every LateUpdate and every viewer extrapolates along it, for up to a full 2 SECONDS
    ///     since the last ZDO revision. There is nothing to add; there is a cap to tighten.
    ///   * the smoothing constant is a hardcoded 0.2f literal (an unreferenced
    ///     `const float m_smoothnessPos = 0.2f` exists but the call sites carry the literal), and
    ///     it is applied per FixedUpdate WITHOUT dt scaling.
    ///
    /// <b>What this module does.</b> The two literals above are the entire knob surface, so this
    /// is a transpiler on SyncPosition that swaps them for calls - exactly the pattern
    /// CreateBudget and ClientNet use, with the same exact-match-count assertion, so a game patch
    /// that changes the maths makes the module refuse to load instead of silently doing something
    /// else. No behaviour is reimplemented, no vanilla branch is skipped, and both hooks return
    /// the vanilla constant whenever the module is off or the object is out of scope - patched IL
    /// with the module disabled is behaviourally identical to unpatched IL.
    ///
    /// <b>Deviation from the brief, deliberately:</b> ExtrapolateMs defaults to 2000 (vanilla's
    /// 2s cap), not 0. "0 = off" would have been a silent behaviour change the moment anyone
    /// enabled the module, because vanilla extrapolates whether we ask it to or not. 0 still
    /// means "no dead reckoning at all"; the useful settings are in between (150-400ms keeps a
    /// sprinting player honest without the 2-second overshoot that produces the classic
    /// rubber-band snap when someone stops dead behind a rock).
    /// </summary>
    internal sealed class SmoothMotionModule : FeatureModule
    {
        public override string Name => "SmoothMotion";
        public override ModuleSide Side => ModuleSide.Client;
        public override bool DefaultEnabled => false;

        /// <summary>Vanilla's hardcoded position lerp factor inside ZSyncTransform.SyncPosition.</summary>
        private const float VanillaLerp = 0.2f;

        /// <summary>Vanilla's hardcoded extrapolation cap, in seconds.</summary>
        private const float VanillaExtrapolationSeconds = 2f;

        protected override string EnabledDescription =>
            "CLIENT ONLY, OFF BY DEFAULT. Tune how other players' and mobs' movement is " +
            "interpolated on YOUR screen. Purely cosmetic and purely local: it changes nothing " +
            "that is sent, stored or simulated, only how this client draws objects it does not " +
            "own between the updates it receives. Turn it on, play for ten minutes, turn it off " +
            "again if you cannot tell the difference.";

        private ConfigEntry<float> _factor;
        private ConfigEntry<int> _extrapolateMs;
        private ConfigEntry<MotionTargets> _applyTo;

        internal static bool Active2;
        internal static float InterpolationFactor = VanillaLerp;
        internal static float ExtrapolationSeconds = VanillaExtrapolationSeconds;
        internal static MotionTargets ApplyTo = MotionTargets.Characters;

        protected override void Bind()
        {
            _factor = BindLocal("InterpolationFactor", VanillaLerp,
                "How much of the gap to the target position a non-owned object closes on each " +
                "physics tick (50/s). Vanilla 0.2. Higher = snappier and more responsive but " +
                "twitchier on a lossy link; lower = smoother but laggier. This is a fraction per " +
                "tick, not a speed, and it is not scaled by frame time - vanilla's is not either. " +
                "Clamped to 0.05-1.0 (1.0 = no smoothing at all, snap straight to the target). " +
                "Machine-local: it describes YOUR screen, not the server's.");

            _extrapolateMs = BindLocal("ExtrapolateMs", 2000,
                "How far ahead, in milliseconds, a non-owned object may be predicted along its " +
                "last known velocity while no fresh update has arrived. VANILLA IS 2000 - Valheim " +
                "already dead-reckons, this knob only caps it. 0 = no prediction at all (objects " +
                "lag by up to one update but never overshoot); 150-400 is the interesting range " +
                "for a low-latency group: enough to hide one lost packet, not enough to produce " +
                "the rubber-band snap when someone stops dead. Clamped to 0-2000. " +
                "Machine-local.");

            _applyTo = BindLocal("ApplyTo", MotionTargets.Characters,
                "Which non-owned objects the two knobs above apply to. Characters = players and " +
                "mobs only (everything else keeps vanilla's 0.2 / 2000ms exactly). All = every " +
                "synced object, including carts, ships and dropped items. Start with Characters. " +
                "Objects you own are never touched on any setting - vanilla's own " +
                "`if (zdo.IsOwner()) return;` runs first. Machine-local.");
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var target = AccessTools.Method(typeof(ZSyncTransform), "SyncPosition");
            if (target == null)
                throw new Exception("SmoothServer SmoothMotion: ZSyncTransform.SyncPosition not found " +
                                    "- the game's interpolation was rewritten, refusing to patch");

            var pars = target.GetParameters();
            if (pars.Length != 3 || pars[0].ParameterType != typeof(ZDO) || pars[1].ParameterType != typeof(float))
                throw new Exception("SmoothServer SmoothMotion: ZSyncTransform.SyncPosition signature " +
                                    "changed (expected (ZDO, float, out bool), got " + pars.Length +
                                    " params) - refusing to patch");

            Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SmoothMotionModule), nameof(Transpiler)));

            Active2 = true;
            Log.LogInfo("[SmoothMotion] factor=" + InterpolationFactor.ToString("0.###") +
                        (Mathf.Approximately(InterpolationFactor, VanillaLerp) ? " (vanilla)" : "") +
                        " extrapolate=" + (int)(ExtrapolationSeconds * 1000f) + "ms" +
                        (Mathf.Approximately(ExtrapolationSeconds, VanillaExtrapolationSeconds) ? " (vanilla)" : "") +
                        " applyTo=" + ApplyTo);
        }

        public override void Disable()
        {
            Active2 = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == EnabledCfg) { Active2 = Applied && Enabled; return; }
            if (entry != _factor && entry != _extrapolateMs && entry != _applyTo) return;
            ReadConfig();
            Log.LogInfo("[SmoothMotion] factor=" + InterpolationFactor.ToString("0.###") +
                        " extrapolate=" + (int)(ExtrapolationSeconds * 1000f) + "ms applyTo=" + ApplyTo);
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return "factor=" + InterpolationFactor.ToString("0.###") +
                   " extrapolate=" + (int)(ExtrapolationSeconds * 1000f) + "ms applyTo=" + ApplyTo;
        }

        private void ReadConfig()
        {
            InterpolationFactor = Mathf.Clamp(_factor.Value, 0.05f, 1f);
            ExtrapolationSeconds = Mathf.Clamp(_extrapolateMs.Value, 0, 2000) / 1000f;
            ApplyTo = _applyTo.Value;
        }

        // ---- the two hooks the transpiled IL calls -----------------------------------------

        /// <summary>
        /// Replaces vanilla's 0.2f. Returns the vanilla constant unless this client is running
        /// the module AND this object is in scope, so the patched method is byte-equivalent in
        /// behaviour whenever the module is off.
        /// </summary>
        internal static float GetLerpFactor(ZSyncTransform zst)
        {
            if (!InScope(zst)) return VanillaLerp;
            return InterpolationFactor;
        }

        /// <summary>Replaces vanilla's 2f extrapolation cap (seconds).</summary>
        internal static float GetExtrapolationSeconds(ZSyncTransform zst)
        {
            if (!InScope(zst)) return VanillaExtrapolationSeconds;
            return ExtrapolationSeconds;
        }

        private static bool InScope(ZSyncTransform zst)
        {
            if (!Active2 || zst == null) return false;
            if (!ClientActive()) return false;
            if (ApplyTo == MotionTargets.All) return true;
            return zst.m_character != null;
        }

        // ---- transpiler ---------------------------------------------------------------------

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int lerps = 0, caps = 0;

            // Walk backwards: each replacement inserts a `ldarg.0` before the literal, so going
            // backwards keeps the indices of everything not yet visited valid.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                float v;
                if (!ILUtil.TryGetR4(list[i], out v)) continue;

                if (Mathf.Approximately(v, VanillaLerp))
                {
                    ILUtil.ReplaceWithThisCall(list, i, typeof(SmoothMotionModule), nameof(GetLerpFactor));
                    lerps++;
                }
                else if (Mathf.Approximately(v, VanillaExtrapolationSeconds))
                {
                    ILUtil.ReplaceWithThisCall(list, i, typeof(SmoothMotionModule), nameof(GetExtrapolationSeconds));
                    caps++;
                }
            }

            // SyncPosition has two copies of the maths - the parented ("standing on a ship")
            // branch and the free branch - so each literal appears exactly twice.
            if (lerps != 2 || caps != 2)
            {
                var msg = "SmoothServer SmoothMotion transpiler: expected exactly 2x " + VanillaLerp +
                          " and 2x " + VanillaExtrapolationSeconds + " in ZSyncTransform.SyncPosition, found " +
                          lerps + " and " + caps + " - game IL changed (or another mod patched it " +
                          "first), refusing to patch";
                SmoothServerPlugin.Log.LogError(msg);
                throw new Exception(msg);
            }

            SmoothServerPlugin.Log.LogInfo("[SmoothMotion] transpiler OK: 2x lerp factor + 2x " +
                                           "extrapolation cap replaced (assertion 2+2 passed)");
            return list;
        }
    }
}
