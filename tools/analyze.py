#!/usr/bin/env python3
"""
SmoothServer stats analyzer. Stdlib only (python3, no pip installs required).

Reads the JSONL files StatsLogModule writes (stats-YYYY-MM-DD.jsonl / events-YYYY-MM-DD.jsonl)
from one or more --stats directories, plus one --host directory of host-YYYY-MM-DD.jsonl files
from collect-host.sh, and produces a markdown report: sessions, frame-time stats, per-player
network stats, AdaptiveBudget backoff time, compression ratio, save stalls, ZDO growth per day,
host CPU/mem per session, and the worst 10-second windows.

Usage:
    python3 analyze.py <stats-dir> [<stats-dir> ...] --host <host-dir> [--out REPORT.md]

    stats-dir   one or more directories containing stats-*.jsonl / events-*.jsonl
                (e.g. the container's mapped dir:
                 /opt/valheim-test/config/bepinex/smoothserver/stats/)
    --host      directory containing host-*.jsonl from collect-host.sh (optional)
    --out       output path (default: REPORT-<from>-<to>.md next to the first stats dir)

A "session" is a maximal run of consecutive stats records with players.count > 0, allowing gaps
of up to SESSION_GAP_SEC (default 5 minutes) between records without ending the session (a
player alt-tabbing or a brief connection blip should not split a session in two).
"""
import argparse
import glob
import json
import os
import statistics
import sys
from datetime import datetime, timezone

SESSION_GAP_SEC = 300  # gap between records (by ts, not just consecutive lines) before a new session starts


