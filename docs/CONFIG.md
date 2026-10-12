# Complete Configuration Reference

All config entries are in `BepInEx/config/com.kk.llmnpc.cfg`.

## [LLM] — The Planner

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Endpoint` | `http://127.0.0.1:11434/v1/chat/completions` | — | OpenAI-compatible chat completions URL. LM Studio default: `http://127.0.0.1:1234/v1/chat/completions` |
| `Model` | `local-model` | — | Model name to request. LM Studio: use the exact model name from the Developer tab. KoboldCpp: `local-model` works. |
| `ApiKey` | *(empty)* | — | Bearer token (may be empty for local servers) |
| `SystemPrompt` | *(built-in)* | — | Override the default system prompt. A customized value always wins; otherwise `SystemPromptFile` is used if readable. |
| `SystemPromptFile` | `system_prompt_default.txt` | — | Path to the full system prompt (relative to game dir, plugin dir, or CWD). Overrides the built-in prompt; ships in this repo. |
| `ThinkInterval` | `0.4` | 0.05–10s | Seconds between perception/decision cycles |
| `Thinking` | `auto` | `auto`, `off` | Control the model's hidden reasoning (chain-of-thought). `off` requests suppression so answers start immediately — the biggest latency win on reasoning families, which otherwise burn their whole token floor on invisible analysis. Sends `think=false` (Ollama), `chat_template_kwargs {enable_thinking:false}` (llama.cpp / vLLM / SGLang, Qwen3-style templates) and `reasoning_budget=0` (llama.cpp hybrid models); servers that don't recognize a field skip it. LM Studio / KoboldCpp expose the toggle in their per-model GUI instead. Leftover reasoning markers are still stripped client-side. |
| `MaxTokens` | `1024` | 64–32768 | Max response tokens (reasoning models burn tokens on analysis before the action — too low and the action dies mid-JSON). Reasoning families automatically get a floor of `1024 + headroom` (Qwen/Bonsai/GLM +512 · Nemotron/Granite +768 · deepseek-style reasoners +1024 · unknown +256); your explicit value always stands when it is higher. |
| `Temperature` | `0.3` | 0–2 | Sampling temperature (lower = faster, more deterministic) |
| `PlanStepDelay` | `0.35` | 0.05–5s | Seconds between each action in a chained plan |
| `PlanMaxSteps` | `8` | 1–8 | Max actions the model may queue in one response (hard cap) |
| `SendImage` | `true` | bool | Attach a first-person JPEG to the action call when useful (vision model required) |
| `ImageEveryNTicks` | `6` | ≥1 | Baseline: attach an image every N ticks even without a trigger |
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

## [Vision] — Background Vision Pass

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `false` | bool | Background vision CAPTION pass. When false the action model sees the first-person image directly instead of a lossy caption. Turn on only if your action model can't read images. |
| `EveryNTicks` | `3` | ≥1 | Run the vision pass every N action ticks (lower = more aware, slower) |
| `Prompt` | *(built-in)* | — | Scene-report + navigation instruction for the vision pass |
| `PromptFile` | `system_prompt_vision.txt` | — | Chat template for the vision caption pass (relative to game dir, plugin dir, or CWD). Placeholder `{stereo}` expands to the stereo-vision clause (only when `Enabled` is on, with `StereoIPD`). An existing file overrides the built-in text; ships in this repo. A customized `Prompt` still wins over the file. |
| `DebugDumpFrames` | `true` | bool | Write the exact JPEG given to the vision model to `BepInEx/plugins/KKLLMNPC_frames/` for inspection |
| `Stereo` | `false` | bool | Render left+right eye cameras and stitch into a side-by-side stereo image (requires vision-capable model) |
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
| `RayCount` | `9` | ≥2 | Number of rays across the frustum fan |
| `RayRange` | `25` | 1–100m | Raycast range (m) |
| `AutoFindRange` | `60` | 1–500m | Radius to look for an entity to hijack |
| `RadarEnabled` | `true` | bool | Add the north-up ASCII sonar map to perception (top edge is ALWAYS north/+Z — it never rotates with facing; the facing arrow beside `@` shows heading). Off = smaller payload + avoids models misreading radar symbols (wall 'W', entity 'K') as living figures |
| `RadarSize` | `10` | 3–30 | Radar half-grid size in cells (grid is a (2*N+1) square) |
| `RadarScale` | `1.2` | 0.1–10m | Radar meters per cell |
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

