# Third-party code and credits

## Vendored / bundled

### ServerSync (source-vendored, MIT-0)

- Author: **blaxxun-boop**
- Source: https://github.com/blaxxun-boop/ServerSync, commit `c57c2aa54e07cdcc7630d6068699ea781622323e`
- License: **MIT-0** (no attribution required; credited anyway). Full text in
  `src/Vendor/ServerSync-LICENSE.txt`.
- Upstream's single source file (`ConfigSync.cs`) is vendored **verbatim** as
  `src/Vendor/ServerSync.cs`, namespace unchanged so it stays diffable against upstream.
  Do not edit it — re-copy from upstream instead.
- It provides SmoothServer's config synchronisation and the optional client-version
  enforcement (`[General] EnforceClientMod`, default **false**).

### BetterNetworking's zstd dictionaries (bundled as data, MIT)

- Author: **CW_Jesse** (CW-Jesse)
- Source: https://github.com/CW-Jesse/valheim-betternetworking (`dict/small`, `dict/big`)
- License: **MIT**
- `src/dict/small` (110 KB) and `src/dict/big` (512 KB) are `zstd --train` outputs over a
  capture of real Valheim network traffic, embedded verbatim as resources in SmoothServer.dll
  and used by the `Compression` module. No BetterNetworking *code* is copied; the compression
  patch is our own (1-byte frame tag, no double-compression, explicit per-peer handshake) and
  is described in `src/Modules/Net/CompressionModule.cs`.

### ZstdSharp.Port (bundled binary, MIT)

- Author: **Oleg Stepanischev** (oleg-st)
- Source: https://github.com/oleg-st/ZstdSharp, NuGet `ZstdSharp.Port` 0.6.4
- License: **MIT**
- A pure-managed C# port of zstd — no native library, works under Unity's Mono. Shipped as
  `ZstdSharp.dll` next to `SmoothServer.dll`, together with its net472 dependencies
  (`System.Memory`, `System.Buffers`, `System.Numerics.Vectors`,
  `System.Runtime.CompilerServices.Unsafe` — Microsoft, MIT), none of which ship with Valheim
  or BepInEx.

### ServerSideMap (format and patterns, MIT / Unlicense)

- Author: **Mydayyy**
- Source: https://github.com/Mydayyy/Valheim-ServerSideMap
- License: dual **MIT / Unlicense**
- SmoothServer's `SharedMap` module replaces this mod. No source is copied, but two things are
  taken from it and are credited here: (a) the *persistence file layout* of
  `<world>.mod.serversidemap.explored`, which `SharedMap`'s one-shot `ImportServerSideMapFile`
  migration parses so existing worlds keep their explored map; and (b) the merge strategy of
  applying server-fed pixels through the vanilla `Minimap.Explore(x, y)` so they bake into the
  player's own `.fch`. Its known defects (a hardcoded 2048 map size, per-pixel unbatched RPCs,
  an uncompressed one-byte-per-pixel file) are documented and fixed in
  `src/Modules/Map/SharedMapModule.cs`.

## Credited ideas (no code copied)

### BetterNetworking

- Author: **CW_Jesse** (CW-Jesse)
- Source: https://github.com/CW-Jesse/valheim-betternetworking
- License: MIT
- SmoothServer's `SendCadence`/`SendBudget`/`CreateBudget` modules address the same class of
  problem (ZDO send-rate/throughput) as BetterNetworking's "Update Rate" and Steamworks
  send-rate tuning, implemented independently (Harmony prefix/transpiler against the
  0.221.12 decompile). No BetterNetworking *source* is included in this repository; its two
  trained zstd dictionaries are bundled as data — see "Vendored / bundled" above.
