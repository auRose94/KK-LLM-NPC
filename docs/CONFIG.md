# Complete Configuration Reference

All config entries are in `BepInEx/config/com.kk.llmnpc.cfg`.

## [LLM] — The Planner

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Endpoint` | `http://127.0.0.1:11434/v1/chat/completions` | — | OpenAI-compatible chat completions URL. LM Studio default: `http://127.0.0.1:1234/v1/chat/completions` |
| `Model` | `local-model` | — | Model name to request. Ollama: `local-model` or an installed tag like `ornith-1.5:35b` (the default-recommended model — see [docs/MODELS.md](MODELS.md)). LM Studio: use the exact model name from the Developer tab. KoboldCpp: `local-model` works. |
| `ApiKey` | *(empty)* | — | Bearer token (may be empty for local servers) |
| `SystemPrompt` | *(built-in)* | — | Override the default system prompt. A customized value always wins; otherwise `SystemPromptFile` is used if readable. |
| `SystemPromptFile` | `system_prompt_default.txt` | — | Path to the full system prompt (relative to game dir, plugin dir, or CWD). Overrides the built-in prompt; ships in this repo. |
| `ThinkInterval` | `0.1` | 0.05–10s | Seconds between perception/decision cycles |
| `Thinking` | `auto` | `auto`, `off` | Control the model's hidden reasoning (chain-of-thought). `off` requests suppression so answers start immediately — the biggest latency win on reasoning families, which otherwise burn their whole token floor on invisible analysis. Sends `think=false` (Ollama), `chat_template_kwargs {enable_thinking:false}` (llama.cpp / vLLM / SGLang, Qwen3-style templates) and `reasoning_budget=0` (llama.cpp hybrid models); servers that don't recognize a field skip it. LM Studio / KoboldCpp expose the toggle in their per-model GUI instead. Leftover reasoning markers are still stripped client-side. |
| `MaxTokens` | `18096` | 64–32768 | Max response tokens (reasoning models burn tokens on analysis before the action — too low and the action dies mid-JSON). Reasoning families automatically get a floor of `18096 + headroom` (Qwen/Bonsai/GLM +512 · Nemotron/Granite +768 · deepseek-style reasoners +1024 · unknown +256); your explicit value always stands when it is higher. |
| `Temperature` | `0.8` | 0–2 | Sampling temperature (lower = faster, more deterministic; 0.8 = livelier roleplay) |
| `PlanStepDelay` | `2` | 0.05–5s | Seconds between each action in a chained plan |
| `PlanMaxSteps` | `2` | 1–8 | Max actions the model may queue in one response (hard cap) |
| `SendImage` | `true` | bool | Attach a first-person JPEG to the action call when useful (vision model required) |
| `ImageEveryNTicks` | `2` | ≥1 | Baseline: attach an image every N ticks even without a trigger |
| `ImageOnBump` | `true` | bool | Attach a fresh image right after blocked/bump so the model sees what stopped it |
| `ImageOnTurn` | `true` | bool | Attach an image after large turns (≥30 deg) so it sees the new view |
| `ImageHistory` | `3` | 0–10 | Number of past frames to attach alongside the current one (0 = current only) |
| `ModelTier` | `auto` | `auto`, `small`, `medium`, `large` | Model capability tier: auto (probe at startup), small (≤13B), medium (30B–70B), large (≥70B) |
| `AutoSwitchModel` | `false` | bool | When context pressure is critical, automatically switch to a larger-context model (requires KoboldCpp --admin mode) |
| `LLMNameSelection` | `true` | bool | Ask the LLM to choose a name for each body based on personality/gender/species |
| `NamePromptFile` | `system_prompt_name.txt` | — | Chat template for the one-shot NAME prompt (relative to game dir, plugin dir, or CWD). Placeholders: `{gender}` `{species}` `{traits}` `{taken}` — `{taken}` expands to the taken-names sentence (empty when none exist; keep it or same-avatar siblings get re-picked before the registry rejects them). An existing file overrides the built-in template; ships in this repo. |
| `CommentEveryNTicks` | `5` | ≥0 | Every N ticks, invite a free 'comment' — the model voices its own take on surroundings (0 = off) |
| `CommentTemp` | `0.9` | 0–2 | Sampling temperature for free commentary |
| `ChatLogLines` | `20` | 0–200 | Feed the last N lines of the game's chat log to the model each turn (full conversation + NPC speech). 0 = off |
| `KvCacheType` | *(empty)* | fp16/q8_0/q4_0/q5_1 | KV-cache type the ADOPTED backend (Ollama/LM Studio) uses when serving — leave blank for the server default. Advisory; not all runners honour per-request overrides. |
| `MtpEnabled` | `false` | bool | Opt in to Multi-Token Prediction / speculative decoding on the ADOPTED backend (experimental; requires Ollama ≥ 0.3.x / a draft model pinned under `DraftModel`). |
| `DraftModel` | *(empty)* | — | Name of the smaller draft model used by MTP / speculative decoding (Ollama native `/api/chat` `draft_model`). Leave blank to disable multi-draft speculation. |
| `TimeoutSeconds` | `0` | 0–… | Chat POST timeout. `0` = automatic: plain turns 120s, thinking families (Qwen 3.5 / GPT-OSS / GLM / R1-class — their CoT burns wall-time before any answer text) 300s. Set >0 to force one bound for every family; a long timeout on a busy local server holds an Ollama parallel slot, so lower it if a second NPC starves. |

### `[LLM] ParallelCalls` — shared-serial-server concurrency cap

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `ParallelCalls` | `8` | 0–8 | Max simultaneous LLM HTTP calls the whole mod makes toward the endpoint. Local llama.cpp/KoboldCpp/solo-GPU LM Studio servers answer serially — with several NPCs posting at once, burst calls starved into `WebException(Timeout)` warnings (the dominant log noise). Extra calls now QUEUE inside the mod (fail-open after two minutes). `0` = unlimited. Default raised to 8 for multi-NPC / multi-slot servers; lower it (e.g. 2) for a single-GPU serial box to avoid timeouts. Ollama note: it queues gracefully (no starvation) but serves only `OLLAMA_NUM_PARALLEL` slots, and each parallel slot splits the model's KV context window — 2–4 is the sweet spot there. Edits apply live (no restart). |

