#!/bin/bash
# run-e2e.sh <path-to-TerroritySmellatory.dll> [--bc] [--keep] [--mode dual|solo] [--spike a|b|c] [--graphics] [--timeout SEC]
#
# End-to-end rig for TerroritySmellatory: ONE headless dedicated server and headless game clients ("TSAdmin",
# a real admin, and "TSPlain", a plain visitor) play one scripted scenario through the real mod; the server is
# then restarted to prove the territory file reloads. Prints PASS/FAIL per check and a summary; exits 0 only if
# every check passed. Everything is throwaway: profiles under ~/valheim-testbed/profiles/terr-e2e-*, fake homes
# and logs under $E2E_WORK/e2e-runs/<timestamp>/. Nothing under steamapps/common is written; the game binaries
# run in place with BepInEx injected by doorstop environment variables. See README.md next to this file.
#
#   --mode dual   two clients at once (needs the server to accept two connections from one Steam account)
#   --mode solo   one client that plays TSAdmin and TSPlain in turn (default: see DEFAULT_MODE below)
#   --bc          Better Continents: server = copy of profiles/terr-bc (BC world ProxEra01), clients also get BC
#   --keep        keep the throwaway profiles and fake homes (logs are always kept)
#   --spike a     one client reaches the main menu and quits (no server)
#   --spike b     server + one client that creates a character, joins and quits
#   --spike c     server + two clients (same Steam account) joining at the same time
#   --spike s     server only (the driver's server checks), no client - allowed while a foreign client runs
#   --graphics    run clients with a real graphics device: -batchmode -force-glcore (no -nographics) on the run's private Xvfb,
#                 where Mesa's llvmpipe gives OpenGL 4.6 in software (Vulkan init crashes there). Slow, but it draws: the driver
#                 then also counts the barrier's flares drawn (P30h) and reads the rendered pixels (P30i on a black backdrop, P30j in the game's own view; F01 in --spike a)
#   --no-xvfb     no private virtual X display for the clients - Unity 6 then SIGSEGVs loading the first scene
#                 (window backend "(null)"); by default each run starts its own Xvfb, which has no screen at all
set -u
export LC_ALL=C

HERE="$(cd "$(dirname "$0")" && pwd)"
TB="$HOME/valheim-testbed"
GAME_CLIENT="$HOME/.local/share/Steam/steamapps/common/Valheim"
GAME_SERVER="$HOME/.local/share/Steam/steamapps/common/Valheim dedicated server"
DOORSTOP="$TB/doorstop_libs"
E2E_WORK="${E2E_WORK:-/tmp/claude-1000/-home-rohan-WubarrkCODING-TerroritySmellatory/c05dbc9b-9aea-41b6-96de-fed685b99bdf/scratchpad}"
RUNS="$E2E_WORK/e2e-runs"; SAFETY="$E2E_WORK/e2e-safety"; LOCK="$E2E_WORK/e2e-rig.lock"
DOOR_DELAY=3                 # 0.3.1: AutoCloseDelay written into the server's config; the door checks' clock (TERR_E2E_DOORDELAY)
DEFAULT_MODE="solo"          # spike (c): the dedicated server refuses a 2nd connection of the same Steam account
MIN_MEM_KB=$((13 * 1024 * 1024))
# The machine's mem-watchdog SIGTERMs the BIGGEST ~/valheim-testbed dedicated server once MemAvailable is under 5 GB,
# and that can be another project's server running next to this rig: under 7 GB during the scenario the rig stops its
# own processes first and exits 5.
YIELD_MEM_KB=$((7 * 1024 * 1024))
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"

DLL=""; BC=0; KEEP=0; MODE=""; SPIKE=""; GRAPHICS=0; TIMEOUT=900; XVFB=1
while [ $# -gt 0 ]; do
  case "$1" in
    --bc) BC=1 ;;
    --keep) KEEP=1 ;;
    --mode) MODE="$2"; shift ;;
    --spike) SPIKE="$2"; shift ;;
    --graphics) GRAPHICS=1 ;;
    --xvfb) XVFB=1 ;;
    --no-xvfb) XVFB=0 ;;
    --timeout) TIMEOUT="$2"; shift ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    -*) echo "unknown option $1"; exit 2 ;;
    *) DLL="$1" ;;
  esac
  shift
done
[ -n "$DLL" ] || { echo "usage: $0 <path-to-TerroritySmellatory.dll> [--bc] [--keep] [--mode dual|solo] [--spike a|b|c]"; exit 2; }
DLL="$(readlink -f "$DLL")"; [ -f "$DLL" ] || { echo "no such DLL: $DLL"; exit 2; }
MODE="${MODE:-$DEFAULT_MODE}"
case "$MODE" in dual|solo) ;; *) echo "--mode must be dual or solo"; exit 2 ;; esac

mkdir -p "$RUNS" "$SAFETY"
exec 9>"$LOCK"
if ! flock -n 9; then echo "ANOTHER RIG RUN HOLDS $LOCK - refusing to overlap"; exit 3; fi

TS="$(date +%Y%m%d-%H%M%S)"
RUN="$RUNS/$TS"; LOGS="$RUN/logs"; mkdir -p "$LOGS" "$RUN/barriers" "$RUN/artifacts"
MARK="terr-e2e-$TS-$$"
PASS_WORD="te2e$RANDOM$RANDOM"
PROFILE_PREFIX="$TB/profiles/terr-e2e-$TS"
SUMMARY="$RUN/summary.txt"
declare -A PIDS=()              # role -> pid of a game process this run started
SHELL_RESULTS="$RUN/results-rig.txt"; : > "$SHELL_RESULTS"
log() { echo "[rig $(date +%H:%M:%S)] $*" | tee -a "$RUN/rig.log"; }
check() { # check <id> <what> <ok 0|1> [why]
  if [ "$3" = "1" ]; then echo "[E2E] PASS $1 $2" | tee -a "$SHELL_RESULTS"; else echo "[E2E] FAIL $1 $2: ${4:-}" | tee -a "$SHELL_RESULTS"; fi
}
soft() { echo "[E2E] SOFT $1 $2: $3" | tee -a "$SHELL_RESULTS"; }

# ─────────────────────────────────────────────────────────────
# Teardown: SIGTERM every process this run started, SIGKILL after 30 s, remove throwaway profiles/homes
# ─────────────────────────────────────────────────────────────
stop_pid() { # stop_pid <pid> <label> [grace]
  local pid="$1" label="$2" grace="${3:-30}"
  kill -0 "$pid" 2>/dev/null || return 0
  kill -TERM "$pid" 2>/dev/null
  for _ in $(seq 1 "$grace"); do kill -0 "$pid" 2>/dev/null || { log "$label (pid $pid) exited"; return 0; }; sleep 1; done
  log "$label (pid $pid) still running after ${grace}s - SIGKILL"; kill -KILL "$pid" 2>/dev/null; sleep 1
}
our_pids() { # every process carrying this run's marker in its environment
  for p in $(pgrep -f 'valheim(_server)?\.x86_64' 2>/dev/null); do
    tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -qx "TERR_E2E_MARK=$MARK" && echo "$p"
  done
}
cleanup() {
  local code=$?
  trap - EXIT INT TERM
  for role in "${!PIDS[@]}"; do stop_pid "${PIDS[$role]}" "$role"; done
  for p in $(our_pids); do stop_pid "$p" "stray"; done
  if [ "$KEEP" = "0" ]; then
    rm -rf "$PROFILE_PREFIX"-* "$RUN"/home-* "$RUN"/cwd-* 2>/dev/null
  else
    log "--keep: profiles $PROFILE_PREFIX-* and fake homes kept"
  fi
  local left; left="$(our_pids)"; [ -n "$left" ] && log "WARNING: processes still alive: $left"
  log "logs: $RUN"
  exit $code
}
trap cleanup EXIT
trap 'log "interrupted"; exit 130' INT TERM

