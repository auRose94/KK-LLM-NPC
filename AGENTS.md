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
./run_tests.sh                  # all 26 pure-C# suites + python linter selftest — THE gate (CI runs this)
./run_tests.sh json swallow     # subset by suite name (exact or prefix)
tools/lint_config.py            # lint the game's live cfg vs docs/CONFIG.md (--selftest, --strict)
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
- `docs/CONFIG.md` — complete config reference (the `tools/lint_config.py` source of truth)
- `docs/BEHAVIOR.md` — long-form behavior highlights (moved out of README 2026-11-15)
- `docs/VORE.md` — swallow-vore/spectate mechanics (opt-in features)
- `docs/MODELS.md` — model compatibility notes + benchmark plan
- `docs/changelog-archive.md` — pre-2026-10-14 changelog rounds (main CHANGELOG keeps the rest)
- `tests/README.md` — suite table, how to add a suite, deleted-fake-tests history
- `CONTRIBUTING.md` — style, PR checklist, game-API integration notes
- `wip/BASE-GAME-AI.md` — reverse-engineered game internals

## Current state (2026-11-17)

- `main` is the PR target and is current; **v1.5.0 is COMMITTED** (GitHub release pending)
  and v1.3.1 was the last GitHub release. CI runs on every branch push plus a nightly deep-soak schedule
  (`workflow_dispatch` works too). `ws/*` branches are checked out in
  worktrees under `/mnt/Matrix/Work/KK-LLM-NPC-wt/<name>` — don't try to
  check them out here.
- 2026-11-17 **OctreeNav — volumetric 3D pathfinding (v1.5.0, committed)** — `src/OctreeNav.cs` (pure): every sampled surface becomes a
  floor voxel in per-column Y-sorted sparse sets; true-3D A* with
  cross-column climb/drop edges + same-column fall shafts + door tolls.
  BUILT FROM THE .kkmap CACHE ARRAYS (background thread, ms) — a cached
  scene gets 3D routing with zero re-sampling. `[Movement].WorldMapRayDepthM`
  (default 150, was fixed 60) samples 2.5× deeper for caves; loader takes 24
  layers (was 8); straight-leg fusing is a 3D wall-voxel march (quadtree =
  fallback); layered A* stays the safety net (`WorldMap3D=false` for legacy).
  `test_octreenav.cs`: stacked tunnels/overpass asymmetry, shafts, 30m
  stepped caves, long-trek smoke. 26 C# suites.
- 2026-11-17 **LM Studio adoption (v1.4.0, committed in v1.5.0)** — the
  `[LMStudio]` section mirrors `[Ollama]`: watcher adopts an externally-running
  LM Studio via `/api/v0/models` (load state + REAL context window + quant in
  one payload), `AutoEndpoint` hand-off (an adopted Ollama OUTRANKS it),
  TTL keep-warm heartbeat (1-token completion; LM Studio's idle TTL resets on
  every request), and a WebUI picker that LOADS the model through
  `/api/v0/models/load` — something Ollama can't do. Pure `LMStudioArgs.cs` +
  `test_lmstudio.cs`; ModelProbe also probes `/api/v0/models` now. Added same day:
  `[Ollama].Replicas` (default 1) — N loaded instances via /api/copy shards
  (`model.kk2`…), one per agent round-robin, keep-alive pings all replicas,
  prewarm/unload lifecycle in the watcher, agents shard-route only while the
  Ollama hand-off is live. CORRECTION same day: LM Studio DOES support multiple
  loaded instances of one model (Nth = '<model>:N', Rose-verified live) —
  [LMStudio].Replicas mirrors it, verification-driven (instance routes agents
  only after a poll confirms it loaded); LM Studio server-auth (0.4+) needs the
  token in [LLM].ApiKey or probes 401.
