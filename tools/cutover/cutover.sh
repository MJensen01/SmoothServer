#!/usr/bin/env bash
# cutover.sh — swap a Valheim server's mod set from the 8 replaced third-party mods to
# NoVikingLeftBehind + SmoothServer, in one guided, reversible run.
#
# DRY-RUN BY DEFAULT. Nothing on disk or in Docker is touched without --apply.
#
# Usage:
#   cutover.sh <server-dir> [--apply] [--nvlb <ver>|latest] [--ss <ver>|latest]
#              [--profile default|fastlink] [--enforce true|false]
#              [--force-empty-check-off] [--force-post-patch]
#   cutover.sh <server-dir> --rollback <ts> [--apply] [--with-world]
#   cutover.sh <server-dir> --list-backups
#
# Live target is NEWWORLD: /opt/valheim2 (container valheim-fresh). That server is FROZEN —
# only ever run --apply against it after Matt has explicitly said "cut over" AND the server
# is empty. Dry-run against it is always safe.
#
# What --apply does, in order:
#   1. player check (StatsLog record, else the `Connections N` log line) — refuses if >0
#   2. backup  -> <server-dir>/backups-cutover/<ts>/  (world, config/bepinex, compose, plugin list)
#   3. park the 8 replaced plugin dirs from BOTH plugin dirs into that backup (moved, never deleted)
#   4. install NoVikingLeftBehind/ + SmoothServer/ (whole release zips) into BOTH plugin dirs
#   5. seed both .cfg files from the tuned NEWTEST cfgs, with test-only values reset
#   6. docker compose restart, wait for `Game server connected`, print + check the proof lines
#   7. print the VALHEIM-CONNECT.md block and the group message
set -uo pipefail

SELF="$(basename "$0")"
LOCK=/var/lock/valheim-cutover.lock

# ---------------------------------------------------------------- configuration
NVLB_REPO="MJensen01/NoVikingLeftBehind"
SS_REPO="MJensen01/SmoothServer"
NVLB_PLUGIN_DIR="NoVikingLeftBehind"
SS_PLUGIN_DIR="SmoothServer"
NVLB_CFG="Nosferatu.NoVikingLeftBehind.cfg"
SS_CFG="Nosferatu.SmoothServer.cfg"
# Tuned reference configs (the values Matt has been playing with on NEWTEST).
REF_CFG_DIR="/opt/valheim-test/config/bepinex"

# The 8 mods NVLB + SmoothServer replace. Parked, never deleted.
RETIRE=(
  JuJuz1-SkillGainModifier
  Smoothbrain-SmartSkills
  Azumatt-AzuCraftyBoxes
  shudnal-ExtraSlots
  shudnal-ConditionalConfigSync
  ValheimModding-YamlDotNet
  CW_Jesse-BetterNetworking_Valheim
  Mydayyy-ServerSideMap
)
# Kept untouched: ValheimModding-Jotunn + LEGIOmods-TraderBrothers blacks7ar-CookingAdditions
# HugotheDwarf-{Shapekeys_and_More,More_and_Modified_Player_Cloth_Colliders,Hugos_Armory}

# ---------------------------------------------------------------- args
SERVER_DIR=""; APPLY=0; NVLB_VER="latest"; SS_VER="latest"
PROFILE="fastlink"; ENFORCE="true"; SKIP_EMPTY=0; FORCE_POST_PATCH=0
ROLLBACK_TS=""; WITH_WORLD=0; LIST_BACKUPS=0

