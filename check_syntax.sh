#!/usr/bin/env bash
# Parse-check every C# source file without needing a KoboldKare install.
#
# build.sh compiles src/*.cs against BepInEx + UnityEngine + Photon +
# Assembly-CSharp, which are game-provided and not redistributable — so CI
# cannot do a real build. What CI CAN do is catch syntax errors, which is what
# this does.
#
# Method: hand every source file to mcs at once and inspect the diagnostics.
# With no references present, mcs reports a flood of "type not found" style
# errors — those are expected and ignored. Anything that indicates the PARSER
# failed (unbalanced braces, stray tokens, bad literals) is a real defect and
# fails this check.
#
# Caveat, stated plainly: because the reference types are missing, this cannot
# catch semantic errors (a renamed or missing member, a wrong argument type).
# It only guarantees the code parses. A real bug of exactly that kind shipped
# through this check once: renaming ContextManager.DynamicMaxThoughts left a
# caller dangling, and only ./build.sh caught it. Use check_build.sh (which
# needs a game install) for anything beyond syntax.
#
# Exits 0 if every file parses, 1 otherwise.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if ! command -v mcs >/dev/null 2>&1; then
  echo "ERROR: mcs not found. Install mono-complete." >&2
  exit 127
fi

OUT="${TMPDIR:-/tmp}/kkllmnpc-syntax.$$"
trap 'rm -f "$OUT"' EXIT

# shellcheck disable=SC2046
mcs -target:library -out:"$OUT.dll" src/*.cs > "$OUT" 2>&1

# Diagnostic codes that mean "the parser choked" (CS1xxx family) plus the
# ones that mean a declaration/structure problem. These are always real bugs.
SYNTAX_RE='error CS(1[0-9]{3}|8[0-9]{3}|0101|0102|0103|0116|1022|1031|1513|1514|1519|1525|1547|1585|1597|8076|8180)'

if grep -qE "$SYNTAX_RE" "$OUT"; then
  echo "::error::C# syntax errors found in src/:"
  grep -E "$SYNTAX_RE" "$OUT" | sort -u
  exit 1
fi

# Report what we ignored, so the check is auditable rather than a black box.
ignored=$(grep -oE 'error CS[0-9]{4}' "$OUT" | sort -u | tr '\n' ' ')
files=$(find src -maxdepth 1 -name '*.cs' | wc -l)
lines=$(cat src/*.cs | wc -l)

echo "OK: $files source files ($lines lines) parse cleanly."
if [ -n "$ignored" ]; then
  echo "Ignored missing-reference diagnostics (expected without game assemblies): $ignored"
fi
exit 0
