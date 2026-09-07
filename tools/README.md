# tools/

Scripts for turning `StatsLog`'s output into something readable. Nothing here is required to run
SmoothServer — `StatsLog` writes its JSONL files on its own; these are just the offline
collector/analyzer pair for a multi-day look at server health.

## What each file is

- **`analyze.py`** — stdlib-only Python 3 (no `pip install` needed). Reads the JSONL files
  `StatsLogModule` writes and produces a single markdown report.
- **`collect-host.sh`** — a small bash script that takes one point-in-time snapshot of host-level
  numbers (`docker stats` per container, 1-minute load average, free/available memory, and each
  container's last `Connections N ...` log line) and appends it as one JSON line to
  `host-YYYY-MM-DD.jsonl`. Read-only — it never touches container state. Meant to be run on a
  timer alongside the game server(s), so `analyze.py` can correlate host CPU/memory with
  in-game sessions.
- **`smoothserver-host-collect.service`** / **`smoothserver-host-collect.timer`** — systemd unit
  and timer that run `collect-host.sh` every 60 seconds. Written for the dev lab's paths
  (`/opt/modlab/stats/`, user `nosferatu`) — copy and edit the paths/user for your own host before
  installing.

## Running the collector

`collect-host.sh` only makes sense on the machine actually running the game server container(s)
(it shells out to `docker`, `/proc/loadavg`, `free`). One-off run:

```bash
./collect-host.sh /path/to/host-stats-dir
# defaults to /opt/modlab/stats/host if no argument is given
```

To run it continuously, install the systemd timer (edit the paths/user in the `.service` file
first if yours differ from the lab's):

```bash
sudo cp smoothserver-host-collect.service smoothserver-host-collect.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now smoothserver-host-collect.timer
# check it's firing:
systemctl list-timers smoothserver-host-collect.timer
journalctl -u smoothserver-host-collect.service --since -10min
```

The host-stats collector is optional — `analyze.py` runs fine with only `--stats` directories and
no `--host` directory; you just lose the host CPU/mem correlation columns.

## Running the analyzer

```bash
python3 analyze.py <stats-dir> [<stats-dir> ...] --host <host-dir> [--out REPORT.md]
```

- `<stats-dir>` — one or more directories containing `stats-*.jsonl` / `events-*.jsonl`, i.e. the
  directory `StatsLogModule` writes to: `<BepInEx config dir>/smoothserver/stats/` by default
  (configurable via `[StatsLog] Dir`; for a Docker-mapped server that's typically something like
  `/opt/valheim-test/config/bepinex/smoothserver/stats/`). Pass more than one directory to combine
  logs from multiple server instances into one report.
- `--host <host-dir>` — optional directory of `host-*.jsonl` files from `collect-host.sh`, used to
  add per-session host CPU/memory numbers to the report.
- `--out REPORT.md` — output path. Defaults to `REPORT-<from-date>-<to-date>.md` next to the first
  stats directory.

Example, against the dev lab's test server:

```bash
python3 tools/analyze.py /opt/valheim-test/config/bepinex/smoothserver/stats \
    --host /opt/modlab/stats/host \
    --out ~/smoothserver-report.md
```

## What the report contains

`analyze.py` groups records into **sessions** — maximal runs of consecutive stats records with at
least one player connected, tolerating gaps up to 5 minutes (`SESSION_GAP_SEC`) so a brief
reconnect or an alt-tab does not split one session into two. The generated markdown has these
sections, in order:

- **Sessions** — start/end time, duration, player names, and (if `--host` was given) host CPU/mem
  during the session.
- **Frame time (whole range)** — average/percentile frame time and fps across every record read.
- **Per-player network stats** — RTT, pending bytes, queued bytes, per player, aggregated across
  all sessions.
- **AdaptiveBudget backoff time per player** — how much wall-clock time each player's connection
  spent congested (backed off), and the target byte budget while congested.
- **Compression** — raw vs. wire byte totals and ratio, in and out, from the deltas `StatsLog`
  records each interval.
- **World save stalls** — count and worst stall duration, from the `events-*.jsonl` `save` events.
- **ZDO growth per day** — ZDO count at the start and end of each UTC day, to spot unbounded
  growth.
- **Other events** — GC sweeps, `SendQueueGuard` drop episodes, and config reloads, from
  `events-*.jsonl`.
- **Top 10 worst 10-second windows** — the ten worst 10-second windows by worst single frame time,
  to find specific moments worth digging into further (e.g. against `docker logs` for the same
  timestamp).

The report is plain markdown — read it directly, or paste it wherever you review server health
(an issue, a chat, a wiki page).