- **Incompatible with SmoothServer's `Compression` module.** Both wrap `ZSteamSocket`'s send
  queue and cannot coexist; SmoothServer detects BetterNetworking in the BepInEx chainloader and
  refuses to install its own compression patches (a loud `FAILED(...)` line in the module
  summary) rather than declaring a hard `BepInIncompatibility`, which would stop the whole
  plugin loading mid-rollout. Uninstall BetterNetworking to use SmoothServer compression.
- Note: while SmoothServer's `SendCadence` module is enabled, BetterNetworking's own "Update Rate"
  option becomes inert (SmoothServer's patch runs at higher Harmony priority and returns before
  BetterNetworking's runs) — its Steamworks send-rate patch is unaffected and still applies.
  Server owners running both should be aware of this overlap.

### Serverside Simulations

- Author: **ddormer**
- Source: https://github.com/ddormer/valheim-serverside
- License: none published by upstream (all rights reserved by default) — credited here for
  the architectural idea (server-authoritative simulation ownership), not for any code, which
  is why nothing from that repository is included or adapted here.

### ValheimPerformanceOptimizations (code adapted, MIT)

- Author: **ontrigger**
- Source: https://github.com/ontrigger/ValheimPerformanceOptimizations
- License: **MIT**, Copyright (c) 2021 ontrigger (LICENSE verified against the upstream clone)
- SmoothServer's `VPOServer` module (`src/Modules/Server/VPOServerModule.cs`) **adapts code**
  from two of VPO's patches, re-derived against the 0.221.12 decompile:
  `WearNTearPatches` (collider -> WearNTear owner memoisation, own-collider HashSet, per-call
  centre-of-mass cache) and `ZDOManReleaseNearbyPatch` (single owner resolution per ZDO in the
  ownership-release scan). It also re-implements the idea behind `MaxPhysicsTimeStepPatch`
  (`Time.maximumDeltaTime = n * Time.fixedDeltaTime`), applied after `ZNet.Start` because VPO's
  own hook, `FejdStartup.Awake`, never runs on a headless server.
- Nothing else from VPO is used: its remaining patches are terrain/water/audio/rendering
  (client-only), and its `Unity.Burst`/`Unity.Jobs`/`Unity.Profiling` dependencies are not taken.

### SmoothSave / Network / DedicatedServer

- Author: **blaxxun-boop** (Smoothbrain)
- License: **none stated** upstream — so nothing is copied.
- SmoothServer's `AsyncSave` module addresses the same symptom (the world-save freeze) from our
  own decompile. Finding of record: vanilla 0.221.12 already writes the world on
  `ZNet.SaveWorldThread`; the residual main-thread stall is `ZDOMan.PrepareSave`'s ZDO clone,
  which is what SmoothServer shrinks.

### No_More_Crashes

- Author: **KGvalheim** — deprecated, license unverified, no source consulted.
- SmoothServer's `SendQueueGuard` re-implements the *idea* (survive Steam send-queue errors such
  as `k_EResultLimitExceeded` instead of throwing) from the public Steamworks error codes and our
  own `ZSteamSocket` decompile.

### ResourceUnloadOptimizer

- Author: **Azumatt** — deprecated, repository removed, license unknown, no source consulted.
- SmoothServer's `GcThrottle` re-implements the idea against `Game.CollectResources` in the
  0.221.12 decompile.

## Runtime dependency (not bundled)

- **BepInEx** / **BepInExPack_Valheim** (denikson), 5.4.2333 — LGPL-2.1 (BepInEx core). Not
  distributed with this mod; required separately (see the Thunderstore dependency in
  `thunderstore/manifest.json`).
- **Harmony (Lib.Harmony / 0Harmony)** — MIT, distributed as part of BepInEx, referenced but
  not bundled.

## Build-time only (not shipped in the plugin DLL)

- `Microsoft.NETFramework.ReferenceAssemblies` (Microsoft, MIT) — compile-time only.
- `BepInEx.AssemblyPublicizer.MSBuild` (BepInEx project, MIT) — compile-time only, rewrites
  reference assemblies so private members are visible; does not affect the shipped DLL.
