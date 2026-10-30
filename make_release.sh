#!/usr/bin/env bash
# make_release.sh — package release zips + release notes for a GitHub release.
#
# The plugin is a plain BepInEx-5 Mono IL plugin: the DLL + prompt files are the
# SAME artifact on Windows/Linux/macOS — only the install context differs (which
# BepInEx build the host game needs, and the game-root path). Each platform zip
# therefore carries the identical payload plus an INSTALL-<platform>.md, laid out
# relative to the GAME ROOT so "unzip over your KoboldKare folder" just works.
#
# Usage:
#   ./make_release.sh             # builds first (./build.sh), then packages
#   ./make_release.sh --no-build  # package the existing KKLLMNPC.dll
#
# Output: dist/KKLLMNPC-<version>_win.zip / _linux.zip / _macos.zip
#         dist/RELEASE_NOTES.md   (paste with `gh release create --notes-file`)
#
# NOTE: releases must be built on a machine with the game installed (the build
# links the game's own Managed assemblies, which cannot legally ride in CI or the
# repo). A GitHub Actions workflow cannot produce these zips.
set -euo pipefail

# BUNDLE_BEPINEX=0 / --no-bepinex: package WITHOUT downloading the BepInEx core
# (for offline builds). Default: bundle the pinned BepInEx 5.4.22.0 into each zip
# so the release is a complete drop-in.
BUNDLE_BEPINEX="${BUNDLE_BEPINEX:-1}"
for _a in "$@"; do [ "$_a" = "--no-bepinex" ] && BUNDLE_BEPINEX=0; done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

VERSION="$(python3 - <<'PY'
import re
s = open("src/Main.cs").read()
m = re.search(r'BepInPlugin\("([^"]+)",\s*"([^"]+)",\s*"([^"]+)"\)', s)
print(m.group(3) if m else "0.0.0")
PY
)"
echo "release version: $VERSION"

# ---------------------------------------------------------------------------
# BepInEx bundling — pin the SAME version + asset mapping as tools/install_bepinex.sh
# (5.4.22.0 is empirically the doorstop-fixed release that boots KoboldKare; the
# asset names are the 5.4.22.0 naming: win→x64, linux→unix, macos→x86). We fetch
# it once into a cache and MERGE it into each per-platform stage tree, so the
# release zip is a complete drop-in: unzip over the game folder and it just works.
# The merge (cp -a .../BepInEx/. ) preserves the plugins/ we already staged.
# ---------------------------------------------------------------------------
BEPINEX_TAG="v5.4.22"
BEPINEX_VERSION="5.4.22.0"
BEPINEX_CACHE="$SCRIPT_DIR/.bepinex-cache"

# bepinex_asset <variant> -> the release asset path for that platform's game build.
bepinex_asset() {
  case "$1" in
    win)    echo "$BEPINEX_TAG/BepInEx_x64_${BEPINEX_VERSION}.zip" ;;
    linux)  echo "$BEPINEX_TAG/BepInEx_unix_${BEPINEX_VERSION}.zip" ;;
    macos)  echo "$BEPINEX_TAG/BepInEx_x86_${BEPINEX_VERSION}.zip" ;;
    *) echo "ERROR: unknown platform $1" >&2; exit 1 ;;
  esac
}

# fetch_bepinex <variant> <stage> — download + extract the pinned BepInEx into
# <stage>/BepInEx (merging the core/managed/doorstop around the staged plugins/).
fetch_bepinex() {
  local variant="$1" stage="$2"
  local asset; asset="$(bepinex_asset "$variant")"
  local url="https://github.com/BepInEx/BepInEx/releases/download/${asset}"
  if [ ! -f "$BEPINEX_CACHE/BepInEx/core/BepInEx.dll" ]; then
    echo "Fetching BepInEx ${BEPINEX_VERSION} (${asset})..."
    mkdir -p "$BEPINEX_CACHE"
    local tmp; tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' RETURN
    if ! curl -sL -o "$tmp/bepinex.zip" "$url"; then
      echo "ERROR: BepInEx download failed from $url" >&2; exit 1
    fi
    if ! unzip -q "$tmp/bepinex.zip" -d "$tmp/x" 2>/dev/null; then
      echo "ERROR: BepInEx archive is not a valid zip." >&2; exit 1
    fi
    if [ ! -d "$tmp/x/BepInEx/core" ] || [ ! -f "$tmp/x/BepInEx/core/BepInEx.dll" ]; then
      echo "ERROR: downloaded archive has no BepInEx/core/BepInEx.dll (unexpected layout)." >&2; exit 1
    fi
    rm -rf "$BEPINEX_CACHE"
    mv "$tmp/x/BepInEx" "$BEPINEX_CACHE"
    trap - RETURN
    echo "  cached at $BEPINEX_CACHE"
  fi
  # Merge the cached BepInEx core/managed/doorstop into the stage tree, keeping the
  # plugins/ we already staged. cp -a .../. copies the contents (incl. hidden files).
  cp -a "$BEPINEX_CACHE/." "$stage/BepInEx/"
  echo "  bundled BepInEx ${BEPINEX_VERSION} into $stage/BepInEx/"
}

if [ "${1:-}" != "--no-build" ]; then
  ./build.sh
fi

for f in KKLLMNPC.dll system_prompt_default.txt system_prompt_console.txt system_prompt_vision.txt system_prompt_name.txt; do
  if [ ! -f "$f" ]; then echo "ERROR: missing $f (run build.sh first)" >&2; exit 1; fi
done

rm -rf dist
mkdir -p dist

# Install notes per platform. The BepInEx step is the platform-specific part
# (BepInEx 5 ships separate builds for Windows/Linux/macOS Mono).
notes_platform() {
  case "$1" in
    win)
      cat <<'MD'
