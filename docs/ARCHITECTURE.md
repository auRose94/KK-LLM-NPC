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
│     • Module perception (body_control, farm, cooking, etc.)     │
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
│  • Ambient commentary — DISABLED (EmitAmbient call sites are    │
│    commented out; the plumbing is in place, no caller fires)   │
│  • Photon ownership re-assertion                                │
│  • PhysicsTick hooks (body control)                             │
│  • Background path worker (A* on separate thread)               │
└──────────────────────────────────────────────────────────────────┘
```

## Component Breakdown

### Plugin Lifecycle (`Main.cs`)

- `LLMNPCPlugin`: BepInEx plugin entry point
- Manages config binding, instance creation, hot-reload watchers
- Registers Photon chat callback
- Main-thread synchronization via `SynchronizationContext`

### NPC Instance (`NPCInstance` — 26 partial files)

| File | Responsibility |
|------|---------------|
| `NPCInstance.cs` | Core state, config fields, thread lifecycle |
| `Llm.cs` | Decision loop, LLM query, tool extraction, plan chaining |
| `Body.cs` | Possession, teardown, camera setup, reagent/penetration |
| `Senses.cs` | Perception: raycasts, clearance, radar, spatial layout |
| `Movement.cs` | Physics-frame movement, walk validation, camera clip |
| `Tools.cs` | Tool implementations (walk, go_to, interact, say, etc.) |
| `Chat.cs` | Photon chat, speech bubbles, chat log processing |
| `Pathfinding.cs` | 3D layered A* on walkability grid + background worker |
| `WorldMap.cs` | Full-scene cached walkability map (shared by all agents) |
| `PathCore.cs` | Pure A* solver, path policy, adaptive cell sizing, goal resolver (Unity-free) |
| `Vision.cs` | Background vision caption pass |
| `BodyControl.cs` | thrust/erection/mount/unmount/orgasm tools + swap awareness |
| `Farming.cs` | plant/water/harvest/plant_egg tools + farm perception |
| `Cooking.cs` | feed_blender/grind tools + cooking perception |
| `Identity.cs` | identity block, rename tool, mailbox/ATM perception |
| `ChatSimilarity.cs` | Jaccard + Levenshtein similarity (pure C#, testable) |
| `SafeHttp.cs` | Retry wrapper with exponential backoff — the single HTTP transport for all LLM/vision/probe calls |
| `Constants.cs` | Named constants for magic numbers |
| `ContextCore.cs` | Pure compaction policy, token estimation, fact/history reduction (Unity-free) |
| `ContextManager.cs` | Thin adapter over `ContextCore`: wall clock, probe lookups, admin-API model switch |
| `ModelProbe.cs` | Server capability detection |
| `Json.cs` | Hand-rolled JSON parser/writer |
| `ModuleRegistry.cs` | Reflection-based module discovery (tools, physics, perception) |
| `IdentityBot.cs` | Second Photon client for NPC room identity |
| `Patches.cs` | Harmony patches for animator rotation conflicts |
| `Overlay.cs` | Debug overlay GUI |
| `GoalMachine.cs` | Persistent goal machine (set/complete/drop) |
| `Console.cs` | Console REPL (default mode): command handlers, event drain, prompt, the command→output→command turn loop |
| `ConsoleShell.cs` | Bash-like command vocabulary + lenient line/reply parser (pure C#, unit-testable) |

### Key Design Decisions

1. **Partial class split**: `NPCInstance` is split across 26 files by subsystem.
   Each file handles one concern. This is intentional — it keeps individual files
   manageable while the logical class is large (~5000 lines total).

2. **Module registry**: New features register via `ModuleRegistry` — tools, physics
   tick hooks, and perception hooks are discovered by reflection. No shared-file
   edits needed to add a new module.

3. **Background path worker**: A* pathfinding runs on a static worker thread. NPCs
   enqueue paths; the worker computes off-main; results are posted to per-NPC slots.
   Milestone-based replanning (only when deviation/target-move/age triggers) replaces
   timer-driven replanning.

4. **JSON schema over tools**: Uses `response_format: json_schema` instead of
   `tool_choice` because many chat templates (Gemma, etc.) reject tool forcing.

5. **Fuzzy tool matching**: Small models produce misspelled tool names. A
   Levenshtein-distance-based fuzzy matcher recovers valid tool calls.

6. **Dynamic compaction**: `ContextCore` monitors context fill ratio and
   escalates compaction (trim history → merge facts → model switch) when needed.
   The policy is a pure state machine in `ContextPolicy`, so the whole escalation
   ladder is unit-tested; `ContextManager` only supplies the clock, the probe
   results and the model switch.

7. **Photon ownership**: The plugin claims PhotonView ownership of possessed
   bodies. Other mods can steal it — hence the 3-second re-assertion watchdog.

8. **Chat freshness**: Timestamped chat log with ack tracking. Say-repeat suppression
   uses Jaccard + Levenshtein similarity. Identity bot uses exponential backoff.
   Empty-content handling retries once before safe no-op.

9. **Harmony patches**: `CharacterControllerAnimator` coroutines fight our
   rotation control. Patches neutralize the `MoveNext()` rotation writes.

10. **Console REPL (default mode)**: Instead of pushing a big perception JSON every
    tick, the model sits at a bash-like shell and *polls* the game — it sends command
    lines (`ls`, `cd`, `cat`, `echo`…), reads the terse output, then sends its next
    command (a command → data → read → command stream). This keeps payloads small (works
    on small local models, no context overload) and lets the NPC ask for exactly the data
    it needs. `echo` is how it speaks. Bash-style names are chosen because small models
    already know them from shell data; legacy `act` names (`say`, `go_to`, `interact`,
    `walk`, …) are kept as aliases, and a deliberately *tight* fuzzy matcher (one-char
    insert/delete only, or substitution for 4+ char commands) recovers typos without
    turning prose into commands. Module tools register themselves into the same
    vocabulary at startup (`ConsoleShell.RegisterExtra`). Set `[Console] Enabled=false`
    to fall back to the legacy push-perception / `act`-JSON loop, which is kept intact.

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
    Module perception → body_control, farm, cooking, identity, etc.
    → JSON payload

LLM (console mode — default):
    drain world events ([chat]/[event]/[ask]) + state anchor + "$" prompt
    → model replies with command lines (ls, cd, echo, cat ...)
    → parse + execute each line → print terse terminal output
    → model reads output, sends more commands (or `sleep` to end the turn)
    → repeat up to MaxRounds; rolling window (HistoryMessages) bounds the context

LLM (legacy mode — [Console] Enabled=false):
    POST perception → model
    Parse act JSON → tool calls
    Execute tools → movement, interaction, speech
    Log outcomes → history, facts

Movement:
    FixedUpdate → apply velocity, validate walk, steer around obstacles
    Background path worker → A* on local grid or full world map
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
| Path worker | Static daemon thread: computes A* paths off the main thread |

### Error Handling

- All LLM, vision and probe calls go through `SafeHttp`, which retries transient failures
  (5xx, 408, 429, connect/timeout faults) with exponential backoff and **fails fast** on
  permanent ones (400/401/403/404/413), so a bad API key or an over-long context costs one
  round-trip instead of three plus backoff sleeps. Failed calls report the server's own error
  body, which is what makes "context length exceeded" vs "no model loaded" diagnosable from
  the log.
- Perception failures return `{ok: false}` instead of crashing the loop
- Mono string conversion errors are sanitized (surrogate scrubbing)
- Thread abort/InterruptedException handled gracefully
- Tool handlers never throw; return `{ok: false, reason: "...", hint: "..."}`