# CombatOwnership — design research (Valheim 1.0.12, decompile-backed)

Paths are `/opt/modlab/game/_dump/v1_0_12/assembly_valheim/`.

## 1. Vanilla facts

**ClaimOwnership is local and unilateral.** `ZNetView.cs:245-251` → `ZDO.SetOwner` (`ZDO.cs:1373-1379`)
→ `SetOwnerInternal` + `IncreaseOwnerRevision` (`ZDO.cs:588-595`). No RPC, no server permission, no
validation. `IsOwner()` is true **the same frame**. The change then rides the ordinary ZDO sync
(`ClientChanged` → next ~20 Hz client tick, `ZDOMan.cs:886-900`); `ZDOPeer.ShouldSend`
(`ZDOMan.cs:52-63`) sends on any OwnerRevision bump and `RPC_ZDOData` (`ZDOMan.cs:1155-1172`) applies an
owner-only change even on a stale DataRevision. **Visible to others in ~1 client tick + 1 server tick +
2 legs ≈ 60-150 ms.** The server's release loop will not steal it back: `ReleaseNearbyZDOS`
(`ZDOMan.cs:950-978`) only reassigns ZDOs that are unowned or whose owner left the active area.

**Vanilla never claims a Character.** All 14 `ClaimOwnership` call sites mean "I am about to *write* or
*destroy* a ZDO I don't own": write — `Container.cs:448` (take-all: claims **then** `ForceSendZDO`, the
precedent for claim+priority-flush), `Sign.cs:179`, `Turret.cs:675`, `CookingStation.cs:783` and
`Fireplace.cs:387` (both only `if (!HasOwner())`); destroy — `Gibber.cs:80`, `TimedDestruction.cs:41`,
`Pickable.cs:107`, `StatusEffect.cs:146`, `SE_Demister.cs:111`, `TerrainModifier.cs:206`,
`Player.cs:3016`, `Fish.cs:314,779`. Separately an owner *hands* ownership to a requester via
`GetZDO().SetOwner(sender)` (`Container.cs:330,376`, `ItemDrop.cs:1704`, `ArmorStand.cs:346`,
`ItemStand.cs:390`, `Sadle.cs:351`, `Ship.cs:690`, `Trap.cs:270`, `Vagon.cs:170`) — and the **server**
does the same at `ZDOMan.cs:973`, which is what makes option (d) legal.

**What changes on the new owner mid-fight.**
- *Transform:* `ZSyncTransform.OwnerSync` (`124-155`) detects `!m_wasOwner && IsOwner`, **snaps**
  position/rotation to the ZDO, restores body velocity, calls `Physics.SyncTransforms()`. In melee range
  at 20 Hz that is centimetres, but it is a real one-frame hitch.
- *AI:* **there is no owner-change hook in the game** (`m_wasOwner` exists only in ZSyncTransform).
  `BaseAI.UpdateAI` (`305-320`) just returns false for non-owners. Persisted in the ZDO: alert,
  aggravated, huntPlayer, spawnPoint, patrol (`282-303`), sleeping (`MonsterAI.cs:873-907`),
  `haveTargetHash` (`1661`). **Not persisted:** the actual target, every attack cooldown, `m_timeSinceHurt`,
  and the in-flight `ZSyncAnimation` trigger (owner-only, `104,165,191,210`). So the mob re-acquires its
  target next `FindEnemy`, an in-progress wind-up is aborted, and it may gain or lose one attack.
- *Character:* the owner alone runs motion, stagger, status effects, `CheckDeath` (`Character.cs:810-846`),
  `SetHealth` (`3003-3026`), `OnDeath`/kill-registration/`CharacterDrop` (`2903-2995`, non-owners
  early-return at `2927`); the collider layer flips `character_net`→`character` (`855-870`).

**Thrash.** Two claimers both write OwnerRevision N+1. The server keeps the first; the loser's is not
*strictly greater*, so it is dropped silently and the loser keeps believing it owns the ZDO for ~1 RTT.
**In that window both clients simulate the mob, both apply damage, both can run OnDeath/CharacterDrop.**
Hysteresis makes this rare, not impossible.

**Why ~0.5 s today.** `Character.Damage` (`2224-2231`) → `InvokeRPC` targeted at the owner
(`ZNetView.cs:331-334`) → attacker→server→owner → `RPC_Damage` (`2233-2248`, `if (!IsOwner()) return;`)
→ `SetHealth` → DataRevision bump → owner's next ~20 Hz tick, gated by the high-water check
(`ZDOMan.cs:1065-1072`) → server → attacker's next tick. Two RTTs + up to two cadence ticks, each leg
behind that peer's socket backlog. (The floating number is a *separate* broadcast,
`DamageText.cs:184-191`, owner→server→everyone — number and HP arrive on different paths, both late.)

