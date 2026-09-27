#!/usr/bin/env bash
# Full compile of the plugin against a real KoboldKare install.
#
# This is the only check that can catch *semantic* errors — a renamed or missing
# member, a wrong argument type, a bad cast. The parse-only check
# (check_syntax.sh) cannot: it has no reference assemblies, so every Unity type
# is unknown and only syntax is verified.
#
# Skips (exit 0) when no game install is found, so it stays usable on a machine
# without the game and on CI. Set KOBOLDKARE_DIR to point at an install.
#
# Usage:
#   ./check_build.sh
#   KOBOLDKARE_DIR=/path/to/KoboldKare ./check_build.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

KDIR="${KOBOLDKARE_DIR:-/mnt/gaming/SteamLibrary/steamapps/common/KoboldKare}"

if [ ! -d "$KDIR/BepInEx/core" ]; then
  echo "SKIP: no KoboldKare install at $KDIR (set KOBOLDKARE_DIR to enable this check)"
  exit 0
fi
if ! command -v mcs >/dev/null 2>&1; then
  echo "SKIP: mcs not found — cannot compile." >&2
  exit 127
fi

OUT="${TMPDIR:-/tmp}/kkllmnpc-build.$$"
trap 'rm -rf "$OUT"' EXIT
mkdir -p "$OUT"

echo "Compiling against $KDIR ..."
# Reuse build.sh's exact reference set so this can't drift from the real build.
MGMT="$KDIR/KoboldKare_Data/Managed"
CORE="$KDIR/BepInEx/core"

mcs -target:library -out:"$OUT/KKLLMNPC.dll" src/*.cs \
  -r:"$CORE/BepInEx.dll" \
  -r:"$CORE/0Harmony.dll" \
  -r:"$MGMT/UnityEngine.dll" \
  -r:"$MGMT/UnityEngine.CoreModule.dll" \
  -r:"$MGMT/UnityEngine.UI.dll" \
  -r:"$MGMT/UnityEngine.UIModule.dll" \
  -r:"$MGMT/UnityEngine.PhysicsModule.dll" \
  -r:"$MGMT/UnityEngine.AnimationModule.dll" \
  -r:"$MGMT/UnityEngine.TextRenderingModule.dll" \
  -r:"$MGMT/UnityEngine.UnityWebRequestModule.dll" \
  -r:"$MGMT/UnityEngine.AudioModule.dll" \
  -r:"$MGMT/UnityEngine.VideoModule.dll" \
  -r:"$MGMT/UnityEngine.ImageConversionModule.dll" \
  -r:"$MGMT/UnityEngine.AIModule.dll" \
  -r:"$MGMT/PhotonUnityNetworking.dll" \
  -r:"$MGMT/Photon3Unity3D.dll" \
  -r:"$MGMT/PhotonRealtime.dll" \
  -r:"$MGMT/Assembly-CSharp.dll" \
  -r:"$MGMT/Naelstrof.PenetrationTech.dll" \
  -r:"$MGMT/UnityEngine.IMGUIModule.dll" \
  -r:"$MGMT/KoboldKare.ISavable.dll" > "$OUT/log" 2>&1
rc=$?

if [ $rc -ne 0 ]; then
  echo "::error::compile failed"
  cat "$OUT/log"
  exit 1
fi

# CONTRIBUTING requires a zero-warning build, so treat warnings as failures.
warns=$(grep -c 'warning CS' "$OUT/log" || true)
if [ "${warns:-0}" -ne 0 ]; then
  echo "::error::$warns compile warning(s) — the build must be warning-free"
  grep 'warning CS' "$OUT/log" | sort -u
  exit 1
fi

files=$(find src -maxdepth 1 -name '*.cs' | wc -l)
lines=$(cat src/*.cs | wc -l)
echo "OK: $files source files ($lines lines) compile with 0 errors, 0 warnings."
