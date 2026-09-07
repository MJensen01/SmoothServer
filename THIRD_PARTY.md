# Third-party code and credits

## Credited ideas (no code copied)

### BetterNetworking

- Author: **CW_Jesse** (CW-Jesse)
- Source: https://github.com/CW-Jesse/valheim-betternetworking
- License: MIT
- SmoothServer's `SendCadence`/`SendBudget`/`CreateBudget` modules address the same class of
  problem (ZDO send-rate/throughput) as BetterNetworking's "Update Rate" and Steamworks
  send-rate tuning, implemented independently (Harmony prefix/transpiler against the
  0.221.12 decompile). No BetterNetworking source is included in this repository.
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