## [Needs] — Body State

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `HornyClimbPerMin` | `5` | 0–60/min | How fast the slow-burn horniness rises while the body gets NO stimulation (0.0-1.0 scale per minute) |
| `HornyBaseline` | `0.08` | 0–1 | Starting horniness when the NPC takes a body |

## [Memory] — Facts & Forgetting

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `FactDecayTicks` | `900` | 0–… | Ticks before a fact decays out of context if the model hasn't re-asserted it. Re-`remember`ing a fact (same category prefix) refreshes its age, so actively-used facts outlive scratch notes. 0 = facts never decay. |

## [Farming] — Farm/Cooking Perception

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `ScanRadius` | `3.0` | 1–20m | Radius (m) to scan for seeds, plants, watering cans, blenders, grinders, and egg spawners |
| `ScanMax` | `8` | 1–20 | Max number of farm/cooking entries in perception (cap to keep payload small) |

## [Console] — Bash-like REPL mode (default)

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `true` | bool | Bash-like console mode: the model polls the game with commands (`ls`, `cd`, `use`, `echo`…) instead of receiving a big perception payload each tick — smaller payloads, no overload, and it asks for exactly the data it needs. `echo` = speak. `false` = legacy push-perception act-JSON loop. |
| `MaxRounds` | `3` | 1–8 | Command rounds per think cycle (each round = one LLM call after reading the output). |
| `MaxTokens` | `256` | — | Max tokens per console reply (command lines are short — low is fast). |
| `HistoryMessages` | `30` | 6–120 | Rolling conversation window (system prompt always kept). When the window trims, the dropped span is replaced by one compact `[session memory]` digest (first/last highlights) instead of vanishing — the story survives context trims. |
| `NativeTools` | `true` | bool | Declare the console commands as an OpenAI tools array in every console request. Tool-aware servers (llama.cpp --jinja, LM Studio, vLLM, Ollama, KoboldCpp) then return real `tool_calls` — deterministic commands with real arguments, one call per command, a multi-call reply executed as a multi-command turn — instead of garbled `<tool_call>` markup in free text. Servers without tool support ignore the field or are fenced off automatically on the first 400 (300 s pause), with the text parsers + markup salvage as the fallback. Disable if your server errors on unknown payload fields. |
| `AutoScreenshotTicks` | `6` | 0– | Attach the NPC's first-person view automatically every N turns (vision-capable models, when it actually moved/turned; also right after a stuck streak). |
| `SystemPromptFile` | `system_prompt_console.txt` | — | Console-mode persona/world-rules file (relative to game dir). The command list + console protocol are appended automatically. Blank = built-in persona. |

## [Multiplayer] — NPC Room Identity

| Key | Default | Purpose |
|-----|---------|---------|
| `IdentityBot` | `false` | Run a second Photon client in the room under the NPC's own name so its chat renders as `KoboldName: text` to everyone (not attributed to the plugin owner). Opt-in: adds a room player, may affect player count / host logic. Off (default) = owner-attributed chat. |
| `IdentityAppId` | *(empty)* | Photon AppId for the identity bot. Blank = reuse the game's own AppId (recommended). |

## [General] — Global

| Key | Default | Purpose |
|-----|---------|---------|
| `MaxNPCs` | `1` | Maximum number of entities the LLM can possess simultaneously (1–8) |
| `SpawnMissingKobolds` | `false` | When the world has fewer free AI kobolds than `MaxNPCs`, spawn fresh AI kobold bodies (random genes, like the in-game dispenser) so the pool can fill up. A vanilla world only ever holds the starting pair — more kobolds otherwise come only from paid deliveries or breeding. Default OFF: the pool just waits for bodies to appear naturally (deliveries, breeding, other mods) and claims each one as it appears. Spawned kobolds are ordinary room objects and persist in the farm save. Needs the master client (always the local player in single player). |
| `DisableReagentMessages` | `true` | Suppress reagent injection messages in log |
| `BlockedScenes` | `MainMenu,Loading,ErrorScene` | Comma-separated scene names where the LLM stays idle |