def parse_ts(s):
    # StatsLog: "yyyy-MM-ddTHH:mm:ss.fffZ" ; collect-host.sh: "yyyy-MM-ddTHH:mm:ssZ"
    s = s.rstrip("Z")
    for fmt in ("%Y-%m-%dT%H:%M:%S.%f", "%Y-%m-%dT%H:%M:%S"):
        try:
            return datetime.strptime(s, fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    raise ValueError("unparseable timestamp: " + s)


def load_jsonl(paths_glob):
    records = []
    for path in sorted(glob.glob(paths_glob)):
        with open(path, "r", encoding="utf-8") as f:
            for lineno, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError as e:
                    sys.stderr.write("WARN: %s:%d: skipping malformed line: %s\n" % (path, lineno, e))
    return records


def load_all(stats_dirs, host_dir):
    stats, events, host = [], [], []
    for d in stats_dirs:
        stats.extend(load_jsonl(os.path.join(d, "stats-*.jsonl")))
        events.extend(load_jsonl(os.path.join(d, "events-*.jsonl")))
    if host_dir:
        host.extend(load_jsonl(os.path.join(host_dir, "host-*.jsonl")))

    for r in stats:
        r["_dt"] = parse_ts(r["ts"])
    for r in events:
        r["_dt"] = parse_ts(r["ts"])
    for r in host:
        r["_dt"] = parse_ts(r["ts"])

    stats.sort(key=lambda r: r["_dt"])
    events.sort(key=lambda r: r["_dt"])
    host.sort(key=lambda r: r["_dt"])
    return stats, events, host


def player_count(r):
    """Robust accessor: tolerates a malformed/legacy record where "players" ended up as a
    JSON-encoded string instead of a nested object (fixed in StatsLog 0.3.1; older logs from
    before the fix may still contain it)."""
    players = r.get("players")
    if isinstance(players, str):
        try:
            players = json.loads(players)
        except (ValueError, TypeError):
            players = {}
    if not isinstance(players, dict):
        players = {}
    return players.get("count", 0) or 0


def player_names(r):
    players = r.get("players")
    if isinstance(players, str):
        try:
            players = json.loads(players)
        except (ValueError, TypeError):
            players = {}
    if not isinstance(players, dict):
        players = {}
    return players.get("names", []) or []


def percentile(values, p):
    if not values:
        return None
    s = sorted(values)
    k = (len(s) - 1) * p
    f, c = int(k), min(int(k) + 1, len(s) - 1)
    if f == c:
        return s[f]
    return s[f] + (s[c] - s[f]) * (k - f)


def find_sessions(stats):
    sessions = []
    cur = None
    for r in stats:
        count = player_count(r)
        if count > 0:
            if cur is None:
                cur = {"start": r["_dt"], "end": r["_dt"], "records": [r]}
            elif (r["_dt"] - cur["end"]).total_seconds() <= SESSION_GAP_SEC:
                cur["end"] = r["_dt"]
                cur["records"].append(r)
            else:
                sessions.append(cur)
                cur = {"start": r["_dt"], "end": r["_dt"], "records": [r]}
        else:
            if cur is not None and (r["_dt"] - cur["end"]).total_seconds() > SESSION_GAP_SEC:
                sessions.append(cur)
                cur = None
    if cur is not None:
        sessions.append(cur)
    return sessions


def fmt_dt(dt):
    return dt.strftime("%Y-%m-%d %H:%M:%S UTC")


def fmt_num(x, nd=1):
    if x is None:
        return "n/a"
    return ("%." + str(nd) + "f") % x


def host_window(host, start, end, pad_sec=90):
    lo = start
    hi = end
    from datetime import timedelta
    lo -= timedelta(seconds=pad_sec)
    hi += timedelta(seconds=pad_sec)
    return [h for h in host if lo <= h["_dt"] <= hi]


def cpu_mem_for(host_recs, container="valheim-test"):
    cpus, mems = [], []
    for h in host_recs:
        for c in h.get("containers", []):
            if c.get("name") != container:
                continue
            cp = c.get("cpuPerc", "")
            mp = c.get("memPerc", "")
            try:
                if cp.endswith("%"):
                    cpus.append(float(cp[:-1]))
            except (ValueError, AttributeError):
                pass
            try:
                if mp.endswith("%"):
                    mems.append(float(mp[:-1]))
            except (ValueError, AttributeError):
                pass
    return cpus, mems


def build_report(stats, events, host, stats_dirs, host_dir):
    lines = []
    if not stats:
        lines.append("# SmoothServer stats report\n")
        lines.append("No stats records found in: " + ", ".join(stats_dirs) + "\n")
        return "\n".join(lines), "none", "none"

    from_ts = stats[0]["_dt"]
    to_ts = stats[-1]["_dt"]

    lines.append("# SmoothServer stats report")
    lines.append("")
    lines.append("Sources: `%s`%s" % (
        "`, `".join(stats_dirs),
        (" + host `%s`" % host_dir) if host_dir else ""))
    lines.append("")
    lines.append("Range: **%s** to **%s** (%d stats records, %d events%s)" % (
        fmt_dt(from_ts), fmt_dt(to_ts), len(stats), len(events),
        ", %d host samples" % len(host) if host else ""))
    lines.append("")

    # ---- sessions ------------------------------------------------------------------------
    sessions = find_sessions(stats)
    lines.append("## Sessions")
    lines.append("")
    if not sessions:
        lines.append("No session with players.count > 0 was recorded in this window.")
    else:
        lines.append("| # | Start | End | Duration | Peak players | Avg FPS | Avg frame ms | Worst frame ms | Host CPU avg/max | Host mem avg/max |")
        lines.append("|---|---|---|---|---|---|---|---|---|---|")
        for i, s in enumerate(sessions, 1):
            dur_min = (s["end"] - s["start"]).total_seconds() / 60.0
            peak = max((player_count(r) for r in s["records"]), default=0)
            fps_vals = [r["fps"] for r in s["records"] if r.get("fps") is not None]
            avg_ms_vals = [r["frameAvgMs"] for r in s["records"] if r.get("frameAvgMs") is not None]
            worst_ms_vals = [r["frameWorstMs"] for r in s["records"] if r.get("frameWorstMs") is not None]
            hrecs = host_window(host, s["start"], s["end"]) if host else []
            cpus, mems = cpu_mem_for(hrecs) if hrecs else ([], [])
            cpu_str = "%s / %s" % (fmt_num(statistics.mean(cpus)) if cpus else "n/a",
                                    fmt_num(max(cpus)) if cpus else "n/a")
            mem_str = "%s / %s" % (fmt_num(statistics.mean(mems)) if mems else "n/a",
                                    fmt_num(max(mems)) if mems else "n/a")
            lines.append("| %d | %s | %s | %.1f min | %d | %s | %s | %s | %s | %s |" % (
                i, fmt_dt(s["start"]), fmt_dt(s["end"]), dur_min, peak,
                fmt_num(statistics.mean(fps_vals)) if fps_vals else "n/a",
                fmt_num(statistics.mean(avg_ms_vals)) if avg_ms_vals else "n/a",
                fmt_num(max(worst_ms_vals)) if worst_ms_vals else "n/a",
                cpu_str, mem_str))
    lines.append("")

    # ---- frame time / fps dips ------------------------------------------------------------
    all_avg_ms = [r["frameAvgMs"] for r in stats if r.get("frameAvgMs") is not None]
    all_worst_ms = [r["frameWorstMs"] for r in stats if r.get("frameWorstMs") is not None]
    all_fps = [r["fps"] for r in stats if r.get("fps") is not None]
    lines.append("## Frame time (whole range)")
    lines.append("")
    lines.append("| Metric | Avg | P95 | Worst |")
    lines.append("|---|---|---|---|")
    lines.append("| frameAvgMs | %s | %s | %s |" % (
        fmt_num(statistics.mean(all_avg_ms)) if all_avg_ms else "n/a",
        fmt_num(percentile(all_avg_ms, 0.95)) if all_avg_ms else "n/a",
        fmt_num(max(all_avg_ms)) if all_avg_ms else "n/a"))
    lines.append("| frameWorstMs | %s | %s | %s |" % (
        fmt_num(statistics.mean(all_worst_ms)) if all_worst_ms else "n/a",
        fmt_num(percentile(all_worst_ms, 0.95)) if all_worst_ms else "n/a",
        fmt_num(max(all_worst_ms)) if all_worst_ms else "n/a"))
    dips = [r for r in stats if r.get("fps") is not None and r["fps"] < 50 and player_count(r) > 0]
    lines.append("")
    lines.append("FPS dips below 50 (target 60, players present): **%d** of %d records with players." % (
        len(dips), sum(1 for r in stats if player_count(r) > 0)))
    lines.append("")

    # ---- per-player network stats ---------------------------------------------------------
    per_player = {}  # id -> {"name":..., "rtt":[], "pending":[], "framed":0, "total":0}
    for r in stats:
        for p in r.get("peers", []):
            pid = p.get("id", "?")
            e = per_player.setdefault(pid, {"name": p.get("name", "?"), "rtt": [], "pending": [], "framed": 0, "total": 0})
            e["name"] = p.get("name", e["name"])
            if p.get("rttMs") is not None:
                e["rtt"].append(p["rttMs"])
            if p.get("pendingBytes") is not None:
                e["pending"].append(p["pendingBytes"])
            e["total"] += 1
            if p.get("framed"):
                e["framed"] += 1

    lines.append("## Per-player network stats")
    lines.append("")
    if not per_player:
        lines.append("No peer records (no players connected while stats were logged).")
    else:
        lines.append("| Player | Samples | RTT p50 | RTT p95 | Pending bytes peak | Framed % |")
        lines.append("|---|---|---|---|---|---|")
        for pid, e in sorted(per_player.items(), key=lambda kv: kv[1]["name"]):
            framed_pct = 100.0 * e["framed"] / e["total"] if e["total"] else 0.0
            lines.append("| %s (`%s`) | %d | %s ms | %s ms | %s B | %.0f%% |" % (
                e["name"], pid, e["total"],
                fmt_num(percentile(e["rtt"], 0.50)) if e["rtt"] else "n/a",
                fmt_num(percentile(e["rtt"], 0.95)) if e["rtt"] else "n/a",
                fmt_num(max(e["pending"]), 0) if e["pending"] else "n/a",
                framed_pct))
    lines.append("")

    # ---- AdaptiveBudget backoff time per player -------------------------------------------
    backoff_spans = {}  # id -> total seconds backing off (from events, start/end pairs)
    open_backoff = {}
    for ev in events:
        t = ev.get("type")
        if t not in ("budget_backoff_start", "budget_backoff_end"):
            continue
        pid = ev.get("id", "?")
        name = ev.get("name", "?")
        if t == "budget_backoff_start":
            open_backoff[pid] = (ev["_dt"], name)
        elif t == "budget_backoff_end" and pid in open_backoff:
            start_dt, name = open_backoff.pop(pid)
            secs = (ev["_dt"] - start_dt).total_seconds()
            e = backoff_spans.setdefault(pid, {"name": name, "seconds": 0.0, "episodes": 0})
            e["seconds"] += max(0.0, secs)
            e["episodes"] += 1

    lines.append("## AdaptiveBudget backoff time per player")
    lines.append("")
    if not backoff_spans:
        lines.append("No completed backoff episodes recorded (either AdaptiveBudget never backed "
                      "off, or no matching start/end pair fell inside this window).")
    else:
        lines.append("| Player | Backoff episodes | Total backoff time |")
        lines.append("|---|---|---|")
        for pid, e in sorted(backoff_spans.items(), key=lambda kv: -kv[1]["seconds"]):
            lines.append("| %s (`%s`) | %d | %.1f s |" % (e["name"], pid, e["episodes"], e["seconds"]))
    lines.append("")

    # ---- compression ratio -----------------------------------------------------------------
    raw_out = sum(r.get("compression", {}).get("rawOutDelta", 0) for r in stats)
    wire_out = sum(r.get("compression", {}).get("wireOutDelta", 0) for r in stats)
    raw_in = sum(r.get("compression", {}).get("rawInDelta", 0) for r in stats)
    wire_in = sum(r.get("compression", {}).get("wireInDelta", 0) for r in stats)
    lines.append("## Compression")
    lines.append("")
    lines.append("| Direction | Raw bytes | Wire bytes | Ratio (wire/raw) |")
    lines.append("|---|---|---|---|")
    lines.append("| Out | %d | %d | %s |" % (raw_out, wire_out, fmt_num(100.0 * wire_out / raw_out, 1) + "%" if raw_out else "n/a"))
    lines.append("| In | %d | %d | %s |" % (raw_in, wire_in, fmt_num(100.0 * wire_in / raw_in, 1) + "%" if raw_in else "n/a"))
    lines.append("")

    # ---- save stalls -------------------------------------------------------------------------
    save_events = [ev for ev in events if ev.get("type") == "save"]
    total_saves = sum(ev.get("count", 0) for ev in save_events)
    max_stall = max((ev.get("maxStallMs", 0) for ev in save_events), default=0)
    lines.append("## World save stalls")
    lines.append("")
    lines.append("Save events: **%d**, max main-thread stall: **%s ms**." % (total_saves, fmt_num(max_stall)))
    lines.append("")

    # ---- zdo growth per day -----------------------------------------------------------------
    by_day = {}
    for r in stats:
        day = r["_dt"].strftime("%Y-%m-%d")
        by_day.setdefault(day, []).append(r)
    lines.append("## ZDO growth per day")
    lines.append("")
    lines.append("| Day | First zdos | Last zdos | Delta |")
    lines.append("|---|---|---|---|")
    for day in sorted(by_day.keys()):
        recs = by_day[day]
        zdos = [r.get("zdos") for r in recs if r.get("zdos") is not None and r.get("zdos") >= 0]
        if not zdos:
            continue
        lines.append("| %s | %d | %d | %+d |" % (day, zdos[0], zdos[-1], zdos[-1] - zdos[0]))
    lines.append("")

    # ---- gc / config reload / drops summary ----------------------------------------------
    gc_count = sum(1 for ev in events if ev.get("type") == "gc")
    drop_events = [ev for ev in events if ev.get("type") == "queue_drop"]
    reload_events = [ev for ev in events if ev.get("type") == "config_reload"]
    join_events = [ev for ev in events if ev.get("type") == "join"]
    leave_events = [ev for ev in events if ev.get("type") == "leave"]
    lines.append("## Other events")
    lines.append("")
    lines.append("- GC sweeps allowed through: **%d**" % gc_count)
    lines.append("- SendQueueGuard drop episodes: **%d** (%d packages total)" % (
        len(drop_events), sum(ev.get("count", 0) for ev in drop_events)))
    lines.append("- Config reloads: **%d**" % len(reload_events))
    lines.append("- Player joins / leaves: **%d / %d**" % (len(join_events), len(leave_events)))
    lines.append("")

    # ---- top 10 worst 10-second windows ----------------------------------------------------
    # Stats records are already ~IntervalSec apart; bucket by a 10s floor of the timestamp and
    # take the worst frameWorstMs bucket, showing what was happening at that time.
    buckets = {}
    for r in stats:
        bucket_key = int(r["_dt"].timestamp() // 10) * 10
        b = buckets.setdefault(bucket_key, {"records": [], "worst": 0.0})
        b["records"].append(r)
        wm = r.get("frameWorstMs") or 0.0
        if wm > b["worst"]:
            b["worst"] = wm

    worst_buckets = sorted(buckets.items(), key=lambda kv: -kv[1]["worst"])[:10]
    lines.append("## Top 10 worst 10-second windows (by worst frame time)")
    lines.append("")
    if not worst_buckets:
        lines.append("No data.")
    else:
        lines.append("| Time (UTC) | Worst frame ms | Players | ZDO sent/s | ZDO recv/s |")
        lines.append("|---|---|---|---|---|")
        for key, b in worst_buckets:
            dt = datetime.fromtimestamp(key, tz=timezone.utc)
            r = max(b["records"], key=lambda x: x.get("frameWorstMs") or 0.0)
            players = player_count(r)
            names = ", ".join(player_names(r))
            lines.append("| %s | %s | %d (%s) | %s | %s |" % (
                fmt_dt(dt), fmt_num(b["worst"]), players, names or "-",
                r.get("zdosSentPerSec", "n/a"), r.get("zdosRecvPerSec", "n/a")))
    lines.append("")

    return "\n".join(lines), from_ts.strftime("%Y%m%d-%H%M"), to_ts.strftime("%Y%m%d-%H%M")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("stats_dirs", nargs="+", help="one or more directories with stats-*.jsonl / events-*.jsonl")
    ap.add_argument("--host", default=None, help="directory with host-*.jsonl from collect-host.sh")
    ap.add_argument("--out", default=None, help="output markdown path")
    args = ap.parse_args()

    stats, events, host = load_all(args.stats_dirs, args.host)
    report, from_tag, to_tag = build_report(stats, events, host, args.stats_dirs, args.host)

    out_path = args.out
    if not out_path:
        out_dir = args.stats_dirs[0]
        out_path = os.path.join(out_dir, "REPORT-%s-%s.md" % (from_tag, to_tag))

    with open(out_path, "w", encoding="utf-8") as f:
        f.write(report)

    print("wrote %s (%d stats records, %d events, %d host samples)" % (
        out_path, len(stats), len(events), len(host)))


if __name__ == "__main__":
    main()
