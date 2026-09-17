# LagProbe lifecycle

LagProbe is **temporary diagnostic scaffolding** for the hit-registration-latency work
(`docs/HIT-LATENCY-PLAN.md`), not a permanent feature. It never changes a gameplay value, but its
two networked probes ride the same per-peer lane as gameplay traffic, so it is not free.

**What it sends, and how often:** per client, the server→peer `SS_Ping`/`SS_Pong` round trip every
`[LagProbe] PingIntervalSec` (default 5 s, synced) plus the client→server `SS_LagReport` summary
every `[LagProbe] SummaryIntervalSec` (default 60 s, local) — together ≤ 4 KB/min per client, not
counting the join/leave edge windows (`EdgeIgnoreSec`, default 30 s), which are dropped, not sent.

**Silence it today:** set `[LagProbe] Enabled=false` (per machine — client or server side). To keep
it on but cut its traffic, raise `PingIntervalSec` and/or `SummaryIntervalSec` instead of disabling.

**Retirement plan:**
- Next release after PriorityLane + CombatOwnership ship and prove out: `[LagProbe] Enabled`
  defaults to `false`. Anyone can still flip it on to diagnose.
- Later release, only if nobody needs it: remove the `SS_Ping`/`SS_Pong`/`SS_LagReport` RPCs
  outright.

**Success criteria that trigger retirement** (per `docs/HIT-LATENCY-PLAN.md`): static-object hit
p95 < 80 ms and creature hit p95 < 150 ms, held over two full-crew sessions.
