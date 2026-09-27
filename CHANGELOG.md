# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **LLM requests now actually retry.** `SafeHttp` built a single `HttpWebRequest` outside its
  retry loop and called `GetRequestStream()`/`GetResponse()` on it repeatedly. A submitted
  `HttpWebRequest` can never be resent — the second attempt threw `InvalidOperationException`
  ("request started") without touching the network — so every retry was a silent no-op that
  still paid its backoff sleep. A transient 503 cost the NPC 3 seconds of frozen decision
  loop and still failed.
- **Wired the retry layer into the real transport.** `SafeHttp` existed but had *no callers*:
  all four LLM call sites (`Llm.cs`), the vision call (`Vision.cs`) and the model probe
  (`ModelProbe.cs`) each hand-rolled their own single-shot `HttpWebRequest` with no retry at
  all. They now all go through `SafeHttp`. This is what makes the retry behavior real rather
  than theoretical.
- **Permanent HTTP errors no longer retried.** 400/401/403/404/413 fail after one attempt
  instead of three, so a bad API key or an over-long context no longer burns backoff sleeps
  and hammers the server. 5xx, 408, 429 and connect/timeout faults still retry with backoff.
- **Failed calls report the server's error body.** Previously a non-2xx reply was reduced to a
  status code and discarded. The log now carries the server's own complaint, which is what
  makes "context length exceeded" distinguishable from "no model loaded" without a debugger.

### Added
- **`src/ContextCore.cs` + 109 tests.** The compaction policy — escalation ladder,
  de-escalation, per-budget dynamic limits and their floors, token estimation, fact merging,
  history summarization — is now a pure, Unity-free state machine (`ContextPolicy`,
  `ContextMath`, `ContextCompaction`). `ContextManager` is a thin adapter holding only the wall
  clock, the `ModelProbe` lookups and the admin-API model switch. Dynamic compaction was listed
  as a "Key Design Decision" with zero test coverage because it couldn't be reached without a
  game install; it now has the most thorough suite in the repo.
- **`GoalResolver` in `PathCore.cs` + 30 tests.** The goal-snapping ring search was inline in
  `Pathfinding.ResolveGoal`, coupled to `PathGridState` and therefore untestable. It now runs
  against an `IGoalGrid` interface that `PathGridState` already satisfied for free (no new
  members), and is covered headlessly. This pins down two behaviours that were previously
  implicit: the search is nearest-in-plane with a *height* tiebreak (a ring-1 cell at the wrong
  floor beats a ring-2 cell at the right one), and equal-height ties resolve to the first cell
  scanned.
- **`run_tests.sh`** — one command to compile and run every pure-C# suite, with a per-suite
  summary and a non-zero exit if anything fails. Previously there was no single entry point and
  no shared exit-code convention.
- **`tests/test_safehttp.cs`** — 23 tests driving the real transport against a loopback
  `HttpListener`. These count actual server-side hits, which is the invariant that broke: the
  suite asserts a 3-retry sequence reaches the server 3 times, and that a 400 reaches it once.
