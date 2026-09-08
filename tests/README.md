# KK-LLM-NPC Tests

Unit tests for the KK-LLM-NPC plugin. These files document the expected behavior
of core algorithms and can be compiled with any C# test framework.

## Files

- `test_json.cs` — Tests for `Json.cs` (parser + writer); `JsonTests.RunAll()` runs them
- `test_json_standalone.cs` — entry-point shim (`Main`) that runs `JsonTests.RunAll()`;
  keeps the standalone build dependency-free
- `test_pathfinding.cs` — Tests for `Pathfinding.cs` (A*, grid sampling)
- `test_contextmanager.cs` — Tests for `ContextManager.cs` (compaction logic)

## Running

### Option 1: Standalone Json tests (no dependencies, runs the real `src/Json.cs`)

```bash
# from the repo root
mcs -target:exe -out:test_runner.exe tests/test_json.cs tests/test_json_standalone.cs src/Json.cs
mono test_runner.exe
# exit code = number of failed tests (0 = all pass)
```

### Option 2: NUnit (for full tests with Unity refs)

```bash
mcs -target:library -out:KKLLMNPC.dll src/*.cs \
  -r:"/path/to/BepInEx/core/BepInEx.dll" \
  -r:"/path/to/BepInEx/core/0Harmony.dll" \
  -r:"/path/to/KoboldKare/KoboldKare_Data/Managed/UnityEngine.dll" \
  -r:"/path/to/KoboldKare/KoboldKare_Data/Managed/Assembly-CSharp.dll" \
  -r:"nunit.framework.dll"

nunit3-console tests/test_*.cs
```

### Option 3: xUnit

```bash
dotnet test tests/
```

### Option 4: Runtime integration

The plugin's LLM loop already exercises all code paths at runtime.
Monitor the BepInEx log for `Json.Parse error at line X, col Y` messages
to catch parser failures in the wild.

## Test Coverage

| Module | Parser Tests | Writer Tests | Integration |
|--------|-------------|-------------|-------------|
| `Json.cs` | ✅ Runs standalone (Option 1) | ✅ Runs standalone (Option 1) | ✅ Runtime |
| `Pathfinding.cs` | ⚠️ Skeleton (requires NPCInstance) | — | ✅ Runtime |
| `ContextManager.cs` | ✅ Logic verified | — | ✅ Runtime |

## Note

These tests are **algorithmic** — they test the pure logic without Unity.
Full integration testing requires a running KoboldKare instance with BepInEx.

## Adding Tests

To add new tests:

1. Create a new `test_*.cs` file in this directory
2. Use `Assert(condition, "test name")` for inline assertions
3. If the test requires Unity types, document that it needs the full build
4. If the test is standalone (no deps), add it to the "Standalone" section above