## 2. Options

**(a) Claim-on-hit.** Hook set = the nine `IDestructible` implementers' `Damage(HitData)`: `Destructible`,
`MineRock5`, `MineRock`, `WearNTear`, `TreeBase` (`62-64`), `TreeLog` (`147-151`), `HitArea`, `Raven`
(skip), `Character` (stage 2). Prefix on the attacker: if `!IsOwner()` and policy allows,
`ClaimOwnership()`, then let vanilla run — `InvokeRoutedRPC` (`ZRoutedRpc.cs:130-137`) handles it locally
and **skips `RouteRPC` entirely**, so damage applies this frame, the RPC never goes on the wire, and only
the health ZDO propagates: one leg instead of three, *less* traffic than today. Ranged/AoE come free —
`Projectile` (`389,531,664`) and `Aoe` (`311,715,736`) are owner-gated on the shooter and call the same
`Damage`. *Statics are safe* (owner-gated `RPC_Damage` at `Destructible.cs:100-107`,
`MineRock5.cs:332-350`, `WearNTear.cs:1193-1200`; no AI, no rigidbody). Vines are almost certainly
`Destructible` — verify the prefab in-game. *Creatures need hysteresis:* owner hasn't damaged it for
≥ grace; we've held it ≥ MinHold; the mob isn't targeting the current owner's player; not tamed, not a
boss; ≤ 16 m.

**(b) Hot-ZDO flush.** Keep vanilla authority; owner calls `ZDOMan.ForceSendZDO(uid)` after `SetHealth`,
server calls `ForceSendZDO(attackerPeerId, uid)` when routing `RPC_Damage`. `AddForceSendZdos`
(`1313-1337`) inserts at index 0 of that peer's list. Reorders *within* a tick only — buys the queueing
component (~100-200 ms), not the 2-RTT structure. Zero authority risk.

**(c) Predictive local feedback.** Cosmetic; the number is wrong whenever armour/resist/block/weak-spot
differ and double-prints when the owner's broadcast lands. Never predict death or loot. Skip once (a) ships.

**(d) Least-latency owner policy (server-side).** Legal (`ZDOMan.cs:973`), single-writer so no double
ownership, needs no client mod — but the server can't know who is *attacking*, and every reassignment
churns AI. Good as a tie-break inside (a), poor alone. This is exactly what NetworkPerformanceSystem
already ships (below).

## 3. Recommendation

**One client module `[CombatOwnership]`, two stages, plus (b) as a cheap server win.**

*Stage 1 — statics only, ship first:* `Enabled=true`, `Statics=true`, `Creatures=false`,
`MaxDistance=16`, `RequirePrivateAreaAccess=true`, `SkipPiecesOwnedByOnlinePeer=true`,
`ExcludeShipsAndVagons=true`, `ForceFlushOnChange=true`. Should kill the vine/ore/tree half outright.

*Stage 2 — creatures, own switch, default off:* `OwnerGraceSec=1.5`, `MinHoldSec=2.0`, `MaxDistance=16`,
`SkipTamed=true`, `SkipBosses=true`, `SkipIfMobTargetsOwner=true`, `MaxClaimsPerSec=3`. Player ZDOs are
excluded unconditionally, not by setting.

*Proof — LagProbe already measures exactly this:* probe (b) hit-registration p50/p95/max/timeouts and
probe (c) ownership churn/min, via `ss.lag`. Target: static p95 from ~500 ms to <80 ms, churn ≤ ~2×
baseline, no rise in late/timeouts.

*Test protocol (4-player cave):* same cave, **same join order** (the earliest-connected player is the
sticky owner — keep it constant). Per pass: 5 min vines/ore + 5 min Seekers, then `ss.lag` on every
client and the server `[PeerTelemetry]` line; record p50/p95/max/timeouts/churn + socketQueue. Pass 1 off,
pass 2 Stage 1, pass 3 Stage 2, pass 4 two players on the same Seeker to force contention — watch for
double loot and mob stutter.

## 4. Failure modes to guard

1. **Double-ownership window → duplicated `CharacterDrop`/`OnDeath` loot** (`Character.cs:2927-2995`).
   Never claim when the incoming hit would be lethal — leave the killing blow with the established owner.
