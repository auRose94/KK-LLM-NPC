# KK-LLM-NPC Tests

Unit tests for the KK-LLM-NPC plugin. These files compile with **pure C#** (no Unity/BepInEx
dependencies) and run via `mono`.

## Files

| File | Tests | What it covers |
|------|-------|----------------|
| `test_json.cs` + `test_json_standalone.cs` | 22 | Json parser/writer (null, bool, int, float, string, object, array, nested, truncated, whitespace, round-trip) |
| `test_pathcore.cs` | 33 | A* solver, PathPolicy.ShouldReplan (14 cases), adaptive cell sizing, auto floor detection |
| `test_chat_similarity.cs` | 19 | Jaccard similarity, Levenshtein ratio, fuzzy matching (6+ significant words) |

## Running

### All tests (from repo root)

```bash
# JSON parser/writer (22 tests)
mcs -target:exe -out:/tmp/t.exe tests/test_json.cs tests/test_json_standalone.cs src/Json.cs && mono /tmp/t.exe

# PathCore (33 tests)
mcs -target:exe -out:/tmp/t_pf.exe tests/test_pathcore.cs src/PathCore.cs && mono /tmp/t_pf.exe

# Chat similarity (19 tests)
mcs -target:exe -out:/tmp/t_chat.exe tests/test_chat_similarity.cs src/ChatSimilarity.cs && mono /tmp/t_chat.exe
```

All tests print `PASS` per case and a summary line (`N passed, 0 failed`).
Exit code = number of failed tests (0 = all pass).

## Design

- Pure C# — no `UnityEngine`, no BepInEx, no external dependencies.
- Test files define a `Main()` entry point that runs all test cases.
- `mono` is the only runtime requirement.