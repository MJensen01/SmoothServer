# Priority lane for latency-critical traffic (SmoothServer) — design, v1.0.12

## 1. Classification

**Wire format is unambiguous.** `ZRpc.Invoke` clears `m_pkg`, writes `method.GetStableHashCode()` as
the first int, serialises the params, then `SendPackage` → `m_socket.Send(m_pkg)`. So byte 0..3 of
every `byte[]` reaching `ZSteamSocket.Send` is the method hash (LE int32). `ZRpc.UpdatePing` writes
literal `0` (**not** the hash of "Ping"), so heartbeat = hash 0.

| package | hash | hex | LE bytes |
|---|---|---|---|
| ping/pong | `0` | `0x00000000` | 00 00 00 00 |
| `RoutedRPC` | `-667652280` | `0xD8346F48` | 48 6F 34 D8 |
| `ZDOData` | `-1975616347` | `0x8A3E7CA5` | A5 7C 3E 8A |
| `PeerInfo` | `-725574882` | `0xD4C09B1E` | 1E 9B C0 D4 |
| `NetTime` | `-2045981424` | `0x860CCD10` | 10 CD 0C 86 |
| `RefPos` | `1664081997` | `0x632FE04D` | 4D E0 2F 63 |
| `PlayerList` | `-265949079` | `0xF025F069` | 69 F0 25 F0 |

