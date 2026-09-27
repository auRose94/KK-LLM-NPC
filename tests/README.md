# KK-LLM-NPC Tests

Unit tests for the KK-LLM-NPC plugin. These files compile with **pure C#** (no Unity/BepInEx
dependencies) and run via `mono`.

## Running

```bash
./run_tests.sh
```

That's the whole thing — it compiles and runs every suite, prints a per-suite summary, and exits
non-zero if any suite fails. Use it locally and in CI.

Requires `mono-complete` (`apt install mono-complete` / `brew install mono`).

`run_tests.sh` also audits `tests/` for any file not wired into a `run_suite` line, so a new test
file can't silently never run.

## Suites

| Suite | File | Tests | What it covers |
|-------|------|-------|----------------|
| `json` | `test_json.cs` + `test_json_standalone.cs` | 22 | Json parser/writer (null, bool, int, float, string, object, array, nested, truncated, whitespace, round-trip) |
| `pathcore` | `test_pathcore.cs` | 33 | A* solver, PathPolicy.ShouldReplan (14 cases), adaptive cell sizing, auto floor detection |
| `goalresolver` | `test_goalresolver.cs` | 30 | Goal snapping: fast path, expanding-ring search, MaxRadius cutoff, height tiebreak, tie-break order, bounds/edge cases |
| `contextcore` | `test_contextcore.cs` | 109 | Compaction escalation ladder, de-escalation, streak reset, per-budget dynamic limits + floors, token estimation, fact merging, history summarization |
| `chat` | `test_chat_similarity.cs` | 19 | Jaccard similarity, Levenshtein ratio, fuzzy matching (6+ significant words) |
| `console` | `test_console_shell.cs` | 90 | Console REPL parser: canonical/alias/fuzzy resolution, prose guard, prompt prefixes, headers, payload + quoted args, legacy call-style args, reply splitting (fences/bullets/mixed), help text, module `RegisterExtra` |
| `safehttp` | `test_safehttp.cs` | 23 | Retry/backoff against a real loopback `HttpListener`: transient 5xx actually re-hits the server, permanent 4xx fails fast, error bodies surfaced, response size caps |

**326 tests, all runnable headless, all in CI.**

To run one suite by hand:

```bash
mcs -target:exe -out:/tmp/t.exe tests/test_json.cs tests/test_json_standalone.cs src/Json.cs && mono /tmp/t.exe
mcs -target:exe -out:/tmp/t_pf.exe tests/test_pathcore.cs src/PathCore.cs && mono /tmp/t_pf.exe
mcs -target:exe -out:/tmp/t_gr.exe tests/test_goalresolver.cs src/PathCore.cs && mono /tmp/t_gr.exe
mcs -target:exe -out:/tmp/t_cm.exe tests/test_contextcore.cs src/ContextCore.cs src/Json.cs src/Constants.cs && mono /tmp/t_cm.exe
mcs -target:exe -out:/tmp/t_chat.exe tests/test_chat_similarity.cs src/ChatSimilarity.cs && mono /tmp/t_chat.exe
mcs -target:exe -out:/tmp/t_cs.exe tests/test_console_shell.cs src/ConsoleShell.cs && mono /tmp/t_cs.exe
mcs -target:exe -out:/tmp/t_http.exe tests/test_safehttp.cs src/SafeHttp.cs && mono /tmp/t_http.exe
```

## Design

- Every suite is pure C# — no `UnityEngine`, no `BepInEx`, no external dependencies.
- Test files define a `Main()` entry point that runs all test cases.
- `mono` is the only runtime requirement.
- **Exit code 0 = pass, non-zero = fail.** Do not use "exit code = number of failures" —
  256 failures wraps to 0 and the suite reports success.

## A test must call the code it claims to test

Three files were deleted during a 2026-09 audit because they asserted against logic re-implemented
in the test body rather than against anything in `src/`. They were worse than no coverage: they
looked like tests, and one of them exited 0 while reporting failures.

- `test_horniness.cs` — reimplemented the horniness arithmetic in local variables.
- `test_contextmanager.cs` — reimplemented the compaction ladder ("simulate escalation without a
  real NPCInstance").
- `test_pathfinding.cs` — every method body was a `// Placeholder:` comment with no assertions and
  no `Main()`.

If the logic you want to test is coupled to Unity, extract the pure part into its own
`src/<Name>Core.cs` and test *that* — this is the established pattern (`PathCore`, `ContextCore`).

## Adding a suite

1. Write `tests/test_<name>.cs` that compiles against your `src/<Name>.cs` alone, and calls the
   real code.
2. Add a `run_suite <name> tests/test_<name>.cs -- src/<Name>.cs` line to `run_tests.sh`.
3. Update the table above. `run_tests.sh` will warn if you skip step 2.

### Existing pure cores to test against

| Core | Source | Covers |
|------|--------|--------|
| `PathCore` | `src/PathCore.cs` | `PathPolicy`, `AstarSolver`, `AdaptiveCell`, `AutoFloor`, `GoalResolver` |
| `ContextCore` | `src/ContextCore.cs` | `ContextPolicy`, `ContextMath`, `ContextCompaction` |
| `ChatSimilarity` | `src/ChatSimilarity.cs` | Jaccard + Levenshtein |
| `ConsoleShell` | `src/ConsoleShell.cs` | REPL command parsing |
| `Json` | `src/Json.cs` | JSON parse/write |
| `SafeHttp` | `src/SafeHttp.cs` | HTTP retry/backoff |
