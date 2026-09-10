# KK-LLM-NPC

LLM-driven Kobold NPCs for [KoboldKare](https://store.steampowered.com/app/1102930/KoboldKare/).

Possesses unoccupied AI kobolds in the world and plays them through an OpenAI-compatible
chat-completion endpoint: each NPC perceives, plans, moves, interacts, talks in chat,
and reacts to the world continuously, as if a player was at the wheel.

> Requires BepInEx 5.4x, and a local OpenAI-compatible server (LM Studio, Ollama, llama.cpp).
> Vision optionally uses a multimodal model.

## What it does

Each possessed kobold becomes an autonomous agent. Every "think" tick it gets:

- **Frustum raycasts** around the head (down/level/up rows) — what's where, with distances.
- **8-direction clearance** — which way is open, what's a wall/sill/window.
- **Ground** — supported / step / sill / ledge / drop distance.
- **Nearby** — kobolds, the player, and usable stations within 14m, with bearing ("front-right") and category (`bed`/`nest`/`play`/...).
- **Whole-map pathfinding** — background A* on a cached 3D walkability grid; time-budgeted with per-frame expansion caps so the main thread never hitches.
- **First-person JPEG** (optional, on a bump / after turns / periodically).
- **Body** — equipment (penis / penetrables), energy, horniness (with trend arrow), egg amount, stimulation, penetration state.
- **State** — in-station, being penetrated (depth/hole/thrust per penetrator), penetrating someone.
- **Reagent events** — drank/sprayed/metabolized ("drank Water 5ml") when the belly changes.
- **Player chat** — what you typed into the game chat; it answers.
- **History** — its last 10 actions with outcomes, recent goals, and a `facts` list it extends via
  `remember` and prunes via `forget` (stale facts also decay out over time).

It then emits one structured `act` per tick (via JSON-schema response_format; works on any
model, not just tool-calling ones), with an optional `plan[]` to chain up to 8 steps.

### Available tools

| Category | Tools |
|----------|-------|
| **Movement** | `walk`, `walk_ray`, `go_to` (name → closest station), `jump` |
| **Interaction** | `look`, `look_around`, `interact`, `crouch`, `grab`, `drop`, `exit_station` |
| **Communication** | `say`, `ask` (self-question), `remember`, `forget` |
| **Goals** | `set_goal`, `complete_goal`, `drop_goal` |
| **Body control** | `thrust`, `erection`, `mount`, `unmount`, `orgasm` |
| **Farming** | `plant`, `water`, `harvest`, `plant_egg` |
| **Cooking** | `feed_blender`, `grind` |
| **Identity** | `rename` |

### Perception keys

| Key | Description |
|-----|-------------|
| `body_control` | erection (0–1), stimulation, penetration state, hip animation status |
| `farm` | nearby plants (name, growing, watered, stage, produce) |
| `cooking` | nearby blenders/grinders, held seed/watering can flags |
| `identity` | orientation, trans status, persona, gender, pronouns |
| `mail_atm` | nearby mailbox/ATM machines |
| `swap_recent` | last 10 body-swap events |
| `my_swap` | whether the NPC is currently in a body it doesn't own |

## Behavior highlights

- **Walks by default**, runs only when asked (`run: true`). Bounded bursts that auto-stop.
- **Obstacle-aware movement** — validates before walking, slips along walls via fan-steering,
  treats low sills/windows as climbable instead of walls, ledges report drop height so it can
  hop down small ones.
- **Whole-map pathfinding** — background worker thread computes A* paths off the main thread;
  milestone-based replanning (only replans when the target moved, path is exhausted, or deviation
  exceeds threshold). Time-budgeted expansion prevents frame hitches on large maps.
- **Vision pipeline** (separate thread): a vision model writes a compact scene report +
  nav hint ("go:-30:bed") feeding the planner. Action model sees a first-person frame on bumps.
- **Camera anti-clip** — detects the head buried in geometry and auto-crouches until vision clears,
  so first-person isn't teeth-and-eyes.
- **Body awareness** — knows which station it can use (`interact` reports `cannot_use` with reason),
  locks gaze on its partner during intimacy, moans on stimulation, gets out of stations via
  `jump` / `exit_station` (the game's own "cancel" path).
- **Body control tools** — `thrust` (hip animation), `erection`, `mount`/`unmount`, `orgasm`
  with consent-aware prompt guidance.
- **Reagent/egg awareness** — detects drinks, sprays, pumps, and egg readiness; seeks a nest to lay.
- **Farming & cooking** — `plant` → `water` → `harvest` cycle, `plant_egg`, `feed_blender`, `grind`.
  Perception reports nearby plants and cooking equipment.
- **Identity** — per-NPC orientation, trans status, expressed persona. `rename` tool with
  uniqueness validation. Mailbox/ATM awareness (mail = selling/trading kobolds).
- **Asks itself questions** (`ask` tool) — answers land next tick and auto-remember as facts.
- **Persistent goal** (`set_goal` / `complete_goal` / `drop_goal`) — one stored goal drives every
  tick instead of re-deliberating; a repetition guard nudges it out of stuck loops, and finishing
  a goal sheds its scratch facts.
- **Forgetting** — stale facts decay out of context (`FactDecayTicks`) and `forget` drops them on
  demand, so finished business stops lingering.
- **Chat freshness** — timestamped chat log with acknowledgment tracking; the NPC only sees
  post-spawn conversation. Say-repeat suppression (Jaccard + Levenshtein) prevents near-identical
  lines from looping.
- **Hears only post-spawn chat** — a baseline is captured when the NPC wakes, so it never
  "reads" the conversation other players had before it existed.
- **Distinct name per body** (from the kobold's own name + instance suffix), says it in chat.
- **Attributed chat** (opt-in) — a second Photon client makes its chat render as `KoboldName: text`
  to everyone, not attributed to the plugin owner.

## Install

Drop `KKLLMNPC.dll` into `KoboldKare/BepInEx/plugins/`. First launch writes
`BepInEx/config/com.kk.llmnpc.cfg`.

## Config (per instance)

### `[LLM]` — the planner

| Key | Default | Purpose |
| --- | --- | --- |
| `Endpoint` | `http://127.0.0.1:11434/v1/chat/completions` | OpenAI-compatible chat completions URL |
| `Model` | `local-model` | model name |
| `ApiKey` | empty | bearer token |
| `SystemPrompt` | built-in | persona; a customized value always wins |
| `SystemPromptFile` | `system_prompt_default.txt` | path to a full system prompt (game dir, plugin dir, or CWD). Overrides the built-in unless `SystemPrompt` is customized; ships in this repo |
| `ThinkInterval` | 0.4 | seconds between ticks |
| `MaxTokens` | 640 | response cap (reasoning models need the headroom) |
| `Temperature` | 0.3 | sampler temp |
| `PlanStepDelay` | 0.35 | pause between plan steps |
| `PlanMaxSteps` | 8 | max chained actions per response |
| `SendImage` | true | attach first-person JPEG when useful |
| `ImageEveryNTicks` | 6 | baseline image cadence |
| `ImageOnBump` | true | extra image right after blocked/bump |
| `ImageOnTurn` | true | extra image after a >=30° turn |
| `ImageHistory` | 3 | number of past frames to attach (0 = current only) |
| `ModelTier` | auto | `auto` / `small` / `medium` / `large` — model capability tier |
| `AutoSwitchModel` | false | auto-switch to larger-context model when pressure is critical |
| `LLMNameSelection` | true | ask the LLM to choose a name for each body |
| `CommentEveryNTicks` | 5 | free commentary interval (0 = off) |
| `CommentTemp` | 0.9 | sampling temperature for commentary |
| `ChatLogLines` | 20 | lines of chat history to feed the model (0 = off) |

### `[Vision]` — the background "eyes" thread

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `Enabled` | false | bool | Background vision CAPTION pass (off by default — direct image is more useful) |
| `EveryNTicks` | 3 | ≥1 | Run the vision pass every N action ticks |
| `Prompt` | built-in | — | Scene-report + navigation instruction for the vision pass |
| `DebugDumpFrames` | true | bool | Write the exact JPEG given to the vision model to `BepInEx/plugins/KKLLMNPC_frames/` |
| `Stereo` | false | bool | Render left+right eye cameras and stitch into side-by-side stereo image |
| `StereoIPD` | 0.063 | — | Inter-pupillary distance in meters |

### `[VisionModel]` — route the captioner to a different model (or same)

All blank = use `[LLM]` config.

| Key | Default | Purpose |
| --- | --- | --- |
| `Model` | blank | Vision model for the scene-caption pass |
| `Endpoint` | blank | Chat-completions URL for the vision model |
| `ApiKey` | blank | Bearer token for the vision endpoint |
| `MaxTokens` | 80 | Caption token cap (short = fast) |

### `[Senses]` — perception settings

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `RayCount` | 9 | ≥2 | Number of rays across the frustum fan |
| `RayRange` | 25 | 1–100m | Raycast range |
| `AutoFindRange` | 60 | 1–500m | Radius to look for an entity to hijack |
| `RadarEnabled` | true | bool | Top-down ASCII radar map in perception |
| `RadarSize` | 10 | 3–30 | Radar half-grid size in cells |
| `RadarScale` | 1.2 | 0.1–10m | Radar meters per cell |
| `ImageSize` | 192 | 32–2048px | Square first-person render size |
| `ImageQuality` | 50 | 1–100 | JPEG quality |
| `CameraNearClip` | 0.10 | 0.01–10m | Camera near clip (raise if you see inside the head) |
| `CameraFarClip` | 80 | 1–500m | Camera far clip (draw distance) |
| `CameraForward` | 0.22 | -2–5m | Camera offset ahead of the head bone |

### `[Movement]` — navigation

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `TurnRate` | 180 | 10–1000°/s | Maximum yaw rotation speed |
| `Acceleration` | 4 | 0.5–50 units/s² | How fast the entity ramps up |
| `Deceleration` | 6 | 0.5–50 units/s² | How fast the entity slows down |
| `BrakeDistance` | 2 | 0.1–20m | Distance from target to start slowing |
| `PathfindingEnabled` | true | bool | A* pathfinding on a local walkability grid |
| `PathfindingCellSize` | 0.5 | 0.1–5m | A* grid cell size |
| `PathfindingWindow` | 20 | 2–100m | A* search window radius |
| `PathfindingNodes` | 9000 | 200–50000 | Max pathfinding node budget |
| `PathTimeBudgetMs` | 8 | ≥1ms | Time budget (ms) for A* expansion. On overrun, returns partial path |
| `PathMaxExpansions` | 20000 | ≥100 | Max A* node expansions. On overrun, returns partial path |
| `WorldMapEnabled` | true | bool | Build & cache a full-scene 3D walkability map shared by ALL agents |
| `WorldMapCellSize` | 1.0 | 0.25–4m | World-map grid cell size in meters |
| `WorldMapMaxSpan` | 2000 | ≥2m | Max span (m) for the world map. Adaptive cell sizing keeps the grid within ~4M cells |
| `WorldMapMaxCells` | 4000000 | ≥1 | Hard cell cap; cell size inflates if exceeded |
| `WorldMapAutoLayers` | true | bool | Auto-detect floor layers (gap > 1.5m = new layer, cap 16) |
| `WorldMapLayers` | 4 | 1–16 | Fixed layer count (only when auto-detect is off) |
| `WorldMapCellsPerFrame` | 48 | ≥1 | Cells sampled per frame while building the world map |

### `[Needs]` — body state

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `HornyClimbPerMin` | 5 | 0–60/min | Slow-burn horniness rise rate per minute |
| `HornyBaseline` | 0.08 | 0–1 | Starting horniness when the NPC takes a body |

### `[Farming]` — farm/cooking perception

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `ScanRadius` | 3.0 | 1–20m | Radius to scan for plants, blenders, grinders, egg spawners |
| `ScanMax` | 8 | 1–20 | Max farm/cooking entries in perception (keeps payload small) |

### `[Memory]` — facts & forgetting

| Key | Default | Purpose |
| --- | --- | --- |
| `FactDecayTicks` | 900 | Ticks before a fact decays out of context if not re-asserted (0 = never decay). Re-`remember`ing a fact refreshes its age. |

### `[Multiplayer]` — NPC room identity

| Key | Default | Purpose |
| --- | --- | --- |
| `IdentityBot` | false | Run a second Photon client in the room under the NPC's own name so its chat renders as `KoboldName: text` to everyone. Opt-in. |
| `IdentityAppId` | blank | Photon AppId for the identity bot. Blank = reuse the game's own AppId (recommended). |

### `[General]` — global

| Key | Default | Purpose |
| --- | --- | --- |
| `MaxNPCs` | 1 | Maximum entities the LLM can possess (1–4) |
| `DisableReagentMessages` | true | Suppress reagent injection messages in log |
| `BlockedScenes` | `MainMenu,Loading,ErrorScene` | Comma-separated scene names where the LLM stays idle |

## Build

### Linux / macOS

```bash
./build.sh        # build + deploy instance
# Or specify your game path:
KOBOLDKARE_DIR=/path/to/KoboldKare ./build.sh
```

### Windows (PowerShell)

The `build.sh` script requires bash. On Windows, use:

```powershell
# Option 1: Git Bash / WSL
bash build.sh

# Option 2: Manual mcs compilation
mcs -target:library -out:KKLLMNPC.dll src/*.cs \
  -r:"C:\path\to\KoboldKare\BepInEx\core\BepInEx.dll" \
  -r:"C:\path\to\KoboldKare\BepInEx\core\0Harmony.dll" \
  -r:"C:\path\to\KoboldKare\KoboldKare_Data\Managed\*.dll"
```

### Multi-instance

```bash
# Instance 1 (default)
./build.sh

# Instance 2 (deployed as KKLLMNPC2.dll)
# Modify build.sh to accept an instance count parameter
```

## Tests

All tests run with zero external dependencies (pure C# + mono):

```bash
# JSON parser/writer (22 tests)
mcs -target:exe -out:/tmp/t.exe tests/test_json.cs tests/test_json_standalone.cs src/Json.cs && mono /tmp/t.exe

# PathCore (33 tests: A*, ShouldReplan, adaptive cell, auto floors)
mcs -target:exe -out:/tmp/t_pf.exe tests/test_pathcore.cs src/PathCore.cs && mono /tmp/t_pf.exe

# Chat similarity (19 tests: Jaccard, Levenshtein, fuzzy matching)
mcs -target:exe -out:/tmp/t_chat.exe tests/test_chat_similarity.cs src/ChatSimilarity.cs && mono /tmp/t_chat.exe
```

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture overview.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

MIT — see [LICENSE.txt](LICENSE.txt).

## Documentation

- [CONFIG.md](docs/CONFIG.md) — Complete configuration reference
- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — Architecture overview
- [CHANGELOG.md](CHANGELOG.md) — Release history