usage() { sed -n '2,30p' "$0" >&2; exit 1; }
while [ $# -gt 0 ]; do
  case "$1" in
    --apply) APPLY=1; shift ;;
    --nvlb) NVLB_VER="${2:-}"; shift 2 ;;
    --ss) SS_VER="${2:-}"; shift 2 ;;
    --profile) PROFILE="$(echo "${2:-}" | tr 'A-Z' 'a-z')"; shift 2 ;;
    --enforce) ENFORCE="$(echo "${2:-}" | tr 'A-Z' 'a-z')"; shift 2 ;;
    --force-empty-check-off) SKIP_EMPTY=1; shift ;;
    --force-post-patch) FORCE_POST_PATCH=1; shift ;;
    --rollback) ROLLBACK_TS="${2:-}"; shift 2 ;;
    --with-world) WITH_WORLD=1; shift ;;
    --list-backups) LIST_BACKUPS=1; shift ;;
    -h|--help) usage ;;
    *) if [ -z "$SERVER_DIR" ]; then SERVER_DIR="$1"; shift; else usage; fi ;;
  esac
done
[ -n "$SERVER_DIR" ] || usage
SERVER_DIR="${SERVER_DIR%/}"
[ -d "$SERVER_DIR" ] || { echo "ERROR: $SERVER_DIR not found" >&2; exit 1; }
[ -f "$SERVER_DIR/docker-compose.yaml" ] || { echo "ERROR: no docker-compose.yaml in $SERVER_DIR" >&2; exit 1; }
case "$SERVER_DIR" in
  /opt/valheim) echo "ERROR: /opt/valheim is BLACKWORLD (vanilla, crossplay) — never a cut-over target." >&2; exit 1 ;;
esac
case "$PROFILE" in default) SS_PROFILE=Default ;; fastlink) SS_PROFILE=FastLink ;;
  *) echo "ERROR: --profile must be default|fastlink" >&2; exit 1 ;; esac
case "$ENFORCE" in true|false) : ;; *) echo "ERROR: --enforce must be true|false" >&2; exit 1 ;; esac

CFG_DIR="$SERVER_DIR/config/bepinex"
PLUG_CFG="$CFG_DIR/plugins"
PLUG_DATA="$SERVER_DIR/data/bepinex/BepInEx/plugins"
WORLD_DIR="$SERVER_DIR/config/worlds_local"
BACKUP_ROOT="$SERVER_DIR/backups-cutover"
CONTAINER="$(grep -m1 'container_name:' "$SERVER_DIR/docker-compose.yaml" | awk '{print $2}')"
WORLD_NAME="$(grep -m1 'WORLD_NAME=' "$SERVER_DIR/docker-compose.yaml" | sed -E 's/.*WORLD_NAME=([^ #]*).*/\1/')"
SERVER_NAME="$(grep -m1 'SERVER_NAME=' "$SERVER_DIR/docker-compose.yaml" | sed -E 's/.*SERVER_NAME=([^ #]*).*/\1/')"
SERVER_PASS="$(grep -m1 'SERVER_PASS=' "$SERVER_DIR/docker-compose.yaml" | sed -E 's/.*SERVER_PASS=([^ #]*).*/\1/')"
SERVER_PORT="$(grep -m1 'SERVER_PORT=' "$SERVER_DIR/docker-compose.yaml" | sed -E 's/.*SERVER_PORT=([^ #]*).*/\1/')"

step() { echo; echo "-- $* --"; }
die()  { echo "ABORT: $*" >&2; exit 3; }

# ---------------------------------------------------------------- list backups
if [ "$LIST_BACKUPS" = 1 ]; then
  ls -1 "$BACKUP_ROOT" 2>/dev/null || echo "(no $BACKUP_ROOT yet)"
  exit 0
fi