# ─────────────────────────────────────────────────────────────
# Pre-flight
# ─────────────────────────────────────────────────────────────
foreign_clients() { # Valheim game clients not started by ANY rig run (Rohan may be playing)
  for p in $(pgrep -f 'valheim\.x86_64' 2>/dev/null); do
    [ "$(readlink -f /proc/$p/exe 2>/dev/null)" = "$GAME_CLIENT/valheim.x86_64" ] || continue
    tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -q '^TERR_E2E_MARK=' || echo "$p"
  done
}
foreign_servers() { # dedicated servers no rig run started that run with the REAL home: Unity rewrites the real
  # ~/.config/unity3d/IronGate/Valheim/prefs whenever such a server starts or exits (Unity's session bookkeeping,
  # ConsoleBindings), and the rule-2 checks cannot tell that write from one of the rig's own
  for p in $(pgrep -f 'valheim_server\.x86_64' 2>/dev/null); do
    [ "$(readlink -f /proc/$p/exe 2>/dev/null)" = "$GAME_SERVER/valheim_server.x86_64" ] || continue
    tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -q '^TERR_E2E_MARK=' && continue
    tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -qx "HOME=$HOME" && echo "$p"
  done
}
mem_kb() { awk '/^MemAvailable:/ {print $2}' /proc/meminfo; }
steam_id() {
  python3 - "$HOME/.local/share/Steam/config/loginusers.vdf" <<'PY'
import re, sys
t = open(sys.argv[1], encoding="utf-8", errors="replace").read()
users = re.findall(r'"(7656\d{13})"\s*\{([^}]*)\}', t)
best = [u for u, body in users if re.search(r'"MostRecent"\s*"1"', body)] or [u for u, _ in users]
print(best[0] if best else "")
PY
}
free_port() {
  for p in $(seq 2670 2 2698); do
    ss -ltnu 2>/dev/null | awk '{print $5}' | grep -qE ":($p|$((p+1)))$" && continue
    echo "$p"; return 0
  done
  return 1
}

log "run $TS  mode=$MODE bc=$BC spike=${SPIKE:-none} graphics=$GRAPHICS"
log "DLL under test: $DLL  sha256 $(sha256sum "$DLL" | cut -c1-64)"
sha256sum "$DLL" > "$RUN/dll.sha256"

case "$SPIKE" in ""|a|b|c|s) ;; *) echo "--spike a|b|c|s"; exit 2 ;; esac
FOREIGN="$(foreign_clients)"
MEM=$(mem_kb)
if [ "$SPIKE" != "s" ]; then   # spike s boots the server only: no client is launched
  if [ -n "$FOREIGN" ]; then
    log "REFUSING: a Valheim client this rig did not start is running (pid(s) $FOREIGN) - Rohan may be playing."
    exit 4
  fi
  if [ "$MEM" -lt "$MIN_MEM_KB" ]; then
    log "REFUSING: MemAvailable is $((MEM/1024)) MB, the rig needs >= $((MIN_MEM_KB/1024)) MB before launching clients."
    exit 5
  fi
fi
FOREIGN_SRV="$(foreign_servers | tr '\n' ' ')"
[ -n "$FOREIGN_SRV" ] && log "WARNING: dedicated server(s) this rig did not start are running with the real HOME (pid(s) $FOREIGN_SRV): Unity rewrites ~/.config/unity3d/IronGate/Valheim/prefs when one starts or exits, and H90/H91 will report that as a change to Rohan's data"
PORT="$(free_port)" || { log "no free port pair in 2670-2699"; exit 6; }
STEAMID="$(steam_id)"
[ -n "$STEAMID" ] || { log "could not read the logged-in Steam id from loginusers.vdf"; exit 6; }
log "port $PORT, Steam id $STEAMID (adminlist of the throwaway server), MemAvailable $((MEM/1024)) MB"
WDOG_LOG="$TB/noodles-work/scratchpad/tools/mem-watchdog.log"
WDOG_LINES=$( [ -f "$WDOG_LOG" ] && wc -l < "$WDOG_LOG" || echo 0 )

# ─────────────────────────────────────────────────────────────
# Build the driver against the DLL under test
# ─────────────────────────────────────────────────────────────
log "building TerrE2E against the DLL under test"
# fd 9 is this run's flock: the build's compiler server must not inherit it (9>&-), or it outlives the run, keeps the
# lock held and the next run refuses with "another rig run holds the lock" (found 2026-09-29 16:40: VBCSCompiler from
# the previous run). No node reuse and no shared compiler, so nothing lingers; a full rebuild (--no-incremental), so a
# stale obj/ from another DLL can never stand in for the one under test.
if ! dotnet build "$HERE/TerrE2E.csproj" -c Release --no-incremental -nr:false -p:UseSharedCompilation=false -p:TerrDll="$DLL" -o "$RUN/build" > "$LOGS/build.log" 2>&1 9>&-; then
  grep -E "error" "$LOGS/build.log" | sort -u | head -30
  log "BUILD FAILED (the driver does not compile against this DLL; see $LOGS/build.log)"; exit 7
fi
DRIVER="$RUN/build/TerrE2E.dll"; sha256sum "$DRIVER" >> "$RUN/dll.sha256"