- 2026-11-17 **gemma playtest dissection (committed in v1.5.0)** — five root
  causes fixed from the first `unseen_gemma_4_26b_nsfw` session: model swaps
  re-resolve family+tier (a stale `deepseek`/`small` profile had zero gemma
  mitigations; `<n>b` name heuristic sizes tier), vision circuit breaker
  (3 empty captions → 10-min pause + "eyes offline" perception note — the
  dead vision path burned 16–40s serial slots all session), JSON act-blobs
  are never spoken (IsRpResponseBlock excludes JSON shapes — gemma's ```json
  replies were voiced in chat), stuck-breaker replans cap at 5/goal with
  first-only NavOutcome (30+ replan storms at unmapped stations), and held
  buckets read their real mix (buckets are the game's FOOD vessels — a Burger
  got thrown away as an "endless water bucket"). `find <unknown>` now lists
  real places. Watch item: gemma empty-delta SSE replies ×2. Same evening (live session): fenced act-JSON with PHRASE actions ("look around") + plan queues salvaged to nothing — fence-tolerant bare path + phrase-action fallback + plan[] recursion now recover them (echo/speech included); regression r5/r6 in test_console_shell. Round 3 (Scamper): gemma switched to XML tool markup (<action>…</action>, truncated </> closes) — UnwrapActionTags salvage pass + IsRpResponseBlock rejects markup + name-prompt anti-restate + parenthetical-annotation salvage. Three dialects now handled: act-JSON, fenced act-JSON, XML tags.
- 2026-11-16 vore batches (all deployed, unreleased): size mechanics
  (capacity gates + scaled timing + throw-up + belly bulge, `SwallowSizeGates`),
  stretch band + kick training (bellySize gene growth) + the player SPACE-mash
  escape mini-game (the swallow-abort CHAT command is REMOVED),
  **churn phase 1** (swallowed victims ride the belly as real `LivePrey`
  reagent — level IS digestion, native bulge; see docs/VORE.md design section
  for the remaining milk/cum/substrate phases), and **reincarnation**
  (`SwallowReincarnateChance` soul eggs — Destroy-fate second chances).
  Also: who's-who legibility (ps ids, death announcements), eat-by-name
  (`swallow target=`), swift silence on absorb/sell (`MarkGone` +
  MailMachine prefix), own-appendage penetration guard, voluntary greetings.
- 2026-11-15: phantom-intimacy fix — perception `touch` field
  (`Body.ContactText`) states real physical contact (penetrated / penetrating /
  mounted / carrying / swallowed / none); arousal alone is never intimacy and
  the prompt says so. Plus a Harmony finalizer on `User.SortGrabbables` that
  swallows the game's per-physics-frame `BreedingStation.CanUse` NRE (null
  `station.info` after a body dies mid-mount) with a throttled log.
- 2026-11-15: Ollama keep-alive heartbeat — `[Ollama].KeepAlive` (default
  `30m`): the watcher pings `/api/generate` with an empty prompt + explicit
  `keep_alive` (Ollama's documented keep-warm call) roughly every half-window,
  so the model never unloads mid-session. `-1` pins, `off` disables; pure
  parse/cadence in `OllamaArgs.KeepAliveSeconds`/`KeepAlivePingSecs`.
- 2026-11-15: phantom-intimacy fix — perception `touch` field
  (`Body.ContactText`) states real physical contact (penetrated / penetrating /
  mounted / carrying / swallowed / none); arousal alone is never intimacy and
  the prompt says so. Plus a Harmony finalizer on `User.SortGrabbables` that
  swallows the game's per-physics-frame `BreedingStation.CanUse` NRE (null
  `station.info` after a body dies mid-mount) with a throttled log.
- 2026-11-15: Ollama keep-alive heartbeat — `[Ollama].KeepAlive` (default
  `30m`): the watcher pings `/api/generate` with an empty prompt + explicit
  `keep_alive` (Ollama's documented keep-warm call) roughly every half-window,
  so the model never unloads mid-session. `-1` pins, `off` disables; pure
  parse/cadence in `OllamaArgs.KeepAliveSeconds`/`KeepAlivePingSecs`.
- The console turn engine lives in pure `src/ConsoleTurnCore.cs` behind an
  `IConsoleTurnEnv` seam (`Console.cs` keeps only the game wiring), and
  `tests/test_turncore.cs` drives whole agent turns with a scripted fake LLM +
  fake world; `tests/test_soak.cs` is the long-horizon soak (3 agents ×
  `SOAK_ROUNDS` turns over pathological replies — CI's nightly schedule scales
  it to 2000). 26 C# suites total.
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
