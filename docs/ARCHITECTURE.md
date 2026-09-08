# KK-LLM-NPC Architecture

## Overview

KK-LLM-NPC is a BepInEx plugin for [KoboldKare](https://store.steampowered.com/app/1102930/KoboldKare/)
that replaces AI kobolds with LLM-driven autonomous agents. Each possessed kobold
perceives its environment, plans actions, moves, interacts, and communicates —
all driven by an OpenAI-compatible chat-completion endpoint.

## Core Flow

```
┌──────────────────────────────────────────────────────────────────┐
│                        LLM Loop (background thread)              │
│                                                                  │
│  1. Perception ──→ BuildPerception() ──→ Json.Write()          │
│     • Raycasts (frustum, clearance, ground)                     │
│     • Nearby objects (OverlapSphere)                            │
│     • Body state (energy, horniness, reagent events)            │
│     • Vision caption (optional, separate thread)                │
│                                                                  │
│  2. Query LLM ──→ QueryLLM() ──→ POST /v1/chat/completions    │
│     • Sends perception + memory + facts as JSON                 │
│     • Receives act JSON (progress, why, thought, action)        │
│     • Retry with exponential backoff via SafeHttp               │
│                                                                  │
│  3. Execute ──→ ExecuteToolCalls() ──→ ExecuteStep()            │
│     • Parse tool calls from response                            │
│     • Execute each tool (walk, go_to, interact, say, ...)       │
│     • Execute plan[] steps with delays                          │
│     • Log outcomes to history                                   │
│                                                                  │
│  4. Sleep ──→ GetDynamicThinkInterval()                         │
│     • Faster when active, slower when idle                      │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│                    FixedUpdate (main thread)                     │
│                                                                  │
│  • Movement application (accel/decel toward target)             │
│  • Walk validation (raycast ahead, obstacle steering)           │
│  • Camera clip detection + auto-crouch                          │
│  • Horniness update (slow-burn while unstimulated)              │
│  • Ambient commentary (moan on stimulation spike)               │
│  • Photon ownership re-assertion                                │
└──────────────────────────────────────────────────────────────────┘
```

## Component Breakdown

### Plugin Lifecycle (`Main.cs`)

- `LLMNPCPlugin`: BepInEx plugin entry point
- Manages config binding, instance creation, hot-reload watchers
- Registers Photon chat callback
- Main-thread synchronization via `SynchronizationContext`

### NPC Instance (`NPCInstance` — 15 partial files)

| File | Responsibility |
|------|---------------|
| `NPCInstance.cs` | Core state, config fields, thread lifecycle |
| `Llm.cs` | Decision loop, LLM query, tool extraction, plan chaining |
| `Body.cs` | Possession, teardown, camera setup, reagent/penetration |
| `Senses.cs` | Perception: raycasts, clearance, radar, spatial layout |
| `Movement.cs` | Physics-frame movement, walk validation, camera clip |
| `Tools.cs` | Tool implementations (walk, go_to, interact, say, etc.) |
| `Chat.cs` | Photon chat, speech bubbles, chat log processing |
| `Pathfinding.cs` | 3D layered A* on walkability grid |
| `Vision.cs` | Background vision caption pass |
| `SafeHttp.cs` | Retry wrapper with exponential backoff |
| `Constants.cs` | Named constants for magic numbers |
| `ContextManager.cs` | Dynamic context window compaction |
| `ModelProbe.cs` | Server capability detection |
| `Json.cs` | Hand-rolled JSON parser/writer |
| `Patches.cs` | Harmony patches for animator rotation |

### Key Design Decisions

1. **Partial class split**: `NPCInstance` is split across 15 files by subsystem.
   Each file handles one concern. This is intentional — it keeps individual files
   manageable while the logical class is large (~5000 lines total).

2. **Background LLM thread**: The decision loop runs on its own thread. Unity API
   access goes through `RunOnMainThread()` which posts to the main thread's
   `SynchronizationContext`.

3. **JSON schema over tools**: Uses `response_format: json_schema` instead of
   `tool_choice` because many chat templates (Gemma, etc.) reject tool forcing.

4. **Fuzzy tool matching**: Small models produce misspelled tool names. A
   Levenshtein-distance-based fuzzy matcher recovers valid tool calls.

5. **Dynamic compaction**: `ContextManager` monitors context fill ratio and
   escalates compaction (trim history → merge facts → model switch) when needed.

6. **Photon ownership**: The plugin claims PhotonView ownership of possessed
   bodies. Other mods can steal it — hence the 3-second re-assertion watchdog.

7. **Harmony patches**: `CharacterControllerAnimator` coroutines fight our
   rotation control. Patches neutralize the `MoveNext()` rotation writes.

### Data Flow

```
Kobold body → Possession (claim, steal PhotonView, disable AI)
    → Camera setup (eye cam, render texture, optional stereo)
    → Belly subscription (reagent events)
    → Penetration subscription (enter/exit/stim)

Perception:
    Raycasts → clearance, rays, ground probe
    OverlapSphere → nearby objects
    Body introspection → energy, horniness, equipment
    Vision caption → scene description + nav hint
    → JSON payload

LLM:
    POST perception → model
    Parse act JSON → tool calls
    Execute tools → movement, interaction, speech
    Log outcomes → history, facts

Movement:
    FixedUpdate → apply velocity, validate walk, steer around obstacles
    Camera clip → auto-crouch
    Photon → re-assert ownership
```

### Thread Model

| Thread | Purpose |
|--------|---------|
| Main (Unity) | Physics, rendering, Photon events, config hot-reload |
| LLM thread | Decision loop: perception → LLM query → tool execution |
| Vision thread | Background caption: render on main, caption on worker |
| Commentary thread | Free-form musings via Task.Run |
| Ask thread | Question answering via Task.Run |
| Plan steps thread | Background plan execution via Task.Run |

### Error Handling

- All LLM calls retry with exponential backoff (SafeHttp)
- Perception failures return `{ok: false}` instead of crashing the loop
- Mono string conversion errors are sanitized (surrogate scrubbing)
- Thread abort/InterruptedException handled gracefully
