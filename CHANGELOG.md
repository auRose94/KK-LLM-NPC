# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Background path worker** — static daemon thread computes A* paths off the main thread;
  milestone-based replanning (only when target moved, path exhausted, or deviation exceeds
  threshold); time-budgeted expansion prevents frame hitches on large maps
- **Whole-map world map** — cached 3D walkability map shared by all agents; adaptive cell
  sizing (span/1200, clamped [0.5,4.0]); hard cell cap (~4M); auto floor detection
  (gap > 1.5m = new layer, cap 16)
- **Body control module** — `thrust` (hip animation), `erection`, `mount`, `unmount`,
  `orgasm` tools with consent-aware prompt guidance; perception `body_control` reports
  erection, stimulation, penetration state, hip animation status
- **Chat freshness** — timestamped chat log with ack tracking; say-repeat suppression
  (Jaccard > 0.65 OR Levenshtein ratio > 0.8); identity bot exponential backoff
  (2s → 30s); empty-content retry; de-duped log
- **Farming & cooking module** — `plant`, `water`, `harvest`, `plant_egg`, `feed_blender`,
  `grind` tools; perception `farm` (nearby plants) and `cooking` (nearby equipment)
- **Identity module** — per-NPC identity block (orientation, trans status, persona);
  `rename` tool with uniqueness validation; mailbox/ATM perception
- **PathCore.cs** — pure C# A* solver, path policy, adaptive cell sizing (Unity-free,
  compiles standalone for testing)