# ─────────────────────────────────────────────────────────────
# Throwaway profiles
# ─────────────────────────────────────────────────────────────
if [ "$BC" = "1" ]; then SRC_PROFILE="$TB/profiles/terr-bc"; WORLD="ProxEra01"; else SRC_PROFILE="$TB/profiles/terr"; WORLD="bcterr"; fi
SRV="$PROFILE_PREFIX-server"
make_server_profile() {
  cp -r "$SRC_PROFILE" "$SRV" || return 1
  rm -f "$SRV/BepInEx/LogOutput.log" "$SRV/server-console.log"
  rm -f "$SRV"/BepInEx/config/TerroritySmellatory/worlds/*.json* 2>/dev/null
  cp "$DLL" "$SRV/BepInEx/plugins/TerroritySmellatory/TerroritySmellatory.dll"
  mkdir -p "$SRV/BepInEx/plugins/TerrE2E"; cp "$DRIVER" "$SRV/BepInEx/plugins/TerrE2E/"
  sed -i 's/^VerboseLogging = .*/VerboseLogging = true/' "$SRV/BepInEx/config/wubarrk.terroritysmellatory.cfg"
  # 0.3.1: the door closer's delay, 3 s (the shipped default is 10 s), so a door check needs seconds, not a minute. Set in place when the
  # key is already there (see the note in make_client_profile), inserted under [1 - Server] when not; a DLL without the setting ignores it.
  if grep -q "^AutoCloseDelay = " "$SRV/BepInEx/config/wubarrk.terroritysmellatory.cfg"; then
    sed -i "s/^AutoCloseDelay = .*/AutoCloseDelay = $DOOR_DELAY/" "$SRV/BepInEx/config/wubarrk.terroritysmellatory.cfg"
  else
    sed -i "/^\[1 - Server\]/a AutoCloseDelay = $DOOR_DELAY" "$SRV/BepInEx/config/wubarrk.terroritysmellatory.cfg"
  fi
  printf '// List admin players ID  ONE per line\n%s\n' "$STEAMID" > "$SRV/saves/adminlist.txt"
  printf '// List permitted players ID ONE per line\n' > "$SRV/saves/permittedlist.txt"
  printf '// List banned players ID  ONE per line\n' > "$SRV/saves/bannedlist.txt"
  # The SERVER's fake home too. The dedicated server is a Unity player with the client's identity (IronGate/Valheim):
  # started with the real HOME it rewrote Rohan's ~/.config/unity3d/IronGate/Valheim/prefs at every start and every
  # exit (Unity's session count and ids, ConsoleBindings) - in every run up to 2026-09-29 15:31, unseen by the rule-2
  # check, whose baseline was taken after the server booted and whose check ran before it stopped.
  local H="$RUN/home-server"
  mkdir -p "$H/.config" "$H/.cache" "$H/.local/share" "$H/.local/state" || return 1
  mkdir -m 700 -p "$H/.run"
  ln -s "$HOME/.steam" "$H/.steam"
  ln -s "$HOME/.local/share/Steam" "$H/.local/share/Steam"
}
make_client_profile() { # <role>
  local P="$PROFILE_PREFIX-$1"
  cp -r "$TB/base-profile" "$P" || return 1
  cp "$SRC_PROFILE/BepInEx/config/BepInEx.cfg" "$P/BepInEx/config/"
  mkdir -p "$P/BepInEx/plugins/TerroritySmellatory" "$P/BepInEx/plugins/TerrE2E"
  cp "$DLL" "$P/BepInEx/plugins/TerroritySmellatory/"; cp "$DRIVER" "$P/BepInEx/plugins/TerrE2E/"
  cp "$SRC_PROFILE/BepInEx/config/wubarrk.terroritysmellatory.cfg" "$P/BepInEx/config/"
  sed -i 's/^VerboseLogging = .*/VerboseLogging = true/' "$P/BepInEx/config/wubarrk.terroritysmellatory.cfg"
  # Client config overrides. A key the file ALREADY holds is rewritten IN PLACE; only a key it lacks is inserted right under
  # the section header. BepInEx writes every entry (with its default) into a profile's config the first time the mod runs
  # there, so the source profile's file gains lines whenever a boot check has run in it (17:09 and 17:37 on 2026-09-29: both
  # profiles then held "BannerCooldownSeconds = 90"), and a line inserted ABOVE such an entry is a duplicate that loses: the
  # lower line of a duplicate key wins. That silently put the banner cooldown back to 90 s and failed P03m in every run
  # after those boot checks (found 18:58, runs 185217 and 185842).
  local CFG="$P/BepInEx/config/wubarrk.terroritysmellatory.cfg"
  set_client_cfg() { # <key> <value>
    if grep -q "^$1 = " "$CFG"; then sed -i "s/^$1 = .*/$1 = $2/" "$CFG"; else sed -i "/^\[2 - Client\]/a $1 = $2" "$CFG"; fi
  }
  # 0.2.0 only (a 0.1.1 DLL ignores the key): the banner cooldown off. The fixtures' control walks leave the circle and
  # come back within seconds, and with the default 90 s the mod says the second entry and the second goodbye silently
  # (BannerPlan COOLDOWN, by design: it is covered by the mod's own harness, tests/BannerTests.cs), so the goodbye check
  # P03m could never see its "Leaving" line. Everything else stays at the mod's defaults (EntryBannerStyle=Epic).
  set_client_cfg BannerCooldownSeconds 0
  # E2E_CLIENT_CFG_EXTRA: more "key = value" lines for the same section (newline separated), for MUTATION runs that
  # switch a feature off through the mod's own config to prove a check can go red, e.g. "MinimapTerritoryName = false".
  # Applied after the cooldown line, so an extra line for that key wins.
  if [ -n "${E2E_CLIENT_CFG_EXTRA:-}" ]; then
    while IFS= read -r line; do
      [ -n "$line" ] && set_client_cfg "${line%% =*}" "${line#*= }"
    done <<<"$E2E_CLIENT_CFG_EXTRA"
    log "client cfg extra lines for [2 - Client]: $E2E_CLIENT_CFG_EXTRA"
  fi
  if [ "$BC" = "1" ]; then cp -r "$SRC_PROFILE/BepInEx/plugins/Wubarrk-BetterContinents" "$P/BepInEx/plugins/"; cp "$SRC_PROFILE/BepInEx/config/BetterContinents.cfg" "$P/BepInEx/config/" 2>/dev/null; fi
  # The fake home: Unity's persistentDataPath, PlayerPrefs and Player.log land here, never in ~/.config.
  local H="$RUN/home-$1"
  mkdir -p "$H/.config" "$H/.cache" "$H/.local/share" "$H/.local/state" "$RUN/saves-$1" "$RUN/cwd-$1"
  mkdir -m 700 -p "$H/.run"   # a private, EMPTY runtime dir: no Wayland, X or PulseAudio socket to reach
  # Steam's own RestartAppIfNecessary answers false when a steam_appid.txt sits in the working directory (the
  # driver also answers it false in-process): a client started outside Steam must never be relaunched BY Steam.
  echo 892970 > "$RUN/cwd-$1/steam_appid.txt"
  ln -s "$HOME/.steam" "$H/.steam"
  ln -s "$HOME/.local/share/Steam" "$H/.local/share/Steam"
}

