# SmoothServer

A BepInEx 5 mod for Valheim 0.221.12, built and maintained for a small dedicated-server group
and released here for anyone to use. MIT licensed.

Server-only networking tuning (frame rate, ZDO send cadence/budgets, telemetry). Nothing to
install on the client.

Pre-release (see version plan below), built and tested against Valheim `0.221.12` / BepInEx
`5.4.2333`. A 1.0-compatible build will follow once the game updates.

## Install (server owners)

1. Drop `SmoothServer.dll` into `BepInEx/plugins/SmoothServer/` on the dedicated server. Depends
   on [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
   5.4.2333.
2. Start the server once to generate `BepInEx/config/Nosferatu.SmoothServer.cfg`, then edit the
   values you want (see below).
3. Players do not need to install anything — this mod is server-only.

## Config overview

- `Telemetry` — periodic fps/frame-time/ZDO-rate log line, no gameplay effect.
- `FrameRate` — raise the dedicated server's Unity frame cap (default 0 = untouched, vanilla ~30).
- `SendCadence` — send ZDO updates to every connected peer on a fixed interval instead of
  vanilla's one-peer-per-frame round robin (bigger effect the more players are online).
- `SendBudget`, `CreateBudget` — the ZDO send-queue high-water mark and objects-created-per-frame
  cap, exposed as config instead of hardcoded.
- `HotReload` — cfg edits on a running server are picked up live, no restart.

## Credits

- Networking-tuning ideas informed by [BetterNetworking](https://github.com/CW-Jesse/valheim-betternetworking)
  (**CW_Jesse**, MIT) and by [Serverside Simulations](https://github.com/ddormer/valheim-serverside)
  (**ddormer**, no published license — credited for the idea, not the code; SmoothServer's
  send-cadence/budget modules are an independent implementation).
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

Currently `0.2.0` (renamed from `OrionNet`; adds live config reload).

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
