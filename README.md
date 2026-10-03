# KK-LLM-NPC

LLM-driven Kobold NPCs for [KoboldKare](https://store.steampowered.com/app/1102930/KoboldKare/).

Possesses unoccupied AI kobolds in the world and plays them through an OpenAI-compatible
chat-completion endpoint: each NPC perceives, plans, moves, interacts, talks in chat,
and reacts to the world continuously, as if a player was at the wheel.

> Requires BepInEx 5.4x, and a local OpenAI-compatible server (LM Studio, Ollama, llama.cpp).
> Vision optionally uses a multimodal model.

## What it does

Each possessed kobold becomes an autonomous agent. Every "think" tick it gets:

- **Possession pool** — every AI kobold the world produces gets an agent (a body that appears later, e.g. from a delivery or breeding, is claimed as it shows up). With `SpawnMissingKobolds` on, it can also spawn extra AI kobolds near the player until `MaxNPCs` is met.
- **Pickup awareness** — knows when its body is grabbed, tracks a position trail while carried, and reports where it was moved (thrown is flagged as startling); it also auto-recovers if ragdolled next to the mail/sell machine and will never interact with that machine, since climbing in starts the sell timer.

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

By default the NPC sits at a **bash-like console** instead of receiving all of that as a
JSON blob every tick: it *polls* the game with short shell commands, reads the output, then
sends its next command (command → data → read → command, in a stream). It asks for exactly
what it needs — `ls` for what's nearby, `status` for a one-line snapshot, `cat
facts|goal|history` for memory — so the payload stays small, works on small local models, and
never overloads the context. It **speaks with `echo`**. (Set `[Console] Enabled=false` to
restore the legacy push-perception / `act`-JSON loop.)

### Console commands (bash-like)

The model types these at the shell prompt, one per line, and reads each result before the
next. Bash-style names are chosen so even small models already know them from shell data.
Legacy `act` names still work as aliases (`say`→`echo`, `go_to`→`cd`, `interact`→`use`,
`walk`→`run`, `exit_station`→`exit`, `grab`→`get`, `set_goal`→`goal`, …).

| Group | Commands |
|-------|----------|
| **Read (poll the game)** | `ls` (nearby), `ps` (who's around — players AND fellow agent NPCs), `pwd` (where + facing in compass words), `whoami` (me), `cat facts\|goal\|chat\|needs\|history\|stations\|map\|body\|hold\|player\|quest`, `status` (one-line snapshot incl. coins + awake time + holding + quest), `find <place\|deg>`, `sonar [filter]` (north-up ASCII map — never rotates), `look left\|right\|up\|down\|around\|<deg>` |
| **Act** | `echo <words>` (**speak**), `emote <what you do>` (**body language**, renders `*like this*`), `cd <place\|id:N>`, `use <thing\|id:N>`, `run [secs] [left\|right]`, `turn [deg]`, `jump`, `crouch [0..1]`, `exit`, `get` (grab what's ~1m in front of the face — result names it), `drop`, `throw` (hurl held item / spray the bucket / fire tools in view direction), `hold` (what's in your hands), `follow on\|off`, `stop`, `sleep [secs]` (ends the turn; with secs it *pauses* that long — chat/events wake it early) |
| **Memory & goals** | `remember <fact>`, `forget <fact>`, `goal <text>\|done\|drop`, `ask <question>` |
| **Economy** | `buy [name\|id:N]` — purchase whatever's within reach: machine contracts, the kobold dispenser, shop items. The NPC spends its OWN coins (shown in status/economy perception). |
| **Module tools** | `thrust`, `erection`, `mount`, `unmount`, `orgasm`, `plant`, `water`, `harvest`, `plant_egg`, `feed_blender`, `grind`, `rename` (positional args: `target`, `amount`) |
| **Misc** | `screenshot` (on-demand vision), `report <what's broken>` (files a dev bug note — AI feedback), `help` (list commands), `clear` |

### Perception keys

| Key | Description |
|-----|-------------|
| `body_control` | erection (0–1), stimulation, penetration state, hip animation status |
| `farm` | nearby plants (name, growing, watered, stage, produce) |
| `cooking` | nearby blenders/grinders, held seed/watering can flags |
| `identity` | orientation, trans status, persona, gender, pronouns |
| `economy` | the body's own coins + nearby purchasables (name/kind/cost/distance) |
| `mail_atm` | nearby mailbox/ATM machines |
| `swap_recent` | last 10 body-swap events |
| `my_swap` | whether the NPC is currently in a body it doesn't own |
| `holding` | what's in its own grabber: name + kind (fruit/seed/water bucket/tool/**person**) + distance + a warning line when it's holding a person |
| `player_activity` | the host player watched LIVE (no storage): what they're doing (station/heading/speed), what they hold, distance/bearing — how the AI "just knows" another player's business |
| `quest` | the game's objective letter scroll (DragonMail): title with live progress, text, stars earned, mail_waiting — letters arrive at the mailbox; the AI can `use mailbox` too |
| (facts) `player_wants:` | the player's latest request, auto-remembered when said — the durable copy of the player's live ask |

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
  locks gaze on its partner during intimacy, gets out of stations via
  `jump` / `exit_station` (the game's own "cancel" path).
- **Body control tools** — `thrust` (hip animation), `erection`, `mount`/`unmount`, `orgasm`
  with consent-aware prompt guidance.
- **Reagent/egg awareness** — detects drinks, sprays, pumps, and egg readiness; seeks a nest to lay.
- **Farming & cooking** — `plant` → `water` → `harvest` cycle, `plant_egg`, `feed_blender`, `grind`.
  Perception reports nearby plants and cooking equipment.
- **Identity** — per-NPC orientation, trans status, expressed persona. `rename` tool with
  uniqueness validation. Mailbox/ATM awareness (mail = selling/trading kobolds).
- **Asks itself questions** (`ask` tool) — answers land next tick and auto-remember as facts.
- **North-up sonar** — the ASCII map's top edge is always north (+Z, right edge east +X) and
  it never rotates with facing; facing is the arrow beside `@` plus compass words in
  `pwd`/`facing`. `sonar <filter>` thins glyphs out (W walls, V windows, S sills, U stations,
  K kobolds, P player). Stations carry world-compass IDs (`Blender#SW4`), landmark clues
  (`near: window, mailbox`), and per-kind closest-to-you/closest-to-player flags, so
  "which blender shall we use?" has a decisive, stable answer.
- **Knows the objective, two ways** — like a fellow player: (1) the game's real quest
  chain (`QuestSense`): objective letters arrive at the **mailbox** (letter box — the AI
  can get them with `use mailbox`); the top-right scroll's title (with live progress,
  e.g. "Create 4 food 2/4"), text, stars and mail-waiting are exposed as perception
  `quest` / `cat quest` / a status line; new letters, progress and completions arrive as
  world events. (2) the host player typed request persists as a `player_wants:` fact, and
  `player_activity`/`cat player` watch them live.
- **Watched like a fellow player** — the AI "just knows" what its player is doing without
  a word or a fact: `player_activity` (perception) / `cat player` (console) read the host
  player live — the station they're in, walking heading, what they hold, ragdoll state —
  refreshed every moment. Their typed requests persist separately as `player_wants:` facts.
- **Routes are learned from players (Destiny-style)** — any body that walks somewhere
  proves that spot walkable in the shared map (a room behind a door closed at bake time
  opens up once your player shows the way), and the player's actual walked path — jumps
  included — is recorded per scene and reused: `go_to` copies their demonstrated trail
  when A* can't route (too_far/gaps/parkour), replaying flagged jumps as hop bursts, and
  follow-mode falls back to it too. ROUTE WATCH events narrate how the player moved
  ("east ~9m, hopping, through the TopDoor") so NPCs understand how they left a room. New
  `HoldSense.cs` reads the grabber's actual grab list: pickup/release
  world events ("you grabbed a PERSON — let them go"), `holding` perception, `hold`/
  status readouts. `grab` names what it caught, `drop` verifies the release (and forces
  `OnReleaseRPC` onto any person still joint-locked — the "AI can't let go of a player"
  fix), and `throw` plays the physical game for real: hurls items in view direction
  (~10 m/s), sprays the water bucket, fires tools; jump right before for range.
- **Small-things physics, taught** — prompts ground the prop gameplay in the game's own
  rules: carried fruit blends when it enters a blender's intake; seeds plant at ≤1m from
  bare soil once used (hold over the dirt or drop there first); the bucket sprays ~10ml
  blobs on throw and never runs dry; laid eggs carry like seeds and grow into fresh EMPTY
  kobold bodies (candidate avatars — the game frames them as your possible children).
- **AI bug reports** — `report` (console) / `report_issue` (act tool) file what the model
  says broke — plus its last tool calls — to `BepInEx/config/kkllmnpc_ai_reports.log` behind
  the `[Debug] AIReportLog` option, so playtests hand you a fix-me list from the agents
  themselves ("tool call was frustrating, do X instead", "sonar is showing garbage").
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
- **Addressing** — say `"Ember, follow me"` (or `"Ember: hi"` / `"@Fern hey"`) and only
  the named NPC hears it; unaddressed lines still reach every NPC. Fellow agents see and
  hear each other (named in `ps`/`ls`), but sibling speech only lands when it addresses
  them, and at most twice a minute, so two chat NPCs can't duet forever.
- **Money & buying** — each body carries its own coins; `buy` purchases machine
  contracts, kobold deliveries and shop items the way a human click does.
- **Persistent life** — a body's facts/goals/name/persona are saved to disk when it's
  given up and restored when the same body is claimed in a later session; world reload
  no longer means total amnesia. (Bodyswaps still keep the mind in place.)
- **Endpoint health** — a dead/offline LLM server backs off between ticks (5→30s) instead
  of hammering retries; the state anchor tells the NPC itself and the F6 overlay shows
  the live status.
- **Debug overlay (F6)** — live state, the NPC's console transcript ("what did it type,
  what came back"), the latest vision caption, all config keys editable in-game, and a
  per-instance say-box that injects a line into one named NPC.
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
| `MaxTokens` | 640 | response cap (reasoning models need the headroom) (reasoning families: automatic floor, see Model families under the Console section) |
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

### `[Console]` — bash-like REPL mode (default ON)

The model polls the game with shell commands instead of receiving a perception payload each
tick — smaller payloads, no overload, and it asks for exactly the data it needs. `echo`
= speak. Set `Enabled=false` for the legacy push-perception / `act`-JSON loop.

**Model families (auto, no config).** The loaded model id is classified into a family
profile (Qwen, Gemma, GLM, Muse Glimmer, Ornith, LFM, Bonsai, Nemotron, Granite, Mistral,
SmolLM, Phi, Llama/GPT-OSS, DeepSeek-style reasoners, vision captioners; unknown → generic).
Reasoning scaffolding (`&lt;think&gt;…&lt;/think&gt;`, any casing) is stripped from replies
before command/JSON parsing for every model, thinking families get an automatic token-budget
floor, and known-chatty families resolve to "small" tier even when the probe is offline.
Logs one line per NPC: `model family 'qwen' from 'qwen3:14b' — …`.

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `Enabled` | true | bool | Console REPL mode (default ON); false = legacy perception loop |
| `MaxRounds` | 3 | 1–8 | Command rounds per think cycle (each round = one LLM call after reading output) |
| `MaxTokens` | 256 | 64–4096 | Max tokens per console reply (command lines are short — low is fast). Reasoning families: automatic floor `256 + headroom`; your value stands when higher. |
| `HistoryMessages` | 30 | 6–120 | Rolling conversation window (system prompt always kept) |
| `SystemPromptFile` | `system_prompt_console.txt` | — | Console persona/world-rules file (command list + protocol appended automatically) |

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
| `Temperature` | 0.2 | Captioner sampling temperature (was hardcoded 0.2); spatial reasoners like ZDTaichu want 0 |

**Models**: Qwen-family models are consistently strong here — vision (VL siblings /
mmproj quants), clean spatial reasoning with the sonar/bearings, and tidy console
discipline; spatial-reasoner checkpoints like ZDTaichu (TaichuAI) are a fine pick and
prefer temperature 0 on the caption pass (temp 0, top_p 0.95, top_k 20 per its card).
The maintained compatibility list — ranked field notes per family, quirks, and the
benchmark plan — lives in [docs/MODELS.md](docs/MODELS.md).

### `[Senses]` — perception settings

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `RayCount` | 9 | ≥2 | Number of rays across the frustum fan |
| `RayRange` | 25 | 1–100m | Raycast range |
| `AutoFindRange` | 60 | 1–500m | Radius to look for an entity to hijack |
| `RadarEnabled` | true | bool | North-up ASCII sonar map in perception (top edge is always north/+Z — never rotates with facing) |
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
| `MaxNPCs` | 1 | Maximum entities the LLM can possess (1–8) |
| `SpawnMissingKobolds` | false | Spawn fresh AI kobolds (random genes, like the dispenser) near the player when the world can't fill `MaxNPCs` — a vanilla world only starts with the breeding pair. Off (default) = wait for bodies to appear naturally and claim each one as it shows up |
| `DisableReagentMessages` | true | Suppress reagent injection messages in log |
| `BlockedScenes` | `MainMenu,Loading,ErrorScene` | Comma-separated scene names where the LLM stays idle |

### `[Debug]` — AI feedback

| Key | Default | Purpose |
| --- | --- | --- |
| `AIReportLog` | true | When an NPC calls `report`/`report_issue("what's broken")`, append a block (its message + position + facing + last tool calls) to `BepInEx/config/kkllmnpc_ai_reports.log`. Read it between sessions — it's the agents telling you which tools frustrate them. Off = reports only reach the BepInEx console log. |

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
- [MODELS.md](docs/MODELS.md) — Ranked model compatibility list + benchmark plan
- [CHANGELOG.md](CHANGELOG.md) — Release history