# ─────────────────────────────────────────────────────────────
# Launchers (run IN PLACE, read-only; the working directory is a throwaway dir, never the install)
# ─────────────────────────────────────────────────────────────
start_server() { # <role server|server-restart> <logname>
  mkdir -p "$RUN/cwd-server"
  ( cd "$RUN/cwd-server" || exit 1
    exec env DOORSTOP_ENABLED=1 DOORSTOP_TARGET_ASSEMBLY="$SRV/BepInEx/core/BepInEx.Preloader.dll" \
      LD_LIBRARY_PATH="$DOORSTOP:$GAME_SERVER/linux64:${LD_LIBRARY_PATH:-}" LD_PRELOAD="libdoorstop_x64.so" \
      HOME="$RUN/home-server" XDG_CONFIG_HOME="$RUN/home-server/.config" XDG_DATA_HOME="$RUN/home-server/.local/share" \
      XDG_CACHE_HOME="$RUN/home-server/.cache" XDG_STATE_HOME="$RUN/home-server/.local/state" XDG_RUNTIME_DIR="$RUN/home-server/.run" \
      SteamAppId=892970 TERR_E2E_ROLE="$1" TERR_E2E_TAG="$1" TERR_E2E_MODE="$MODE" TERR_E2E_RUNDIR="$RUN" TERR_E2E_MARK="$MARK" TERR_E2E_RIGPID="$$" \
      TERR_E2E_FORCE030="${E2E_FORCE_030:-}" TERR_E2E_FORCE031="${E2E_FORCE_031:-}" TERR_E2E_DOORDELAY="$DOOR_DELAY" \
      "$GAME_SERVER/valheim_server.x86_64" -nographics -batchmode -name "terr-e2e-$TS" -port "$PORT" -world "$WORLD" \
      -password "$PASS_WORD" -savedir "$SRV/saves" -public 0 -saveinterval 1800 ) > "$LOGS/$2" 2>&1 &
  PIDS[$1]=$!
  log "started $1 (pid ${PIDS[$1]}) on port $PORT, world $WORLD"
}
wait_server() { # <logname> <timeout>
  local start; start=$(date +%s)
  while kill -0 "${PIDS[$SROLE]}" 2>/dev/null; do
    grep -q "Zonesystem Awake" "$LOGS/$1" 2>/dev/null && return 0
    grep -qE "Could not run Chainloader|Failed to load world" "$LOGS/$1" 2>/dev/null && return 1
    [ $(( $(date +%s) - start )) -ge "$2" ] && return 1
    sleep 2
  done
  return 1
}
XDISPLAY=""
start_xvfb() { # a PRIVATE virtual X server: nothing it shows is on Rohan's screen (it has no screen at all)
  local fd_file="$RUN/xvfb.display"
  ( exec Xvfb -displayfd 3 -screen 0 1024x768x24 -nolisten tcp -dpi 96 3> "$fd_file" ) > "$LOGS/xvfb.log" 2>&1 &
  PIDS[xvfb]=$!
  for _ in $(seq 1 50); do [ -s "$fd_file" ] && break; sleep 0.2; done
  XDISPLAY=":$(tr -dc '0-9' < "$fd_file")"
  [ "$XDISPLAY" = ":" ] && { log "Xvfb did not start"; return 1; }
  log "private Xvfb (pid ${PIDS[xvfb]}) on display $XDISPLAY - a virtual display with no screen; the clients run $([ "$GRAPHICS" = "1" ] && echo "with a GL device (-force-glcore)" || echo "-batchmode -nographics")"
}
start_client() { # <role> <character|""> <driver role>
  local role="$1" P="$PROFILE_PREFIX-$1" H="$RUN/home-$1"
  local gfx=(-batchmode -nographics); [ "$GRAPHICS" = "1" ] && gfx=(-batchmode -force-glcore -screen-width "${E2E_SCREEN_W:-640}" -screen-height "${E2E_SCREEN_H:-360}" -screen-fullscreen 0)   # GL on the private Xvfb (Vulkan init crashes there); small, because llvmpipe draws every pixel on the CPU
  # E2E_WINDOWED=1 (with --graphics): no -batchmode, so the client opens a real window on the private Xvfb. A batchmode client never repaints its IMGUI (the panel's
  # OnGUI gets Layout and input events and no Repaint, and a screen read comes back black), so the panel can only be photographed from a windowed one.
  [ "$GRAPHICS" = "1" ] && [ "${E2E_WINDOWED:-0}" = "1" ] && gfx=(-force-glcore -screen-width "${E2E_SCREEN_W:-1024}" -screen-height "${E2E_SCREEN_H:-768}" -screen-fullscreen 0)
  ( cd "$RUN/cwd-$role" || exit 1
    exec env -i PATH="/usr/local/bin:/usr/bin:/bin" USER="$USER" LOGNAME="${LOGNAME:-$USER}" LANG="en_US.UTF-8" \
      HOME="$H" XDG_CONFIG_HOME="$H/.config" XDG_DATA_HOME="$H/.local/share" XDG_CACHE_HOME="$H/.cache" XDG_STATE_HOME="$H/.local/state" \
      XDG_RUNTIME_DIR="$H/.run" ${XDISPLAY:+DISPLAY=$XDISPLAY} \
      PULSE_SERVER="unix:/nonexistent/terr-e2e-no-audio" SDL_AUDIODRIVER=dummy ALSOFT_DRIVERS=null \
      DOORSTOP_ENABLED=1 DOORSTOP_TARGET_ASSEMBLY="$P/BepInEx/core/BepInEx.Preloader.dll" \
      LD_LIBRARY_PATH="$DOORSTOP:$GAME_CLIENT" LD_PRELOAD="libdoorstop_x64.so" SteamAppId=892970 \
      TERR_E2E_BOOTH="${E2E_BOOTH:-}" TERR_E2E_ROLE="$3" TERR_E2E_TAG="$role" TERR_E2E_MODE="$MODE" TERR_E2E_CHAR="$2" TERR_E2E_RUNDIR="$RUN" TERR_E2E_MARK="$MARK" TERR_E2E_RIGPID="$$" \
      TERR_E2E_HOLD="${HOLD:-45}" TERR_E2E_STEP_TIMEOUT="${STEP_TIMEOUT:-90}" TERR_E2E_FORCE030="${E2E_FORCE_030:-}" TERR_E2E_FORCE031="${E2E_FORCE_031:-}" TERR_E2E_DOORDELAY="$DOOR_DELAY" \
      TERR_E2E_SAVEDIR="$RUN/saves-$role" TERR_E2E_REALHOME="$HOME" TERR_E2E_JOIN="127.0.0.1:$PORT" \
      TERR_E2E_WORLDFILE="$SRV/BepInEx/config/TerroritySmellatory/worlds/$WORLD.json" \
      "$GAME_CLIENT/valheim.x86_64" "${gfx[@]}" -logFile "$LOGS/$role.player.log" -password "$PASS_WORD" ) > "$LOGS/$role.stdout.log" 2>&1 &
  PIDS[$role]=$!
  log "started client $role (pid ${PIDS[$role]}, character ${2:-none}, driver role $3, ${gfx[*]})"
}
done_line() { grep -h "\[E2E\] DONE" "$RUN/results-$1.txt" 2>/dev/null | tail -1; }

safety_after() { # <id> <when>: rule 2 after EVERY client run (H90), and again once the servers have stopped (H91)
  local id="${1:-H90-rule2}" when="${2:-after the client run}" fs
  local what="Rohan's ~/.config/unity3d/IronGate/Valheim, the Steam Cloud dir and both install dirs unchanged since before this run's first Valheim process ($when)"
  if python3 "$HERE/safety.py" check "$RUN/safety" | tee -a "$RUN/rig.log" | grep -q "RULE-2 VIOLATION"; then
    cp "$RUN/safety/check.json" "$RUN/safety/check-$id.json" 2>/dev/null
    fs="$(foreign_servers | tr '\n' ' ')"
    check "$id" "$what" 0 "SAFETY VIOLATION - see $RUN/safety/check-$id.json${FOREIGN_SRV:+; dedicated servers this rig did not start were running with the real HOME at the start: $FOREIGN_SRV}${fs:+; and now: $fs}"
    log "!!!!!!!! RULE-2 SAFETY VIOLATION - STOPPING !!!!!!!!"
    return 1
  fi
  cp "$RUN/safety/check.json" "$RUN/safety/check-$id.json" 2>/dev/null
  check "$id" "$what (safety.py)" 1
  return 0
}

