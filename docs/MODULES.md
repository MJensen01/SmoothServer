# Modules

One row per module. "Side" is the `FeatureModule.Side` this module declares (`Server` = dedicated
server only, `Client` = player only, `Both` = runs on either half). "Hot-reload" describes whether
a live edit to the module's cfg entries takes effect without a server/game restart — every entry
bound with `BindSynced`/`BindLocal` (or wired with `Watch()`) is pushed through `OnConfigChanged`
automatically; the exceptions are called out. "Needs real players" says whether the module's
actual effect can be verified with 0 players connected (self-tests, static config plumbing,
solo-safe patches) or needs at least one real client connection to observe (per-peer telemetry,
budgets, compression handshake, map sharing, etc.) — the module still *loads* and *patches*
with 0 players either way.

All modules are discovered by reflection (see `src/Plugin.cs` `DiscoverModules()`); there is no
registration list to keep in sync. Every module's own `Enabled` toggle lives in its own `[Section]`
and is itself hot-reloadable (a live edit updates `EnabledCfg.Value` immediately), but note that
disabling a module that already installed its Harmony patches does **not** unpatch it — only a
process restart fully removes an already-applied module's patches; only the module's *behavior
gates* (the `internal static bool Active` / `IsActive` checks in patch bodies) turn off live.

| Module | Side | Section | Purpose | Key settings (default) | Hot-reload | Needs real players |
| --- | --- | --- | --- | --- | --- | --- |
| Telemetry | Server | `[Telemetry]` | Periodic fps / frame-time / ZDO-rate log line. No gameplay effect. | `IntervalSeconds` (10) | Yes | No |
| FrameRate | Server | `[FrameRate]` | Raises the dedicated server's Unity headless frame cap (default is Unity's own ~30 fps, not a Valheim setting). | `TargetFrameRate` (0 = leave vanilla; 60/120 to raise; -1 = uncapped) | Yes (re-applies immediately, restarts the 5s post-`ZNet.Start` safety re-assert) | No |
| SendCadence | Server | `[SendCadence]` | Sends ZDO updates to every peer on a fixed interval instead of vanilla's one-peer-per-frame round robin. | `SendHz` (20) | Yes | Yes (effect is on peer send timing) |
| SendBudget | Server | `[SendBudget]` | ZDO send-queue high-water mark and minimum chunk size (vanilla literals made configurable via transpiler). Fallback when AdaptiveBudget is off. | `HighWaterBytes` (65536, vanilla 10240), `MinChunkBytes` (2048, vanilla 2048) | Yes | Yes (queue behavior only shows under real send load) |
| CreateBudget | Server | `[CreateBudget]` | Objects `ZNetScene` may instantiate per frame outside the loading screen, made configurable. | `MaxCreatedPerFrame` (10, vanilla 10 — no-op until raised) | Yes | No (config plumbing verifiable solo, but the budget itself only matters with load) |
| PeerTelemetry | Server | `[PeerTelemetry]` | Per-peer ping, throughput and Steam send-queue sampling. No patches; feeds AdaptiveBudget and StatsLog. | `IntervalSec` (10), `SampleIntervalSec` (1) | Yes | Yes (nothing to sample with 0 peers) |
| AdaptiveBudget | Server | `[AdaptiveBudget]` | Per-peer send budget from each peer's measured bandwidth-delay product; clamped, EMA-smoothed, backs off under Steam congestion. | `FloorBytes` (16384), `CeilingBytes` (131072), `K` (2.0), `PendingBackoffBytes` (8192), `Smoothing` (0.3), `LogIntervalSec` (10), `UseCallSiteSwap` (false, legacy path) | Yes | Yes |
| SteamRates | Server | `[SteamRates]` | Raises Steam's per-connection `SendRateMax`; leaves `SendRateMin` at vanilla so congestion control still backs off for weak links. | `SendRateMax` (1048576, vanilla 153600), `SendRateMin` (0 = leave vanilla), `WriteGameServerUtils` (true), `WriteUserUtils` (true) | Yes | No (applies globally at `RegisterGlobalCallbacks`, verifiable via the module summary/log alone) |
| SendQueueGuard | Server | `[SendQueueGuard]` | Wraps vanilla's `ZSteamSocket.SendQueuedPackages` drain (prefix/finalizer) so a full/erroring Steam send queue backs off instead of throwing, and logs once per episode. | `BackoffMs` (50), `MaxQueuedBytes` (0 = never drop, defer only), `ReportIntervalSec` (30) | Yes | Yes (nothing to guard with an empty queue) |
| SyncListCache | Server | `[SyncListCache]` | Caches the per-peer sector scan in `ZDOMan.CreateSyncList` briefly; filter/sort still run every send. | `CacheMs` (100), `StatsIntervalSec` (60) | Yes | Yes |
| VPOServer | Server | `[VPOServer]` | Server-safe parts of ValheimPerformanceOptimizations: WearNTear owner-cache, faster `ReleaseNearbyZDOS` scan, physics step cap post-`ZNet.Start`. | `WearNTearCache` (true), `ReleaseScanSpeedup` (true), `MaxPhysicsStepsPerFrame` (0 = leave vanilla) | Yes | No (world simulation load matters more than player count) |
| AsyncSave | Server | `[AsyncSave]` | Pre-sizes `ZDOMan.GetSaveClone()`'s list from the live ZDO count to shrink the main-thread autosave stall. | `PreSizeClone` (true), `LogStalls` (true), `SelfTestSeconds` (0 = off) | Yes | No (measurable on a populated world with 0 players, via `SelfTestSeconds`) |
| GcThrottle | Server | `[GcThrottle]` | Rate-limits `Resources.UnloadUnusedAssets` on an idle server. | `MinIntervalSec` (3600), `OffPeak` (true), `LogSkips` (true) | Yes | No |
| OwnershipRelease | Server | `[OwnershipRelease]` | Shortens the ZDO ownership-release interval from vanilla's 2s so objects change hands faster as players move. | `ReleaseIntervalSec` (0.5) | Yes | Yes (only observable with peers moving between ownership zones) |
| MapSelfTest | Server | `[MapSelfTest]` | Headless unit tests for the SharedMap store (bit set/get, delta round trip, persistence, ServerSideMap import) at load. Logs one PASS/FAIL line. | `Enabled` only | N/A (runs once at load) | No |
| StatsLog | Server | `[StatsLog]` | Persistent JSONL stats + events logging to disk for `tools/analyze.py`. | `IntervalSec` (10, hot-reloadable), `RetentionDays` (30, hot-reloadable), `Dir` ("" = `<config>/smoothserver/stats/`, **not** hot-reloadable — needs a restart) | Mostly (see `Dir`) | No for the plumbing (writes with 0 players); yes to exercise the per-peer/event columns |
| LowLatency | Both | `[LowLatency]` | Writes Steam's global networking config on whichever side it is running (`SteamGameServerNetworkingUtils` on a dedicated server, `SteamNetworkingUtils` on a client, per NOTES §20); logs a before/after readback of every value it touches, and goes quiet if that interface is not initialised. Vanilla never changes either value. | `NagleMicros` (5000 = vanilla; 0 = Nagle off, worth up to 5 ms each way — what `FastLink` sets), `SendBufferBytes` (0 = leave Steam's 512 KB) | Yes (re-applies and re-logs the readback) | No (global config, verifiable from the readback line alone) |
| Compression | Both | `[Compression]` | zstd (ZstdSharp) over Steam sockets using BetterNetworking's trained dictionaries; explicit frame tag + `SS_Caps`/`SS_Ready` handshake so a peer without the mod stays uncompressed. Self-tested at load. Refuses to patch if BetterNetworking is also loaded. | `MinBytes` (256), `Level` (1), `UseBigDictionary` (false) | Yes | Yes (handshake and ratio only show between two real installs) |
| ClientNet | Client | `[Client]` | The client's own ZDO send high-water mark and Steam `SendRateMax`. Inactive on a dedicated server. | `HighWaterBytes` (49152, vanilla 10240), `SendRateMaxBytesPerSec` (1048576, local-only) | Yes | Yes (a client-only module — needs a real client to run at all) |
| SmoothMotion | Client | `[SmoothMotion]` | **Off by default.** Tunes how non-owned objects (other players, mobs) are interpolated between ZDO updates on your screen, by transpiling the two hardcoded literals in `ZSyncTransform.SyncPosition`. Purely local and cosmetic — nothing sent, stored or simulated changes, and objects you own are never touched. Refuses to patch if the literal count is not exactly 2+2. | `Enabled` (**false**), `InterpolationFactor` (0.2 = vanilla; higher = snappier), `ExtrapolateMs` (2000 = vanilla — Valheim already dead-reckons this far; 150-400 hides a lost packet without the rubber-band snap; 0 = no prediction), `ApplyTo` (`Characters` / `All`) | Yes | Yes (a client-only module, and the effect is other people moving) |
| SharedMap | Both | `[Map]` | Server-wide shared map exploration and pins, bit-packed and compressed, replacing ServerSideMap. Imports an existing `<world>.mod.serversidemap.explored` file once. Merges through vanilla `Minimap.Explore`. | `ShareExploration` (true), `SharePins` (true), `SharedPinTypes` (`Icon0..4,Bed,Boss,Hildir1..3`), `ShareDeathPins` (false), `ExcludeCartographyTable` (false), `DeltaHz` (1), `ImportServerSideMapFile` (true) | Yes | Yes (sharing needs two real clients; the import self-test runs solo) |