2. **Mob snap** from `ZSyncTransform:124-155` — distance cap; never claim an airborne/knocked-back mob.
3. **AI hiccup** — dropped wind-up, re-target, free or lost attack. `MinHoldSec` + `SkipIfMobTargetsOwner`.
4. **Tamed animals / Sadle riders** (`Tameable.cs:304-720`, `Sadle.cs:351`) — never claim.
5. **Bosses** — kill registration, `s_bossCount` (`Character.cs:2989`, `BaseAI.cs:1574`) — never claim.
6. **Players** — never claim a Player ZDO, including the local player's own.
7. **WearNTear on ships/carts** (`Ship.cs:690`, `Vagon.cs:170`; owner runs support/decay at `529`,`1151`)
   — exclude vehicles and anything parented to one. Every prior mod that moved rigidbody authority broke
   boats and carts first.
8. **Other players' pieces** — require PrivateArea access, skip pieces owned by an online peer. Note
   `PrivateArea.OnObjectDamaged` (`Destructible.cs:130-133`, `WearNTear.cs:1206-1212`) runs on the owner,
   so after a claim ward alarms fire on the attacker's machine — re-test wards.
9. **Collider-layer flip** on claim (`Character.cs:855-870`) — check for a one-frame melee whiff.
10. **Ownership storm** — `MaxClaimsPerSec`; churn is itself a ZDO write, so a bad policy *adds* traffic.
11. **NVLB interop (verified).** `FastMiningModule.cs:150-175` patches the **owner-side**
    `Destructible.RPC_Damage` / `MineRock5.RPC_Damage` / `MineRock.RPC_Hit` — no patch collision, but
    after a claim those mining bonuses and the drop-context swap run on the *attacker's* machine instead.
    Arguably more correct; still a behaviour move. **Re-test ore yields, regrowth hash and drop tables.**
    `CombatRechargeModule.cs:87-107` postfixes `Character.Damage` — harmless.
12. **LagProbe would blind itself.** It postfixes the same `Damage` methods and `NoteHit` returns early
    when `owner == GetSessionID()` — after a claim the postfix sees us as owner and logs nothing, so p95
    would "improve" by vanishing. Fix first: have CombatOwnership push a 0 ms sample plus a `claimed`
    counter into LagProbe, or capture the owner in a prefix ordered before the claim.

## 5. Prior art

**Nobody has shipped "claim ZDO ownership on hit."** The ecosystem does one of three things.

- **CoopParryFix** — github.com/Ageous27/CoopParryFix. Best statement of the mechanic ("the mob is
  simulated on someone else's PC; their attack travels owner→server→you"). Measures owner-path RTT with
  its own ping (Steam ping doesn't measure it) and *widens the parry window* instead of moving ownership.
  1.0-built; needs the plugin on clients and server. Strong signal on the risk profile.
- **NetworkPerformanceSystem** (MidnightMods, Thunderstore, actively developed) — option (d) shipped:
  assigns moving objects to whoever minimises staleness, with hysteresis; server wins contested objects in
  server-loaded zones; RTT-sized send window. **Not** transfer-on-hit. Needs Steam RTT (degrades on
  PlayFab/crossplay); long incompatibility list incl. BetterNetworking, SkadiNet, FiresGhettoNetworking.
- **FiresGhettoNetworking** (github.com/fire-VA) — the only user-facing creature-ownership-to-server
  switch; author's own words: *"We do not recommend enabling this… vehicles are the usual casualty."*
- **Serverside Simulations** (thunderstore mvp / github ddormer/valheim-serverside) — blanket server
  authority by creating objects server-side; explicitly trades the area-owner's latency for everyone
  else's. **Last release 1.1.9 for 0.220.5, unmaintained;** PR #118 is a community 1.0 port
  (`Vector2i`→`Vector2s` in `ReleaseNearbyZDOS` et al.) — small, portable, worth reading.
- **ValheimCommunityPatch** (MidnightsFX) — ships the claim-then-act pattern for stations ("takes
  ownership of a smelter before adding fuel so the item can't be lost"). Actively maintained.
- **Bandwidth-only, no ownership:** BetterNetworking (CW_Jesse, no official 1.0 build) + liekos47's 1.0
  rebuild; SwmarlyValheimNetworking (**its PatchGuard already refuses to load alongside SmoothServer**);
  ValheimTune (explicitly states it does not alter ownership or hit registration);
  ValheimPerformanceOptimizations; ValheimPerformanceOverhaul (prioritises packets, doesn't move
  ownership); ValheimPlus has no ownership/networking section at all; Less Zdo Corruption is deprecated.
- **Background:** the hardcoded `10240` send-queue constant in `SendZDOs` is the old 64 KB/s cap's
  descendant (jamesachambers.com) — orthogonal to ownership, but it lengthens every leg above.
