# Hit-registration latency on a small dedicated server — plan (2026-09-17)

Matt's brief: with the whole crew in a Mistlands cave, a melee hit on a vine or a Seeker takes ~0.5 s to register, even after
the send-rate and queue-cap tuning of 2026-09-15/16. Iron Gate's netcode is tuned for a listen server (the host's own client
owns most things and never waits); on a dedicated server every hit crosses two links and two queues. Goal: fix it for every
remote player, measured, not felt. Research: `docs/research/hit-latency/OWNERSHIP.md`, `PRIORITY.md`, and the read-only
hit-path trace summarised in `Documents\AI\HANDOFF.md` (2026-09-16 11:45 UTC).

## What actually happens (1.0.12, decompile-verified)

1. Damage is applied only by the **ZDO owner's client**. Owner of a cave object = the earliest-connected player who first had it
   in range (`ZDOMan.ReleaseNearbyZDOS` in peer join order); sticky; vanilla never re-assigns a Character.
2. The attacker's `RPC_Damage` goes attacker → server → owner. Per peer there is ONE reliable-ordered Steam lane shared by routed
   RPCs and bulk `ZDOData`, so the hit waits behind everything queued for the owner (head-of-line blocking). The backlog lives
   **inside Steam's buffer**, not in the game's queue, so it cannot be reordered after the fact.
3. The owner applies the hit; the HP/state change returns owner → server → everyone on the owner's ~20 Hz client cadence and the
   same single lane.
4. Budget ≈ ½RTT + server frame + **Q(server→owner)** + ½RTT + owner frame + ≤50 ms client tick + **Q(owner→server)** + ½RTT +
   ≤33 ms send tick + Q(server→attacker) + ½RTT. The two Q terms dominated: 64 KB at Steam's 153.6 KB/s floor = 427 ms one way.
5. Already done live (2026-09-16): server queue cap 64→16 KB, client 128→24 KB, adaptive ceiling 32 KB, SendHz 30, Steam rate cap
   384 KB/s. Bounds the queue delay at ~80-100 ms per leg; does not remove it. Per-peer bandwidth (~150 kB/s needed vs ~190 kB/s
   available near the base) is the true limiter; compression (OFF until a live test) cuts bytes 2-3×.

## The fix, three layers (all SmoothServer, all measured by LagProbe)

**A. `PriorityLane` (side Both, new module) — shaping + hot objects.** Take over the socket drain (the seam SendQueueGuard already
owns), hold the backlog in two managed queues (priority: routed RPCs and "hot" ZDOs; bulk: everything else), and feed Steam only
~1 bandwidth-delay-product at a time (`SteamInflightBytes`=6144) so the in-Steam backlog stays ~20-25 ms instead of 80-430.
Hot ZDOs = objects damaged in the last `HotWindowMs`=1500, partitioned (never duplicated) into a small package sent first. On the
owner's client, a postfix on the three `RPC_Damage` bodies calls `ForceSendZDO` + nudges the send tick (`FastFlushOnDamage`).
Ordering guards (mandatory): never promote a deny-listed routed method (`DestroyZDO`), never promote an RPC whose target ZDO the
peer has not been sent (`RequireKnownZdo`), never split a Compression frame, throw at patch time on any signature mismatch.
Server half works for unmodded clients (send order only; every byte stays vanilla-legal). Steam multi-lane API exists in the
shipped Steamworks.NET 20.2.0 but needs `SendMessages` + unmanaged allocation and breaks the Compression `SS_Ready` invariant —
skipped. ~600 lines + self-test.