# ─────────────────────────────────────────────────────────────
# Spike (a): one client, main menu only - no server
# ─────────────────────────────────────────────────────────────
SROLE=server
python3 "$HERE/safety.py" first "$SAFETY" >/dev/null
if [ "$SPIKE" = "a" ]; then
  make_client_profile probe
  python3 "$HERE/safety.py" baseline "$RUN/safety" | tee -a "$RUN/rig.log"
  if [ "$XVFB" = "1" ]; then start_xvfb || exit 11; fi
  start_client probe "" probe
  start=$(date +%s)
  while kill -0 "${PIDS[probe]}" 2>/dev/null && [ -z "$(done_line probe)" ] && [ $(( $(date +%s) - start )) -lt 300 ]; do sleep 3; done
  log "probe: $(done_line probe)"
  [ "${E2E_BOOTH:-}" = "1" ] && log "panel booth: $(grep -c 'panel draw tick failed' "$PROFILE_PREFIX-probe/BepInEx/LogOutput.log" 2>/dev/null) panel draw error(s) in the client's log; pictures: $(ls "$RUN/shots" 2>/dev/null | grep -c '^panel-')"
  sleep 5; stop_pid "${PIDS[probe]}" probe 30; unset 'PIDS[probe]'
  cp "$PROFILE_PREFIX-probe/BepInEx/LogOutput.log" "$LOGS/probe.LogOutput.log" 2>/dev/null
  log "throwaway dirs after the probe:"; (cd "$RUN" && find home-probe saves-probe cwd-probe -maxdepth 6 \( -type f -o -type l \) | sort | sed 's/^/   /') | tee -a "$RUN/rig.log"
  safety_after || exit 10
  grep -h "\[E2E\]" "$RUN"/results-*.txt; exit 0
fi

# ─────────────────────────────────────────────────────────────
# Server
# ─────────────────────────────────────────────────────────────
make_server_profile || { log "could not make the server profile"; exit 8; }
SROLE=server
# Rule 2 covers the servers as well as the clients: the baseline is taken BEFORE this run's first Valheim process.
# (It used to be taken after the server had booted, so the server's own write to Rohan's prefs was never seen.)
python3 "$HERE/safety.py" baseline "$RUN/safety" | tee -a "$RUN/rig.log"
start_server server server.console.log
if ! wait_server server.console.log 480; then
  log "SERVER DID NOT BOOT - see $LOGS/server.console.log"; tail -30 "$LOGS/server.console.log"; exit 9
fi
log "server world scene is up (Zonesystem Awake)"
for _ in $(seq 1 60); do [ -f "$RUN/barriers/server.ready.json" ] && break; sleep 2; done
[ -f "$RUN/barriers/server.ready.json" ] || log "WARNING: the server driver never signalled server.ready"

if [ "$SPIKE" = "s" ]; then   # server only: the driver's own server checks, then stop
  sleep 5; stop_pid "${PIDS[server]}" server 90; unset 'PIDS[server]'
  cp "$SRV/BepInEx/LogOutput.log" "$LOGS/server.LogOutput.log" 2>/dev/null
  safety_after H91-rule2-servers "after the server stopped" || exit 10
  grep -h "\[E2E\]" "$RUN"/results-*.txt; grep -hE "TerroritySmellatory|TerrE2E" "$LOGS/server.LogOutput.log" | grep -v "\[E2E\]" | head -20; exit 0
fi

# ─────────────────────────────────────────────────────────────
# Clients
# ─────────────────────────────────────────────────────────────
FOREIGN="$(foreign_clients)"; [ -n "$FOREIGN" ] && { log "REFUSING: a foreign Valheim client appeared (pid(s) $FOREIGN)"; exit 4; }
[ "$(mem_kb)" -lt "$MIN_MEM_KB" ] && { log "REFUSING: MemAvailable dropped under 13 GB before the client launch"; exit 5; }
if [ "$XVFB" = "1" ]; then start_xvfb || exit 11; fi