# ---------------------------------------------------------------- player check
player_check() {  # echoes "<count> <source>" or "? none"
  local newest rec cnt
  newest="$(ls -1t "$CFG_DIR"/smoothserver/stats/stats-*.jsonl 2>/dev/null | head -1)"
  if [ -n "$newest" ]; then
    cnt="$(tail -5 "$newest" | python3 -c 'import sys,json
c=None
for l in sys.stdin:
    l=l.strip()
    if not l: continue
    try: r=json.loads(l)
    except Exception: continue
    p=r.get("players")
    if isinstance(p,dict) and p.get("count") is not None: c=p["count"]
print("" if c is None else c)' 2>/dev/null)"
    [ -n "$cnt" ] && { echo "$cnt statslog:$(basename "$newest")"; return; }
  fi
  rec="$(cd "$SERVER_DIR" && docker compose logs --tail=600 2>/dev/null | grep -oE 'Connections [0-9]+' | tail -1)"
  if [ -n "$rec" ]; then echo "$(echo "$rec" | awk '{print $2}') connections-log"; return; fi
  echo "? none"
}

# ---------------------------------------------------------------- rollback
if [ -n "$ROLLBACK_TS" ]; then
  B="$BACKUP_ROOT/$ROLLBACK_TS"
  [ -d "$B" ] || die "no backup $B (try --list-backups)"
  echo "== $SELF rollback plan =="
  echo "server dir  : $SERVER_DIR   container: $CONTAINER"
  echo "backup      : $B  ($(cat "$B/STAMP" 2>/dev/null))"
  echo "restores    : config/bepinex (cfgs + plugins), docker-compose.yaml,"
  echo "              the parked plugin dirs back into $PLUG_DATA"
  echo "world       : $([ "$WITH_WORLD" = 1 ] && echo 'RESTORED from backup (--with-world) — DESTROYS play since the cut-over' || echo 'left alone (pass --with-world only if the world itself is broken)')"
  echo "then        : docker compose restart + wait for 'Game server connected'"
  echo "run mode    : $([ $APPLY -eq 1 ] && echo APPLY || echo DRY-RUN)"
  [ $APPLY -eq 1 ] || { echo; echo "Re-run with --apply to execute."; exit 0; }
  PC="$(player_check)"; echo "player check: $PC"
  case "$PC" in "0 "*) : ;; "? "*) [ $SKIP_EMPTY -eq 1 ] || die "player count unknown; --force-empty-check-off to override" ;;
    *) [ $SKIP_EMPTY -eq 1 ] || die "players connected ($PC)" ;; esac
  exec 9>"$LOCK"; flock -w 300 9 || die "another cutover run holds $LOCK"
  step "restore config/bepinex"
  rm -rf "$CFG_DIR.rollback-tmp"; cp -a "$B/config-bepinex" "$CFG_DIR.rollback-tmp"
  rm -rf "$CFG_DIR"; mv "$CFG_DIR.rollback-tmp" "$CFG_DIR"
  step "restore data-side plugins"
  rm -rf "$PLUG_DATA/$NVLB_PLUGIN_DIR" "$PLUG_DATA/$SS_PLUGIN_DIR"
  if [ -d "$B/retired/data" ]; then
    for d in "$B/retired/data"/*; do [ -d "$d" ] && cp -a "$d" "$PLUG_DATA/"; done
  fi
  step "restore docker-compose.yaml"
  [ -f "$B/docker-compose.yaml" ] && cp -a "$B/docker-compose.yaml" "$SERVER_DIR/docker-compose.yaml"
  if [ "$WITH_WORLD" = 1 ] && [ -d "$B/worlds_local" ]; then
    step "restore world (--with-world)"
    mv "$WORLD_DIR" "$WORLD_DIR.pre-rollback-$(date -u +%Y%m%d-%H%M%S)"
    cp -a "$B/worlds_local" "$WORLD_DIR"
  fi
  step "restart"
  (cd "$SERVER_DIR" && docker compose restart)
  D=$((SECONDS+300)); while [ $SECONDS -lt $D ]; do
    (cd "$SERVER_DIR" && docker compose logs --tail=400 2>/dev/null) | grep -q "Game server connected" && break; sleep 5; done
  (cd "$SERVER_DIR" && docker compose logs --tail=200 2>/dev/null) | grep -iE "Game server connected|Loading \[" | tail -20
  echo; echo "Rolled back to $ROLLBACK_TS. Tell the group to re-import the OLD profile (MODDY)."
  exit 0
fi

# ---------------------------------------------------------------- version resolve
gh_latest() {  # <owner/repo> -> version without leading v
  curl -fsSL "https://api.github.com/repos/$1/releases/latest" \
    | python3 -c 'import sys,json;print(json.load(sys.stdin).get("tag_name","").lstrip("v"))' 2>/dev/null
}
[ "$NVLB_VER" = "latest" ] && NVLB_VER="$(gh_latest "$NVLB_REPO")"
[ "$SS_VER"  = "latest" ] && SS_VER="$(gh_latest "$SS_REPO")"
[ -n "$NVLB_VER" ] || die "could not resolve the latest NoVikingLeftBehind release (no network?) — pass --nvlb <ver>"
[ -n "$SS_VER" ]  || die "could not resolve the latest SmoothServer release (no network?) — pass --ss <ver>"
NVLB_URL="https://github.com/$NVLB_REPO/releases/download/v$NVLB_VER/NoVikingLeftBehind-$NVLB_VER.zip"
SS_URL="https://github.com/$SS_REPO/releases/download/v$SS_VER/SmoothServer-$SS_VER.zip"

# ---------------------------------------------------------------- plan
echo "== $SELF plan =="
echo "server dir     : $SERVER_DIR"
echo "container      : ${CONTAINER:-<not found>}   server/world: ${SERVER_NAME:-?} / ${WORLD_NAME:-?}"
echo "plugin dirs    : $PLUG_CFG"
echo "                 $PLUG_DATA  (image rsyncs config->data, never prunes: both sides get edited)"
echo "install        : NoVikingLeftBehind $NVLB_VER   SmoothServer $SS_VER"
echo "                 $NVLB_URL"
echo "                 $SS_URL"
echo "cfg seed from  : $REF_CFG_DIR/{$NVLB_CFG,$SS_CFG}"
echo "  SmoothServer : [Profiles] Profile = $SS_PROFILE ; [General] EnforceClientMod = $ENFORCE"
echo "  NVLB         : [General] EnforceClientMod = true (always — it is the everyone-installs mod)"
echo "  reset        : [Frontier] TierOverride=-1, [Playtime] MinGroupSize=3, every SelfTest*=false/0"
echo "  [ServerKeys] : taken verbatim from the NEWTEST cfg (SkillGainRate etc.)"
echo "retire (parked): ${RETIRE[*]}"
echo "run mode       : $([ $APPLY -eq 1 ] && echo APPLY || echo DRY-RUN)"

step "current plugins (config side)"; ls -1 "$PLUG_CFG" 2>/dev/null
step "current plugins (data side)";   ls -1 "$PLUG_DATA" 2>/dev/null

step "shared-map carry-over"
if [ -f "$WORLD_DIR/$WORLD_NAME.mod.serversidemap.explored" ]; then
  echo "OK  $WORLD_DIR/$WORLD_NAME.mod.serversidemap.explored present — SmoothServer imports it on first boot"
else
  echo "!!  no $WORLD_NAME.mod.serversidemap.explored — nothing to import (group map exploration will start empty)"
fi
if [ -f "$CFG_DIR/smoothserver/$WORLD_NAME.map" ]; then
  echo "!!  $CFG_DIR/smoothserver/$WORLD_NAME.map already exists — the one-shot import will NOT run."
fi

step "post-patch guard"
if ls /opt/modlab/watch/CHANGED-public-* >/dev/null 2>&1; then
  echo "!!  Steam's public branch has MOVED (flag in /opt/modlab/watch/). A container restart re-runs"
  echo "!!  steamcmd and would put the new game build under mods compiled for the old one."
  echo "!!  Run the valheim-mod-update skill first. --force-post-patch overrides."
  [ $APPLY -eq 1 ] && [ $FORCE_POST_PATCH -eq 0 ] && die "refusing --apply after a game-build change"
else
  echo "OK  no game-build-change flag in /opt/modlab/watch/"
fi

step "player check"
PC="$(player_check)"; echo "  $PC  (count source)"
PCOUNT="${PC%% *}"
if [ $APPLY -eq 1 ] && [ $SKIP_EMPTY -eq 0 ]; then
  case "$PCOUNT" in
    0) echo "  server is empty — OK to apply" ;;
    "?") die "player count unknown (no StatsLog, no Connections line). Check by hand, then --force-empty-check-off" ;;
    *) die "$PCOUNT player(s) connected. Wait for 0 and re-run (or --force-empty-check-off)" ;;
  esac
fi

if [ $APPLY -eq 0 ]; then
  cat <<PLAN

DRY RUN — with --apply this would, in order:
  1. flock $LOCK
  2. backup -> $BACKUP_ROOT/<ts>/ : worlds_local/, config-bepinex/, docker-compose.yaml,
     plugins-before-config.txt, plugins-before-data.txt
  3. MOVE the ${#RETIRE[@]} retired plugin dirs out of BOTH plugin dirs into <ts>/retired/{config,data}/
  4. unzip both releases into $PLUG_CFG/{$NVLB_PLUGIN_DIR,$SS_PLUGIN_DIR} and the same under $PLUG_DATA
     (all files, incl. SmoothServer's 5 runtime DLLs)
  5. copy the two tuned cfgs from $REF_CFG_DIR, then set Profile=$SS_PROFILE, EnforceClientMod=$ENFORCE,
     TierOverride=-1, MinGroupSize=3, SelfTest*=off
  6. cd $SERVER_DIR && docker compose restart ; wait up to 300s for 'Game server connected'
  7. print both module summaries, ServerSync/[Profiles]/[SharedMap] lines; fail on FAILED( or Exception
  8. print the VALHEIM-CONNECT.md block + the group message

Re-run with --apply to execute.  Roll back with: $SELF $SERVER_DIR --rollback <ts> --apply
PLAN
  exit 0
fi

# ================================================================ APPLY
exec 9>"$LOCK"; flock -w 300 9 || die "another cutover run holds $LOCK"
TS="$(date -u +%Y%m%d-%H%M%S)"
B="$BACKUP_ROOT/$TS"
mkdir -p "$B/retired/config" "$B/retired/data"
echo "$TS UTC — cutover of $SERVER_DIR to NVLB $NVLB_VER + SmoothServer $SS_VER (profile=$SS_PROFILE enforce=$ENFORCE)" > "$B/STAMP"

step "1/7 backup -> $B"
ls -1 "$PLUG_CFG" > "$B/plugins-before-config.txt" 2>/dev/null
ls -1 "$PLUG_DATA" > "$B/plugins-before-data.txt" 2>/dev/null
cp -a "$SERVER_DIR/docker-compose.yaml" "$B/docker-compose.yaml"
cp -a "$CFG_DIR" "$B/config-bepinex"
cp -a "$WORLD_DIR" "$B/worlds_local"
du -sh "$B" | sed 's/^/  backup size: /'
sed 's/^/  was: /' "$B/plugins-before-config.txt"

step "2/7 park the retired plugins (moved, not deleted)"
for p in "${RETIRE[@]}"; do
  for side in config data; do
    src="$PLUG_CFG/$p"; [ "$side" = data ] && src="$PLUG_DATA/$p"
    if [ -d "$src" ]; then mv "$src" "$B/retired/$side/$p"; echo "  parked $side/$p"; fi
  done
done

step "3/7 install NoVikingLeftBehind $NVLB_VER + SmoothServer $SS_VER"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
install_pkg() {  # <url> <plugin-dir-name>
  local url="$1" name="$2" z="$TMP/$2.zip" x="$TMP/$2"
  curl -fsSL -o "$z" "$url" || die "download failed: $url"
  mkdir -p "$x"; python3 -m zipfile -e "$z" "$x" || die "unzip failed: $z"   # `unzip` is not installed on this box
  ls -1 "$x"/*.dll >/dev/null 2>&1 || die "$name zip contains no DLL"
  for dest in "$PLUG_CFG/$name" "$PLUG_DATA/$name"; do
    rm -rf "$dest"; mkdir -p "$dest"; cp -a "$x"/. "$dest"/
  done
  echo "  installed $name: $(ls -1 "$PLUG_CFG/$name"/*.dll | xargs -n1 basename | tr '\n' ' ')"
}
install_pkg "$NVLB_URL" "$NVLB_PLUGIN_DIR"
install_pkg "$SS_URL"  "$SS_PLUGIN_DIR"
chown -R --reference="$SERVER_DIR/docker-compose.yaml" "$PLUG_CFG" "$PLUG_DATA" 2>/dev/null || true

step "4/7 seed the two configs from $REF_CFG_DIR"
set_key() {  # <file> <section> <key> <value>
  python3 - "$@" <<'PY'
import sys
path, section, key, value = sys.argv[1:5]
out, cur, done = [], None, False
for line in open(path, encoding='utf-8').read().splitlines(True):
    s = line.strip()
    if s.startswith('[') and s.endswith(']'):
        cur = s[1:-1]
    elif cur == section and s.split('=')[0].strip() == key and '=' in s:
        line = "%s = %s\n" % (key, value); done = True
    out.append(line)
if not done:
    sys.stderr.write("  WARN: [%s] %s not found in %s\n" % (section, key, path))
open(path, 'w', encoding='utf-8').writelines(out)
PY
}
for f in "$NVLB_CFG" "$SS_CFG"; do
  [ -f "$REF_CFG_DIR/$f" ] || die "reference cfg missing: $REF_CFG_DIR/$f"
  [ -f "$CFG_DIR/$f" ] && cp -a "$CFG_DIR/$f" "$B/$f.was"
  cp -a "$REF_CFG_DIR/$f" "$CFG_DIR/$f"
  echo "  seeded $f"
done
sed -i -E 's/^SelfTest = .*/SelfTest = false/; s/^SelfTestSeconds = .*/SelfTestSeconds = 0/; s/^SteamSelfTest = .*/SteamSelfTest = false/' "$CFG_DIR/$NVLB_CFG" "$CFG_DIR/$SS_CFG"
set_key "$CFG_DIR/$NVLB_CFG" Frontier TierOverride -1
set_key "$CFG_DIR/$NVLB_CFG" Playtime MinGroupSize 3
set_key "$CFG_DIR/$NVLB_CFG" General  EnforceClientMod true
set_key "$CFG_DIR/$SS_CFG"   Profiles Profile "$SS_PROFILE"
set_key "$CFG_DIR/$SS_CFG"   General  EnforceClientMod "$ENFORCE"
grep -E '^(TierOverride|MinGroupSize|EnforceClientMod|Profile|SkillGainRate) =' "$CFG_DIR/$NVLB_CFG" "$CFG_DIR/$SS_CFG" | sed 's/^/  /'

