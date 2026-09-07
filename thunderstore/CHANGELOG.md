# Changelog — SmoothServer

## 0.3.0 (2026-09-07)

**SmoothServer is now a both-ends mod.** The same `SmoothServer.dll` runs on a dedicated server
and in a player's BepInEx profile; `[General] Mode` (Auto/Server/Client) picks the half, and a
module that does not belong to the running half reports `disabled(side)` and patches nothing.

`[General] EnforceClientMod` **defaults to `false`**, so nothing changes for existing server
admins: SmoothServer still works as a pure server-side mod with zero client installs, and
vanilla clients can still join. Turn it on only if your whole group installs the mod.

### Read this before updating

- **BetterNetworking must be uninstalled** — on the server *and* on every client. SmoothServer's
  `Compression` module replaces it and the two cannot coexist (both wrap `ZSteamSocket`'s send
  queue). If BetterNetworking is present, `Compression` refuses to patch and reports
  `FAILED(...)`; the rest of the plugin still loads.
- **ServerSideMap must be uninstalled** — on the server *and* on every client. `SharedMap`
  replaces it. On first start SmoothServer imports your existing
  `<world>.mod.serversidemap.explored` file automatically (once), so no exploration is lost.
- **The zip now contains six DLLs, not one.** `SmoothServer.dll` plus `ZstdSharp.dll`,
  `System.Memory.dll`, `System.Buffers.dll`, `System.Numerics.Vectors.dll` and
  `System.Runtime.CompilerServices.Unsafe.dll`. A mod manager installs all of them; if you copy
  files by hand, copy all six into `BepInEx/plugins/SmoothServer/`.

### New server modules

- **PeerTelemetry** `[PeerTelemetry]` — per-peer ping, throughput and Steam send-queue sampling.
  No patches; it is the input to AdaptiveBudget and the source of the per-peer log line.
- **AdaptiveBudget** `[AdaptiveBudget]` — per-peer ZDO send budget derived from each peer's
  measured bandwidth-delay product, clamped and EMA-smoothed, backing off while Steam's *pending*
  byte count says the pipe is congested. SendBudget's configured value becomes the fallback.
- **SteamRates** `[SteamRates]` — raises Steam's `SendRateMax` on the game-server networking
  interface (default 1 MB/s). `SendRateMin` is deliberately left at vanilla.
- **SendQueueGuard** `[SendQueueGuard]` — defers (never drops) sends while a peer's Steam queue
  is backed up, and swallows the "Steamworks is not initialized" throws that can happen mid-handshake.
- **SyncListCache** `[SyncListCache]` — caches the per-peer sector scan in `ZDOMan.CreateSyncList`
  for a short window; the filter and sort still run on every send.
- **VPOServer** `[VPOServer]` — server-safe parts of ValheimPerformanceOptimizations (MIT):
  `WearNTear` support caching and a faster `ReleaseNearbyZDOS` scan.
- **AsyncSave** `[AsyncSave]` — pre-sizes `ZDOMan.GetSaveClone()`'s list from the live ZDO count.
  Measured on a 136 000-ZDO world: main-thread autosave stall **67 ms → 47 ms** and **78 ms → 45 ms**.
- **GcThrottle** `[GcThrottle]` — rate-limits `Resources.UnloadUnusedAssets` on an idle server.
- **OwnershipRelease** `[OwnershipRelease]` — shortens the ZDO ownership-release interval from
  vanilla's 2 s (default 0.5 s), so objects change hands faster as players move.

### New both-ends / client modules

- **Compression** `[Compression]`, both ends — zstd (ZstdSharp, pure managed) over Steam sockets
  using BetterNetworking's trained dictionaries. Explicit 1-byte frame tag and an
  `SS_Caps`/`SS_Ready` handshake, so the receiver never guesses and never uses an exception as a
  signal; a peer without the mod simply stays uncompressed. Self-tested at load.
- **SharedMap** `[Map]`, both ends — server-side shared map exploration and pins, replacing
  ServerSideMap. Bit-packed and compressed store (**1.7 KB** where ServerSideMap wrote **4.2 MB**
  for the same world), map size read from the client instead of hardcoded, deltas batched at
  1 Hz into sparse chunks instead of one RPC per pixel. Merges through vanilla `Minimap.Explore`,
  so shared pixels bake into your own map file. Optional death-pin sharing.
- **MapSelfTest** `[MapSelfTest]`, server — headless unit tests for the map codec, run at load.
- **ClientNet** `[Client]`, client only — the client's own ZDO send high-water mark (default
  48 KB) and Steam `SendRateMax`. Never active on a dedicated server.

### Other

- Modules are now discovered by reflection: one `[BepInPlugin]`, one config file, one module
  summary line, hot reload covering every module.
- Config sharing via ServerSync — the server's values win on clients that run the mod.
- `SendBudget`'s high-water hook now defers to AdaptiveBudget per peer;
  `[AdaptiveBudget] UseCallSiteSwap` is a legacy path and now defaults to `false`.

## 0.2.0 (2026-09-06)
- **Renamed** from `OrionNet`. New plugin GUID `Nosferatu.SmoothServer`, new config file
  `Nosferatu.SmoothServer.cfg`. Old `net.mjensen.orion.net.cfg` settings are **not** migrated —
  re-apply them once.
- Live config reload (hot reload): edits to the cfg file are picked up on a running server
  without a restart (`[General] HotReload`).

## 0.1.0 (2026-09-06)
- Initial release. Modules: `Telemetry`, `FrameRate`, `SendCadence`, `SendBudget`, `CreateBudget`.
- Server-only; no client install required.
