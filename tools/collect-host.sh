#!/bin/bash
# Host-side collector for SmoothServer stats analysis (Matt's multi-day play-and-analyze pass).
# Appends one JSON line per run to host-YYYY-MM-DD.jsonl: per-container docker stats (cpu%, mem),
# 1-minute load average, free memory, and each container's last "Connections N ..." log line.
# Read-only: never touches container state, never restarts anything.
#
# Usage: /opt/modlab/stats/collect-host.sh [output-dir]
#   output-dir defaults to /opt/modlab/stats/host
#
# Installed as a systemd timer (system-level, sudo -n): see
# /opt/modlab/stats/smoothserver-host-collect.service / .timer, or tools/ in the SmoothServer repo.
set -u

OUT_DIR="${1:-/opt/modlab/stats/host}"
mkdir -p "$OUT_DIR"

TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
DATE="$(date -u +%Y-%m-%d)"
OUT_FILE="$OUT_DIR/host-${DATE}.jsonl"

CONTAINERS="valheim-fresh valheim-test valheim-server"

json_str() {
  # minimal JSON string escaper (backslash, quote, control chars)
  local s="$1"
  s="${s//\\/\\\\}"
  s="${s//\"/\\\"}"
  s="$(printf '%s' "$s" | tr -d '\000-\010\013\014\016-\037')"
  printf '%s' "$s"
}

# --- docker stats --no-stream for all present containers in one call (cheap, one snapshot) ---
STATS_RAW=""
if command -v docker >/dev/null 2>&1; then
  STATS_RAW="$(docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.MemPerc}}' 2>/dev/null)"
fi

stat_for() {
  # $1 = container name; prints "cpu|memUsage|memPerc" or empty if not running
  printf '%s\n' "$STATS_RAW" | awk -F'|' -v n="$1" '$1==n{print $2"|"$3"|"$4; found=1} END{if(!found) exit 1}'
}

connections_line_for() {
  # last "Connections N ..." line from this container's docker logs, or empty
  docker logs --tail 500 "$1" 2>&1 | grep -a 'Connections [0-9]' | tail -1 | sed 's/^[^:]*: //'
}

LOAD1="$(awk '{print $1}' /proc/loadavg 2>/dev/null)"
[ -z "${LOAD1:-}" ] && LOAD1="null"

FREE_MEM_MB="$(free -m 2>/dev/null | awk '/^Mem:/{print $4}')"
[ -z "${FREE_MEM_MB:-}" ] && FREE_MEM_MB="null"
AVAIL_MEM_MB="$(free -m 2>/dev/null | awk '/^Mem:/{print $7}')"
[ -z "${AVAIL_MEM_MB:-}" ] && AVAIL_MEM_MB="null"

CONTAINERS_JSON=""
first=1
for c in $CONTAINERS; do
  if ! docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$c"; then
    continue   # skip missing, per spec
  fi

  ST="$(stat_for "$c")"
  if [ -n "$ST" ]; then
    CPU="$(printf '%s' "$ST" | cut -d'|' -f1)"
    MEMUSE="$(printf '%s' "$ST" | cut -d'|' -f2)"
    MEMPCT="$(printf '%s' "$ST" | cut -d'|' -f3)"
  else
    CPU=""; MEMUSE=""; MEMPCT=""
  fi

  CONN_LINE="$(connections_line_for "$c")"
  CONN_N="$(printf '%s' "$CONN_LINE" | grep -oE 'Connections [0-9]+' | grep -oE '[0-9]+' | head -1)"
  [ -z "${CONN_N:-}" ] && CONN_N="null"

  CPU_ESC="$(json_str "$CPU")"
  MEMUSE_ESC="$(json_str "$MEMUSE")"
  MEMPCT_ESC="$(json_str "$MEMPCT")"
  CONN_LINE_ESC="$(json_str "$CONN_LINE")"

  ENTRY="{\"name\":\"$c\",\"cpuPerc\":\"$CPU_ESC\",\"memUsage\":\"$MEMUSE_ESC\",\"memPerc\":\"$MEMPCT_ESC\",\"connections\":$CONN_N,\"connectionsLine\":\"$CONN_LINE_ESC\"}"

  if [ $first -eq 1 ]; then CONTAINERS_JSON="$ENTRY"; first=0; else CONTAINERS_JSON="$CONTAINERS_JSON,$ENTRY"; fi
done

LINE="{\"ts\":\"$TS\",\"load1\":$LOAD1,\"freeMemMB\":$FREE_MEM_MB,\"availMemMB\":$AVAIL_MEM_MB,\"containers\":[$CONTAINERS_JSON]}"

printf '%s\n' "$LINE" >> "$OUT_FILE"