step "5/7 restart $CONTAINER"
(cd "$SERVER_DIR" && docker compose restart) || die "docker compose restart failed"
SINCE="$(date -u +%Y-%m-%dT%H:%M:%S)"
DEADLINE=$((SECONDS+300)); UP=0
while [ $SECONDS -lt $DEADLINE ]; do
  (cd "$SERVER_DIR" && docker compose logs --tail=800 2>/dev/null) | grep -q "Game server connected" && { UP=1; break; }
  sleep 5
done
[ $UP -eq 1 ] && echo "  Game server connected." || echo "  WARNING: timed out waiting for 'Game server connected'"
sleep 20

step "6/7 verify"
LOG="$TMP/log.txt"; RAW="$TMP/raw.txt"
(cd "$SERVER_DIR" && docker compose logs --tail=4000 2>/dev/null) > "$RAW"
# slice to THIS boot only — the log still holds the pre-cutover boot's plugin list
tac "$RAW" | awk '{print} /Preloader started/{exit}' | tac > "$LOG"
[ -s "$LOG" ] || cp "$RAW" "$LOG"
echo "  -- module summaries --"
grep -E 'NoVikingLeftBehind [0-9].* loaded, [0-9]+ modules|SmoothServer [0-9].* loaded, [0-9]+ modules' "$LOG" | tail -4 | sed 's/^/  /'
echo "  -- ServerSync / Profiles / SharedMap --"
grep -iE 'ServerSync|\[Profiles\]|\[SharedMap\]' "$LOG" | tail -12 | sed 's/^/  /'
echo "  -- plugins now loaded --"
grep -oE 'Loading \[[^]]+\]' "$LOG" | sort -u | sed 's/^/  /'
# Same sweep the lab's smoke.sh uses. BENIGN = vanilla headless noise proven present before the
# cut-over too: ShieldDomeImageEffect throws ArgumentNullException(shader) on every GfxDevice-Null boot.
BENIGN='ShieldDomeImageEffect|Parameter name: shader|IMGUI module is stripped|at UnityEngine\.|SteamSelfTest'
BAD="$(grep -nE 'FAILED\(|MissingMethod|TypeLoad|NullReference|HarmonyException|Could not load|FileNotFound' "$LOG" \
       | grep -vE "$BENIGN" | tail -20)"
