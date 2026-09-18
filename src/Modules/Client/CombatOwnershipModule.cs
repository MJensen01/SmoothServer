using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// Layer B Stage 1 of the hit-registration latency work (docs/HIT-LATENCY-PLAN.md,
    /// docs/research/hit-latency/OWNERSHIP.md): **claim on hit, statics only**.
    ///
    /// <b>Why a hit takes ~0.5 s on a dedicated server.</b> Damage is applied only by the ZDO
    /// owner's client. For a cave object that is whichever player happened to be nearest when the
    /// area loaded (`ZDOMan.ReleaseNearbyZDOS`, peer join order) and it is sticky. So a vine you
    /// swing at is simulated on somebody else's PC: `Destructible.Damage` is nothing but
    /// `m_nview.InvokeRPC("RPC_Damage", hit)`, which goes you -> server -> owner; the owner applies
    /// it and its new health comes back owner -> server -> you on the owner's ~20 Hz tick. Two
    /// round trips and two send cadences, each leg behind that peer's socket backlog.
    ///
    /// <b>What this does.</b> A prefix on the six static `Damage(HitData)` methods: if the local
    /// player is the attacker and we are not the owner, call `ZNetView.ClaimOwnership()` before
    /// vanilla runs. `ClaimOwnership` (ZNetView.cs:245-251 -> ZDO.SetOwner) is local, unilateral and
    /// instant - no RPC, no server permission - and `IsOwner()` is true the same frame. Vanilla's
    /// `Damage` then calls `InvokeRoutedRPC` with us as the target, and `ZRoutedRpc.InvokeRoutedRPC`
    /// (1.0.14, lines 120-138) runs it **locally and skips `RouteRPC` entirely**: the damage applies
    /// this frame, the RPC never goes on the wire, and only the health ZDO propagates. One leg
    /// instead of three, and *less* traffic than before. Vanilla uses the same claim-then-write
    /// pattern in 14 places (`Container.cs:448` even claims and then `ForceSendZDO`s, which is
    /// exactly what `ForceFlushOnChange` does here).
    ///
    /// <b>Statics only, deliberately.</b> `Character` is NOT patched in this version and there is no
    /// switch for it. Stage 2 (creatures) is designed in OWNERSHIP.md but not built: two clients can
    /// both believe they own a mob for ~1 RTT and both run `OnDeath`/`CharacterDrop` - duplicate
    /// loot - and a mid-fight claim snaps the transform and drops the in-flight attack wind-up. That
    /// needs its own hysteresis and its own test night; a dead knob shipped in the meantime would
    /// only look like the feature exists. Statics have no AI and no rigidbody authority and their
    /// `RPC_Damage` bodies are plainly owner-gated (`Destructible.cs:100-107`, `MineRock5.cs:332-350`,
    /// `WearNTear.cs:1193-1200`, `TreeBase.cs:101-106`, `TreeLog.cs:155-160`, `MineRock.cs:120-125`).
    ///
    /// <b>Guards, all of them.</b> `MaxDistance` (16 m) - never claim something far away;
    /// `RequirePrivateAreaAccess` - respect other people's wards (note `PrivateArea.OnObjectDamaged`
    /// runs on the owner, so after a claim ward alarms fire on *our* machine - re-test wards);
    /// `SkipPiecesOwnedByOnlinePeer` - do not take a building piece off a player who is online;
    /// `ExcludeShipsAndVagons` - never touch anything parented to a ship or a cart (every prior mod
    /// that moved rigidbody authority broke boats and carts first); Player ZDOs are excluded
    /// unconditionally, not by setting. Ranged and AoE come free: `Projectile` and `Aoe` are
    /// owner-gated on the shooter and call the same `Damage`.
    ///
    /// <b>Interop, both verified in our own repos and both needing a re-test in game.</b>
    ///   * <b>NoVikingLeftBehind FastMining</b> (`FastMiningModule.cs:150-175`) patches the
    ///     **owner-side** `Destructible.RPC_Damage` / `MineRock5.RPC_Damage` / `MineRock.RPC_Hit`.
    ///     There is no patch collision, but after a claim those mining bonuses and the drop-context
    ///     swap run on the attacker's machine instead of the old owner's. Arguably more correct;
    ///     still a behaviour move. <b>Re-test ore yields, regrowth and drop tables.</b>
    ///   * <b>LagProbe</b> postfixes the same `Damage` methods and (on this branch) `NoteHit`
    ///     returns early when the local session already owns the ZDO - so after a claim it records
    ///     nothing and hit p95 would "improve" by losing its samples. `feat/lagprobe-local` counts
    ///     owner-local hits as `local=N`; that change must be in before any measurement pass.
    ///     This module's own `Claims` counter is the cross-check.
    /// </summary>
    internal sealed class CombatOwnershipModule : FeatureModule
    {
        public override string Name => "CombatOwnership";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "CombatOwnership";

        protected override string EnabledDescription =>
            "Take ownership of a static object (ore, tree, vine, building piece) the moment you hit " +
            "it, so your damage is applied on your own machine instead of travelling to whichever " +
            "player's PC happens to simulate it. Client-side; creatures are never claimed.";

        private ConfigEntry<float> _maxDistance;
        private ConfigEntry<bool> _requirePrivateAreaAccess;
        private ConfigEntry<bool> _skipPiecesOwnedByOnlinePeer;
        private ConfigEntry<bool> _excludeShipsAndVagons;
        private ConfigEntry<bool> _forceFlushOnChange;
        private ConfigEntry<string> _statics;

        internal static bool Active;
        internal static float MaxDistance = 16f;
        internal static bool RequirePrivateAreaAccess = true;
        internal static bool SkipPiecesOwnedByOnlinePeer = true;
        internal static bool ExcludeShipsAndVagons = true;
        internal static bool ForceFlushOnChange = true;

        /// <summary>Claims made since load - the cross-check on LagProbe's histogram.</summary>
        internal static int Claims;
        /// <summary>Hits we looked at and left alone, by reason.</summary>
        internal static int Skipped;

        private static bool _warned;
        private string _detail;

        private static readonly Dictionary<Type, FieldInfo> NViewFields = new Dictionary<Type, FieldInfo>();

        // ---- config ------------------------------------------------------------------------

        protected override void Bind()
        {
            _maxDistance = BindSynced("MaxDistance", 16f,
                "Never claim an object further than this (metres) from your player. Keeps a stray " +
                "long-range hit from moving ownership of something you are not really fighting.");
            _requirePrivateAreaAccess = BindSynced("RequirePrivateAreaAccess", true,
                "Never claim inside a ward you do not have access to. Leave this on: after a claim " +
                "the ward's own alarm (PrivateArea.OnObjectDamaged) runs on YOUR machine, because " +
                "it runs on the owner.");
            _skipPiecesOwnedByOnlinePeer = BindSynced("SkipPiecesOwnedByOnlinePeer", true,
                "Never claim a building piece whose current owner is another player who is online " +
                "- they are probably building with it.");
            _excludeShipsAndVagons = BindSynced("ExcludeShipsAndVagons", true,
                "Never claim anything parented to a ship or a cart. Ownership of a rigidbody vehicle " +
                "is what every previous mod in this space broke first. Leave this on.");
            _forceFlushOnChange = BindSynced("ForceFlushOnChange", true,
                "After a claim, push the changed ZDO to the front of the next sync list and nudge " +
                "the send timer, so everyone else sees the new owner and the new health a tick " +
                "sooner. This is vanilla's own claim-then-ForceSendZDO pattern (Container.cs:448).");
            _statics = BindSynced("Statics", "Destructible,MineRock5,MineRock,WearNTear,TreeBase,TreeLog",
                "Which static object families claim-on-hit applies to, by component type name. " +
                "Character is deliberately NOT supported in this version (see the module docs: " +
                "duplicate loot and AI hiccups need Stage 2's hysteresis first). Changing this " +
                "needs a restart - the patches are installed at load.");

            Watch(_maxDistance); Watch(_requirePrivateAreaAccess); Watch(_skipPiecesOwnedByOnlinePeer);
            Watch(_excludeShipsAndVagons); Watch(_forceFlushOnChange);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[CombatOwnership] maxDistance=" + MaxDistance + "m ward=" + RequirePrivateAreaAccess +
                        " skipOnlinePeerPieces=" + SkipPiecesOwnedByOnlinePeer +
                        " excludeVehicles=" + ExcludeShipsAndVagons + " flush=" + ForceFlushOnChange);
        }

        private void ReadConfig()
        {
            MaxDistance = Mathf.Clamp(_maxDistance.Value, 0f, 128f);
            RequirePrivateAreaAccess = _requirePrivateAreaAccess.Value;
            SkipPiecesOwnedByOnlinePeer = _skipPiecesOwnedByOnlinePeer.Value;
            ExcludeShipsAndVagons = _excludeShipsAndVagons.Value;
            ForceFlushOnChange = _forceFlushOnChange.Value;
        }

        // ---- patches ---------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            ReadConfig();

            var wanted = new List<string>();
            foreach (var raw in _statics.Value.Split(','))
            {
                var n = raw.Trim();
                if (n.Length > 0) wanted.Add(n);
            }

            var patched = new List<string>();
            foreach (var name in wanted)
            {
                if (string.Equals(name, "Character", StringComparison.OrdinalIgnoreCase))
                {
                    Log.LogWarning("[CombatOwnership] 'Character' is listed in Statics but creature " +
                                   "claiming is not implemented in this version (Stage 2, see the " +
                                   "module docs) - ignoring it.");
                    continue;
                }

                var type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    // A user typo or a prefab family from another mod: warn, do not take the whole
                    // module down. A missing METHOD on a type that exists is a different story.
                    Log.LogWarning("[CombatOwnership] no component type named '" + name +
                                   "' - skipping it (check [CombatOwnership] Statics).");
                    continue;
                }
                if (typeof(Character).IsAssignableFrom(type))
                    throw new Exception("CombatOwnership: " + name + " derives from Character - creature " +
                                        "claiming is not implemented and must not be enabled by config");

                var m = AccessTools.Method(type, "Damage", new[] { typeof(HitData) });
                if (m == null)
                    throw new Exception("CombatOwnership: " + name + ".Damage(HitData) not found - " +
                                        "refusing to patch (the game's hit path has changed)");

                // Priority.First: the claim must land before anything else reads IsOwner() on this
                // hit, and before vanilla's own body builds the routed RPC.
                Harmony.Patch(m, prefix: new HarmonyMethod(typeof(CombatOwnershipModule), nameof(DamagePrefix))
                {
                    priority = Priority.First
                });
                patched.Add(name);
            }

            if (patched.Count == 0)
                throw new Exception("CombatOwnership: no usable type in [CombatOwnership] Statics");

            if (AccessTools.Method(typeof(ZNetView), "ClaimOwnership") == null)
                throw new Exception("CombatOwnership: ZNetView.ClaimOwnership() not found - refusing to patch");
            if (AccessTools.Method(typeof(PrivateArea), "CheckAccess",
                    new[] { typeof(Vector3), typeof(float), typeof(bool), typeof(bool) }) == null)
                throw new Exception("CombatOwnership: PrivateArea.CheckAccess(Vector3,float,bool,bool) not " +
                                    "found - refusing to patch (the ward guard could not be enforced)");

            Active = true;
            _detail = "claim-on-hit: " + string.Join(", ", patched.ToArray());
            Log.LogInfo("[CombatOwnership] " + _detail + " (creatures NOT claimed) maxDistance=" +
                        MaxDistance + "m ward=" + RequirePrivateAreaAccess + " skipOnlinePeerPieces=" +
                        SkipPiecesOwnedByOnlinePeer + " excludeVehicles=" + ExcludeShipsAndVagons +
                        " flush=" + ForceFlushOnChange);
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return _detail + "; claims=" + Claims + " skipped=" + Skipped;
        }

        // ---- the hook ---------------------------------------------------------------------

        private static void DamagePrefix(object __instance, HitData hit)
        {
            if (!Active || !ClientActive() || hit == null) return;
            try
            {
                var nview = NViewOf(__instance);
                if (nview == null || !nview.IsValid()) return;

                var player = Player.m_localPlayer;
                if (player == null) return;

                var ctx = new ClaimCtx
                {
                    Enabled = true,
                    AttackerIsLocal = !hit.m_attacker.IsNone() && hit.m_attacker == player.GetZDOID(),
                    AlreadyOwner = nview.IsOwner(),
                    IsPlayerZdo = __instance is Character,
                    Distance = Vector3.Distance(player.transform.position, nview.GetZDO().GetPosition()),
                    MaxDistance = MaxDistance,
                };

                // Cheap gates first; the expensive ones (ward query, component walk, player list)
                // only once the hit is already a candidate.
                if (!ClaimPolicy.PassesCheapGates(ctx)) { Skipped++; return; }

                var comp = __instance as Component;
                ctx.OnVehicle = ExcludeShipsAndVagons && comp != null && OnVehicle(comp);
                ctx.ExcludeVehicles = ExcludeShipsAndVagons;
                ctx.PrivateAreaOk = !RequirePrivateAreaAccess ||
                                    PrivateArea.CheckAccess(nview.GetZDO().GetPosition(), 0f, false, false);
                ctx.RequireWard = RequirePrivateAreaAccess;
                ctx.IsPiece = __instance is WearNTear;
                ctx.SkipOnlinePeerPieces = SkipPiecesOwnedByOnlinePeer;
                ctx.OwnerIsOnlinePeer = SkipPiecesOwnedByOnlinePeer && ctx.IsPiece &&
                                        OwnerIsAnotherOnlinePlayer(nview.GetZDO().GetOwner());

                string reason;
                if (!ClaimPolicy.ShouldClaim(ctx, out reason)) { Skipped++; return; }

                nview.ClaimOwnership();
                Claims++;

                if (ForceFlushOnChange && ZDOMan.instance != null)
                {
                    ZDOMan.instance.ForceSendZDO(nview.GetZDO().m_uid);
                    if (ZNet.instance != null && !ZNet.instance.IsServer())
                        ZDOMan.instance.m_sendTimer = Mathf.Max(ZDOMan.instance.m_sendTimer, 0.0501f);
                }
            }
            catch (Exception e)
            {
                // Never break a swing.
                if (!_warned)
                {
                    _warned = true;
                    Log.LogWarning("[CombatOwnership] claim check failed once (hit handled as vanilla; " +
                                   "logged only once): " + e);
                }
            }
        }

        private static bool OnVehicle(Component comp)
        {
            return comp.GetComponentInParent<Ship>() != null || comp.GetComponentInParent<Vagon>() != null;
        }

        /// <summary>
        /// True when this ZDO's owner is some other player who is currently online. A client cannot
        /// enumerate peers, but every player in `ZNet.GetPlayerList()` carries their character's
        /// ZDOID, whose UserID is exactly the session id a ZDO records as its owner.
        /// </summary>
        private static bool OwnerIsAnotherOnlinePlayer(long owner)
        {
            if (owner == 0L) return false;
            if (owner == ZDOMan.GetSessionID()) return false;
            var net = ZNet.instance;
            if (net == null) return false;
            var players = net.GetPlayerList();
            if (players == null) return false;
            for (int i = 0; i < players.Count; i++)
                if (players[i].m_characterID.UserID == owner) return true;
            return false;
        }

        private static ZNetView NViewOf(object instance)
        {
            if (instance == null) return null;
            var t = instance.GetType();
            FieldInfo f;
            if (!NViewFields.TryGetValue(t, out f))
            {
                f = AccessTools.Field(t, "m_nview");
                NViewFields[t] = f;
            }
            return f == null ? null : f.GetValue(instance) as ZNetView;
        }
    }

    /// <summary>Everything the claim decision depends on, gathered so the policy can be pure.</summary>
    internal struct ClaimCtx
    {
        public bool Enabled;
        public bool AttackerIsLocal;
        public bool AlreadyOwner;
        public bool IsPlayerZdo;
        public float Distance;
        public float MaxDistance;

        public bool RequireWard;
        public bool PrivateAreaOk;
        public bool ExcludeVehicles;
        public bool OnVehicle;
        public bool SkipOnlinePeerPieces;
        public bool IsPiece;
        public bool OwnerIsOnlinePeer;
    }

    /// <summary>
    /// The claim decision, as a pure function over <see cref="ClaimCtx"/> so
    /// <see cref="HitLatencySelfTest"/> can exercise every guard headlessly. The order matters:
    /// the cheap gates are also the ones that must be checked before anything queries the world.
    /// </summary>
    internal static class ClaimPolicy
    {
        internal static bool PassesCheapGates(ClaimCtx c)
        {
            if (!c.Enabled) return false;
            if (!c.AttackerIsLocal) return false;      // somebody else's hit, or an environmental one
            if (c.AlreadyOwner) return false;          // nothing to gain: vanilla already runs locally
            if (c.IsPlayerZdo) return false;           // never, at any setting
            if (c.Distance > c.MaxDistance) return false;
            return true;
        }

        internal static bool ShouldClaim(ClaimCtx c, out string reason)
        {
            reason = null;
            if (!c.Enabled) { reason = "disabled"; return false; }
            if (!c.AttackerIsLocal) { reason = "not our hit"; return false; }
            if (c.AlreadyOwner) { reason = "already owner"; return false; }
            if (c.IsPlayerZdo) { reason = "player zdo"; return false; }
            if (c.Distance > c.MaxDistance) { reason = "too far"; return false; }
            if (c.ExcludeVehicles && c.OnVehicle) { reason = "on a ship or cart"; return false; }
            if (c.RequireWard && !c.PrivateAreaOk) { reason = "no ward access"; return false; }
            if (c.SkipOnlinePeerPieces && c.IsPiece && c.OwnerIsOnlinePeer)
            { reason = "piece owned by an online player"; return false; }
            return true;
        }
    }
}
