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
  Main.cs          — Plugin entry, config binding, instance lifecycle
  NPCInstance.cs   — Per-NPC state (partial class, 15 source files)
  Llm.cs           — LLM decision loop, tool extraction, plan chaining
  Body.cs          — Possession, teardown, camera, reagent/penetration listeners
  Senses.cs        — Perception: raycasts, clearance, radar, spatial layout
  Movement.cs      — Physics-frame movement: walk validation, obstacle steering
  Tools.cs         — Tool implementations (walk, go_to, interact, say, etc.)
  Chat.cs          — Photon chat, speech bubbles, chat log
  Pathfinding.cs   — 3D layered A* on a local walkability grid
  Vision.cs        — Background vision caption pass
  SafeHttp.cs      — Retry wrapper with exponential backoff
  Constants.cs     — Named constants for magic numbers
  ContextManager.cs — Dynamic context compaction
  ModelProbe.cs    — Server capability detection
  Json.cs          — Hand-rolled JSON parser/writer (no external deps)
  Patches.cs       — Harmony patches for animator rotation conflicts
tests/
  test_json.cs     — Json parser/writer tests
  test_pathfinding.cs — Pathfinding algorithm tests
  test_contextmanager.cs — Context compaction tests
  README.md        — Test instructions
docs/
  ARCHITECTURE.md  — Architecture overview
  CONFIG.md        — Complete config reference
wip/               — Work-in-progress notes
```

## Making Changes

### Code Style

- Follow the existing style: `try { ... } catch (Exception) { }` for silent catches,
  `try { ... } catch (Exception e) { Logger.LogWarning("...: " + e.Message); }` for logged catches
- Use `F()` for float-to-string formatting in LLM-facing output
- Keep partial class files focused: each file handles one concern
- Add comments explaining *why*, not *what* (the code shows what)

### Testing

- Algorithmic tests live in `tests/` — they test pure logic without Unity
- Run tests with NUnit: compile with `-r:nunit.framework.dll`
- Full integration requires a running KoboldKare instance with BepInEx
- Monitor BepInEx logs for `Json.Parse error` messages in production

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

## Architecture Notes

- `NPCInstance` is a partial class split across 15 files — each handles one subsystem
- The LLM loop runs on a background thread; Unity access goes through `RunOnMainThread`
- Photon ownership must be maintained — other mods can steal it
- The game's `CharacterControllerAnimator` fights our rotation — see `Patches.cs`
- Reagent events (drinking, spraying) use the belly container's `OnChange` delegate
- Penetration uses `PenetrationTech.Penetrable.penetrationNotify` and `PenetratorListener`

## Known Limitations

- Requires BepInEx 5.x (not 6.x)
- Only works with KoboldKare's specific API surface
- LLM quality depends entirely on your model and endpoint
- Vision requires a multimodal model
- Multiplayer: only the host's NPCs are fully autonomous
