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
| `CommentEveryNTicks` | `5` | ≥0 | Every N ticks, invite a free 'comment' — the model voices its own take on surroundings (0 = off) |
| `CommentTemp` | `0.9` | 0–2 | Sampling temperature for free commentary |
| `ChatLogLines` | `20` | 0–200 | Feed the last N lines of the game's chat log to the model each turn (full conversation + NPC speech). 0 = off |

## [Vision] — Background Vision Pass

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `Enabled` | `false` | bool | Background vision CAPTION pass. When false the action model sees the first-person image directly instead of a lossy caption. Turn on only if your action model can't read images. |
| `EveryNTicks` | `3` | ≥1 | Run the vision pass every N action ticks (lower = more aware, slower) |
| `Prompt` | *(built-in)* | — | Scene-report + navigation instruction for the vision pass |
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
| `SwallowEffect` | `Egg` | What a fully-digested person becomes: **Egg** — an egg pops out at your feet carrying `KoboldGenes.Mix(your genes, theirs)`; plant it in soil and the hatched body is claimed again with their saved persona/name/facts (genes changed). **BellyEgg** — their `Egg` reagent (20 units; a nest lay needs ≥5) + THEIR genes ride in YOUR belly: sit on a nest and lay them yourself, then plant that egg (the rebirth link follows it). **Grow** — no egg; you absorb them (`SwallowGrowLarger`/`SwallowGrowStrength` choose what, `SwallowGrowPercent` the gain). **Destroy** — nothing gained or laid; they're gone. In the no-rebirth modes (Grow/Destroy) the consumed NPC's saved AI memories (persona/name/facts) are ERASED from disk with the body; narration to the swallowed NPC is honest per mode. |
| `SwallowGrowLarger` | `true` | `[SwallowEffect=Grow]` gain the person's size: your `baseSize` gene += `SwallowGrowPercent`% of theirs — you visibly grow. |
| `SwallowGrowStrength` | `true` | `[SwallowEffect=Grow]` gain strength: +1 grab (carry more at once, cap 8) and `maxEnergy` += `SwallowGrowPercent`% of theirs. |
| `SwallowGrowPercent` | `10` | `[SwallowEffect=Grow]` percent of the consumed person's baseSize/maxEnergy gained per swallow (0–100). The +1 grab is flat regardless. |

## [Debug] — AI Feedback

| Key | Default | Purpose |
|-----|---------|---------|
| `AIReportLog` | `true` | AI bug reports. When an NPC calls `report` (console) / `report_issue` (act tool) with a description of what broke or frustrated it, append a block — its message, position, facing, current goal, and its last ~10 tool calls — to `BepInEx/config/kkllmnpc_ai_reports.log`. Read it between sessions: it's the agents telling you which tools lie ("sonar is busted and showing garbage", "go_to kept picking the far blender"). Off = reports only surface in the BepInEx console log (`[AIReport]` lines). |
