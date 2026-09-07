# SmoothServer

A BepInEx 5 mod for Valheim 0.221.12, built and maintained for a small dedicated-server group
and released here for anyone to use. MIT licensed.

Networking and performance tuning for dedicated servers (frame rate, ZDO send cadence, per-peer
adaptive budgets, async save, telemetry). Since 0.3.0 the same DLL also carries an **optional
client half** — zstd packet compression, a client send budget and a server-wide shared map —
but `[General] EnforceClientMod` defaults to `false`, so the server half still works with
**nothing installed on the client** and vanilla players can always join.

Replaces **BetterNetworking** and **ServerSideMap**; both must be uninstalled from the server
and from every client (see `thunderstore/README.md`). `SharedMap` imports an existing
`<world>.mod.serversidemap.explored` file once on first start, so no exploration is lost.

Pre-release (see version plan below), built and tested against Valheim `0.221.12` / BepInEx
`5.4.2333`. A 1.0-compatible build will follow once the game updates.

## Install (server owners)

1. Drop **all six DLLs** from the release zip (`SmoothServer.dll`, `ZstdSharp.dll`,
   `System.Memory.dll`, `System.Buffers.dll`, `System.Numerics.Vectors.dll`,
   `System.Runtime.CompilerServices.Unsafe.dll`) into `BepInEx/plugins/SmoothServer/` on the
   dedicated server. Depends on
   [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
   5.4.2333.
2. Start the server once to generate `BepInEx/config/Nosferatu.SmoothServer.cfg`, then edit the
   values you want (see below).
3. Players do not need to install anything. Installing the same package on their machines too
   additionally enables compression, the client send budget and the shared map.

## Config overview

One file, one section per module, each with its own `Enabled` toggle. Full list, defaults and
descriptions in [`docs/MODULES.md`](docs/MODULES.md); install/usage detail in
[`thunderstore/README.md`](thunderstore/README.md).

- `[General]` — `Mode` (Auto/Server/Client), `EnforceClientMod` (default false), `HotReload`,
  `SteamSelfTest`.
- `[Profiles]` — `Profile` (`Default` / `FastLink` / `Custom`), the one-setting preset for
  the whole mod. See below.
- Server: `Telemetry`, `FrameRate`, `SendCadence`, `SendBudget`, `CreateBudget`, `PeerTelemetry`,
  `AdaptiveBudget`, `SteamRates`, `SendQueueGuard`, `SyncListCache`, `VPOServer`, `AsyncSave`,
  `GcThrottle`, `OwnershipRelease`, `MapSelfTest`, `StatsLog`.
- Both ends: `Compression`, `LowLatency`, `Map` (SharedMap). Client only: `Client`
  (ClientNet), `SmoothMotion` (off by default).
- `HotReload` — cfg edits on a running server are picked up live, no restart, for almost every
  module (a handful of settings need a restart — see the Hot-reload column in
  [`docs/MODULES.md`](docs/MODULES.md)).

## Profiles — `FastLink`

`[Profiles] Profile` tunes the whole mod with one setting. It is a preset, not a feature: it
writes a fixed table of values into settings that already exist, at load, whenever the profile
changes, and after every live cfg reload — so the cfg file, `tools/cfg.py get` and each
module's own log line always agree on the one number that is running.

| Profile | What it does |
| --- | --- |
| `Default` | The ten keys below are forced back to their shipped defaults. |
| `FastLink` | *"Make it feel like LAN"* — a small group on strong PCs and good links. |
| `Custom` | The plugin overrides nothing; every value in the file stands. |

`FastLink` sets exactly: `[SendCadence] SendHz=60`, `[AdaptiveBudget] CeilingBytes=262144`
`FloorBytes=32768`, `[SteamRates] SendRateMax=4194304`, `[Client] HighWaterBytes=131072`
`SendRateMaxBytesPerSec=4194304`, `[Compression] Enabled=true`, `[LowLatency] NagleMicros=0`,
`[FrameRate] TargetFrameRate=60`, `[CreateBudget] MaxCreatedPerFrame=20`. Nothing else is
touched, and every affected key says so in its own cfg description.

`Profile` is **synced**, so a client joining a `FastLink` server runs `FastLink` too —
including the machine-local keys ServerSync does not push. Hand-tuning belongs on
`Profile=Custom`: on `Default` or `FastLink` the plugin re-asserts its table over a hand edit
(and logs that it did).

`tools/cfg.py preset ss fastlink|default|custom` flips it on a running server.

## Stats logging

`StatsLog` (server-side, on by default) writes newline-delimited JSON to
`<BepInEx config dir>/smoothserver/stats/`: a `stats-YYYY-MM-DD.jsonl` snapshot every
`IntervalSec` (fps/frame time, ZDO counts, per-peer network + budget + compression state) and an
`events-YYYY-MM-DD.jsonl` stream of joins/leaves, save stalls, GC sweeps, AdaptiveBudget backoff
transitions, SendQueueGuard drops and config reloads. Files rotate daily and are pruned past
`RetentionDays` (default 30). `tools/analyze.py` (stdlib-only Python) turns a few days of these
logs into a markdown report — see [`tools/README.md`](tools/README.md).

## Credits

- Config sync: [ServerSync](https://github.com/blaxxun-boop/ServerSync) (**blaxxun-boop**, MIT-0),
  vendored as source.
- Networking-tuning ideas and trained zstd dictionaries from
  [BetterNetworking](https://github.com/CW-Jesse/valheim-betternetworking) (**CW_Jesse**, MIT).
- `VPOServer` adapts patches from
  [ValheimPerformanceOptimizations](https://github.com/ontrigger/ValheimPerformanceOptimizations)
  (**ontrigger**, MIT).
- Server-authoritative simulation-ownership idea from
  [Serverside Simulations](https://github.com/ddormer/valheim-serverside) (**ddormer**, no
  published license — credited for the idea, not the code; SmoothServer's implementation is
  independent).
- `SharedMap` takes its persistence-file layout and merge strategy from
  [ServerSideMap](https://github.com/Mydayyy/Valheim-ServerSideMap) (**Mydayyy**, MIT/Unlicense);
  no source is copied.
- See [`THIRD_PARTY.md`](THIRD_PARTY.md) for the full list and license texts.

## Building from source

See `src/Directory.Build.props` for how game/BepInEx references resolve — either via the
build-lab's container mount, or via `VALHEIM_MANAGED` / `VALHEIM_BEPINEX_CORE` environment
variables (or `-p:ValheimManagedDir=... -p:BepInExCoreDir=...`) pointing at a local Valheim +
BepInEx install. CI (`.github/workflows/build.yml`) fetches both from scratch on every push.

```powershell
$env:VALHEIM_MANAGED = "D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed"
$env:VALHEIM_BEPINEX_CORE = "D:\SteamLibrary\steamapps\common\Valheim\BepInEx\core"
dotnet build src -c Release
python scripts\package.py   # builds thunderstore/dist zip
```

## Versioning

Currently `0.4.0` (renamed from `OrionNet` at 0.2.0; 0.3.0 added the optional client half,
compression, the shared map and per-peer adaptive budgets; 0.3.1 fixed a server-build Steam
interface bug in `SendQueueGuard`/`SteamRates`/`PeerTelemetry` and added `StatsLog` + the
`tools/` analysis scripts; 0.4.0 added the `FastLink` profile, the `LowLatency` module and
the client-side `SmoothMotion` module). See [`thunderstore/CHANGELOG.md`](thunderstore/CHANGELOG.md) for the
full history.

Name and Thunderstore namespace are **final**: package namespace/team `Nosferatu`, package
`SmoothServer`. Both are immutable once the first version is uploaded — see
[`NoVikingLeftBehind`](https://github.com/MJensen01/NoVikingLeftBehind), its companion QoL mod,
and `PUBLISHING.md` in `MJensen01`'s local workspace for the upload steps.

**Upgrading from OrionNet:** the plugin GUID and config file name changed, so the server
generates a fresh `Nosferatu.SmoothServer.cfg` with defaults on first boot — copy your old
values across and delete the old `net.mjensen.orion.*.cfg` file and plugin folder.

## History

Split from the combined [`valheim-mods`](https://github.com/MJensen01/valheim-mods) repo
(archived) on 2026-09-07, at the point both mods reached their first Thunderstore release
(SmoothServer 0.2.0). Full history up to that point lives in the archived repo.