if [ -n "$BAD" ]; then
  echo; echo "*** PROBLEMS FOUND ***"; echo "$BAD" | sed 's/^/  /'
  echo "*** roll back with: $SELF $SERVER_DIR --rollback $TS --apply"
  exit 4
fi
NOK=$(grep -cE 'NoVikingLeftBehind [0-9].* loaded' "$LOG"); SOK=$(grep -cE 'SmoothServer [0-9].* loaded' "$LOG")
[ "$NOK" -gt 0 ] && [ "$SOK" -gt 0 ] || { echo "*** one or both mods did not report a load summary — roll back with --rollback $TS --apply"; exit 4; }
echo "  CLEAN: both mods loaded, no FAILED(/exception lines."

step "7/7 connect block + group message"
PUBIP="$(curl -s --max-time 5 https://api.ipify.org || echo '<public ip>')"
cat <<CONNECT

--- paste into Documents\AI\Valheim (VALHEIM-CONNECT.md), server #2 table ---
| **Connect (Steam -> Join by IP)** | **$PUBIP:$SERVER_PORT** (public IP is dynamic) |
| **Password** | \`$SERVER_PASS\` |
| **Server name** | $SERVER_NAME (world $WORLD_NAME) |
| **Steam branch** | **Betas = None** (live) |
| **Mods** | r2modman profile **NEWWORLD** (import code from profile-code.py) — NoVikingLeftBehind $NVLB_VER + SmoothServer $SS_VER replace the 8 old mods |

--- group message ---
NEWWORLD is now on our own mods. In r2modman: Profiles -> Import/Update -> Import code -> paste
the code I sent -> "Import as new profile" -> name it NEWWORLD -> launch modded from there.
Steam -> Valheim -> Properties -> Betas = None. Same server, same characters, same map — your
explored map carried over. Connect: $PUBIP:$SERVER_PORT, password $SERVER_PASS.
Gone: SkillGainModifier, SmartSkills, AzuCraftyBoxes, ExtraSlots, ConditionalConfigSync,
YamlDotNet, BetterNetworking, ServerSideMap — NoVikingLeftBehind $NVLB_VER does all of that now
and SmoothServer $SS_VER does the networking + the shared map.
CONNECT

echo
echo "Done. Backup + parked plugins: $B"
echo "Rollback: $SELF $SERVER_DIR --rollback $TS --apply"
