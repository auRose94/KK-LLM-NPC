#!/usr/bin/env python3
"""Locate the KoboldKare Steam install across platforms.

Instead of trusting a hardcoded path, this reads Steam's own records:
`libraryfolders.vdf` lists every mounted library, and each library's
`appmanifest_*.acf` records the app's name, installdir, and appid. So the
game resolves even after it's moved to a new disk or library — which is the
whole point once the install has been migrated between machines.

Why it verifies on disk instead of trusting the vdf: Steam sometimes writes a
library path whose mount point differs from what's actually mounted (a case
mismatch, or a volume that isn't mounted right now). Every candidate is checked
against the filesystem before use; a listed-but-absent library is skipped, not
fatal. As a backstop, common Steam-library mount points are globbed too, so a
case-mismatched or vdf-unlisted-but-mounted library still resolves.

The returned game dir is confirmed by matching the appmanifest's name /
installdir / appid, then checking `<library>/steamapps/common/<installdir>`
actually exists.

Usage:
    python3 find_koboldkare.py                 # prints resolved path, or exits 1
    python3 find_koboldkare.py --appid 1102930 # override the appid to look for

Exit codes: 0 = resolved (path on stdout); 1 = nothing found (reason on stderr).
"""
import os
import re
import sys
import glob

DEFAULT_APPID = "1102930"
DEFAULT_INSTALLDIR = "KoboldKare"


def vdf_field(text, key):
    """Pull the first quoted value for `key` out of a VDF-ish blob.

    VDF (Valve Data Format) is brace-delimited `"key"\t"value"` pairs. We only
    need a handful of flat fields, so a targeted regex is enough and avoids a
    full parser. Matches the key at line start to dodge same-named keys nested
    deeper in the tree.
    """
    m = re.search(r'^\s*"%s"\s+"([^"]*)"' % re.escape(key), text, re.MULTILINE)
    return m.group(1) if m else None


def candidate_steam_roots():
    """Directories that might contain a `steamapps/` folder, per platform."""
    home = os.path.expanduser("~")
    roots = []
    if sys.platform.startswith("win"):
        roots += [
            os.path.join(os.environ.get("ProgramFiles(x86)") or r"C:\Program Files (x86)", "Steam"),
            os.path.join(os.environ.get("ProgramFiles") or r"C:\Program Files", "Steam"),
            os.path.join(os.environ.get("LOCALAPPDATA") or os.path.join(home, "AppData", "Local"), "Steam"),
            os.path.join(home, ".steam"),
        ]
    elif sys.platform == "darwin":
        roots += [os.path.join(home, "Library", "Application Support", "Steam")]
    else:  # linux / bsd
        xdg = os.environ.get("XDG_DATA_HOME")
        roots += [
            os.path.join(home, ".steam", "steam"),
            os.path.join(home, ".local", "share", "Steam"),
            os.path.join(xdg, "Steam") if xdg else None,
            os.path.join(home, ".steam"),
        ]
    return [r for r in roots if r]


def candidate_library_roots(steam_roots):
    """Every directory that might hold a `steamapps/appmanifest_*.acf`.

    Union of (a) library paths declared in each steam root's
    libraryfolders.vdf and (b) globbed common mount points. The glob is what
    makes this survive the vdf's case mismatch / stale entries.
    """
    libs = []
    seen = set()

    def add(path):
        if not path:
            return
        norm = os.path.normpath(path)
        if norm not in seen:
            seen.add(norm)
            libs.append(norm)

    for steam in steam_roots:
        add(steam)  # the steam root's own default library
        for vdf in (
            os.path.join(steam, "steamapps", "libraryfolders.vdf"),
            os.path.join(steam, "config", "libraryfolders.vdf"),
        ):
            try:
                with open(vdf, "r", encoding="utf-8", errors="ignore") as fh:
                    text = fh.read()
            except OSError:
                continue
            for path in re.findall(r'^\s*"path"\s+"([^"]*)"', text, re.MULTILINE):
                add(path)

    if sys.platform.startswith("win"):
        for pat in (r"C:\Program Files (x86)\Steam\steamapps", r"C:\Program Files\Steam\steamapps"):
            add(pat)
    else:
        # Glob the common Steam-library mount points so a mounted-but-unlisted
        # library (or one whose vdf path differs in case) still resolves.
        for pat in ("/mnt/*/SteamLibrary", "/mnt/*/steam", "/media/*/SteamLibrary"):
            for p in glob.glob(pat):
                add(p)
    return libs


def find_game(appid, installdir):
    """Return the game dir, or None. Tries every library, scoring matches."""
    steam_roots = candidate_steam_roots()
    best = None
    best_score = 99
    for lib in candidate_library_roots(steam_roots):
        sa = os.path.join(lib, "steamapps")
        if not os.path.isdir(sa):
            continue
        for acf in sorted(glob.glob(os.path.join(sa, "appmanifest_*.acf"))):
            try:
                with open(acf, "r", encoding="utf-8", errors="ignore") as fh:
                    text = fh.read()
            except OSError:
                continue
            a_name = vdf_field(text, "name")
            a_dir = vdf_field(text, "installdir")
            a_id = vdf_field(text, "appid")
            if not (a_name or a_dir or a_id):
                continue
            # Score: exact appid match beats name match beats installdir match.
            if a_id == appid:
                score = 0
            elif a_name == "KoboldKare":
                score = 1
            elif a_dir == DEFAULT_INSTALLDIR:
                score = 2
            else:
                continue
            game = os.path.join(sa, "common", a_dir or DEFAULT_INSTALLDIR)
            if os.path.isdir(game):
                if score < best_score:
                    best, best_score = game, score
    return best


def main(argv):
    appid = DEFAULT_APPID
    installdir = DEFAULT_INSTALLDIR
    if "--appid" in argv:
        try:
            appid = argv[argv.index("--appid") + 1]
        except IndexError:
            pass
    game = find_game(appid, installdir)
    if game:
        print(game)
        return 0
    sys.stderr.write(
        "KoboldKare not found. Ensure Steam is installed and the game is "
        "downloaded, or set KOBOLDKARE_DIR to its install path.\n"
    )
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