- **ChatSimilarity.cs** — Jaccard + Levenshtein similarity (pure C#, testable)
- **New tests** — `test_pathcore.cs` (33 tests: A*, ShouldReplan, adaptive cell, auto floors);
  `test_chat_similarity.cs` (19 tests: Jaccard, Levenshtein, fuzzy matching)
- **New prompt extras** — `body_control.txt`, `chat.txt`, `farmcook.txt`, `identity.txt`

### Changed
- **Pathfinding** — moved from inline FixedUpdate A* to background worker; added time budget
  and expansion cap; world map with auto-layers and adaptive cells
- **Movement config** — added `PathTimeBudgetMs`, `PathMaxExpansions`, `WorldMapEnabled`,
  `WorldMapCellSize`, `WorldMapMaxSpan`, `WorldMapMaxCells`, `WorldMapAutoLayers`,
  `WorldMapLayers`, `WorldMapCellsPerFrame`
- **Farming config** — new `[Farming]` section with `ScanRadius` and `ScanMax`
- **Architecture** — 26 partial class files (up from 15); new `ModuleRegistry` for
  reflection-based feature discovery
- **Warning count** — reduced from 19 to 0 (removed dead locals, fields, and assignments)

### Added
- **Goal machine** — `set_goal` / `complete_goal` / `drop_goal` tools with a persistent goal in perception (`goal` block + `nudge` when stuck); a thought-repetition guard for models that ignore the tools; completing/dropping a goal sheds its scratch facts (`src/GoalMachine.cs`)
- **`forget` tool** — drop a fact by category, text, or substring (no argument = drop the oldest fact); the explicit half of forgetting
- **Fact decay** — `Memory.FactDecayTicks` (default 900; 0 = off): facts the model hasn't re-asserted age out of context; re-`remember`ing refreshes a fact's age; cap eviction now removes the oldest fact
- **Chat baseline** — the game chat log is snapshotted when the NPC acquires its body; `chat_log` only feeds lines added after that, so the NPC never "reads" pre-spawn conversation
- **NPC room identity (opt-in)** — `Multiplayer.IdentityBot` + `IdentityAppId`: a second Photon client joins the room under the NPC's name so its chat renders `KoboldName: text` to everyone; auto-falls back to owner-attributed chat whenever the bot is down (`src/IdentityBot.cs`)
- **`LLM.SystemPromptFile`** — full system prompt from a file (resolved relative to game dir, plugin dir, or CWD) overriding the built-in; `system_prompt_default.txt` ships in the repo
- **SafeHttp utility** — retry wrapper with exponential backoff (3 attempts, 1s/2s/4s delays), proper resource disposal, and TLS validation for all LLM HTTP calls
- **Constants.cs** — named constants for all magic numbers (HTTP timeouts, perception ranges, movement values, etc.) — replaces scattered literals throughout the codebase
- **Thread pool semaphore** — caps concurrent `Task.Run` calls to prevent thread pool starvation under heavy multi-NPC load
- **Improved error logging** — all bare `catch (Exception) { }` blocks now log the exception message via `Logger.LogWarning("...: " + e.Message)`
- **Cross-platform build support** — build.sh includes dotnet SDK fallback alongside mcs
- **docs/ARCHITECTURE.md** — complete architecture overview with component breakdown, data flow, and thread model
- **docs/CONFIG.md** — complete configuration reference with all keys, defaults, ranges, and purposes
- **CONTRIBUTING.md** — contribution guidelines, code style, testing, and debugging instructions
- **CODE_OF_CONDUCT.md** — Contributor Covenant Code of Conduct
- **GitHub Actions CI** — `.github/workflows/build.yml` for automated build verification
- **tests/test_json_standalone.cs** — standalone Json parser/writer test specifications (no Unity/BepInEx deps)
- **Improved README** — complete config table with ranges, Windows build instructions, architecture doc links

### Changed
- **Own-speech filtering** — `Main.cs` now also matches on the sender's nickname, so a bot-attributed chat line (identity-bot path) isn't heard back by the sending NPC (no feedback loop)
- **System prompt resolution** — explicit `SystemPrompt` > `SystemPromptFile` > built-in; the full prompt now lives in `system_prompt_default.txt` and the compact small-model prompt includes the goal tools
- **Config duplication eliminated** — config entries are now bound in one place (`Main.cs`) and read via a shared config accessor, removing the duplicate field copies in `NPCInstance.cs`
- **HTTP resource management** — `HttpWebRequest` streams are now properly disposed via `using` blocks and `SafeHttp`'s finally blocks, preventing connection pool exhaustion
- **Magic numbers replaced** — all magic numbers (timeouts, ranges, thresholds) replaced with named constants in `Constants.cs`
- **Vision hung timeout** — reduced from 2 minutes to a configurable value with logging

### Fixed
- **Goal flapping** — `set_goal` now refuses a goal that was dropped/completed within the recent window (the `recently_dropped` ring is enforced, not just displayed), so weaker models can't loop set→drop→set
- **Fact-shed safety** — `ShedFactsForGoal` no longer wipes a world-map fact (e.g. `nest: upstairs`) when a goal shares its category prefix; a fact is only shed if the goal is category-tagged AND shares ≥2 significant words with it
- **Identity-bot staleness** — the bot now restarts when the game's room changes (it used to stay bound to the old room and chat into it), recovers after exhausting its reconnect budget (rate-limited restart instead of giving up forever), and `Stop()` no longer blocks the game's main thread for up to 1s
- **Standalone Json tests** — `tests/test_json_standalone.cs` `Main` called test methods that didn't exist, so the documented no-deps test path never compiled; it now delegates to `JsonTests.RunAll()`, which exercises the real `src/Json.cs` (22 tests). `Json.cs` no longer references `LLMNPCPlugin` (parse-error logging moved to a pluggable `Json.ErrorLog` sink wired in `Main.cs`)
- **Inverted test assertion** — `test_json.cs` asserted `Has("x")` should be false (message said "should be true"); corrected
- **HTTP connection leaks** — `HttpWebRequest.GetRequestStream()` is now always disposed in a `using` block, even when `GetResponse()` throws
- **Silent exception swallowing** — removed 15+ bare `catch (Exception) { }` blocks across Senses.cs, Movement.cs, Body.cs, Vision.cs, and Tools.cs
- **Door toggle-fluttering** — `MaybeOpenDoorAhead` cooldown now uses a named constant (`Consts.DoorCooldown`) for clarity
- **Camera clip detection** — improved camera-clip fix timing with configurable thresholds
- **Build.sh** — added mcs availability check with installation instructions

## [1.0.0] - 2026-09-05