#!/usr/bin/env bash
# build-guard-bodies.sh - builds Assets/terroritysmellatory-guards.bundle, the territory guards' Meshy bodies (Visuals/GuardBodies.cs), which
# TerroritySmellatory.csproj EMBEDS in the DLL. It is ValtimaOnline's tools/build-npc-bodies.sh for the two guard looks only:
#   1. makes (once) a small Unity project at unity-project/ (git-ignored) for Unity 6000.0.61f1, the version ValtimaOnline's bodies were baked with;
#   2. copies the two guard bakes from ValtimaOnline's sources - guard_v2 (the approved pilot guard) and guard_warrior_f - with their albedo and
#      normal maps into Assets/TSGuards/, guard_v2.fbx once more as shared/guard_humanoid.fbx (the source of the shared Humanoid clips
#      guard_warrior_f plays), and unity/Editor/BuildTerritoryGuards.cs into Assets/Editor/ (only what changed, so Unity keeps its import cache);
#   3. runs the editor in batch mode WITH a graphics device (`unity run`, Unity's CLI; never -nographics, the bake lessons);
#   4. copies the bundle to Assets/, where the csproj picks it up on the next build.
# Environment:
#   TS_GUARD_SOURCES   ValtimaOnline's Sources/npc folder (default: ../ValtimaOnline/unity-project/Sources/npc)
#   TS_UNITY_PROJECT   the Unity project to build in (default: unity-project/ of this repo)
# Needs: the Unity CLI signed in with a licence (unity auth login; unity license activate --personal), editor 6000.0.61f1 with Windows Build
# Support (Mono) (the bundle targets StandaloneWindows64, as ValtimaOnline's does), and on Arch libxml2-legacy (the editor links libxml2.so.2).
# Heavy: run it through heavy.sh, e.g.  heavy.sh 10G ts-guard-bundle tools/build-guard-bodies.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="${TS_GUARD_SOURCES:-$ROOT/../ValtimaOnline/unity-project/Sources/npc}"
PROJ="${TS_UNITY_PROJECT:-$ROOT/unity-project}"
VERSION="6000.0.61f1"
BUNDLE="terroritysmellatory-guards.bundle"
UNITY="${UNITY_CLI:-$(command -v unity || echo "$HOME/.local/bin/unity")}"

[ -x "$UNITY" ] || { echo "no Unity CLI ($UNITY): curl -fsSL https://unity.com/install.sh | bash"; exit 2; }
if pgrep -if -- "-projectpath[= ].*$(basename "$PROJ")" > /dev/null; then
  echo "a Unity editor has $PROJ open: close it first"; exit 2
fi

# 1. the project
mkdir -p "$PROJ/Assets/Editor" "$PROJ/Assets/TSGuards/shared" "$PROJ/Packages" "$PROJ/ProjectSettings"
if [ ! -f "$PROJ/ProjectSettings/ProjectVersion.txt" ]; then
  printf 'm_EditorVersion: %s\n' "$VERSION" > "$PROJ/ProjectSettings/ProjectVersion.txt"
fi
if [ ! -f "$PROJ/Packages/manifest.json" ]; then
  # ValtimaOnline's own set: the built-in modules only. No com.unity.collections, whose player-script compile fails for a Release
  # StandaloneWindows64 target and leaves a 0-byte bundle (libs-Tools/JOTUNN-AND-HEADLESS-AUTOMATION-FACTS.md section 8).
  cat > "$PROJ/Packages/manifest.json" <<'JSON'
{
  "dependencies": {
    "com.unity.modules.animation": "1.0.0",
    "com.unity.modules.assetbundle": "1.0.0",
    "com.unity.modules.imageconversion": "1.0.0",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.physics": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.uielements": "1.0.0"
  }
}
JSON
fi

# 2. the bakes and the editor script (a re-copy of an unchanged FBX would make Unity import it again)
sync_file() { if ! cmp -s "$1" "$2"; then cp "$1" "$2"; fi; }
DEST="$PROJ/Assets/TSGuards"
for pair in "pilot_guard_V2:guard_v2" "guard_warrior_f:guard_warrior_f"; do
  dir="$SRC/${pair%%:*}/bake"; id="${pair##*:}"
  for f in "$id.fbx" "${id}_albedo.png" "${id}_normal.png"; do
    [ -f "$dir/$f" ] || { echo "missing $dir/$f (ValtimaOnline's guard bake)"; exit 2; }
    sync_file "$dir/$f" "$DEST/$f"
  done
done
sync_file "$SRC/pilot_guard_V2/bake/guard_v2.fbx" "$DEST/shared/guard_humanoid.fbx"
sync_file "$ROOT/unity/Editor/BuildTerritoryGuards.cs" "$PROJ/Assets/Editor/BuildTerritoryGuards.cs"

# 3. the build
mkdir -p "$PROJ/Logs"
LOG="$PROJ/Logs/build-guard-bodies.log"
if ! "$UNITY" run "$PROJ" --editor-version "$VERSION" --log-file "$LOG" --no-tail -- -executeMethod BuildTerritoryGuards.Build; then
  echo "Unity failed; the log is $LOG:"
  grep -n -i '\[VALtima\]\|error\|exception' "$LOG" | tail -40
  exit 1
fi
grep '\[VALtima' "$LOG" | grep -v '^UnityEngine' || true

# 4. the bundle, where the csproj embeds it from
OUT="$PROJ/Build/TSGuards/$BUNDLE"
[ -s "$OUT" ] || { echo "no bundle at $OUT after the build"; exit 1; }
mkdir -p "$ROOT/Assets"
cp "$OUT" "$ROOT/Assets/$BUNDLE"
echo "bundle: $ROOT/Assets/$BUNDLE ($(stat -c%s "$ROOT/Assets/$BUNDLE") bytes, md5 $(md5sum "$ROOT/Assets/$BUNDLE" | cut -d' ' -f1))"
