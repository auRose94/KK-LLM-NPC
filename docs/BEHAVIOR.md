# Behavior highlights — the long version

Every agent behavior in detail, with the reasoning behind each design choice.
Moved from README.md on 2026-11-15 to keep the README a quick read.

- **Walks by default**, runs only when asked (`run: true`). Bounded bursts that auto-stop.
- **Obstacle-aware movement** — validates before walking (three body-width lanes: the
  path and the steering only go where the capsule fits — no more door-frame grinding),
  auto-hops fences/sills/low walls when there's ground beyond, slips along walls via
  fan-steering, ledges report drop height so it can hop down small ones, and a
  stuck-breaker sidesteps then re-plans the route around a spot it keeps bumping.
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
- **Body control tools** — `thrust` (slow deep hip strokes — the rate follows intensity, ~2.3 strokes/s at the default; the OLD version was a 10 Hz sine the animator's smoothing ate down to a shiver), `erection`, `mount`/`unmount`, `orgasm`
  with consent-aware prompt guidance.
- **Reagent/egg awareness** — detects drinks, sprays, pumps, and egg readiness; seeks a nest to lay.
- **Swallow-vore & spectate (opt-in)** — both directions exist (the player eats an NPC;
  an agent eats the player) as complete, honest loops — narration, memory/skill transfer,
  rebirth, the no-cheat abort. Brief summary above; **the full mechanics live in
  [docs/VORE.md](docs/VORE.md)**.
- **Farming & cooking** — `plant` → `water` → `harvest` cycle, `plant_egg`, `feed_blender`, `grind`.
  Perception reports nearby plants and cooking equipment.
- **Identity** — per-NPC orientation, trans status, expressed persona. `rename` tool with
  uniqueness validation. Mailbox/ATM awareness (mail = selling/trading kobolds).
- **Debug console in the browser (`[Web]`)** — the in-game F6 IMGUI debug overlay was
  removed; the browser page on the loopback (default `http://127.0.0.1:8338/`) is THE
  debug surface now, and it grew a working side: **Settings** (every config key, live —
  saved to the BepInEx config file), **Ops** (delete memory files, delete map/trail
  data, reset settings to defaults — confirmation-gated), per-instance **start/stop/inject-say**
  controls, and a **full stat dump** per kobold (`/api/instances`: genes, energy, egg
  volume, position, penetration state, belly guests, skills, goal details, endpoint
  health, model tier/family, …). Status tabs keep the live goal/facts/history/terminal,
  the spectate status, the mod's log tail and the AI-reports log. Updates are
  reader-friendly: unchanged boxes are never rewritten (scroll never resets), the
  log and AI-reports boxes FREEZE while you are scrolled up (a hold bar appears;
  scrolling back to the bottom flushes the held lines), an ⏸ pause button stops
  polling, and the stats grid refreshes every ~6s. `/api/state` +
  `/api/reports` remain curl-able for a dev assistant mid-session; the snapshot thread
  reads only lock-guarded strings so the page answers even while the main thread is
  wedged (the stat dump is a main-thread-built ~1.5s cache).
- **Plans and executes (`plan` ladder)** — a goal stores WHAT to do; `plan <step>` stores
  the ordered HOW. Steps persist across turns (the state anchor carries the current one and
  `cat plan` shows them all), so a small model never re-derives its approach: set goal →
  lay the steps once → work them one per turn → `plan next` marks each done. The ladder
  clears automatically when the goal completes or is dropped.
- **Native tool calling, when the server has it** — every console request can declare the
  console commands as an OpenAI tools array (`NativeTools`, default on). Tool-aware servers
  (llama.cpp --jinja, LM Studio, vLLM, Ollama, KoboldCpp) then parse the model's output into
  real `tool_calls` — deterministic commands with real arguments, and a multi-call reply is
  a multi-command turn — instead of the old garbled `<tool_call>` markup in free text. Servers
  without tool support are fenced off automatically on the first 400 (300 s pause) and the
  text fallback (parsers + the tool-call-markup salvage) keeps working either way.
- **Fact hygiene** — facts are STICKY knowledge only: the `remember` tool (and the fact
  store itself, for every writer) refuses viewer-relative facts like "object to left" or
  "the door is here" — those go stale the moment anything moves. Good facts name the thing
  and its durable place ("bed is upstairs") or a lesson; the refusal note teaches the
  restatement, and the mod no longer auto-writes "kind is front-right here" landmark junk.
- **Key places, known from boot** — a scene-wide directory of the survival machines a
  small model must be able to find without ever walking there first: every **nest**
  (egg laying), **bed** (rest), **farm plot** (`SoilTile`) and **watering can** on the map,
  with addr/distance/world-bearing/stable id, exposed as perception `places`, the console
  `cat places` command, and the console **boot line** itself. `cd`/`go_to` resolves these
  names scene-wide too (the old 60m search radius answered "unknown_place" for exactly
  the far machines), and the `water` tool's no-can dead end now reports the nearest can
  map-wide instead of nothing.
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
  of hammering retries; the state anchor tells the NPC itself and the WebUI shows
  the live status.
- **Debug console in the browser (`[Web]`)** — the in-game F6 IMGUI overlay was removed;
  the browser page on the loopback (default `http://127.0.0.1:8338/`) is the debug surface
  now: live state, the NPC's console transcript ("what did it type, what came back"), the
  latest vision caption, all config keys editable in-game, and a per-instance say-box that
  injects a line into one named NPC.
- **Distinct name per body** (from the kobold's own name + instance suffix), says it in chat.
- **Attributed chat** (opt-in) — a second Photon client makes its chat render as `KoboldName: text`
  to everyone, not attributed to the plugin owner.

### Movement & navigation

The model feels **inside the world** because it can walk around and pivot its head to look,
not just teleport to goals. Every primitive is a console command:

- **Walk** — `run [secs] [left|right]` is a short, time-bounded step (default 2s; `run 3`
  walks ~3s, `run 3 left` strafes). It is *not* a teleport — the body keeps walking until
  the seconds elapse, so `stop` halts it.
- **Pivot your head to look** — `look around` turns and returns what's in each direction
  (front/left/right… with names + distances); `look left|right|up|down` pivot 90°/25°;
  `look <deg>` an exact angle. `sonar` is a north-up map (`^` is your facing).
- **Go somewhere** — `cd <place>` / `cd id:N` plans the real route (stairs/ramps, whole
  scene) and walks it; `look around` when you arrive.
- **Walk-and-look loop** — to actually feel a room, the model alternates:
  `look around; run 3; look around; run 3`.

A fresh agent wakes with **built-in navigation routines** it can run by name — `skill
explore` (walk + look), `skill scan` (look + `ls` + `sonar`), `skill look around` — so it
doesn't have to author them first. These are normal skills (see below): they compose the
existing `run`/`turn`/`look`/`cd` primitives into a taught pattern, and the `man movement`
page teaches the walk-and-look loop.

### Skills

`skill add <name> | <step> ; <step>` saves a reusable procedure — steps must be real
commands separated by `;` — and `skill <name>` runs them as one turn. `cat skills` lists
them; `skill forget <name>` drops one. Skills **save with the agent's memories**, **transfer**
when it is swallowed and absorbed (the eater inherits them), and **ride the rebirth copy**
(a hatched child egg wakes with its parent's skills). A fresh agent seeds a few built-in
navigation routines (`look around`, `scan`, `explore`) only when its skill box is empty, so
a saved or absorbed set always wins.