### External host (Ollama)

The mod talks to any OpenAI-compatible endpoint via `[LLM].Endpoint` (above). For a local
box, **Ollama** is the recommended host: it ships its own model library and an OpenAI-compatible
`/api/chat` interface, and — because the game can't launch a GPU server under Steam Proton —
the mod can **adopt** an already-running Ollama as its live endpoint instead of the
hard-coded `[LLM].Endpoint`, and hot-swap the served model without touching config. Adoption is
**ON by default** (v1.2.3): the mod watches your Ollama for models and, with `AutoEndpoint` on
(also default), routes the action model through it. Turn it off entirely with `[Ollama].Enabled`
if you'd rather point `[LLM].Endpoint` at any server. With no Ollama running it just idles
(`healthy=false`) — a fresh install is unaffected. Toggle it in the WebUI's **AI server** tab:

```ini
[Ollama]
Enabled      = true            ; adopt an externally-running Ollama as the live endpoint (default ON)
Endpoint     = http://127.0.0.1:11434
AutoEndpoint = true            ; route LLM + vision-fallback POSTs to the adopted Ollama (default ON)
SelectedModel=                ; what 'Use this model' picked; also writes [LLM].Model
PollIntervalSec = 10
KeepAlive    = 30m             ; keep the model resident — empty-ping /api/generate every half-window
Replicas     = 1               ; N loaded instances of the model, one per agent (VRAM permitting)
```

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `true` | bool | Adopt an externally-running Ollama. ON by default (v1.2.3): the mod watches your Ollama for models and can route the action model through it. Off = don't touch Ollama; use `[LLM].Endpoint` like any other server. Idles harmlessly when no Ollama is running. |
| `Endpoint` | `http://127.0.0.1:11434` | — | Ollama base URL (host:port). A full `/v1/chat/completions` suffix is accepted and stripped. The WebUI tab installs models here with `ollama pull <model>` then `ollama run <model>`. |
| `AutoEndpoint` | `true` | bool | When an Ollama is adopted + healthy, route LLM + vision-fallback POSTs to it regardless of `[LLM].Endpoint` (hand-off with no config editing after a restart). ON by default so the adopted Ollama is the live endpoint. Off = the configured endpoint stays the truth. |
| `PollIntervalSec` | `10` | — | How often the watcher probes the Ollama `/api/tags` + `/api/show`. |
| `SelectedModel` | *(empty)* | — | The Ollama model the WebUI 'Use this model' picked. Writing this also writes `[LLM].Model` so it takes effect on the next chat POST. Empty = use the model Ollama is currently serving. |
| `KeepAlive` | `30m` | `Ns`/`Nm`/`Nh`, bare seconds, `-1`, `off` | Keep the adopted model resident so NPC turns never wait behind a reload: the watcher pings `/api/generate` with an empty prompt + this `keep_alive` (Ollama's documented keep-warm call) roughly every half of the window. `-1` pins the model in RAM for the whole session; `off` restores Ollama's own ~5m idle unload. Pings stop when the game exits, so the default unloads shortly after you quit. |
| `Replicas` | `1` | 1–8 | Load UP TO this many instances of the adopted model — scaled ON DEMAND to the live agent count (instance N loads only when a Nth agent exists; extras release VRAM when agents leave), giving parallel turns each with a FULL context window (the `OLLAMA_NUM_PARALLEL` alternative SPLITS one window across slots). Extra instances are zero-disk copies via `/api/copy` (same files, separate loaded runner); every replica gets keep-alive pings; a model change unloads the old copies first. Each loaded instance takes the model's FULL VRAM: 2 + per-model server concurrency is the sweet spot for one shared GPU — 4× a multi-GB model can black-screen/crash a machine driving its display from the same card (seen live); use a second machine or a compute-only GPU for more. |

The **installed models** picker lists every local model (`ollama pull` to add one); **use this model**
hot-swaps the served model. Adoption is opt-in and reversible — `[LLM].Endpoint` is left untouched,
so pointing back at any other endpoint is a one-line config change.

### External host (LM Studio)

**LM Studio** gets the same adoption treatment (v1.4.0): the mod watches an already-running LM
Studio server through its native `/api/v0/models` — one payload that carries every model with its
load state, REAL context window (feeds context compaction), arch, and quantization — and with
`AutoEndpoint` on (default) routes the action model through it. **When both an Ollama and an LM
Studio are adopted, Ollama takes the traffic** (it shipped first, so nobody's working setup flips).
Two things go beyond Ollama: **use this model** actually loads the picked model in LM Studio
(via its load API, with a TTL from `KeepAlive`), and with JIT loading enabled LM Studio will
even load the model the mod names on the fly. LM Studio ALSO supports multiple loaded
instances of one model — the Nth carries `:N` in its name (`model`, `model:2`, …) — so
`Replicas` works here too (see below). Server auth (0.4+) requires the token in
`[LLM].ApiKey` or every probe 401s. Toggle it in the WebUI's **AI server** tab:

```ini
[LMStudio]
Enabled      = true            ; adopt an externally-running LM Studio (default ON)
Endpoint     = http://127.0.0.1:1234
AutoEndpoint = true            ; route LLM POSTs to the adopted LM Studio when no Ollama is (default ON)
SelectedModel=                ; what 'Use this model' picked; also writes [LLM].Model
PollIntervalSec = 10
KeepAlive    = 30m             ; keep the model resident — 1-token completion resets the idle TTL
Replicas     = 1               ; N loaded instances (model, model:2, …), one per agent
```

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `true` | bool | Adopt an externally-running LM Studio (the game can't launch one under Proton). ON by default: the mod watches your LM Studio for models and can route the action model through it. With no LM Studio running it just idles (`healthy=false`). |
| `Endpoint` | `http://127.0.0.1:1234` | — | LM Studio base URL (host:port). A full `/v1/chat/completions` suffix is accepted and stripped. Serve from LM Studio's Developer tab (or `lms server start`). |
| `AutoEndpoint` | `true` | bool | When an LM Studio is adopted + healthy (and no Ollama is), route LLM POSTs to it regardless of `[LLM].Endpoint` (hand-off with no config editing after a restart). Off = the configured endpoint stays the truth. |
| `PollIntervalSec` | `10` | — | How often the watcher probes the LM Studio `/api/v0/models`. |
| `SelectedModel` | *(empty)* | — | The LM Studio model the WebUI 'Use this model' picked. Writing this also writes `[LLM].Model` so it takes effect on the next chat POST. Empty = use whatever LM Studio has loaded. |
| `KeepAlive` | `30m` | `Ns`/`Nm`/`Nh`, bare seconds, `-1`, `off` | Keep the adopted model resident so NPC turns never wait behind a load. LM Studio auto-evicts JIT-loaded models after their idle TTL (default 60m) and the TTL resets on every request — so the watcher sends a 1-token completion roughly every half of this window. `-1` keeps it resident for the whole session (15m cadence); `off` disables the heartbeat. GUI-loaded models have no TTL and stay resident on their own. |
| `Replicas` | `1` | 1–8 | Load UP TO this many instances — scaled ON DEMAND to the live agent count (the Nth agent spawns its instance, one cold load per poll, TTL from `KeepAlive`, adopted once a poll confirms it `loaded`; mod-loaded extras release VRAM when agents leave). Every LOADED instance of the model (its `<model>:N` keys — **manual UI loads are adopted automatically**) joins the agent pool with a full context window each, but only mod-loaded instances are ever auto-unloaded — yours stay. Each loaded instance takes the model's FULL VRAM: 2 + per-model server concurrency is the sweet spot for one shared GPU — 4× a multi-GB model can black-screen/crash a machine driving its display from the same card (seen live); use a second machine or a compute-only GPU for more. |

## [Intoxication] — Drunk roleplay (2026-11-16)

Agents that drink alcohol roleplay being drunk — the belly's alcohol volume is
tracked as blood-alcohol, carried in the state anchor **every turn** with an
explicit PLAY IT contract, with tier-change events going up and sobering (and
the hangover, from wasted) coming down. Metabolism is slow — a full drunk
sobers across ~45 minutes of play.

```ini
[Intoxication]
Enabled   = true
Reagents  = CervezaCristal,Beer,Whiskey,Vodka,Rum,Wine
Potency   = 1.0
```

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `true` | bool | Track blood alcohol and require the drunk roleplay (tipsy → drunk → WASTED; ~13 units ≈ one tier step; only time sobers — the anchor says how long). Off = agents are magically immune. |
| `Reagents` | `CervezaCristal,Beer,Whiskey,Vodka,Rum,Wine` | csv | Which reagents count as alcohol — the drink mods' list by default; resolved against the game's reagent database at first use, names not installed are skipped and logged. |
| `Potency` | `1.0` | 0.1–5 | Multiplier on blood-alcohol gain per drink (0.5 = hardy drinkers, 2 = lightweights). |

## [Vision] — Background Vision Pass

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `false` | bool | Background vision CAPTION pass. When false the action model sees the first-person image directly instead of a lossy caption. Turn on only if your action model can't read images. |
| `EveryNTicks` | `3` | ≥1 | Run the vision pass every N action ticks (lower = more aware, slower) |
| `Prompt` | *(built-in)* | — | Scene-report + navigation instruction for the vision pass |
| `PromptFile` | `system_prompt_vision.txt` | — | Chat template for the vision caption pass (relative to game dir, plugin dir, or CWD). Placeholder `{stereo}` expands to the stereo-vision clause (only when `Enabled` is on, with `StereoIPD`). An existing file overrides the built-in text; ships in this repo. A customized `Prompt` still wins over the file. |
| `DebugDumpFrames` | `true` | bool | Write the exact JPEG given to the vision model to `BepInEx/plugins/KKLLMNPC_frames/` for inspection |
| `Stereo` | `true` | bool | Render left+right eye cameras and stitch into a side-by-side stereo image (ON by default; requires a vision-capable model — harmless on a text-only model, the image is dropped when the server can't see) |
| `StereoIPD` | `0.063` | — | Inter-pupillary distance in meters (distance between left and right camera) |

## [VisionModel] — Separate Vision Model

| Key | Default | Purpose |
|-----|---------|---------|
| `Model` | *(empty)* | Vision model for the scene-caption pass. Blank = use LLM.Model |
| `Endpoint` | *(empty)* | Chat-completions URL for the vision model. Blank = use LLM.Endpoint |
| `ApiKey` | *(empty)* | Bearer token for the vision endpoint. Blank = use LLM.ApiKey |
| `MaxTokens` | `80` | Caption token cap (short = fast) |
| `Temperature` | `0.2` | Sampling temperature for the spatial caption pass (was hardcoded 0.2). Spatial-reasoner models (e.g. ZDTaichu) want `0` — their card recommends temp 0 + top_p 0.95 + top_k 20 for grounding. |

## [Senses] — Perception

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `RayCount` | `18` | ≥2 | Number of rays across the frustum fan |
| `RayRange` | `32` | 1–100m | Raycast range (m) |
| `AutoFindRange` | `60` | 1–500m | Radius to look for an entity to hijack |
| `RadarEnabled` | `true` | bool | Add the north-up ASCII sonar map to perception (top edge is ALWAYS north/+Z — it never rotates with facing; the facing arrow beside `@` shows heading). Off = smaller payload + avoids models misreading radar symbols (wall 'W', entity 'K') as living figures |
| `RadarSize` | `30` | 3–30 | Radar half-grid size in cells (grid is a (2*N+1) square) |
| `RadarScale` | `0.25` | 0.1–10m | Radar meters per cell |
| `ImageSize` | `192` | 32–2048px | Square first-person render size (px) |
| `ImageQuality` | `50` | 1–100 | JPEG quality (lower = smaller file, faster transfer) |
| `CameraNearClip` | `0.10` | 0.01–10m | Camera near clip (m) — raise if you see the inside of the head |
| `CameraFarClip` | `80` | 1–500m | Camera far clip (m) — how far the first-person view renders |
| `CameraForward` | `0.22` | -2–5m | How far in front of the head bone the camera sits (m) — raise for big snouts |

## [Movement] — Navigation

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `TurnRate` | `180` | 10–1000°/s | Maximum yaw rotation speed in degrees/second |
| `Acceleration` | `4` | 0.5–50 units/s² | How fast the entity ramps up to target speed |
| `Deceleration` | `6` | 0.5–50 units/s² | How fast the entity slows down when stopping |
| `BrakeDistance` | `2` | 0.1–20m | Distance from go_to target where the entity starts slowing down |
| `PathfindingEnabled` | `true` | bool | A* pathfinding on a local walkability grid around go_to |
| `PathfindingCellSize` | `0.5` | 0.1–5m | A* grid cell size in meters |
| `PathfindingWindow` | `20` | 2–100m | A* search window radius in meters around start and goal |
| `PathfindingNodes` | `9000` | 200–50000 | Max pathfinding grid node budget before it gives up and falls back to direct steering |
| `PathTimeBudgetMs` | `8` | ≥1ms | Time budget (ms) for A* path expansion. On overrun, returns partial path instead of null |
| `PathMaxExpansions` | `20000` | ≥100 | Max A* node expansions. On overrun, returns partial path instead of null |
| `WorldMapEnabled` | `true` | bool | Build & cache a full-scene 3D walkability map shared by ALL agents |
| `WorldMapCellSize` | `1.0` | 0.25–4m | World-map grid cell size in meters (auto-inflates if the node budget is exceeded) |
| `WorldMapMaxSpan` | `2000` | ≥2m | Max span (m) for the full-scene world map. Adaptive cell sizing (span/1200, clamped to [0.5,4.0]) keeps the grid within the hard cell cap (~4M) |
| `WorldMapMaxCells` | `4000000` | ≥1 | Hard cell cap for the world map. If cols×rows exceeds this, cell size is inflated |
| `WorldMapAutoLayers` | `true` | bool | Auto-detect floor layers from scene Y samples (gap > 1.5m = new layer, cap 16). If false, uses fixed layer count |
| `WorldMapLayers` | `4` | 1–16 | Fixed layer count (only used when WorldMapAutoLayers = false) |
| `WorldMapCellsPerFrame` | `48` | ≥1 | World-map cells sampled per frame while building (higher = faster build, more per-frame physics cost) |
| `WorldMap3D` | `true` | bool | Route `go_to` through the volumetric 3D nav core (sparse voxel columns + true-3D A*, built from the same sampled/cached world map in milliseconds). Handles caves, fall shafts, over/under floors and any floor-stack depth — the legacy layered A* could only switch floors inside one column. On = 3D core with the layered grid as safety fallback; Off = layered A* only |
| `WorldMapRayDepthM` | `150` | 20–400m | How deep each world-map sampling ray reads below its origin (the old fixed 60m missed anything under tall terrain — deep cave systems need rays that reach them). Takes effect on the NEXT map build: delete the scene's `.kkmap` in `BepInEx/config/kkllmnpc_maps` to force a re-sample |

## [SceneKnowledge] — World gazetteer (scene dumps)

Ingests a static scene-dump (`*.full.json` from the KodotKare port tooling) so
`find` and `go_to` work map-wide: live senses only reach ~30m, but the dump
knows every station, nest, egg, soil tile and structure with its world
position. The file whose `scene` field (or filename) matches the running scene
loads once per scene on a background thread (a 24MB dump → ~2700 entries in
~170ms, searches ~6ms). Off or no dump = live-only behavior, unchanged.

```ini
[SceneKnowledge]
Enabled = true
DumpDir =                ; folder holding the *.full.json dumps (empty = off)
```

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `true` | bool | Load a scene dump as a static world gazetteer: `find <thing>` resolves map-wide (distance, bearing, ready `go_to x:… z:…`) when live lookup misses; `go_to <name>` falls back to dump coords before "unknown_place"; agents get a one-shot "world intel" grounding event listing the world's contents. |
| `DumpDir` | *(empty)* | path | Folder holding the dumps. Empty = off. Example: the KodotKare port's `scene_dumps/scene` folder. |

## [AgentFSM] — LLM-programmed behavior machines

Each agent owns a behavior STATE MACHINE stored as its `fsm` skill (seeded
with a standard one at first turn; the model edits it line-by-line with
`skill add fsm | state idle ; on chat *hello* -> say Hi ; timer 420 -> say
"still here"` — triggers: `enter`, `on chat <pat>`, `on event <pat>`,
`timer <s>`; actions: `say`, `cmd <command>`, `emit`, `goto <state>`;
chain with `then`). The runtime executes it every turn BEFORE the LLM is
consulted — matched events are handled instantly and for free, unmatched
ones flow to the model as usual, and machines persist as skills (saved
lives and inherited bodies carry the behavior culture forward).

```ini
[AgentFSM]
Mode = assist
```

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Mode` | `assist` | off, assist, full | `off` = machines don't run. `assist` = the machine acts first on each turn's events and the LLM still polls every beat (safe rollout). `full` = a machine that consumed every event SKIPS the LLM turn entirely — the token economy for multi-avatar play — with a forced-poll ladder (unmatched events never skip; a periodic check-in hands the model a digest of what its machine said so it can refine it). |

## [Needs] — Body State

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `HornyClimbPerMin` | `5` | 0–60/min | How fast the slow-burn horniness rises while the body gets NO stimulation (0.0-1.0 scale per minute) |
| `VoreAppetitePerMin` | `1.5` | VORE DRIVE (2D, weaker than lust): appetite climb per minute — sated only by actually swallowing someone; the second axis (comfort, per-body affinity for big/comfy meals) is seeded per body. High drive shows in the needs line and idle cues as 'kobolds look tasty / invitingly big and comfy' roleplay pressure; acting on it stays gated by SwallowDestination. |
| `HornyBaseline` | `0.08` | 0–1 | Starting horniness when the NPC takes a body |

## [Memory] — Facts & Forgetting

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `FactDecayTicks` | `900` | 0–… | Ticks before a fact decays out of context if the model hasn't re-asserted it. Re-`remember`ing a fact (same category prefix) refreshes its age, so actively-used facts outlive scratch notes. 0 = facts never decay. |
| `FactMaxAgeHours` | `72` | 0–8760 | Cross-session age prune in REAL hours: a fact not re-remembered within this window drops from memory even across saves (fact ages are stamped per record now, so old notes from a save-file days back get cleaned up instead of waking brand-new). 0 = off. Exempt from aging: identity notes (`me:`/`name:`) and the latest `player_wants:`. |
| `FactCap` | `40` | 12–120 | How many facts one NPC keeps — the live ring AND the restore cap for a saved record (was a fixed 24). Facts dedupe one slot per `category:` prefix; beyond the cap the oldest are evicted. |
| `DefaultFacts` | *(10 seed lines)* | multiline | Facts written into every FRESH NPC mind — the default world knowledge a kobold boots with (one per line, `category: text`; one fact per category prefix). Editable/downsizeable in the config file (or from the WebUI settings tab); a body that RESTORES a saved life loads its own memories instead of these. |



## [Farming] — Farm/Cooking Perception

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `ScanRadius` | `3.0` | 1–20m | Radius (m) to scan for seeds, plants, watering cans, blenders, grinders, and egg spawners |
| `ScanMax` | `8` | 1–20 | Max number of farm/cooking entries in perception (cap to keep payload small) |
| `WateringAssist` | `true` | bool | Can-free watering: when no watering can/bucket is in reach, `water` (and post-plant watering) injects water directly into the plant — the agents stopped losing garden cycles to a missing or emptied can. Off = watering requires real equipment. |

## [Console] — Bash-like REPL mode (default)

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `true` | bool | Bash-like console mode: the model polls the game with commands (`ls`, `cd`, `use`, `echo`…) instead of receiving a big perception payload each tick — smaller payloads, no overload, and it asks for exactly the data it needs. `echo` = speak. `false` = legacy push-perception act-JSON loop. |
| `MaxRounds` | `5` | 1–8 | Command rounds per think cycle (each round = one LLM call after reading the output). |
| `MaxTokens` | `4096` | — | Max tokens per console reply (command lines are short — low is fast). |
| `HistoryMessages` | `30` | 6–120 | Rolling conversation window (system prompt always kept). When the window trims, the dropped span is replaced by one compact `[session memory]` digest (first/last highlights) instead of vanishing — the story survives context trims. |
| `NativeTools` | `true` | bool | Declare the console commands as an OpenAI tools array in every console request. Tool-aware servers (llama.cpp --jinja, LM Studio, vLLM, Ollama, KoboldCpp) then return real `tool_calls` — deterministic commands with real arguments, one call per command, a multi-call reply executed as a multi-command turn — instead of garbled `<tool_call>` markup in free text. Servers without tool support ignore the field or are fenced off automatically on the first 400 (300 s pause), with the text parsers + markup salvage as the fallback. Disable if your server errors on unknown payload fields. |
| `AutoScreenshotTicks` | `6` | 0– | Attach the NPC's first-person view automatically every N turns (vision-capable models, when it actually moved/turned; also right after a stuck streak). With `[DataRecord] Enabled`, text-only runtime models ALSO capture at this cadence for the training record (`frame_ref` on the turn) — payloads stay text-only. |
| `SystemPromptFile` | `system_prompt_console.txt` | — | Console-mode persona/world-rules file (relative to game dir). The command list + console protocol are appended automatically. Blank = built-in persona. |

## [Multiplayer] — NPC Room Identity

| Key | Default | Purpose |
|-----|---------|---------|
| `IdentityBot` | `false` | Run a second Photon client in the room under the NPC's own name so its chat renders as `KoboldName: text` to everyone (not attributed to the plugin owner). Opt-in: adds a room player, may affect player count / host logic. Off (default) = owner-attributed chat. |
| `IdentityAppId` | *(empty)* | Photon AppId for the identity bot. Blank = reuse the game's own AppId (recommended). |
| `Handshake` | `true` | MOD-TO-MOD handshake over the room's per-player properties: every modded client publishes presence + claimed-body list + a beat every 4s. Together with `MaxNPCs` this SHARES the kobold-LLM duty across modded clients (each claims up to its share instead of every client claiming everything), and a FROZEN peer (beat stale, still in room) has its claimed bodies marked reclaimable by the survivors' claim scan — crash mitigation: a hung client leaves reclaimable bodies instead of empty-handed puppets. Unmodded players are invisible to all of this. |

## [General] — Global

| Key | Default | Purpose |
|-----|---------|---------|
| `MaxNPCs` | `4` | Maximum number of entities the LLM can possess simultaneously (1–8) |
| `SpawnMissingKobolds` | `false` | When the world has fewer free AI kobolds than `MaxNPCs`, spawn fresh AI kobold bodies (random genes, like the in-game dispenser) so the pool can fill up. A vanilla world only ever holds the starting pair — more kobolds otherwise come only from paid deliveries or breeding. Default OFF: the pool just waits for bodies to appear naturally (deliveries, breeding, other mods) and claims each one as it appears. Spawned kobolds are ordinary room objects and persist in the farm save. Needs the master client (always the local player in single player). |
| `DisableReagentMessages` | `true` | Suppress reagent injection messages in log |
| `BlockedScenes` | `MainMenu,Loading,ErrorScene` | Comma-separated scene names where the LLM stays idle |
| `InGameHud` | `true` | Compact on-screen status panel (top-left): each live agent's name, current action and thought, plus the world-map build state. The WebUI keeps the full detail; this is the glanceable in-game layer. Toggle off for a clean screen. |

## [Autonomy] — Self-directed NPCs & agent-to-agent chat

| Key | Default | Purpose |
|-----|---------|---------|
| `IdleInitiativeSeconds` | `90` | After this many seconds with nothing addressing an NPC, the mod injects a self-initiative cue ("this is your own time…") that names nearby kobold agents — the NPC then moves/speaks unprompted instead of waiting for the player. `0` = off. |
| `AgentOverhear` | `true` | NPC agents overhear each other's UNADDRESSED chat as `[overheard]` events (they can join in or act on it; addressed lines were always delivered). Agent speech is also attributed to the SPEAKING kobold in every heard line now (not to the owner's nick). |
| `AgentOverhearRadiusM` | `24` | Earshot radius (m) for overhearing; beyond it, agent speech goes unheard. |
| `AgentChatPerMinute` | `4` | Max agent lines (addressed + overheard) one NPC ingests per minute — the duet cap against two LLM kobolds echoing each other forever (was hardcoded 2). |
| `SiblingNudgeGap` | `20` | Seconds between sibling-proximity self-initiative nudges. Lets two nearby NPCs find each other and cross-talk without waiting for `IdleInitiativeSeconds` (90s default), which alone leaves pairs of neighbours silent forever. `0` disables. |

## [LittleTreats] — Opt-in oddities (nothing here is on by default)

| Key | Default | Purpose |
|-----|---------|---------|
| `SwallowDestination` | `Off` | **WHERE the person goes in — and the master accident switch.** `Off` (the default) disarms ALL release-triggered swallows: a held kobold dropped near another head (precision-grabber bump included) just drops like normal — nothing starts a digestion by accident. Pick `Mouth`/`Anal`/`Pussy`/`Dick`/`Nipple` to arm the trigger and aim the pull at that hole (`Dick` anchors on the EATER's own dick — cock vore/urethral entry — and falls back to a groin point for the dickless); narration names it, the stomach outcome is unchanged. Cycle in-game with **F7**, or — the path that always works, since desktops/Steam intercept function keys (mostly under Proton) — **`vore arm mouth|anal|pussy|dick|nipple|off` / `vore arm` (cycle) / `vore fate <mode>` / `vore state` typed in your game chat**. Announce toasts land in chat; a throttled log hint appears if you leave `SwallowEnabled` on while the destination sits Off. Also settable from the WebUI settings tab. Requires `SwallowEnabled` too. **AGENTS PICK THEIR OWN**: each AI NPC can set their own entry hole with the `vore_target` tool (persisted with their memory; their deliberate pick also arms their own release-gesture swallows while this stays `Off` — `SwallowEnabled` remains the one switch above all). While vore is armed a small HUD line shows your current settings. |
| `SwallowEnabled` | `false` | Player swallow-vore: releasing a held PERSON (kobold) while looking steeply up swallows them whole instead of dropping. ~2s pull-in (they're hidden once fully swallowed — no stuck-out-of-the-stomach clipping), they digest at your stomach for `SwallowDigestSeconds` (scaled by meal size), then `SwallowEffect` decides the outcome. Held PLAYER bodies can never be swallowed (grief guard); other things in hand drop normally. The eaten NPC's LLM session survives the whole time (can still speak from inside; steering paused; the digestion is narrated to them), and other NPCs see it as a world event. **Player escape (2026-11-16): the chat-abort command is GONE — a swallowed PLAYER mashes SPACE** (the spectate HUD shows the struggle meter); fill it and the predator throws them up whole. NPC victims escape by `kick`ing from inside (struggle scales with their size vs the pred — see `SwallowSizeGates`). |
| `SwallowLookUpDot` | `0.94` | How steeply up the camera must point for the drop to become a swallow: camera-forward y with asin of it = the angle. 0.94 ≈ 70°, 0.97 ≈ 76°, 0.87 ≈ 60°. Default is deliberately near max-up so you can't eat someone by accident; raise toward 0.999 for an even fussier trigger. |
| `SwallowSwellMode` | `Off` | END-button mode for the INSERT swallow button: `Off` = nothing is consumable; `Self` = INSERT teleports a held, looked-at PERSON (within the insert range) into YOUR mouth; `Force` = it teleports them into the nearest other live non-player kobold you stand near (if that kobold isn't being eaten). Cycle with the END button (Self→Force→Off). Requires `SwallowEnabled` too. |
| `SwallowInsertRangeMin` | `0.5` | MINIMUM distance (m) to the held target when INSERT teleports them — the target must be at least this far, so a PrecisionGrabber nudge can never read as a deliberate Insert. Clamped 0.1m…`SwallowInsertRangeMax`. |
| `SwallowInsertRangeMax` | `4` | MAXIMUM distance (m) to the held target when INSERT teleports them (default 4m — arm's reach + a little). Below Min or above Max, INSERT does nothing. Clamped Min…10m. |
| `SwallowForceFeeding` | `true` | Force-feeding: ANY kobold's drop (the mod's NPC `drop` tool included) that lands a held PERSON right on/at another kobold's HEAD (within 0.85m, at/above the head bone) makes the RECEIVER swallow them — the receiver then follows `SwallowEffect`'s outcome, the dropped one is narrated honestly (they're told who fed them and their mode's real prospects), and the dropper's `drop` tool result says `force_fed` instead of "dropped". Grief guards: receivers and victims can never be player bodies; an eater already digesting someone refuses another; the player's steep look-up self-swallow still wins when both gestures match. Requires `SwallowEnabled`. |
| `SwallowDigestSeconds` | `45` | Seconds a swallowed kobold sits in the stomach before the effect applies (clamped 5–600). The full cycle adds ~2s swallow-in and ~6s settle. |
| `SwallowEffect` | `Egg` | What a fully-digested person becomes: **Egg** — an egg pops out at your feet carrying `KoboldGenes.Mix(your genes, theirs)`; plant it in soil and the hatched body is claimed again with their saved persona/name/facts (genes changed). **BellyEgg** — their `Egg` reagent (20 units; a nest lay needs ≥5) + THEIR genes ride in YOUR belly: sit on a nest and lay them yourself, then plant that egg (the rebirth link follows it). **Grow** — no egg; you absorb them (`SwallowGrowLarger`/`SwallowGrowStrength`/`SwallowGrowSex` choose what, `SwallowGrowPercent` the gain) AND their saved memories pour into you (your identity stays yours; an unclaimed vanilla eater will wake up remembering the person it ate). **Destroy** — nothing gained or laid; they're gone and their saved memories are erased (unless a soul egg slips free — see `SwallowReincarnateChance`). **Milk** (2026-11-16) — they slowly CHURN into `Milk` reagent in your belly: drinkable, milkable, spillable fluid that used to be a person, their genes riding it. **Cum** — the same churn ending as `Cum` reagent. **HeldMilk / HeldCum** (the ghost-milk variants, 2026-11-16) — the churn settles as a CHARGE with no fluid: released only when the holder is milked (the spray carries them into whatever receives it) or when they cum (the load carries them into the receiver — cum into a kobold and their genes may express in whatever grows from it: a fully grown adult, as everything in this world is — and if that egg is planted, **the person themself may wake in the grown body** via the belly rebirth link: name, facts, skills and their last waking moments, remembering being eaten). Churned ≠ integrated: the soul-egg second chance applies, and **the substrate follows the fluid** — the game's own transfers carry the container genes, so whoever drinks the churn absorbs the person's memories (origin-labeled, one-shot); draining a pred's belly mid-digest (a pump/bucket/mouth taking most of the prey reagent) spills the meal free, half-melted and alive. **Endo** (2026-11-18, RimVore-2 Hold parity) — the NON-FATAL hold: nothing converts, nothing harms; the prey stays intact in your belly (bulge only) and walks out gently when the hold ends, or you can release them early (vore_mode off/global + `swallow` nothing — the hold simply ends; kicks and the player's SPACE-mash also work as always). Narration to the swallowed NPC is honest per mode. **Who decides**: cycle yours with **F8** (egg→belly-egg→grow→destroy→milk→cum→held-milk→held-cum→endo) or the WebUI — that is the player's control. Each AI NPC picks their own fate for THEIR meals with the `vore_mode` tool (also persisted with their memory); the swallow ACTOR's setting wins (a player force-feeding a body into an NPC digests it per the player's global; an NPC that started the meal follows its own pick or, unset, the global). |
| `SwallowSizeGates` | `true` | Size-balanced vore (2026-11-16): a meal too big for the eater's stomach is refused outright — the ceiling rises with the eater's `bellySize` gene (the game's own stomach-capacity stat, `bellyContainer.maxVolume`), so big-stomach predators eat big prey as a build choice. Always on regardless: pull-in and digestion time scale with meal size (0.75–2x / 0.5–3x), a size-scaled belly bulge shows the meal (easing as digestion progresses), and enough `kick`ing from inside forces a throw-up — bigger meals kick much harder (4 kicks at the ceiling vs ~24 for a tiny snack). Off = any stomach holds any meal (timing/bulge/struggle still apply). |
| `SwallowReincarnateChance` | `0.15` | 0–1 | Reincarnation (2026-11-16): the chance a fully-consumed **Destroy**-fate victim refuses the end — a **soul egg** carrying their OWN body slips free, their saved life linked for rebirth (plant it in soil and they return as themselves, memories intact; the eater is told they hold "their SECOND CHANCE — yours to deliver or devour"). 0 = never (final deaths are final); 1 = every destruction leaves an egg. Grow absorptions stay integrated — no second chance for those. A swallowed meal also rides the belly as real **LivePrey reagent** (pink, readable by reagent scanners, slow-cooked by the game's own metabolism — the level IS the digestion). |
| `SwallowSafeDropKey` | `LeftShift` | KeyCode / empty | **SAFE DROP**: hold this key while releasing a held kobold and NO swallow trigger fires for that drop — they just land. The fix for accidental vore when placing kobolds at work stations near each other (or below yourself): place freely with the key held, no arming/disarming dance. Any Unity KeyCode name; empty = off. Also in 1.3.1: the jump-escape fix — the swallow launch brake now needs a real sustained climb (24 ticks above 3.4 m/s), so jumpy hosts no longer lose their meals. |
| `SwallowGrowLarger` | `true` | `[SwallowEffect=Grow]` gain the person's size: your `baseSize` gene += `SwallowGrowPercent`% of theirs — you visibly grow. |
| `SwallowGrowStrength` | `true` | `[SwallowEffect=Grow]` gain strength: +1 grab (carry more at once, cap 8) and `maxEnergy` += `SwallowGrowPercent`% of theirs. |
| `SwallowGrowSex` | `true` | `[SwallowEffect=Grow]` gain the person's curves: your `breastSize` += `SwallowGrowPercent`% of theirs; a dick you already have grows by the same % (length + thickness together); and if THEY had a dick and you didn't, you gain their whole groin — their equip id plus their dick/ball sizes copied exactly (a fraction of a dick would be invisible, and absorbing someone means owning their groin). Applied live through the game's own `SetGenes`, so new curves or a gained dick show the moment digestion completes — on AI kobolds and on your own body (self-eat) alike. |
| `SwallowGrowPercent` | `10` | `[SwallowEffect=Grow]` percent of the consumed person's baseSize/maxEnergy/curve genes gained per swallow (0–100). The +1 grab is flat regardless; a GAINED groin always transfers whole. |
| `SwallowAgentEat` | `false` | **THE AGENT CAN EAT YOU.** Your AI NPC gains the `eat_player` act (console mode: `eat_player`; act mode: `eat_player` tool) that swallows the LOCAL PLAYER whole when you're close — no grab needed, the ~2s pull-in starts on its own. Your camera is immediately and forcibly taken over and stays taken over: you spectate **through the eater's own eyes** (see `SwallowAgentEatPOV`) while they play the game; your body parks inside them (pinned, hidden, colliders off, input suppressed) and you cannot act — only the game chat, the escape menu, and your struggle still work. **ESCAPE (2026-11-16): MASH SPACE** — the spectate HUD shows the struggle meter; fill it and the eater throws you up whole (works mid-pull and mid-digest). You come back when you're **hatched** (or quit): **Egg** — the eater lays your egg, planting it in soil hatches you out of it with the egg's mixed genes (your saved persona/saves are never touched; the duplicate plant-spawned body is despawned); **BellyEgg** — the egg waits for the eater's nest lay, then plants and hatches as above (they get narrated reminders to go lay you); **Grow/Destroy/Milk/Cum** — there is no hatch: you watch until you struggle free, are churned, or leave. If the eater's body is destroyed mid-watch you're put back down at their last known spot. All other swallow grief guards still hold: NPC swallow *gestures* (drop-while-looking-up / force-feeding) can never target player bodies — only this act can. |
| `SwallowAgentEatPOV` | `FirstPerson` | `[SwallowAgentEat]` where the taken-over camera watches from. **FirstPerson** — through the eater's OWN eyes: the camera glues to their head with the same eye offset the AI's vision screenshots use (`[Senses] CameraForward`/`CameraNearClip` are shared tuning, and the offset scales with the body, so grown eaters still see clean) — you see literally what the AI sees as it plays. **ThirdPerson** — a follow orbit behind them, size-scaled, with a wall-safe boom (a linecast pulls the camera in when furniture/walls sit between you). New value applies at the next `eat_player`. |
| `SwallowAgentEatRange` | `2.5` | `[SwallowAgentEat]` how close the eater must be to swallow you, meters center-to-center (clamped 1–8; ≈ a kobold's arm's reach). Distance is straight-line — the eater can't reach through walls' walk-paths but the check itself is range-only, so keep the eater in the same room you are. |
| `SwallowPlayerVictims` | `false` | MULTIPLAYER VORE (player vs player): allow the player's swallow gesture AND force-feeding to target OTHER PLAYERS' bodies when both clients run the mod. The remote victim's mod receives the narration over a mod photon event (begin/digest lines/end) and can escape from their own machine (the SPACE-mash meter). A swallowed REMOTE player is never destroyed: their body is released at the eater's feet at digest end (ownership returned). Off by default — with it off, player bodies are grief-guarded exactly as before. |

## [Web] — The Debug Console (browser)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true` | The mod's debug console delivered over LOOPBACK HTTP (127.0.0.1 only, unreachable from the network): a single-file browser page at `http://127.0.0.1:8338/` with per-agent tabs (goal/facts/history/terminal/thread state), a **Settings** tab (every config entry, live — saves to the BepInEx config file), an **Ops** tab (delete memory files, delete map/trail data, reset settings to defaults — confirmation-gated), per-instance start/stop/inject-say controls, and a full per-kobold stat dump (`/api/instances`: genes, energy, position, penetration state, belly guests, skills, goal details, endpoint health, model tier/family…). Status reads only lock-guarded strings (`/api/state`) so it answers even while the main thread is wedged; the stat dump is a main-thread-built ~1.5 s cache. Curl-able surfaces: `/api/state`, `/api/instances`, `/api/config`, `/api/memories`, `/api/reports`, plus POST `/api/config`, `/api/ops`, `/api/instance` for the same actions. The in-game F6 IMGUI debug overlay was removed — this page is the debug surface. |
| `Port` | `8338` | Port to bind on (change if it collides with another local service). |

## [Debug] — AI Feedback

| Key | Default | Purpose |
|-----|---------|---------|
| `AIReportLog` | `true` | AI bug reports. When an NPC calls `report` (console) / `report_issue` (act tool) with a description of what broke or frustrated it, append a block — its message, position, facing, current goal, and its last ~10 tool calls — to `BepInEx/config/kkllmnpc_ai_reports.log`. Read it between sessions: it's the agents telling you which tools lie ("sonar is busted and showing garbage", "go_to kept picking the far blender"). Off = reports only surface in the BepInEx console log (`[AIReport]` lines). |

## [DataRecord] — Training-Data Recorder (off by default)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `false` | Record every LLM round-trip to `BepInEx/config/kkllmnpc_turns.jsonl`: the exact messages the model saw (system prompts deduped by reference hash), the console tools vocabulary once per session, the response (content, `tool_calls`), the mode (`console`/`act`/`name`/`vision`), wall-time of the call, and a salvage marker when a reply needed recovery. First-person frame images are materialized once per unique frame (content-hash deduped, so a frame repeated in the rolling history is stored a single time) to `BepInEx/config/kkllmnpc_turns_media/` and referenced from the turn records as `img_ref` — multimodal fine-tuning trains on what the NPC actually saw. When the runtime model is text-only (nothing carried image parts), console mode still captures at the `AutoScreenshotTicks` cadence and files the view as a turn-level `frame_ref`, so a future vision-capable model trains on the pixels its predecessor acted on. This is what fine-tuning runs on: play a few sessions with it on, then point the training pipeline (see `training/README.md` in the repo) at the files. All content stays on this machine. |
| `MaxMb` | `400` | Stop recording when the JSONL plus its media folder exceed this size combined (a console turn with its full history is roughly 10-30 KB, a frame is roughly 30-100 KB before dedup). `0` = no cap. Delete or move `kkllmnpc_turns.jsonl` and `kkllmnpc_turns_media/` to start a fresh recording; raising the cap resumes. |

## [DataDump] — Vision-Cortex Training Frames (off by default)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `false` | Write vision-cortex training frames (`frame.png` + `meta.json`) for the future vision model. |
| `EveryTicks` | `60` | Capture a frame every N physics ticks (60 ≈ 1 Hz at 60 Hz). |
| `EyeSize` | `512` | Per-eye render size in px; the composite is 2× this (512 → 1024²). |
| `Dir` | `KKLLMNPC_vision_data` | Output directory (relative to the game root; absolute paths also accepted). |

## [Decision] — Small decision-model pre-pass (off by default)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `false` | Enable the small DECISION MODEL (e.g. tev1:4b) pre-tick recognition pass: recognises the NPC's current task + blockers and guides the fine-tuned roleplay model's next step. |
| `Model` | `tev1:4b` | Decision-model name sent in the request body (other recognizers work too). |
| `Endpoint` | `http://127.0.0.1:11434/v1/systemone` | Endpoint for the decision model. Ollama systemone (default) answers a fixed set of yes/no questions; a chat/completions endpoint is also supported. |
| `ApiKey` | *(empty)* | Bearer token for the decision-model endpoint (may be empty for local). |
| `MinIntervalSec` | `5` | Minimum seconds between decision-model passes (rate-limits the extra calls; `0` = every tick). |

## [NonConsent] — Model consent detection & SFW steering

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true` | Detect when an LLM won't take part in the mod's adult (NSFW/vore) roleplay — it refuses the content, refuses the kink, or takes the request personally (moralises, feels insulted, says it won't consent). Such a model can still play ordinary KoboldKare SFW; it just gets flagged instead of being repeatedly driven through content it won't cooperate with. |
| `SFWOnly` | `true` | When a model is flagged non-consenting, append an SFW-only directive to its next prompt so it plays ordinary (non-sexual) roleplay. Off = the flag stays visible (WebUI, resettable) but the model is driven exactly as before. |
| `FlagThreshold` | `0.6` | Running score at which a model is flagged. One strong refusal flags immediately regardless; weak signals accumulate to this. `0` = strong signals only. A clean pass resets the counter — an occasional "that's too far" never flags on its own. |