(The `RoutedRPC` bytes match the literal `{0x48,0x6f,0x34,0xd8,…}` already hard-coded in
`CompressionModule`'s self-test — independent confirmation of the hash algorithm.)

Inside a `RoutedRPC` package: outer hash(4) + `ZPackage` length prefix(4) + msgID(8) + sender(8) +
target(8) + targetZDO.UserID(8) + targetZDO.ID(4) + **inner methodHash at offset 44**. So a routed
RPC's *inner* method and target ZDO are readable from the raw buffer with two `BitConverter` reads —
no allocation. Cleaner still: a Harmony prefix on `ZRoutedRpc.RouteRPC(RoutedRPCData)` stashes
`(m_methodHash, m_targetZDO)` in a `[ThreadStatic]` that `Send` consumes; exact, no parsing. Use the
byte-offset reader as the fallback/validator.

**Hot ZDOs.** Don't inspect a serialised `ZDOData` blob — hook `ZDOMan.SendZDOs(ZDOPeer, bool)`.
A prefix builds the sync list itself (or reads `m_tempToSync` via a transpiler), partitions it into
*hot* (ZDOID in a `HotSet`) and bulk, emits the hot ones as their own small `ZDOData` package
classed **priority**, then lets vanilla send the bulk. `HotSet` is fed by: (a) `ZRoutedRpc` prefix —
any `RPC_Damage`/`RPC_Destroy`-class routed RPC marks its target ZDO hot for `HotWindowMs`;
(b) vanilla's own `ZDOPeer.m_forceSend` (already front-inserted by `AddForceSendZdos`).
`ZDO.Type == ObjectType.Prioritized` is a weaker existing signal; keep it as a secondary.

## 2. Option A — two Steam lanes

**API exists.** `com.rlabrecque.steamworks.net.dll` shipped with 1.0.12 is Steamworks.NET **20.2.0 /
SDK 1.57**, and exports `SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes`,
`..._SendMessages`, `SteamAPI_ISteamNetworkingUtils_AllocateMessage`,
`SteamNetworkingMessage_t.m_idxLane`, plus the `SteamGameServerNetworking{Sockets,Utils}` wrappers.

**But `SendMessageToConnection` has no lane parameter.** Lane selection only exists on
`SendMessages()` via `SteamNetworkingMessage_t::m_idxLane`. So Option A is not "add an argument" —
it is replacing `SendQueuedPackages`' whole drain with `AllocateMessage` + fill `m_pData/m_cbSize/
m_conn/m_nFlags/m_idxLane` + `SendMessages`, i.e. taking over the send path anyway (and owning the
unmanaged buffer lifetime Steam then frees). Configure lanes once per connection in a postfix on the
`ZSteamSocket(HSteamNetConnection)` ctor and on `ConnectByIPAddress`/`ConnectP2P`, both sides:
`ConfigureConnectionLanes(con, 2, new[]{0,1}, new ushort[]{1,4})` — lane 0 priority 0 (higher
priority number = *lower* precedence in Steam's model; verify empirically), weights as the
starvation guard.

**Ordering.** Steam guarantees reliable-ordered *per lane*; cross-lane ordering is undefined. Three
real hazards: (i) `DestroyZDO` is a **routed** RPC (`ZDOMan.SendDestroyed` →
`InvokeRoutedRPC(0L,"DestroyZDO",…)`), so a blanket "all RoutedRPC is priority" lets a destroy
overtake a queued `ZDOData` for the same object, and `RPC_ZDOData` will happily
`CreateNewZDO` a ghost (the `m_deadZDOs` guard is `IsServer()`-only — clients have no protection);
(ii) `ZRoutedRpc.HandleRoutedRPC` **silently drops** an RPC whose target ZDO is unknown
(`GetZDO()==null` or `FindInstance()==null`) — no queue, no retry — so an RPC overtaking the ZDO
that creates its object is lost; (iii) Compression's handshake depends on both ends flipping at *the
same byte position of one reliable FIFO*; with two lanes that position no longer exists, and framing
state would have to be per-lane (`SS_Ready` sent on, and gating, each lane separately). Frames must
never straddle lanes — each `byte[]` is one frame, so that part is fine.

**Socket accounting.** `GetSendQueueSize()` reads `m_cbPendingReliable` etc. for the *connection*
(lane 0 status struct), which still aggregates — fine, but per-lane depth needs
`GetConnectionRealTimeStatus` with a `SteamNetConnectionRealTimeLaneStatus_t[]`, and
`SendQueueGuard`/`AdaptiveBudget` would want the bulk lane's depth, not the sum.

**Verdict: don't ship first.** Highest blast radius (rewrites the send path *and* breaks Compression's
invariant), and needs both ends modded to help at all.

## 3. Option B — reorder inside the single lane

The naive version does almost nothing: `ZSteamSocket.Send` enqueues **and immediately calls
`SendQueuedPackages`**, which drains to Steam until Steam says no. So `m_sendQueue` is normally
*empty* — essentially the whole 16–64 KB standing queue lives inside Steam
(`m_cbPendingReliable + m_cbSentUnackedReliable`), where nothing can be reordered. Front-inserting
into `m_sendQueue` buys ~0 ms.

The version that works is a **shaper**: keep the backlog in managed space. Prefix
`SendQueuedPackages` (same seam `SendQueueGuard` already owns), return `false`, and drain ourselves:
two queues (`prio`, `bulk`), prio first, and stop handing bytes to Steam once
`m_cbPendingReliable > SteamInflightBytes` (≈4–8 KB, ~1 BDP). Bulk then waits in *our* deque where a
hit can jump it. Cost of the remaining HOL is bounded by `SteamInflightBytes / bandwidth` —
at the ~150–200 KB/s implied by "16–64 KB = 80–430 ms", 4 KB ≈ 20–25 ms instead of 80–430 ms.

Ordering safety is *stronger* here than in Option A, because we can inspect state at drain time:
- promote a `RoutedRPC` only if `targetZDO.IsNone()` **or** the peer's `ZDOPeer.m_zdos` already
  contains it (proof the receiver has the ZDO) — kills hazard (ii);
- **never** promote `DestroyZDO` (or any inner hash on a deny-list) — kills hazard (i);
- a promoted hot-`ZDOData` package must not overtake a bulk `ZDOData` containing the *same* ZDOID;
  since we build the hot package by *removing* those ZDOs from the bulk list in the same
  `SendZDOs` call, that can't happen.
- Compression is untouched: everything still goes through `m_sendQueue` (we re-fill it prio-first
  before letting the framing prefix run, or frame at the same seam) — one FIFO, one flip point.

## 4. Option C — hot-ZDO fast flush on the owner's client

Return leg. Postfix `Character.RPC_Damage` / `Destructible.RPC_Damage` / `WearNTear.RPC_Damage`
(all three start with an `m_nview.IsOwner()` guard, so a postfix runs only on the owner). Do:
`ZDOMan.instance.ClientChanged(zdo.m_uid)` (already implicit via `ZDO.Set`) **plus**
`ZDOMan.instance.ForceSendZDO(uid)` → `AddForceSendZdos` does `syncList.Insert(0, zdo)`, so it is
first in the next package regardless of `ClientSortSendZDOS`. Then nudge the tick: set
`ZDOMan.instance.m_sendTimer = 1f/SendHz` (SendCadence's own gate) or call
`SendZDOs(peer,false)` directly for the server peer, then `socket.Flush()`. Saves the residual
0–50 ms cadence wait. Cost: three postfixes, one hash-set add, one flush — negligible. Client-side
only; a vanilla client just keeps the 0–50 ms.

## 5. Recommendation

**Build B + C. Skip A** (revisit only if measurements show the residual `SteamInflightBytes` wait
dominates).

**Module `PriorityLane`, side `Both`, `[PriorityLane]`:**

| setting | default |
|---|---|
| `Enabled` | `true` |
| `SteamInflightBytes` | `6144` (0 = off/shaping disabled) |
| `HotWindowMs` | `1500` |
| `MaxHotZdosPerPackage` | `24` |
| `PromoteRouted` | `true` |
| `DenyRoutedMethods` | `DestroyZDO` (hash deny-list, comma list of names) |
| `RequireKnownZdo` | `true` (the `m_zdos` proof rule) |
| `FastFlushOnDamage` | `true` (Option C, client) |
| `MaxPrioBytesPerDrain` | `8192` (starvation guard for bulk) |

Ordering rules it must keep: never promote a deny-listed routed method; never promote an RPC whose
target ZDO the peer has not been sent; never let a hot ZDO and its bulk copy be in flight in the
wrong order (partition, don't duplicate); never split a Compression frame; always fall back to the
vanilla drain if `ZSteamSocket` fields/signatures don't match (the module should `throw` at patch
time like the others do, not silently mis-send).

**Self-test headlessly:** the existing `SteamSelfTest`/`Compression` pattern — a fake `ISocket` +
synthetic packages: assert (a) hash classification for all seven package types incl. the offset-44
inner-hash reader, (b) prio-before-bulk drain order with a stubbed inflight counter, (c) the deny-list
and `m_zdos` gates actually demote, (d) a partitioned `SendZDOs` never emits a ZDOID twice. One
PASS/FAIL line at load, 0 players needed.

**Metric:** LagProbe probe (b) — client hit-registration p50/p95/max/late/timeouts (`ss.lag`), which
already measures exactly this path (`Damage` → owner's ZDO `DataRevision` bump). Expect p95 to fall
from the 80–430 ms band toward `SteamInflightBytes/bw + RTT`. Watch `[PeerTelemetry] socketQueue`
doesn't grow (shaping moved bytes, it must not lose throughput) and SendQueueGuard deferrals.

**Vanilla clients:** the server half is entirely server-local (send-side reordering only — the wire
bytes and their relative order are all vanilla-legal), so an unmodded client benefits from B with no
change and simply doesn't get C. Nothing in B requires negotiation, unlike Compression. Keep
`[General] EnforceClientMod=false` working.

**Size:** ~450–600 lines for `PriorityLaneModule.cs` (shaper drain, classifier, hot partition) plus
~120 lines of self-test and ~40 lines of Option C postfixes; one new MODULES.md row.