# INSTALL — Windows (tested)

BepInEx 5.4.22 is **included** in this archive — no separate download needed.

1. Unzip this archive over your `KoboldKare` folder (it drops `BepInEx/` — core,
   managed, doorstop and `plugins/` — into the game root).
2. Launch the game. The mod's config appears at `BepInEx/config/com.kk.llmnpc.cfg`
   after the first run; the debug console opens at http://127.0.0.1:8338/.

Requires an OpenAI-compatible chat server (local LM Studio / llama.cpp /
KoboldCpp, a second GPU machine, or a hosted endpoint) — see the README's
Requirements section.
MD
      ;;
    linux)
      cat <<'MD'
# INSTALL — Linux (untested end-to-end)

The game's Linux branch ships under a separate depot; this zip's payload is the
same Mono IL plugin as the Windows one, and BepInEx 5.4.22 (unix build) is
**included** — no separate download needed.

1. Unzip this archive over your `KoboldKare` folder (it drops `BepInEx/` into the
   game root).
2. Launch the game. Config at `BepInEx/config/com.kk.llmnpc.cfg`; debug console
   at http://127.0.0.1:8338/.

KNOWN UNTESTED: the game's Linux branch has shader/texture artefacts on custom
maps (a game-side issue, not this mod's). If BepInEx's unix build differs in
patching from the Windows one, report what the log says (BepInEx/LogOutput.log).
MD
      ;;
    macos)
      cat <<'MD'
# INSTALL — macOS (untested — no Apple hardware in the lab)

The payload is the same Mono IL plugin on every OS, and BepInEx 5.4.22 is
**included** — no separate download needed.

1. Unzip this archive over your `KoboldKare.app`'s game root (Contents/ side of
   the app bundle: the folder that contains `KoboldKare_Data`).
2. Launch the game. Config at `BepInEx/config/com.kk.llmnpc.cfg`; debug console
   at http://127.0.0.1:8338/.

If the macOS Mono player refuses to load a Unity-built mixed assembly, tell us
in an issue with the LogOutput.log first lines.
MD
      ;;
  esac
}

stage_and_zip() {
  local plat="$1"
  local stage="dist/stage_$plat"
  mkdir -p "$stage/BepInEx/plugins/prompt_extras"
  cp KKLLMNPC.dll "$stage/BepInEx/plugins/"
  for f in system_prompt_default.txt system_prompt_console.txt system_prompt_vision.txt system_prompt_name.txt; do
    cp "$f" "$stage/BepInEx/plugins/"
  done
  if [ -d prompt_extras ]; then
    for f in prompt_extras/*.txt; do
      [ -f "$f" ] && cp "$f" "$stage/BepInEx/plugins/prompt_extras/"
    done
  fi
  # Bundle the pinned BepInEx core (unless --no-bepinex). Merges around plugins/.
  if [ "${BUNDLE_BEPINEX}" = "1" ]; then fetch_bepinex "$plat" "$stage"; fi
  notes_platform "$plat" > "$stage/INSTALL.md"
  [ -f README.md ] && cp README.md "$stage/"
  mkdir -p "$stage/docs"
  [ -f docs/CONFIG.md ] && cp docs/CONFIG.md "$stage/docs/"
  [ -f docs/VORE.md ] && cp docs/VORE.md "$stage/docs/"
  (cd "$stage" && zip -qr "$SCRIPT_DIR/dist/KKLLMNPC-${VERSION}_${plat}.zip" .)
  rm -rf "$stage"
}

stage_and_zip win
stage_and_zip linux
stage_and_zip macos

# Release notes: the newest CHANGELOG section (Unreleased if present, else the
# topmost version), capped — 135KB of history does not belong on a release page;
# the full log ships in the repo and in the zips.
python3 - "$VERSION" <<'PY'
import sys
version = sys.argv[1]
s = open("CHANGELOG.md").read()
lines = s.split("\n")
# First section with actual content (an empty [Unreleased] is skipped).
sections = []
i = 0
while i < len(lines):
    if lines[i].startswith("## "):
        j = i + 1
        while j < len(lines) and not lines[j].startswith("## "):
            j += 1
        sections.append((i, j))
        i = j
    else:
        i += 1
start = None
for (a, b) in sections:
    body = "\n".join(lines[a + 1:b]).strip()
    if body:
        start = a
        end = b
        break
if start is None:
    raise SystemExit("no non-empty CHANGELOG section found")
first_version = lines[start]  # keeps its own "## ..." heading in the notes
block = "\n".join(lines[start:end] if end else lines[start:]).strip()
CAP = 24 * 1024
if len(block) > CAP:
    block = block[:CAP] + "\n\n…(trimmed — the complete changelog is in the repo/zip)"
head = """# KKLLMNPC %s

One BepInEx 5 Mono plugin, packaged per platform so the release page lists them.
The **payload inside every zip is identical** (the mod is OS-independent IL); the
platforms differ only in which BepInEx 5 build your KoboldKare host needs — each
zip carries an INSTALL-<platform>.md (plus the full README + CONFIG/VORE docs).

- **Windows — tested** (the lab plays the Windows build under Proton; installs
  into the game root the same way).
- **Linux — untested end-to-end.** Same Mono payload; the game's Linux branch has
  known shader/texture artefacts on custom maps (game-side, pre-existing).
- **macOS — untested** (no Apple hardware in the lab).

Each zip is a complete drop-in: **BepInEx 5.4.22 is pre-included** — just unzip
over your KoboldKare folder and launch. It also requires an OpenAI-compatible chat
server reachable from the game machine (local LM Studio / llama.cpp / a hosted
endpoint) — see the README's Requirements section.

---

""" % version
open("dist/RELEASE_NOTES.md", "w").write(head + block + "\n")
PY