## [LittleTreats] — Opt-in oddities (nothing here is on by default)

| Key | Default | Purpose |
|-----|---------|---------|
| `SwallowEnabled` | `false` | Player swallow-vore: releasing a held PERSON (kobold) while looking steeply up swallows them whole instead of dropping. ~2s pull-in (they're hidden once fully swallowed — no stuck-out-of-the-stomach clipping), they digest at your stomach for `SwallowDigestSeconds`, then `SwallowEffect` decides the outcome. Held PLAYER bodies can never be swallowed (grief guard); other things in hand drop normally. The eaten NPC's LLM session survives the whole time (can still speak from inside; steering paused; the digestion is narrated to them), and other NPCs see it as a world event. **Digest abort (no cheats): type "swallow abort"** (or "spit out" / "cough up" / "regurgitate" / "swallow stop" / "abort swallow") **in the game chat** — every in-flight session releases its victims alive and whole in front of you; the line is consumed by the mod, so the NPCs never hear it; works in pull phase and mid-digest, does nothing once an egg was already laid. |
| `SwallowLookUpDot` | `0.94` | How steeply up the camera must point for the drop to become a swallow: camera-forward y with asin of it = the angle. 0.94 ≈ 70°, 0.97 ≈ 76°, 0.87 ≈ 60°. Default is deliberately near max-up so you can't eat someone by accident; raise toward 0.999 for an even fussier trigger. |
| `SwallowForceFeeding` | `true` | Force-feeding: ANY kobold's drop (the mod's NPC `drop` tool included) that lands a held PERSON right on/at another kobold's HEAD (within 0.85m, at/above the head bone) makes the RECEIVER swallow them — the receiver then follows `SwallowEffect`'s outcome, the dropped one is narrated honestly (they're told who fed them and their mode's real prospects), and the dropper's `drop` tool result says `force_fed` instead of "dropped". Grief guards: receivers and victims can never be player bodies; an eater already digesting someone refuses another; the player's steep look-up self-swallow still wins when both gestures match. Requires `SwallowEnabled`. |
| `SwallowDigestSeconds` | `45` | Seconds a swallowed kobold sits in the stomach before the effect applies (clamped 5–600). The full cycle adds ~2s swallow-in and ~6s settle. |
| `SwallowEffect` | `Egg` | What a fully-digested person becomes: **Egg** — an egg pops out at your feet carrying `KoboldGenes.Mix(your genes, theirs)`; plant it in soil and the hatched body is claimed again with their saved persona/name/facts (genes changed). **BellyEgg** — their `Egg` reagent (20 units; a nest lay needs ≥5) + THEIR genes ride in YOUR belly: sit on a nest and lay them yourself, then plant that egg (the rebirth link follows it). **Grow** — no egg; you absorb them (`SwallowGrowLarger`/`SwallowGrowStrength`/`SwallowGrowSex` choose what, `SwallowGrowPercent` the gain) AND their saved memories pour into you (your identity stays yours; an unclaimed vanilla eater will wake up remembering the person it ate). **Destroy** — nothing gained or laid; they're gone and their saved memories are erased. Narration to the swallowed NPC is honest per mode. |
| `SwallowGrowLarger` | `true` | `[SwallowEffect=Grow]` gain the person's size: your `baseSize` gene += `SwallowGrowPercent`% of theirs — you visibly grow. |
| `SwallowGrowStrength` | `true` | `[SwallowEffect=Grow]` gain strength: +1 grab (carry more at once, cap 8) and `maxEnergy` += `SwallowGrowPercent`% of theirs. |
| `SwallowGrowSex` | `true` | `[SwallowEffect=Grow]` gain the person's curves: your `breastSize` += `SwallowGrowPercent`% of theirs; a dick you already have grows by the same % (length + thickness together); and if THEY had a dick and you didn't, you gain their whole groin — their equip id plus their dick/ball sizes copied exactly (a fraction of a dick would be invisible, and absorbing someone means owning their groin). Applied live through the game's own `SetGenes`, so new curves or a gained dick show the moment digestion completes — on AI kobolds and on your own body (self-eat) alike. |
| `SwallowGrowPercent` | `10` | `[SwallowEffect=Grow]` percent of the consumed person's baseSize/maxEnergy/curve genes gained per swallow (0–100). The +1 grab is flat regardless; a GAINED groin always transfers whole. |
| `SwallowAgentEat` | `false` | **THE AGENT CAN EAT YOU.** Your AI NPC gains the `eat_player` act (console mode: `eat_player`; act mode: `eat_player` tool) that swallows the LOCAL PLAYER whole when you're close — no grab needed, the ~2s pull-in starts on its own. Your camera is immediately and forcibly taken over and stays taken over: you spectate **through the eater's own eyes** (see `SwallowAgentEatPOV`) while they play the game; your body parks inside them (pinned, hidden, colliders off, input suppressed) and you cannot act — only the game chat, the escape menu, and the abort command still work. An on-screen HUD line explains where you are and what you're waiting for. You come back when you're **hatched** (or quit): **Egg** — the eater lays your egg, planting it in soil hatches you out of it with the egg's mixed genes (your saved persona/saves are never touched; the duplicate plant-spawned body is despawned); **BellyEgg** — the egg waits for the eater's nest lay, then plants and hatches as above (they get narrated reminders to go lay you); **Grow/Destroy** — there is no hatch: you watch until you type the abort command or leave. The abort ("swallow abort" etc. in game chat) works in every phase — mid-pull, mid-digest, and even once the egg is laid and planted (you're released alive and whole at wherever you were stopped; a not-yet-planted egg then hatches as a plain NPC, its rebirth link dropped). If the eater's body is destroyed mid-watch you're put back down at their last known spot. All other swallow grief guards still hold: NPC swallow *gestures* (drop-while-looking-up / force-feeding) can never target player bodies — only this act can. |
| `SwallowAgentEatPOV` | `FirstPerson` | `[SwallowAgentEat]` where the taken-over camera watches from. **FirstPerson** — through the eater's OWN eyes: the camera glues to their head with the same eye offset the AI's vision screenshots use (`[Senses] CameraForward`/`CameraNearClip` are shared tuning, and the offset scales with the body, so grown eaters still see clean) — you see literally what the AI sees as it plays. **ThirdPerson** — a follow orbit behind them, size-scaled, with a wall-safe boom (a linecast pulls the camera in when furniture/walls sit between you). New value applies at the next `eat_player`. |
| `SwallowAgentEatRange` | `2.5` | `[SwallowAgentEat]` how close the eater must be to swallow you, meters center-to-center (clamped 1–8; ≈ a kobold's arm's reach). Distance is straight-line — the eater can't reach through walls' walk-paths but the check itself is range-only, so keep the eater in the same room you are. |

## [Web] — The Debug Console (browser)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true` | The mod's debug console delivered over LOOPBACK HTTP (read-only; 127.0.0.1 only, unreachable from the network): a single-file browser page at `http://127.0.0.1:8338/` with one tab per agent (live goal/facts/history/console terminal/thread state) plus the spectate status, the mod's log tail (`/api/state` JSON includes it) and the AI-reports tail (`/api/reports`). It reads only lock-guarded strings — no Unity calls — so it keeps answering even while the main thread is wedged, which is exactly when you want it. An agent with shell access can also fetch it mid-session (`curl http://127.0.0.1:8338/api/state`). `false` = the F6 IMGUI overlay remains the only UI. |
| `Port` | `8338` | Port to bind on (change if it collides with another local service). |

## [Debug] — AI Feedback

| Key | Default | Purpose |
|-----|---------|---------|
| `AIReportLog` | `true` | AI bug reports. When an NPC calls `report` (console) / `report_issue` (act tool) with a description of what broke or frustrated it, append a block — its message, position, facing, current goal, and its last ~10 tool calls — to `BepInEx/config/kkllmnpc_ai_reports.log`. Read it between sessions: it's the agents telling you which tools lie ("sonar is busted and showing garbage", "go_to kept picking the far blender"). Off = reports only surface in the BepInEx console log (`[AIReport]` lines). |
