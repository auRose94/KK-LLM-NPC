# KK-LLM-NPC (Name WIP)

## License: Open-Source MIT

LLM-driven Kobold NPCs for [KoboldKare](https://store.steampowered.com/app/2938400/KoboldKare/).

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
- **Pathfinding** — A* pathfinding allows the npcs to walk to locations and navigate obstacles.
- **First-person JPEG** (optional, on a bump / after turns / periodically).
- **Body** — equipment (penis / penetrables), energy, horniness (with trend arrow), egg amount.
- **State** — in-station, being penetrated (depth/hole/thrust per penetrator), penetrating someone.
- **Reagent events** — drank/sprayed/metabolized ("drank Water 5ml") when the belly changes.
- **Player chat** — what you typed into the game chat; it answers.
- **History** — its last 10 actions with outcomes, recent goals, and a growing `facts` list it can extend via `remember`.

It then emits one structured `act` per tick (via JSON-schema response_format; works on any
model, not just tool-calling ones), with an optional `plan[]` to chain up to 8 steps:
`walk` / `walk_ray` / `go_to` (name → closest matching station!) / `jump` / `look` /
`look_around` / `interact` / `crouch` / `grab` / `drop` / `say` / `ask` / `interact` / `exit_station`.

Two NPCs in the same game see each other as regular `kobold`s in `nearby` and react to
each other's chat bubbles.

## Behavior highlights

- **Walks by default**, runs only when asked (`run: true`). Bounded bursts that auto-stop.
- **Obstacle-aware movement** — validates before walking, slips along walls via fan-steering,
  treats low sills/windows as climbable instead of walls, ledges report drop height so it can
  hop down small ones.
- **Vision pipeline** (separate thread): a vision model writes a compact scene report +
  nav hint ("go:-30:bed") feeding the planner. Action model sees a first-person frame on bumps.
- **Camera anti-clip** — detects the head buried in geometry and auto-crouches until vision clears,
  so first-person isn't teeth-and-eyes.
- **Body awareness** — knows which station it can use (`interact` reports `cannot_use` with reason),
  locks gaze on its partner during intimacy, moans on stimulation, gets out of stations via
  `jump` / `exit_station` (the game's own "cancel" path).
- **Reagent/egg awareness** — detects drinks, sprays, pumps, and egg readiness; seeks a nest to lay.
- **Asks itself questions** (`ask` tool) — answers land next tick and auto-remember as facts.
- **Distinct name per body** (from the kobold's own name + instance suffix), says it in chat.

```bash
./build.sh 
```

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
| `SystemPrompt` | built-in | persona; blank = use built-in |
| `ThinkInterval` | 0.4 | seconds between ticks |
| `MaxTokens` | 640 | response cap (reasoning models need the headroom) |
| `Temperature` | 0.3 | sampler temp |
| `PlanStepDelay` | 0.35 | pause between plan steps |
| `PlanMaxSteps` | 8 | max chained actions per response |
| `SendImage` | true | attach first-person JPEG when useful |
| `ImageEveryNTicks` | 6 | baseline image cadence |
| `ImageOnBump` | true | extra image right after blocked/bump |
| `ImageOnTurn` | true | extra image after a >=30° turn |

### `[Vision]` — the background "eyes" thread

| Key | Default | Purpose |
| --- | --- | --- |
| `Enabled` | true | run the caption pass |
| `EveryNTicks` | 3 | every N action ticks |
| `Prompt` | built-in | vision task instruction |
| `DebugDumpFrames` | true | dump frames to `BepInEx/plugins/KKLLMNPC_frames/` |

### `[VisionModel]` — route the captioner to a different model (or same)

All blank = use `[LLM]` config.
| `Model` `Endpoint` `ApiKey` | blank | vision model identity |
| `MaxTokens` | 80 | caption length |

### `[Senses]`

| `RayCount` | 9 | rays per vertical row |
| `RayRange` | 25 | meters |
| `AutoFindRange` | 60 | possess radius |
| `ImageSize` | 192 | px |
| `CameraNearClip` | 0.15 | clip past face geometry |
| `CameraForward` | 0.22 | how far ahead of the head bone the camera sits |

### `[General]`

| `BlockedScenes` | `MainMenu,Loading,ErrorScene` | idle on these |
| `MaxKobolds` | 1 | (future single-plugin multi-kobold) |

## Build

```bash
./build.sh        # build + deploy instance
```
