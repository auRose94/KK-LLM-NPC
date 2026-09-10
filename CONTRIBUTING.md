# Contributing to KK-LLM-NPC

Thank you for your interest in improving LLM-driven NPCs for KoboldKare!

## Quick Start

1. **Install dependencies**: BepInEx 5.4x, a local OpenAI-compatible server (LM Studio, Ollama, or llama.cpp)
2. **Clone and build**: `./build.sh` (sets `KOBOLDKARE_DIR` env var to your game path)
3. **Install**: Copy `KKLLMNPC.dll` to `<KoboldKare>/BepInEx/plugins/`
4. **Configure**: Edit `BepInEx/config/com.kk.llmnpc.cfg`

## Project Structure

```
src/
  Main.cs            — Plugin entry, config binding, instance lifecycle
  NPCInstance.cs     — Per-NPC state (partial class, 26 source files)
  Llm.cs             — LLM decision loop, tool extraction, plan chaining
  Body.cs            — Possession, teardown, camera, reagent/penetration listeners
  Senses.cs          — Perception: raycasts, clearance, radar, spatial layout
  Movement.cs        — Physics-frame movement: walk validation, obstacle steering
  Tools.cs           — Tool implementations (walk, go_to, interact, say, etc.)
  Chat.cs            — Photon chat, speech bubbles, chat log processing
  Pathfinding.cs     — 3D layered A* on walkability grid + background worker
  WorldMap.cs        — Full-scene cached walkability map (shared by all agents)
  PathCore.cs        — Pure A* solver, path policy, adaptive cell sizing
  Vision.cs          — Background vision caption pass
  BodyControl.cs     — thrust/erection/mount/unmount/orgasm tools + swap awareness
  Farming.cs         — plant/water/harvest/plant_egg tools + farm perception
  Cooking.cs         — feed_blender/grind tools + cooking perception
  Identity.cs        — identity block, rename tool, mailbox/ATM perception
  ChatSimilarity.cs  — Jaccard + Levenshtein similarity (pure C#, testable)
  ModuleRegistry.cs  — Reflection-based module discovery (tools, physics, perception)
  IdentityBot.cs     — Second Photon client for NPC room identity
  GoalMachine.cs     — Persistent goal machine (set/complete/drop)
  ContextManager.cs  — Dynamic context compaction
  ModelProbe.cs      — Server capability detection
  Json.cs            — Hand-rolled JSON parser/writer (no external deps)
  SafeHttp.cs        — Retry wrapper with exponential backoff
  Constants.cs       — Named constants for magic numbers
  Patches.cs         — Harmony patches for animator rotation conflicts
  Overlay.cs         — Debug overlay GUI
tests/
  test_json.cs       — Json parser/writer tests
  test_json_standalone.cs — Entry-point shim (runs JsonTests.RunAll())
  test_pathcore.cs   — A* solver, path policy, adaptive cell, auto floors
  test_chat_similarity.cs — Jaccard, Levenshtein, fuzzy matching
  README.md          — Test instructions
prompt_extras/
  body_control.txt   — Body control tool descriptions + consent rules
  chat.txt           — Chat freshness guidance
  farmcook.txt       — Farming/cooking cycle guidance
  identity.txt       — Identity block + consent rules
docs/
  ARCHITECTURE.md    — Architecture overview
  CONFIG.md          — Complete config reference
CHANGELOG.md         — Release history
wip/                 — Work-in-progress notes (reverse-engineered game API)
```

## Making Changes

### Code Style

- Follow the existing style: `try { ... } catch (Exception) { }` for silent catches,
  `try { ... } catch (Exception e) { Logger.LogWarning("...: " + e.Message); }` for logged catches
- Use `F()` for float-to-string formatting in LLM-facing output
- Keep partial class files focused: each file handles one concern
- Add comments explaining *why*, not *what* (the code shows what)
- **Zero warnings** — the build should end with 0 warnings. Remove dead code, unused locals, and unassigned fields.

### Adding a New Module

1. Create `src/<Name>.cs` as a partial class on `NPCInstance`
2. Register via `ModuleRegistry` in a `<Name>Module.Register()` method:
   - `ModuleRegistry.Tool(name, handler, schemas)` for tool calls
   - `ModuleRegistry.PhysicsTick((n, dt) => {...})` for physics-frame hooks
   - `ModuleRegistry.Perception((n, dict) => { dict["key"] = ...; })` for perception
3. Create `prompt_extras/<name>.txt` for system prompt guidance
4. Add config entries in `Main.cs` under a dedicated section
5. Write tests in `tests/` if there's pure logic to test

### Testing

- Algorithmic tests live in `tests/` — they test pure logic without Unity
- Run all tests:
  ```bash
  mcs -target:exe -out:/tmp/t.exe tests/test_json.cs tests/test_json_standalone.cs src/Json.cs && mono /tmp/t.exe
  mcs -target:exe -out:/tmp/t_pf.exe tests/test_pathcore.cs src/PathCore.cs && mono /tmp/t_pf.exe
  mcs -target:exe -out:/tmp/t_chat.exe tests/test_chat_similarity.cs src/ChatSimilarity.cs && mono /tmp/t_chat.exe
  ```
- All tests print `PASS` per case and a summary line (`N passed, 0 failed`)
- Full integration requires a running KoboldKare instance with BepInEx

### Debugging

- Check `BepInEx/plugins/LogOutput.log` for plugin logs
- Enable vision frame dumps in config to see what the NPC "sees"
- The `model_error` field in perception contains LLM formatting feedback

## Pull Request Guidelines

1. **Describe the change** — what problem it solves
2. **Test locally** — verify in-game behavior
3. **Update CHANGELOG.md** — add entries under `[Unreleased]`
4. **No breaking config changes** — new configs should be opt-in with sensible defaults
5. **Keep dependencies minimal** — no new external libraries
6. **Zero warnings** — the build must end with 0 warnings

## Architecture Notes

- `NPCInstance` is a partial class split across 26 files — each handles one subsystem
- The LLM loop runs on a background thread; Unity access goes through `RunOnMainThread`
- Photon ownership must be maintained — other mods can steal it
- The game's `CharacterControllerAnimator` fights our rotation — see `Patches.cs`
- Reagent events (drinking, spraying) use the belly container's `OnChange` delegate
- Penetration uses `PenetrationTech.Penetrable.penetrationNotify` and `PenetratorListener`
- Background path worker computes A* off the main thread; milestone-based replanning

## Known Limitations

- Requires BepInEx 5.x (not 6.x)
- Only works with KoboldKare's specific API surface
- LLM quality depends entirely on your model and endpoint
- Vision requires a multimodal model
- Multiplayer: only the host's NPCs are fully autonomous