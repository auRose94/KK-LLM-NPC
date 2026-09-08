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
| `MaxTokens` | `1024` | 64–32768 | Max response tokens (reasoning models burn tokens on analysis before the action — too low and the action dies mid-JSON) |
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

## [Senses] — Perception

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `RayCount` | `9` | ≥2 | Number of rays across the frustum fan |
| `RayRange` | `25` | 1–100m | Raycast range (m) |
| `AutoFindRange` | `60` | 1–500m | Radius to look for an entity to hijack |
| `RadarEnabled` | `true` | bool | Add the top-down ASCII radar map to perception. Off = smaller payload + avoids models misreading radar symbols as living figures |
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

## [Needs] — Body State

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `HornyClimbPerMin` | `5` | 0–60/min | How fast the slow-burn horniness rises while the body gets NO stimulation (0.0-1.0 scale per minute) |
| `HornyBaseline` | `0.08` | 0–1 | Starting horniness when the NPC takes a body |

## [Memory] — Facts & Forgetting

| Key | Default | Range | Purpose |
|-----|---------|-------|---------|
| `FactDecayTicks` | `900` | 0–… | Ticks before a fact decays out of context if the model hasn't re-asserted it. Re-`remember`ing a fact (same category prefix) refreshes its age, so actively-used facts outlive scratch notes. 0 = facts never decay. |

## [Multiplayer] — NPC Room Identity

| Key | Default | Purpose |
|-----|---------|---------|
| `IdentityBot` | `false` | Run a second Photon client in the room under the NPC's own name so its chat renders as `KoboldName: text` to everyone (not attributed to the plugin owner). Opt-in: adds a room player, may affect player count / host logic. Off (default) = owner-attributed chat. |
| `IdentityAppId` | *(empty)* | Photon AppId for the identity bot. Blank = reuse the game's own AppId (recommended). |

## [General] — Global

| Key | Default | Purpose |
|-----|---------|---------|
| `MaxNPCs` | `1` | Maximum number of entities the LLM can possess simultaneously (1–4) |
| `DisableReagentMessages` | `true` | Suppress reagent injection messages in log |
| `BlockedScenes` | `MainMenu,Loading,ErrorScene` | Comma-separated scene names where the LLM stays idle |