**B. `CombatOwnership` (client module) — claim on hit.** `ZNetView.ClaimOwnership()` is local, unilateral and instant; after it,
`InvokeRoutedRPC` runs `RPC_Damage` **locally and never puts it on the wire** — faster and cheaper than today. Vanilla uses the
same pattern in 14 places ("claim before I write a ZDO I don't own"), never for Characters.
- Stage 1, statics only, ship first: `Destructible`/`MineRock5`/`WearNTear`/`TreeBase`/`TreeLog` (vines, ore, trees, pieces);
  `MaxDistance=16`, `RequirePrivateAreaAccess=true`, `SkipPiecesOwnedByOnlinePeer=true`, `ExcludeShipsAndVagons=true`,
  `ForceFlushOnChange=true`. Ranged/AoE come free via `Projectile`/`Aoe`. Should remove the vine/ore half outright.
- Stage 2, creatures, own switch default OFF: on a mid-fight claim the new owner snaps transform/velocity and resumes AI from its
  own locals (target, attack cooldowns and the in-flight animation trigger are lost → a hiccup). Two simultaneous claimers both
  believe they own it for ~1 RTT → both can run `OnDeath` → **duplicate loot**. Hysteresis: `OwnerGraceSec=1.5`, `MinHoldSec=2`,
  `MaxDistance=16`, `SkipTamed`, `SkipBosses`, `SkipIfMobTargetsOwner`, `MaxClaimsPerSec=3`; player ZDOs excluded always.
- Gotchas found in our own repos: LagProbe's hit timer returns early when we are the owner → after a claim it records nothing
  (p95 "improves" by vanishing) — fix the probe to time local applies too, BEFORE measuring. NVLB FastMining patches owner-side
  `RPC_Damage`/`RPC_Hit`, so after a claim mining bonuses/drop swaps run on the attacker's machine — re-test ore yields/regrowth.

**C. Fewer bytes.** Compression ON after the live join test (2-3× fewer bytes → proportionally shorter queues); SendHz 20 if the
churn counter shows no single culprit; the `zdo churn by prefab` counter (0.5.2) names what floods the base.

## Prior art (16 items in OWNERSHIP.md)
Nobody ships claim-on-hit. CoopParryFix compensates for owner-path RTT instead of moving ownership (a risk signal for Stage 2).
NetworkPerformanceSystem ships proactive least-latency ownership with hysteresis. FiresGhettoNetworking has a creatures-to-server
switch its author says not to enable. Serverside Simulations is unmaintained (community 1.0 port in PR #118). Everything else is
bandwidth-only. SwmarlyValheimNetworking's PatchGuard refuses to load next to SmoothServer.

## Measurement and test protocol
LagProbe (0.5.1) already measures this path: client hit-registration p50/p95/max/late/timeouts per owner, server→peer RTT/jitter/
loss, socketQueue, ownership churn/min; 0.5.2 adds the session-edge filter, the churn counter and the client→server report so the
box collects every player's swing data. Targets: static-object p95 from ~500 ms to <80 ms; creature p95 <150 ms; churn ≤ 2×
baseline; late/timeouts not up; socketQueue not up; no duplicate loot.
Four-player cave, **same cave, same join order** (the earliest-connected player is the sticky owner — keep it constant), 5 min
vines/ore + 5 min Seekers per pass, `ss.lag` on every client + the server summary after each: pass 1 = today's config; pass 2 =
PriorityLane; pass 3 = + CombatOwnership Stage 1; pass 4 = + Stage 2 with two players on the same Seeker (watch loot, stutter).

## Order of work
1. 0.5.2: LagProbe refinements (in flight) + fix the probe for local applies. Ship once Matt's join test also proves compression.
2. 0.6.0: PriorityLane (server benefit for everyone immediately) + CombatOwnership Stage 1 (statics). Stage 2 present but OFF.
3. Measure with the protocol; flip Stage 2 on only if pass 4 is clean.

## Decisions for Matt
1. Build both A and B-Stage-1 for 0.6.0 (recommended), or A first alone?
2. Stage 2 (creatures): build it now behind the OFF switch (recommended) or leave it out until statics are proven?
3. Test night: which evening the four of you can do the four-pass cave (≈40 min)?