if [ -n "$SPIKE" ]; then
  CLIENT_ROLES=(admin); CHARS=(TSAdmin); HOLD=20
  [ "$SPIKE" = "c" ] && { CLIENT_ROLES=(admin plain); CHARS=(TSAdmin TSPlain); HOLD=180; }   # long enough to overlap
  for i in "${!CLIENT_ROLES[@]}"; do make_client_profile "${CLIENT_ROLES[$i]}"; done
  for i in "${!CLIENT_ROLES[@]}"; do start_client "${CLIENT_ROLES[$i]}" "${CHARS[$i]}" join; sleep 25; done
  start=$(date +%s)
  while [ $(( $(date +%s) - start )) -lt 420 ]; do
    all=1; for r in "${CLIENT_ROLES[@]}"; do [ -n "$(done_line "$r")" ] || { kill -0 "${PIDS[$r]}" 2>/dev/null && all=0; }; done
    [ "$all" = "1" ] && break; sleep 3
  done
  for r in "${CLIENT_ROLES[@]}"; do log "$r: $(done_line "$r")"; done
  sleep 5; for r in "${CLIENT_ROLES[@]}"; do stop_pid "${PIDS[$r]}" "$r" 30; unset "PIDS[$r]"; cp "$PROFILE_PREFIX-$r/BepInEx/LogOutput.log" "$LOGS/$r.LogOutput.log" 2>/dev/null; done
  safety_after || exit 10
  stop_pid "${PIDS[server]}" server 60; unset 'PIDS[server]'
  cp "$SRV/BepInEx/LogOutput.log" "$LOGS/server.LogOutput.log" 2>/dev/null
  safety_after H91-rule2-servers "after the server stopped" || exit 10
  log "server: refusals / joins:"; grep -hE "has invalid session ticket|Got connectoin error|Already connected|Got character ZDOID|wrong password|Closing socket" "$LOGS/server.console.log" "$LOGS"/*.player.log 2>/dev/null | tail -20 | tee -a "$RUN/rig.log"
  grep -h "\[E2E\]" "$RUN"/results-*.txt; exit 0
fi

if [ "$MODE" = "dual" ]; then CLIENT_ROLES=(admin plain); CHARS=(TSAdmin TSPlain); DRV=(admin plain)
else CLIENT_ROLES=(solo); CHARS=(""); DRV=(solo); fi
for r in "${CLIENT_ROLES[@]}"; do make_client_profile "$r"; done
for i in "${!CLIENT_ROLES[@]}"; do start_client "${CLIENT_ROLES[$i]}" "${CHARS[$i]}" "${DRV[$i]}"; [ "$i" = "0" ] && [ "${#CLIENT_ROLES[@]}" -gt 1 ] && sleep 20; done

# ─────────────────────────────────────────────────────────────
# Wait for every role's DONE line (global timeout)
# ─────────────────────────────────────────────────────────────
start=$(date +%s)
while :; do
  all=1
  for r in "${CLIENT_ROLES[@]}"; do
    [ -n "$(done_line "$r")" ] && continue
    if ! kill -0 "${PIDS[$r]}" 2>/dev/null; then log "client $r exited without a DONE line"; continue; fi
    all=0
  done
  [ -n "$(done_line server)" ] || { kill -0 "${PIDS[server]}" 2>/dev/null && all=0; }
  [ "$all" = "1" ] && break
  if [ $(( $(date +%s) - start )) -ge "$TIMEOUT" ]; then log "GLOBAL TIMEOUT after ${TIMEOUT}s"; break; fi
  m=$(mem_kb)
  if [ "$m" -lt "$YIELD_MEM_KB" ]; then
    log "YIELDING: MemAvailable $((m/1024)) MB < $((YIELD_MEM_KB/1024)) MB during the run - stopping this run's own processes before the memory watchdog picks a test server"
    exit 5
  fi
  f="$(foreign_clients)"; [ -n "$f" ] && log "WARNING: a foreign Valheim client started during the run (pid(s) $f)"
  sleep 3
done
log "scenario finished after $(( $(date +%s) - start ))s"
for r in "${CLIENT_ROLES[@]}"; do
  for _ in $(seq 1 30); do kill -0 "${PIDS[$r]}" 2>/dev/null || break; sleep 1; done   # the driver quits by itself
  stop_pid "${PIDS[$r]}" "$r" 30; unset "PIDS[$r]"
  cp "$PROFILE_PREFIX-$r/BepInEx/LogOutput.log" "$LOGS/$r.LogOutput.log" 2>/dev/null
done
safety_after || exit 10

# ─────────────────────────────────────────────────────────────
# Restart the server: the territory file must reload
# ─────────────────────────────────────────────────────────────
stop_pid "${PIDS[server]}" server 90; unset 'PIDS[server]'
cp "$SRV/BepInEx/LogOutput.log" "$LOGS/server.LogOutput.log" 2>/dev/null
cp "$SRV"/BepInEx/config/TerroritySmellatory/worlds/"$WORLD".json* "$RUN/artifacts/" 2>/dev/null
SROLE=server-restart
start_server server-restart server-restart.console.log
if wait_server server-restart.console.log 480; then
  for _ in $(seq 1 60); do [ -n "$(done_line server-restart)" ] && break; sleep 2; done
else
  log "the restarted server did not boot"
fi
sleep 3; stop_pid "${PIDS[server-restart]}" server-restart 90; unset 'PIDS[server-restart]'
cp "$SRV/BepInEx/LogOutput.log" "$LOGS/server-restart.LogOutput.log" 2>/dev/null
# Rule 2 once more, now that no process of this run is left: the servers start and stop after the client check.
log "the servers' own Unity prefs went to their fake home: $(ls -l --time-style=+%H:%M:%S "$RUN/home-server/.config/unity3d/IronGate/Valheim/prefs" 2>&1 | cut -d' ' -f5-)"
SAFETY_BAD=0
safety_after H91-rule2-servers "after the server and the restarted server stopped" || SAFETY_BAD=1

# ─────────────────────────────────────────────────────────────
# Rig-level checks (logs), summary
# ─────────────────────────────────────────────────────────────
SL="$LOGS/server.LogOutput.log"; RL="$LOGS/server-restart.LogOutput.log"
VER=$(grep -ohE "TerroritySmellatory [0-9.]+ loaded[^\r]*" "$SL" | head -1)
check H01-server-loaded "server log: '$VER'" "$([ -n "$VER" ] && ! grep -q "failed to bind" <<<"$VER" && echo 1 || echo 0)" "no clean 'loaded.' line"
check H02-selfcheck "server log: 'Territory file format self-check passed'" "$(grep -q "Territory file format self-check passed" "$SL" && echo 1 || echo 0)" "line missing"
NC=$(grep -cE "created territory #[0-9]+ 'E2E" "$SL"); check H03-created-lines "server log: $NC 'created territory #N' lines for the E2E territories" "$([ "$NC" = "3" ] && echo 1 || echo 0)" "expected 3"
grep -hE "created territory|updated territory|reshaped territory|deleted territory" "$SL" | sed 's/^/   /' | tee -a "$RUN/rig.log"
check H04-no-plain-refusal "server log: no 'Refused territory ... from TSPlain' line (the plain client never sent a request)" "$(grep -q "Refused territory.*TSPlain" "$SL" && echo 0 || echo 1)" "$(grep -h "Refused territory" "$SL" | head -3)"
EXP=$(python3 -c "import json;print(json.load(open('$RUN/barriers/server.expected.json'))['all'])" 2>/dev/null || echo "?")
LOADED=$(grep -ohE "Loaded [0-9]+ territor(y|ies) for world '$WORLD'" "$RL" | head -1)
check H05-reload-line "restart log: '$LOADED' (expected $EXP)" "$(echo "$LOADED" | grep -qE "^Loaded $EXP territor" && echo 1 || echo 0)" "line missing or count differs"
ERRS=$(grep -hE "\[(Error|Fatal) *: *(TerroritySmellatory|TerrE2E)\]|Exception.*(TerroritySmellatory|TerrE2E)|at TerroritySmellatory\." "$LOGS"/*.LogOutput.log 2>/dev/null | grep -v "\[E2E\] FAIL" | head -5)
check H06-no-mod-errors "no TerroritySmellatory/TerrE2E errors or exceptions in any BepInEx log" "$([ -z "$ERRS" ] && echo 1 || echo 0)" "$ERRS"
PF=$(grep -ohE "[0-9]+ patch class\(es\) failed" "$LOGS"/*.LogOutput.log | head -1)
check H07-patches "every Harmony patch class bound on server and clients" "$([ -z "$PF" ] && echo 1 || echo 0)" "$PF"
for r in "${CLIENT_ROLES[@]}" server server-restart; do
  d="$(done_line "$r")"; check "H10-done-$r" "role $r reported: ${d:-nothing}" "$([ -n "$d" ] && echo 1 || echo 0)" "no DONE line"
done
if [ -f "$WDOG_LOG" ] && tail -n +"$((WDOG_LINES + 1))" "$WDOG_LOG" | grep -q "LOW MEMORY"; then
  check H11-mem-watchdog "the memory watchdog stayed quiet" 0 "$(tail -n +"$((WDOG_LINES + 1))" "$WDOG_LOG" | grep "LOW MEMORY" | head -3)"
else
  check H11-mem-watchdog "the memory watchdog stayed quiet during the run ($WDOG_LOG)" 1
fi

# The DLL under test's own version, from its "loaded." line on the server: from 0.2.0 on, the combine phase's ids (A20,
# P20, S30, S31, R30) must all have reported. The driver decides whether to run that phase by LOOKING at the DLL
# (Features.cs), so a 0.2.0 DLL whose members it cannot find skips the phase and H12 fails the run instead of it passing.
MODVER=$(sed -nE 's/^TerroritySmellatory ([0-9.]+) loaded.*/\1/p' <<<"$VER")
V020=0; [ -n "$MODVER" ] && [ "$(printf '%s\n0.2.0\n' "$MODVER" | sort -V | head -1)" = "0.2.0" ] && V020=1
# ... and from 0.3.0 on the runic barrier phase's ids (A30, A31, P30, S31, R31 and the two extra logins)
V030=0; [ -n "$MODVER" ] && [ "$(printf '%s\n0.3.0\n' "$MODVER" | sort -V | head -1)" = "0.3.0" ] && V030=1
# ... and from 0.3.1 on the auto-close doors' ids (A32, P31, S32, R32, H14)
V031=0; [ -n "$MODVER" ] && [ "$(printf '%s\n0.3.1\n' "$MODVER" | sort -V | head -1)" = "0.3.1" ] && V031=1
# ... and from 0.4.0 on the sub-territory phase's ids (A40, P40, S40, R40, H15) and the banner's PvP notch checks
V040=0; [ -n "$MODVER" ] && [ "$(printf '%s\n0.4.0\n' "$MODVER" | sort -V | head -1)" = "0.4.0" ] && V040=1
V041=0; [ -n "$MODVER" ] && [ "$(printf '%s\n0.4.1\n' "$MODVER" | sort -V | head -1)" = "0.4.1" ] && V041=1
if [ "$V040" = "1" ]; then
  # The SERVER's own record of what it did, independent of what the clients saw: the town created inside the land (the parent it
  # picked is logged), the land deleted with the town moved up, and the second pair deleted in one request.
  SUBC=$(grep -cE "created territory #[0-9]+ 'TS Sub Town' .*, inside #[0-9]+ 'TS Sub Land'" "$SL" 2>/dev/null)
  SUBD=$(grep -cE "deleted territory #[0-9]+ 'TS Sub Land'\. Its sub-territory keeps the rules it had and now sits at the top level" "$SL" 2>/dev/null)
  SUBT=$(grep -cE "deleted territory #[0-9]+ 'TS Sub Land [24]' and the 1 territory inside it" "$SL" 2>/dev/null)
  SUBM=$(grep -cE "combined territory #[0-9]+ 'TS Sub Land 3' into #[0-9]+ 'TS Sub Land 4'" "$SL" 2>/dev/null)
  # The rig lowers the piece size on its server (Scenario040.SmallParts): the server says once that a list goes out in pieces.
  PARTS=$(grep -cE "The territory list is [0-9]+ (KiB|bytes): it goes to each player in [0-9]+ pieces of at most 512 bytes" "$SL" 2>/dev/null)
  check H16-parts-log "server log: the territory list went out in pieces of at most 512 bytes, said once ($PARTS) - wanted 1" "$([ "$PARTS" = "1" ] && echo 1 || echo 0)" "$(grep -hE "territory list is" "$SL" | head -c 400 | tr '\n' '|')"
  check H15-sub-log "server log: the town created inside the land ($SUBC), the land deleted with its sub-territory kept and moved up ($SUBD), a land with a town combined into another ($SUBM), and two lands deleted with the town inside each in one request ($SUBT) - wanted 1, 1, 1, 2" \
    "$([ "$SUBC" = "1" ] && [ "$SUBD" = "1" ] && [ "$SUBM" = "1" ] && [ "$SUBT" = "2" ] && echo 1 || echo 0)" "created $SUBC, deleted-with-sub $SUBD, combined $SUBM, deleted-tree $SUBT; sub-territory lines: $(grep -hE "TS Sub" "$SL" | head -c 900 | tr '\n' '|')"