## Profiles (`[Profiles]`, not a module)

| Setting | Default | Notes |
| --- | --- | --- |
| `Profile` | `Default` | `Default` / `FastLink` / `Custom`. **Synced** — the server dictates it. A preset, not a feature: it writes a fixed table of values into ten settings that already exist (`[SendCadence] SendHz`, `[AdaptiveBudget] CeilingBytes`/`FloorBytes`, `[SteamRates] SendRateMax`, `[Client] HighWaterBytes`/`SendRateMaxBytesPerSec`, `[Compression] Enabled`, `[LowLatency] NagleMicros`, `[FrameRate] TargetFrameRate`, `[CreateBudget] MaxCreatedPerFrame`) at load, on every profile change, and after every live cfg reload. `Default` forces those keys to their shipped defaults; `FastLink` to the LAN-feel set; `Custom` overrides nothing. Every affected key repeats this in its own cfg description. Hot-reloadable in both directions; `tools/cfg.py preset ss fastlink\|default\|custom` flips it. |

Precedence: on `Default` and `FastLink` the profile beats the file for those ten keys, and
re-asserts itself (with a log line) over a hand edit — hand-tuning belongs on `Custom`.
The new value is written into the cfg file, so what you read there is always what is running.

## General settings (`[General]`, not a module)

| Setting | Default | Notes |
| --- | --- | --- |
| `Mode` | `Auto` | `Auto` = a dedicated server (`-batchmode`) runs the server half, everything else runs the client half. Machine-local, never synced. |
| `EnforceClientMod` | `false` | Server: require every connecting client to run SmoothServer or newer; locks the synced config. Default off so the server half works with zero client installs. |
| `HotReload` | `true` | Watches `Nosferatu.SmoothServer.cfg` on disk and reloads it automatically. Machine-local. |
| `SteamSelfTest` | `false` | Diagnostic: logs which half of Steamworks is initialised and which interfaces this build's `ZSteamSocket` calls, read from its IL. Machine-local. |