- **`check_syntax.sh` + real CI gates.** CI previously compiled nothing and ran nothing — it
  counted lines and printed file listings, so a syntax error in `src/` merged green. CI now
  runs the unit suites and a parse check over all of `src/` (a full build still needs a game
  install, since BepInEx/Unity/Photon aren't redistributable).
- **`check_build.sh`** — a real compile against a game install, reusing `build.sh`'s exact
  reference set, and treating warnings as errors. Skips cleanly when no install is present, so
  it works locally and is a no-op on CI. Added after the parse-only check proved it could not
  catch a renamed member: `ContextManager.DynamicMaxThoughts` was briefly renamed, the call site
  in `NPCInstance.cs` was left dangling, and every headless gate stayed green. Only `build.sh`
  caught it. The limitation is now documented in `check_syntax.sh` itself so it isn't
  rediscovered.
- **CI fails on stray sources.** `build.sh` compiles `src/*.cs`, so a `.cs` file dropped into a
  `src/` subdirectory would be silently excluded from the DLL. CI now rejects that.

### Removed
- **`tests/test_pathfinding.cs`.** Every one of its ten methods was a `// Placeholder:` comment
  with no assertion and no `Main()` — it was a TODO list wearing a test file's name. The one
  piece of real logic it was gesturing at (goal resolution) is now `GoalResolver`, tested.
- **`tests/test_contextmanager.cs`.** Same anti-pattern as `test_horniness.cs`: it re-implemented
  the compaction ladder inline ("simulate escalation without a real NPCInstance") and asserted on
  its own local variables. Superseded by the 109 real tests in `test_contextcore.cs`.
- **`tests/test_horniness.cs`.** It never referenced `NPCInstance` or any file in `src/` — it
  re-implemented the horniness arithmetic in local variables and asserted on its own locals.
  Two of its six cases failed (`HorninessBounds` computed -0.05; `EggLayReadiness` got false)
  and it still exited 0, so it reported success while red. Its failures described arithmetic
  production never performs: the real `UpdateHorniness` only ever clamps the upper bound and
  resets on climax, so `_horny` cannot go negative. Deleting it removes a false signal; the
  honest fix is to extract the horniness math into a Unity-free `HorninessCore.cs` and test
  that, the way `PathCore` was extracted. Not done here.

### Documentation
- Corrected the ambient-commentary claims. `EmitAmbient` has no live callers (all three call
  sites are commented out), so the README's "moans on stimulation" and ARCHITECTURE's
  "Ambient commentary (moan on stimulation spike)" described behavior the build does not have.
  The docs now say it is disabled. Restoring the calls or deleting the dead method is a
  gameplay decision, so neither was done unilaterally.
- `tests/README.md` now lists all 7 suites (326 tests). Previously documented as 4 test files, all
  described as pure C#, while 3 files existed that either needed a game install or asserted
  nothing at all.

### Known gaps (pre-existing, not addressed here)
Two compaction features are implemented and unit-tested but never called, so compaction level 3
advertises more than it does:
- `ContextCompaction.SummarizeHistory` has no caller — level 3 merges facts but never summarizes
  history, despite the log text and ARCHITECTURE both saying "fact merging + history summary".
- `ContextManager.DynamicMaxChatLog` has no caller, so `[LLM] ChatLogLines` is not reduced under
  compaction.

Both are behaviour changes to wire up (how many chat lines to keep at level 5 is a tuning
decision), so they were left for a deliberate choice rather than changed silently.

### Added (Console REPL and earlier work)
- **Console REPL (bash-like AI interface, default ON)** — the LLM now *polls* the game from a shell
  instead of receiving a perception JSON blob every tick: it sends command lines (`ls`, `ps`, `pwd`,
  `whoami`, `cat facts|goal|chat|needs|history|stations|map|body`, `status`, `find`, `look`; `echo
  <words>` to **speak**; `cd`, `use`, `run`, `turn`, `jump`, `crouch`, `exit`, `get`, `drop`, `follow`,
  `stop`, `sleep`; `remember`, `forget`, `goal`, `ask`), reads the terse output, then sends more
  commands — a command → data → read → command stream. Payloads stay small (works on small local
  models, no overload), and the model asks for exactly the data it needs. Bash-style names are
  chosen so small models already know them; legacy `act` names (`say`→`echo`, `go_to`→`cd`,
  `interact`→`use`, `walk`→`run`, …) are aliases, with a deliberately tight fuzzy matcher that
  recovers typos without turning prose into commands. Module tools (thrust, plant, water, harvest,
  plant_egg, feed_blender, grind, rename, …) register into the same vocabulary at startup.
  New `[Console]` config section (`Enabled`, `MaxRounds`, `MaxTokens`, `HistoryMessages`,
  `SystemPromptFile`); a `system_prompt_console.txt` persona ships with the plugin.
  `Console.Enabled=false` restores the legacy push-perception / `act`-JSON loop (kept intact).
  New files: `src/Console.cs`, `src/ConsoleShell.cs`, `system_prompt_console.txt`;
  tests: `tests/test_console_shell.cs` (90 tests).
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