fi
if [ "$V031" = "1" ]; then
  # The SERVER's own record of what it closed (VerboseLogging: "auto-closed wood_door at (...) in 'TS Doors' after Ns open"),
  # independent of what the client saw: the doors territory's two doors (P and Q) were closed there, and nothing was closed in the
  # nested territory or the one without the flag.
  DCL=$(grep -hE "auto-closed [A-Za-z_]+ at \(" "$SL" 2>/dev/null)
  DIN=$(grep -c "in 'TS Doors' after" <<<"$DCL")
  DOTHER=$(grep -E "in 'TS Doors (Nested|Off)'" <<<"$DCL" | head -2)
  check H14-door-log "server log: $DIN 'auto-closed ... in 'TS Doors' after N s open' line(s) (wanted 2: the lone door and the door left when the player walked away), none in the nested or the unflagged territory" \
    "$([ "$DIN" -ge 2 ] && [ -z "$DOTHER" ] && echo 1 || echo 0)" "lines in 'TS Doors': $DIN; lines elsewhere: ${DOTHER:-none}; all: $(head -c 600 <<<"$DCL" | tr '\n' '|')"
fi
if [ "$V030" = "1" ]; then
  # Rules/Barrier.cs and Visuals/BarrierFlare.cs log a failure once per 30 s ("Runic barrier failed on one character",
  # "Barrier flare tick failed"); a run with the barrier must show none, on any client or server. 0.3.0 shipped a flare
  # that threw while building its texture ("LoadRawTextureData: not enough data provided") and then disabled itself,
  # logging one WARNING that this check did not look for: Rohan saw no shield. So this also fails on the build-failure
  # line at ANY level ("Barrier flare: could not build ..."), and on every ERROR the flare logs, whatever its wording.
  BERR=$(grep -hiE "runic barrier.*failed|flare (flare |tick |rune )?failed|barrier flare: could not build|\[error *: *terroritysmellatory\] barrier flare" "$LOGS"/*.LogOutput.log 2>/dev/null | head -3)
  check H08-no-barrier-errors "no 'Runic barrier failed', 'Barrier flare ... failed' or 'Barrier flare: could not build' line, and no ERROR from the flare, in any BepInEx log" "$([ -z "$BERR" ] && echo 1 || echo 0)" "$BERR"
  if [ "$GRAPHICS" = "1" ]; then
    # A --graphics client has a real GL device, so its flare must have picked a shader and said so exactly once.
    SHL=$(grep -h "Barrier flare: drawing with shader" "$LOGS"/*.LogOutput.log 2>/dev/null | head -2)
    check H13-flare-shader "a graphics client logged 'Barrier flare: drawing with shader X': ${SHL:-nothing}" "$([ -n "$SHL" ] && echo 1 || echo 0)" "no such line: the flare never built its material"
  fi
fi
python3 - "$RUN" "$SUMMARY" "$MODE" "$V020" "$V030" "$GRAPHICS" "$V031" "$V040" "$V041" <<'PY'
import glob, os, re, sys
run, out, mode, v020, v030, graphics, v031, v040, v041 = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4] == "1", sys.argv[5] == "1", sys.argv[6] == "1", sys.argv[7] == "1", sys.argv[8] == "1", sys.argv[9] == "1"
lines, done = [], {}
for f in sorted(glob.glob(os.path.join(run, "results-*.txt"))):
    role = os.path.basename(f)[8:-4]
    for l in open(f, encoding="utf-8", errors="replace"):
        l = l.rstrip("\n")
        m = re.match(r"\[E2E\] (PASS|FAIL|SOFT) (\S+) (.*)", l)
        if m: lines.append((role, m.group(1), m.group(2), m.group(3)))
        d = re.match(r"\[E2E\] DONE (.*)", l)
        if d: done[role] = d.group(1)

# H12: a run passes on "no FAIL line", so a check that never ran is invisible unless something counts it. Two counts:
# every driver's DONE line (its own pass/fail/soft counters) must match the lines its results file holds (a lost
# write), and in solo mode every check id of the scenario must have reported (a step that ended early without a
# FAIL). The ids are those of the 2026-09-29 runs (plain and --bc alike); a new check only has to be added here if
# it must never go missing. Dual mode is untested (it needs a second Steam account), so its list is not asserted.
EXPECTED_SOLO = """
A00-character A00-join A00b-join A01-snapshot-admin A03-create-circle A03-create-circle-rules A04-create-square
A04-create-square-rules A05-create-rect A05-create-rect-rules A06a-bypass-access A06b-bypass-build A06c-bypass-harvest
A06d-structure-admin A06e-structure-control A07a-edit-tool A07b-update-rules A08a-edit-tool A08b-member A09-reshape
A10a-remove-arms A10b-remove C01-menu C02-redirect H01-server-loaded H02-selfcheck H03-created-lines
H04-no-plain-refusal H05-reload-line H06-no-mod-errors H07-patches H10-done-server H10-done-server-restart
H10-done-solo H11-mem-watchdog H90-rule2 H91-rule2-servers P00-character P00-join P00b-join P01-snapshot-plain P02-territories P02b-home
P03a-presence P03b-banner-enter P03c-pvp-forced-off P03d-pvp-toggle-locked P03e-sanctuary P03f-sanctuary-bonuses
P03g-access-refused P03h-build-refused P03i-door-refused P03j-chest-refused P03k-harvest-refused P03l-presence-leave
P03m-banner-leave P03n-pvp-restored P03o-sanctuary-gone P03p-sanctuary-hud P04a-structure-protected
P04b-structure-control P04c-presence P04d-banner-enter P04e-pvp-forced-on P04f-pvp-toggle-locked
P04g-no-monster-spawns P04h-creature-spawner P04i-banner-leave P04j-pvp-restored P05a-rotation P05b-updated-rules
P05c-presence P05d-no-banner P05e-access-refused P05f-harvest-refused P05g-border P05h-map P06-no-request
P07a-member-snapshot P07b-banner-updated P07c-member-access P07d-member-door P07e-member-chest P07f-member-harvest
P08a-reshape-snapshot P08b-reshape-enforced P08c-reshape-presence P09a-removed-snapshot P09b-removed-unprotected
R01-reload S01-mod-loaded S02-selfcheck S03-store S10a-file-vs-store S10b-file-vs-admin S10c-bak S12a-file-vs-store
S12b-file-vs-admin S20a-final-file S20b-final-content S21-no-request-from-plain S22-admin-by-server S23-seam S24-peers
""".split()
# A 0.2.0 DLL adds the combine phase (Scenario020.cs, ServerRole.cs); nothing above changes meaning.
EXPECTED_020 = """
A20a-comb-create A20b-combine P20a-comb-snapshot P20b-comb-access P20c-comb-presence P20d-comb-seam-quiet
P20e-comb-minimap S30-combined-store S31-parts-file R30-reload-parts
""".split()
# A 0.3.0 DLL adds the runic barrier phase (Scenario030.cs, ServerRole.cs) and two more logins (admin phase 3 adds TSPlain
# to the visitor barrier, plain phase 3 walks in); the driver runs it when TerritoryFlags has RunicBarrier, so a 0.3.0 DLL
# whose barrier phase silently skipped FAILS here.
EXPECTED_030 = """
A00c-join P00c-join H08-no-barrier-errors A30a-create-barrier A30b-create-guard A30c-create-control A31-guard-member
P30a-hold P30b-pushout P30c-flare P30d-pass-tamed P30d2-pass-passive P30e-visitor-held P30e2-open-barrier-free
P30e3-member-passes P30f-visitor-pushout P30g-visitor-flare S31a-barrier-store S31b-barrier-file R31-reload-barrier
""".split()
# A 0.3.1 DLL adds the auto-close doors phase (Scenario031.cs, ServerRole.cs) and the server-log check H14.
EXPECTED_031 = """
A32a-create-doors A32b-create-nested A32c-create-off P31a-door-closes P31b-door-outside-stays P31c-door-flag-off-stays
P31d-door-excluded-stays P31e-door-nested-stays P31f-door-player-near S32a-doors-store S32b-doors-file R32-reload-doors H14-door-log
""".split()
# A 0.4.0 DLL adds the sub-territory phase (Scenario040.cs, ServerRole.cs), the server-log check H15, and the banner's PvP notch checks
# (P03q and P04k on the two forced zones, P20f the control that a territory leaving PvP alone has none).
EXPECTED_040 = """
A40a-create-land A40a2-land-top-level A40b-create-town A40c-edit-land A40d-follow-again A40e-delete-land A40f-pair A40f-pair-land A40f-pair-town A40g-delete-tree
A40h-land3 A40h-town3 A40h-land4 A40h-combine A40h-cleanup
P40a-snapshot P40b-enforced P40c-pvp-banner S40a-sub-store S40b-sub-file S40c-sub-baked R40-reload-sub H15-sub-log
P03q-banner-notch P04k-banner-notch P20f-banner-no-notch
S41a-parts-sent P41a-parts-received H16-parts-log
""".split()
if v020:
    EXPECTED_SOLO += EXPECTED_020
if v030:
    EXPECTED_SOLO += EXPECTED_030
    if v031:
        EXPECTED_SOLO += EXPECTED_031
    if v040:
        EXPECTED_SOLO += EXPECTED_040
    if v041:   # 0.4.1: the minimap's name label on one line (Scenario in MapFogShot.cs; every client, the label exists headless too)
        EXPECTED_SOLO += ["P41c-label-one-line"]
    if graphics:   # a --graphics client has a GL device, so the driver also counts the flares drawn and looks at the pixels
        EXPECTED_SOLO += ["P30h-flare-drawn", "P30i-flare-pixels", "P30j-flare-in-game-view"]
        if v041:   # 0.4.1: the map's explored mask, compared with the game's own explored cells (Scenario041, graphics only: the minimap updates only with a device)
            EXPECTED_SOLO += ["P41b-fog-mask"]
problems = []
counted = {}
for role, kind, cid, what in lines:
    counted.setdefault(role, [0, 0, 0])[("PASS", "FAIL", "SOFT").index(kind)] += 1
for r, d in sorted(done.items()):
    m = re.search(r"passed=(\d+) failed=(\d+) soft=(\d+)", d)
    if m and [int(x) for x in m.groups()] != counted.get(r, [0, 0, 0]):
        problems.append(f"{r}: its DONE line says {m.group(0)} but results-{r}.txt holds passed/failed/soft={counted.get(r, [0, 0, 0])}")
if mode == "solo":
    seen = {re.sub(r"#\d+$", "", cid) for _, _, cid, _ in lines}
    missing = [i for i in EXPECTED_SOLO if i not in seen]
    if missing:
        problems.append(f"{len(missing)} check(s) never reported: " + " ".join(missing))
what = ("every scenario check reported (" + str(len(EXPECTED_SOLO)) + " ids) and every driver's DONE counters match its results file"
        if mode == "solo" else "every driver's DONE counters match its results file (dual mode: the id list is not asserted)")
kind = "FAIL" if problems else "PASS"
h12 = f"[E2E] {kind} H12-results-complete {what}" + (": " + " | ".join(problems) if problems else "")
print(h12)
with open(os.path.join(run, "results-rig.txt"), "a", encoding="utf-8") as rf:
    rf.write(h12 + "\n")
lines.append(("rig", kind, "H12-results-complete", what + (": " + " | ".join(problems) if problems else "")))

p = sum(1 for x in lines if x[1] == "PASS"); f_ = sum(1 for x in lines if x[1] == "FAIL"); s = sum(1 for x in lines if x[1] == "SOFT")
with open(out, "w") as o:
    for role, kind, cid, what in lines:
        o.write(f"{kind:4} {role:15} {cid} {what}\n")
    o.write(f"\nTOTAL: {p} passed, {f_} failed, {s} soft\n")
    for r, d in sorted(done.items()): o.write(f"  {r}: {d}\n")
print(open(out).read())
sys.exit(0 if f_ == 0 and p > 0 else 1)
PY
RC=$?
log "summary: $SUMMARY"
if [ "$SAFETY_BAD" = "1" ]; then
  log "RESULT: RULE-2 SAFETY VIOLATION after the servers stopped (H91; see $RUN/safety/check-H91-rule2-servers.json) - exit 10"
  exit 10
fi
[ "$RC" = "0" ] && log "RESULT: ALL CHECKS PASSED" || log "RESULT: FAILURES (exit 1)"
exit $RC
