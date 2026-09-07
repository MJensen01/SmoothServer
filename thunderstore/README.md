# SmoothServer

Server-only networking tuning for Valheim dedicated servers. Drop `SmoothServer.dll` into the
**server's** `BepInEx/plugins/SmoothServer/` — players install nothing.

## Modules (each has its own `Enabled` toggle)

- **Telemetry** — periodic fps / frame-time / ZDO-rate log line. No gameplay effect.
- **FrameRate** — raises the dedicated server's Unity frame cap (`TargetFrameRate`, default 0 =
  leave vanilla's ~30 fps untouched). 60 is a good default on modern hardware: roughly +4pp of
  one CPU core for half the tick latency.
- **SendCadence** — sends ZDO updates to every connected peer on a fixed interval
  (`SendHz`, default 20) instead of vanilla's one-peer-per-frame round robin. The win scales
  with player count. Note: while this is enabled, BetterNetworking's own "Update Rate" setting
  becomes inert (its Steamworks send-rate tuning is unaffected).
- **SendBudget** — the ZDO send-queue high-water mark / minimum chunk size, exposed as config.
- **CreateBudget** — objects created per frame, exposed as config (default matches vanilla,
  i.e. a no-op until raised).

Config: `Nosferatu.SmoothServer.cfg` in the server's `BepInEx/config/`.

## Dependencies

- [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) 5.4.2333

## Source & issues

https://github.com/MJensen01/SmoothServer — MIT licensed.
