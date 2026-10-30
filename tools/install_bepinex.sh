#!/usr/bin/env bash
# Fetch and install the right BepInEx 5.x build into a KoboldKare install.
#
# Why the build must match the GAME, not the host: BepInEx 5.x loads by
# injecting its doorstop loader into the game's native UnityPlayer. A game that
# ships UnityPlayer.dll is a WINDOWS game running under Proton — it needs the
# Windows BepInEx build (Proton's wine supplies the .NET runtime); a native
# Linux build would look for UnityPlayer.so and never find it. So we inspect the
# game binary to decide, not the OS we happen to run on.
#
# Why 5.4.22 and NOT 5.4.23.6 (the latest): 5.4.23.x upgraded the bundled
# UnityDoorstop to 4.6.0, which calls Module.GetPEKind(PortableExecutableKinds&,
# ImageFileMachine&) — an API that only exists in .NET Core/.NET 5+, NOT the
# .NET Framework runtime Unity Mono uses. So doorstop 4.6.0 throws a
# MissingMethodException and never boots on a Unity Mono game.
# Why 5.4.22 and not 5.4.21: on this machine 5.4.21's plugin GameObject gets
# disabled during the game's FIRST scene transition (update-probe never fires,
# OnDisable right after "Chainloader startup complete") — the mod goes silent
# forever. 5.4.22.0 is the version empirically proven to work with KoboldKare
# (the user's tests + the third-party mod's bundle). Both ship the old doorstop.
PINNED_TAG="v5.4.22"
PINNED_VERSION="5.4.22.0"
#
# Install is idempotent: if BepInEx/core/BepInEx.dll is already present it
# exits 0 unless --force. Pass --force to re-download the latest.
#
# Usage:
#   tools/install_bepinex.sh            # install if missing
#   tools/install_bepinex.sh --force    # re-download latest even if present
#   KOBOLDKARE_DIR=/path tools/install_bepinex.sh   # override game dir
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1

# --- resolve game dir (reuse the Steam locator) ------------------------------
if [ -n "${KOBOLDKARE_DIR:-}" ]; then
    KDIR="$KOBOLDKARE_DIR"
elif [ -x "$SCRIPT_DIR/find_koboldkare.py" ] && KDIR="$(python3 "$SCRIPT_DIR/find_koboldkare.py" 2>/dev/null)" && [ -n "$KDIR" ]; then
    :
else
    echo "ERROR: KoboldKare not found. Set KOBOLDKARE_DIR or install Steam." >&2
    exit 1
fi
if [ ! -d "$KDIR/KoboldKare_Data" ]; then
    echo "ERROR: $KDIR is not a KoboldKare install (no KoboldKare_Data)." >&2
    exit 1
fi

# --- detect the game's native platform from its binary -----------------------
# UnityPlayer.dll  -> Windows (run under Proton) -> win build
# UnityPlayer.so   -> native Linux              -> linux build
# KoboldKare.app / MacOS/ -> macOS              -> macos universal build
if   [ -f "$KDIR/UnityPlayer.dll" ]; then VARIANT="win_x64";   echo "Game: Windows (Proton) -> BepInEx_win_x64"
elif [ -f "$KDIR/UnityPlayer.so"  ]; then VARIANT="linux_x64"; echo "Game: native Linux     -> BepInEx_linux_x64"
elif [ -d "$KDIR/KoboldKare.app" ] || [ -d "$KDIR/MacOS" ]; then VARIANT="macos_universal"; echo "Game: macOS -> BepInEx_macos_universal"
else
    echo "ERROR: couldn't detect game platform (no UnityPlayer.dll/.so, no .app)." >&2
    exit 1
fi

# --- already installed? ------------------------------------------------------
if [ -f "$KDIR/BepInEx/core/BepInEx.dll" ] && [ "$FORCE" -eq 0 ]; then
    echo "BepInEx already installed at $KDIR/BepInEx (use --force to update)."
    exit 0
fi

# --- download the PINNED 5.4.21 release, mapping the game variant to its asset ---
# 5.4.21's asset names differ from 5.4.23.6's (win→x64, linux→unix, macos→x86),
# so we build the name directly rather than query for the latest.
echo "Installing BepInEx $PINNED_VERSION (doorstop-fixed release)..."
case "$VARIANT" in
  win_x64)     ASSET="$PINNED_TAG/BepInEx_x64_${PINNED_VERSION}.zip" ;;
  linux_x64)   ASSET="$PINNED_TAG/BepInEx_unix_${PINNED_VERSION}.zip" ;;
  macos_universal) ASSET="$PINNED_TAG/BepInEx_x86_${PINNED_VERSION}.zip" ;;
  *) echo "ERROR: unknown variant $VARIANT" >&2; exit 1 ;;
esac
URL="https://github.com/BepInEx/BepInEx/releases/download/${ASSET}"

# --- download + extract into the game root -----------------------------------
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
echo "Downloading $URL"
if ! curl -sL -o "$TMP/bepinex.zip" "$URL"; then
    echo "ERROR: download failed." >&2; exit 1
fi
if ! unzip -q "$TMP/bepinex.zip" -d "$TMP/x" 2>/dev/null; then
    echo "ERROR: zip is not a valid BepInEx archive." >&2; exit 1
fi

SRC="$TMP/x/BepInEx"
if [ ! -d "$SRC/core" ] || [ ! -f "$SRC/core/BepInEx.dll" ]; then
    echo "ERROR: downloaded archive has no BepInEx/core/BepInEx.dll (unexpected layout)." >&2
    ls "$TMP/x" | head >&2; exit 1
fi

echo "Installing BepInEx $PINNED_VERSION -> $KDIR/BepInEx"
# Preserve user state across the swap: config/ (mod settings + kkllmnpc_memory/
# NPC memories) and the ENTIRE plugins/ dir (the mod's own KKLLMNPC.dll + prompt
# files AND any foreign mod DLLs — the fresh BepInEx archive ships none of this,
# so wholesale restore is correct). Skipping the mod's own payload here once
# deleted a just-deployed KKLLMNPC.dll and left "0 plugins to load".
STAGE="$(mktemp -d)"
if [ -d "$KDIR/BepInEx/config" ]; then
    cp -a "$KDIR/BepInEx/config" "$STAGE/config"
    echo "  preserved BepInEx/config ($(find "$STAGE/config" -type f | wc -l) files)"
fi
if [ -d "$KDIR/BepInEx/plugins" ]; then
    cp -a "$KDIR/BepInEx/plugins" "$STAGE/plugins"
    echo "  preserved BepInEx/plugins ($(find "$STAGE/plugins" -type f | wc -l) files)"
fi
rm -rf "$KDIR/BepInEx"
cp -a "$SRC" "$KDIR/BepInEx"
[ -d "$STAGE/config" ] && cp -a "$STAGE/config" "$KDIR/BepInEx/config"
[ -d "$STAGE/plugins" ] && cp -a "$STAGE/plugins" "$KDIR/BepInEx/plugins"
rm -rf "$STAGE"

# Make the launcher executable (no-op on Windows/Proton, harmless on native).
[ -f "$KDIR/BepInEx/run_bepinex.sh" ] && chmod +x "$KDIR/BepInEx/run_bepinex.sh"

# create the plugins dir build.sh deploys into (the game would create it on
# first launch; make it explicit so the build can deploy without a launch).
mkdir -p "$KDIR/BepInEx/plugins"

echo "Done. BepInEx $PINNED_VERSION installed."
echo "Next: ./build.sh  (compiles KKLLMNPC.dll and deploys it to BepInEx/plugins/)"
