#!/usr/bin/env bash
# Run the pure-C# unit test suites. No Unity/BepInEx/game install required.
#
# Usage:
#   ./run_tests.sh              # run everything
#   ./run_tests.sh json path    # run only the named suites
#
# Exits 0 only if EVERY runnable suite passes. (Suites must not use
# "exit code = failure count": 256 failures wraps to 0 and reports success.)
#
# Adding a suite: add a line to the run_suite calls below. Suites must be
# self-contained pure C# (see tests/README.md).
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

OUT="${TMPDIR:-/tmp}/kkllmnpc-tests.$$"
mkdir -p "$OUT"
trap 'rm -rf "$OUT"' EXIT

if ! command -v mcs >/dev/null 2>&1; then
  echo "ERROR: mcs not found. Install mono-complete (Debian/Ubuntu: apt install mono-complete)." >&2
  exit 127
fi
if ! command -v mono >/dev/null 2>&1; then
  echo "ERROR: mono not found. Install mono-complete." >&2
  exit 127
fi

FAILED=0
PASSED=0
declare -a FAILED_SUITES=()

# run_suite <name> <test files...> -- <src files...>
run_suite() {
  local name="$1"; shift
  local tests=() srcs=()
  local seen_sep=0
  for arg in "$@"; do
    if [ "$arg" = "--" ]; then seen_sep=1; continue; fi
    if [ $seen_sep -eq 0 ]; then tests+=("$arg"); else srcs+=("$arg"); fi
  done

  printf '\n=== %s ===\n' "$name"

  # -warnaserror is deliberately OFF: legacy suites have unused-variable
  # warnings. Compile warnings are a separate concern from test failures.
  if ! mcs -target:exe -out:"$OUT/$name.exe" "${tests[@]}" "${srcs[@]}" 2>"$OUT/$name.build"; then
    echo "  BUILD FAILED"
    sed 's/^/    /' "$OUT/$name.build"
    FAILED=$((FAILED + 1))
    FAILED_SUITES+=("$name (build failed)")
    return
  fi
  # Surface compile warnings without failing the run.
  if [ -s "$OUT/$name.build" ]; then
    local warns
    warns=$(grep -c 'warning CS' "$OUT/$name.build" 2>/dev/null || echo 0)
    if [ "$warns" != "0" ]; then
      echo "  (${warns} compile warning(s) — see run_tests.sh output)"
      grep 'warning CS' "$OUT/$name.build" | sed 's/^/    /'
    fi
  fi

  mono "$OUT/$name.exe"
  local rc=$?
  if [ $rc -ne 0 ]; then
    echo "  => FAILED (exit $rc)"
    FAILED=$((FAILED + 1))
    FAILED_SUITES+=("$name")
  else
    PASSED=$((PASSED + 1))
  fi
}

# Suites that need no game assemblies — these are what CI runs.
run_headless_suites() {
  run_suite json        tests/test_json.cs tests/test_json_standalone.cs -- src/Json.cs
  run_suite pathcore    tests/test_pathcore.cs -- src/PathCore.cs
  run_suite goalresolver tests/test_goalresolver.cs -- src/PathCore.cs
  run_suite contextcore tests/test_contextcore.cs -- src/ContextCore.cs src/Json.cs src/Constants.cs
  run_suite chat        tests/test_chat_similarity.cs -- src/ChatSimilarity.cs
  run_suite console     tests/test_console_shell.cs -- src/ConsoleShell.cs
  run_suite safehttp    tests/test_safehttp.cs -- src/SafeHttp.cs
}

# Every *.cs in tests/ must be reachable from a run_suite line above, otherwise a
# new test file would silently never run. Suites needing game assemblies go in
# the skip list below instead.
audit_coverage() {
  local declared
  declared=$(grep -oE 'tests/test_[a-z_]+\.cs' "$0" | sort -u)
  local orphan
  orphan=$(for f in tests/test_*.cs; do
             echo "$declared" | grep -q "$f" || echo "$f"
           done)
  if [ -n "$orphan" ]; then
    echo "  WARN: test file(s) not wired into run_tests.sh:"
    echo "$orphan" | sed 's/^/    /'
  fi
}

printf 'KK-LLM-NPC unit tests (pure C#)\n'
run_headless_suites
audit_coverage

printf '\n========================================\n'
if [ $FAILED -eq 0 ]; then
  printf 'ALL SUITES PASSED (%d suite(s))\n' "$PASSED"
  exit 0
fi
printf '%d suite(s) FAILED: %s\n' "$FAILED" "${FAILED_SUITES[*]}"
exit 1
