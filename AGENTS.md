# AGENTS.md — KK-LLM-NPC

Guidance for AI coding agents working in this repo. Read this first; go deeper
with the docs map at the bottom.

## What this is

A BepInEx 5 plugin (C#, compiled with `mcs`) for [KoboldKare](https://store.steampowered.com/app/1102930/KoboldKare/)
that possesses the game's AI kobolds and plays them through an OpenAI-compatible
chat-completions endpoint (Ollama, LM Studio, llama.cpp, vLLM, …). Each NPC
perceives, plans, moves, farms, talks in chat and reacts continuously. Default
mode is a **bash-like console REPL** the LLM polls with shell-style commands
(`ls`, `cd`, `cat`, `echo` = speak); a legacy push-perception / `act`-JSON loop
is kept intact behind `[Console] Enabled=false`.

**Content note, so you're not surprised:** KoboldKare is an adult game and the
mod works with the game's own content — expect NSFW domain terms in code and
prompts (penetration, vore, etc.). Treat them as ordinary game mechanics.
Consent/opt-in guardrails are a deliberate design pillar: swallow-vore and
spectate are double-gated and OFF by default; `[NonConsent]` detects a model
declining the adult content and (by default) steers it to SFW roleplay. Don't
weaken those gates casually; see `docs/VORE.md` before touching that area.

## Commands

```bash
./run_tests.sh                  # all 21 pure-C# suites — THE gate (CI runs this)
./run_tests.sh json swallow     # subset by suite name
./check_syntax.sh               # parse-only check, no game install needed
./check_build.sh                # FULL compile vs real game install — only check that
                                #   catches semantic errors; skips cleanly (exit 0) if
                                #   no game found; warnings are errors
./build.sh                      # compile KKLLMNPC.dll + deploy into the game's BepInEx/plugins/
./make_release.sh               # release zips into dist/ (BepInEx 5.4.22 bundled)
./make_release.sh --no-build    # package existing DLL only
BUNDLE_BEPINEX=0 ./make_release.sh --no-build   # offline packaging
```

Requires `mono-complete` (mcs + mono). The game install is resolved from
`KOBOLDKARE_DIR`, else `tools/find_koboldkare.py` (Steam library scan), else
`/mnt/gaming/SteamLibrary/steamapps/common/KoboldKare`.

`.bepinex-cache/` (repo root, untracked) is make_release.sh's BepInEx download
cache — ignore it, it's safe to delete.

Before any PR: `./run_tests.sh` AND `./check_build.sh` both pass. The unit
suites cannot catch a renamed/missing member (they compile only the pure-C#
subset), which is exactly why the full build check exists.

## Repo map

```
src/                 ~50 files; 27 of them declare `partial class NPCInstance`
  Main.cs            BepInEx plugin entry: config binding, instance lifecycle
  NPCInstance.cs     core per-NPC state; the rest of the class is split by subsystem:
  Llm.cs             decision loop, LLM query, tool extraction, plan chaining
  Body.cs            possession/teardown, camera, reagent/penetration listeners
  Senses.cs          perception: raycasts, clearance, north-up sonar
  Movement.cs        physics-frame movement, walk validation, obstacle steering
  Tools.cs           tool implementations (walk, go_to, grab/drop/throw, …)
  Console.cs         console REPL turn loop + command dispatch (wiring)
  ConsoleShell.cs    bash-like command vocabulary + lenient parser (pure C#, testable)
  Swallow.cs         vore/spectate runtime; SwallowLogic.cs = pure logic
  WebUI.cs           browser debug surface at http://127.0.0.1:8338/
  Json.cs            hand-rolled JSON parser/writer (no external deps)
  <Name>Core.cs      pure-C# extract of Unity-coupled logic (PathCore, ContextCore, …)
tests/               one self-contained suite per pure file; see tests/README.md
prompt_extras/       per-module system-prompt guidance (.txt, auto-concatenated)
system_prompt_*.txt  persona/world-rule prompt files, shipped to BepInEx/plugins/
tools/               find_koboldkare.py, install_bepinex.sh
wip/BASE-GAME-AI.md  reverse-engineered game API notes
docs/                ARCHITECTURE / CONFIG / MODELS / VORE / vision-dump-spec
```

## Architecture essentials

- **NPCInstance is one giant partial class** split by subsystem. Keep each file
  single-concern; don't create new top-level classes when a partial file is the
  local idiom.
- **Two loops**: the LLM decision loop runs on a background thread; Unity API
  access goes through `RunOnMainThread`. Never touch Unity objects from the LLM
  thread. `ResetMind`-style wipes are applied at the next turn boundary via
  volatile flags, not from the caller's thread.
- **ModuleRegistry**: new tools / physics ticks / perception keys register via
  reflection — no shared-file edits needed. Console tools additionally
  `ConsoleShell.RegisterExtra` into the command vocabulary.
- **Pure-core pattern**: logic you want to unit-test must be extracted into
  `src/<Name>Core.cs` free of `UnityEngine`/`BepInEx` (see `PathCore.cs`,
  `ContextCore.cs`, `SwallowLogic.cs`, `ConsoleShell.cs`). Suites compile with
  `mcs` against that file alone and run under `mono`.
- **All HTTP** (LLM, vision, probes) goes through `SafeHttp` — retries transient
  failures, fails fast on 4xx.
- **Harmony patches** (`Patches.cs`) neutralize the game's
  `CharacterControllerAnimator` rotation writes; Photon ownership of possessed
  bodies must be re-asserted (other mods can steal it).

## Project rules (what actually gates a change)

- **Zero warnings** — the real build must end with 0 warnings; remove dead code
  and unused locals.
- **New tests must call the real code** — suites that re-implement logic in the
  test body were audited and deleted; don't recreate them. Exit 0 = pass only
  (never "exit = failure count"; 256 wraps to 0).
- **Every new test file gets a `run_suite` line in `run_tests.sh`** — the script
  audits for orphaned test files and warns.
- **No new external dependencies**; no NuGet. The build is `mcs` against
  game-provided assemblies only.
- **New config keys go in `Main.cs`** with a dedicated `[Section]`, opt-in
  defaults, and a row in `README.md` + `docs/CONFIG.md`. No breaking config
  changes.
- **Update `CHANGELOG.md`** — follow its existing style (dated `### Added/Fixed`
  sections, not `[Unreleased]`).
- **Version bump** lives in the `[BepInPlugin]` attribute in `src/Main.cs`.
- Match surrounding style: `F()` for float formatting in LLM-facing strings,
  silent catches for expected failures, logged catches otherwise, comments
  explain *why*.

## Adding a module (the standing recipe)

1. `src/<Name>.cs` — partial class on `NPCInstance`, one concern.
2. Register via `ModuleRegistry` (`Tool` / `PhysicsTick` / `Perception`) in a
   `<Name>Module.Register()`; console commands via `ConsoleShell.RegisterExtra`.
3. `prompt_extras/<name>.txt` — system-prompt guidance for it.
4. Config entries in `Main.cs` under a dedicated section.
5. Pure logic → `src/<Name>Core.cs` + `tests/test_<name>.cs` + a `run_suite`
   line in `run_tests.sh` + a row in the `tests/README.md` table.

## Gotchas

- The plugin **cannot be compiled on CI** — BepInEx/UnityEngine/Photon/
  Assembly-CSharp are game-provided and not redistributable. That's why
  `check_build.sh` skips without a game and CI only syntax-checks.
- Verify in-game behavior for anything touching movement, possession, chat, or
  the game API — unit tests can't see it. Logs:
  `BepInEx/plugins/LogOutput.log`; AI bug reports:
  `BepInEx/config/kkllmnpc_ai_reports.log`; live debug:
  WebUI `http://127.0.0.1:8338/`.
- In-game F6 debug overlay was removed — the browser page is the debug surface.
- Prompt files exist in two places by design: repo-root `system_prompt_*.txt`
  (source of truth) and the copies `build.sh` ships into the game's
  `BepInEx/plugins/`. Edit the repo copies.
- The console's command table, manual pages (`Manual.cs`), persona text, and
  `ConsoleShell` help must stay in sync — several past bugs were exactly one of
  these drifting from the others. `tests/test_console_shell.cs` covers part of
  this; extend it when you add commands.

## Docs map

- `docs/ARCHITECTURE.md` — full component breakdown, data flow, thread model
- `docs/CONFIG.md` — complete config reference
- `docs/VORE.md` — swallow-vore/spectate mechanics (opt-in features)
- `docs/MODELS.md` — model compatibility notes + benchmark plan
- `tests/README.md` — suite table, how to add a suite, deleted-fake-tests history
- `CONTRIBUTING.md` — style, PR checklist, game-API integration notes
- `wip/BASE-GAME-AI.md` — reverse-engineered game internals

## Current state (2026-11-05)

- `main` is the PR target. `ws/*` branches are checked out in git worktrees
  elsewhere — don't try to check them out here.
- `feature/headless-agent-sim` (current branch) is even with `main` — the
  headless agent-sim work it's named for hasn't started yet.
- v1.2.3 (2026-11-05): new default profile — rapid/immersive/Ollama-first.
  Shipped defaults now reflect a well-played setup (no existing config file is
  touched — BepInEx only writes a key on first launch). Ollama adoption
  (`[Ollama] Enabled` + `AutoEndpoint`) ships ON so the mod routes through your
  Ollama (idles harmlessly when none is running); `Model` stays the neutral
  `local-model`. Turn cadence/immediacy: `ThinkInterval` 0.4→0.1,
  `Temperature` 0.3→0.8, `PlanStepDelay` 0.35→2, `PlanMaxSteps` 8→2. Immersion:
  `MaxTokens` 1024→18096, `ImageEveryNTicks` 6→2, `[Vision] Stereo`→on,
  `RayCount` 9→18, `RayRange` 25→32, `RadarSize` 10→30, `RadarScale` 1.2→0.25.
  Console/concurrency: `[Console] MaxRounds` 3→5, `[Console] MaxTokens`
  256→4096, `ParallelCalls` 2→8, `MaxNPCs` 1→4. The opt-in adult features
  (`SwallowEnabled`/`SwallowAgentEat`/`SwallowPlayerVictims`) stay OFF by
  default — that safety pillar is unchanged. `ornith-1.5:35b` is recommended
  higher (🟡2) in docs/MODELS.md as the default-recommended model. NOTE:
  `IdentityBot` was deliberately left OFF (multiplayer safe default).
- v1.2.0 shipped: bundled-BepInEx releases, tiered console help + failure
  escalation, wired `skill` command + built-in navigation skills, README/INSTALL
  rework.
- v1.2.2 (2026-11-04): server-stability + console-log hardening. `ps` now lists
  every other kobold (player/agent/wild, classified) instead of silently
  dropping wild or not-yet-named bodies — a tasked target like "go eat Flint"
  was invisible when Flint was a wild NPC. Commentary worker got a busy guard
  (it had none, so threads stacked under a slow server) and the `model_unloaded`
  retry was cut from 3×4s re-POSTs to one quick retry + defer (it was hammering a
  reloading server). Console `now:` clock rebuilds every 5s and the state anchor
  is re-emitted only when it/its events changed — the per-tick "now" flood is gone.
- v1.2.1 (2026-11-04): `Json.Write` hardened into a depth-capped
  map/array/object builder — any `IDictionary`, `KeyValuePair`, struct fields —
  fixing the "System.Collections…" type-name leak that reached tool responses
  and logs. Same release: `Json.Parse` decodes `\uXXXX` string escapes (Go
  servers HTML-escape `<>&`; the gap registered the name "u003cthe" from a
  mangled template echo), the name prompt's final line uses a concrete
  example name instead of an angle-bracket placeholder, the salvage path's
  act-schema `params` dict flattens instead of ToString-ing (the
  "echo System.Collections…" parrot loop), the WebUI terminal renders
  multipart messages, the eater's swallow state got its own `Digesting` flag
  (it was set to `Swallowed` — the predator read victim text), and the
  name flow no longer names kobolds out of reasoning words. Next-up notes
  (if any) go in the repo's plan files or CHANGELOG, not here.
