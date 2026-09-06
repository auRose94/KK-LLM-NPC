# Changes Log

## 2026-09-05
- Configurable camera far clip
  - New `[Senses] CameraFarClip=80` (m) replaces the hardcoded farClipPlane on
    the first-person eye camera(s); lowering it shrinks draw distance and image
    size for smaller payloads. Hot-reloaded on config change (MaybeRebuildCamera)
- Killed 'barrier'/'beam' hallucinations in perception
  - The code literally branded anonymous low geometry kind="barrier" every
    turn (rays + radar 'B'), which models kept dramatizing; that word is gone
    from all runtime payloads. Low anonymous geometry (sills/windows) is now
    kind "s" with radar char 'S', phrasing "sill" in survey/openings, and the
    LEGEND teaches 's = low sill/window (step-over, harmless)'
  - New hard instruction in LEGEND + vision prompt: walls/ceilings/floors/
    beams/sills/distant furniture are BACKGROUND — never comment, narrate, or
    react; they matter only when they actually block a path or target (then
    'ground'/'clearance'/'blocked' say so). The vision scene-caption model is
    told outright to ignore structural architecture (beams/supports/trims)
- Slow-burn horniness
  - New per-NPC `_horny` accumulator (0-1) that climbs slowly while the body
    gets NO stimulation, so the NPC starts wanting play even when nothing is
    happening. Main-thread FixedUpdate (`Movement.UpdateHorniness`), volatile
    read by perception
  - Gates: any game stim (>= 0.15), being in an animation station, penetrated,
    or dick-inside holds the value; a climax (stim swinging from >= 0.8 to
    < 0.3) spends it back to ~0.05. Resets to baseline when taking a body
  - Perception `needs.horniness` now reports live game stim while being played
    with, otherwise the slow-burn value + "(slow-burn: unstimulated a while —
    horniness climbing)" so the model can act on it. GOAL priority 3 extended
    ('stim' up OR 'horniness' high/slow-burn → find play)
  - Config: `[Needs] HornyClimbPerMin` (5 = half-horny after ~6 min idle),
    `[Needs] HornyBaseline` (0.08)
- Pathfinding now understands doors (closed vs open)
  - Before: door colliders were unconditionally skipped when sampling cells, so
    A* treated a closed door exactly like an open one (pass-through) and the
    planner "didn't know" doors existed
  - Now: a cell whose only obstruction is a door panel is marked as a CLOSED
    DOOR (not a wall). A* will still route through it if it's the only way (the
    path is valid — the body opens the door on arrival via
    Movement.MaybeOpenDoorAhead), but crossing one costs ~5.5 cells of
    "toll", so any open detour is strongly preferred. An OPEN door's panel is
    out of the way → its cells read as plain clear floor
  - go_to now reports `(a CLOSED door is on the way — the body will open it
    when it gets there, or use interact on it)` so the LLM can plan around it
  - Ground layer: door top-faces are still excluded from floor sampling (the
    NPC never "walks on top of" a door)
- NPC body-swap with the player via the body-swap machine
  - The game's `BrainSwapperMachine` (station class "bodyswap") swaps who
    controls the two pod occupants: after both climb on, `AssignKobolds`
    flips `CharacterDescriptor` LocalPlayer/AIPlayer flags + transfers money
    and ends by raising static `bodySwapped(Kobold a, Kobold b)`.
    The Kobold objects themselves don't move — only control does.
  - `Body.cs`: `PossessBody` binding extracted into `BindBody(target,
    reuseIdentity)`; NPC now subscribes to `BrainSwapperMachine.bodySwapped`
    while it owns a body (unhooked on teardown). `OnBodySwap` -> `RebindAfterSwap`
    re-claims the *other* body, keeps NPC name, rebuilds persona from the new
    body, re-wires controller/head/camera/photon/station/penetration state.
    Camera/RTT/first-person rig freed before re-wiring so the swap doesn't
    strand cameras on the old head
  - Prompt THINGS: teaches the ritual — you + a partner BOTH climb on, it
    swaps after a few seconds, you keep name/memories but wake up in their
    body, ask the player to climb on, jump off if nobody joins
  - Local game: NPC is on pod A, player gets on pod B → ~4s later the player
    takes the NPC's old body and the NPC rebinds to the player's old body
- Senses: radar now configurable + disable-able
  - New binds (defaults = previous behavior): `[Senses] RadarEnabled=true`,
    `[Senses] RadarSize=10`, `[Senses] RadarScale=1.2`
  - `RadarEnabled=false` omits the radar from perception entirely (smaller
    payload); prompt now frames radar glyphs as abstract map symbols (not
    living figures) — some models were misreading `W`/`K` glyphs as "people
    staring at them" and treating the map as a threat
  - `BuildRadarMap` reads size/scale from config instead of `const`s
- Walk / go_to movement reliability
  - `Tools.ToolGoTo` guards before doing anything:
    - `no_movement_controller` — explicit `ok:false` instead of silently
      "ok" while the body never moves (was the invisible failure mode)
    - `too_far` — refuses targets beyond ~`PathfindingWindow*2 + 30m` (70m
      default). Root cause of "walk/goto not being used": the model chased a
      phantom 345m-away target on the huge `MainMap++`, A* always returned
      null (unreachable), every go_to fell back to straight-line obstacle
      steering, and the kobold physically never moved (go_to dist frozen ~345)
    - Auto-exits an animation station on go_to (`StopAnimationRPC`) so a body
      that "wakes up" mid-station-animation can move again without requiring
      the model to separately call exit_station
  - `Pathfinding.cs` diagnostics: `path: null — ...` log lines stating which
    stage failed (start floor / goal ring / A* expanded count), plus
    `Astar.ExpandedCount`; `FindPath` now logs a reason instead of a silent null
  - System prompt NAVIGATION: documents the ~70m reach, teaches that a
    `too_far` result means unreachable (re-pick or ask the player), keeps
    go_to as the only reliable travel tool

### Completed
- Visionless spatial awareness (`area` field)
  - `Senses.cs:SpatialLayout` — 360° chest-height sweep (16 rays, 22.5° steps)
    reporting cardinal distances (front/right/back/left + what's there), open
    headings (>=8m), a recommended best heading, and nearest named things
    (walls vs. usables vs. kobolds vs. player)
  - `BuildPerceptionSafe` adds `area` to perception JSON
  - Backfills `_sceneDesc` (visionless fallback) when `Vision.Enabled=false` or
    `_sceneDesc` is empty/unknown, so commentary / ask / scene memory get the
    description instead of "scene: unknown"
  - System prompt default: short clause teaching the model to trust `area`
    for navigation esp. with no image attached
- Kill Mono "Illegal byte sequence" tool failures (lone-surrogate poisoning)
  - Root cause: an unpaired UTF-16 surrogate from LLM output reaches a native
    call and Mono dies with `ExecutionEngineException: String conversion error:
    Illegal byte sequence ... in the input`; the exception text then gets stored
    in tool result/history and re-serialized every tick ("happens once, carries")
  - Added `Llm.cs:Sanitize` — removes lone surrogates, keeps valid pairs
  - `Json.WriteStr` now scrubs lone surrogates at serialization (every stored
    string crosses this path: history/facts/thoughts/chat/perception)
  - Sanitized at ingest: LLM SSE content+tool_args (`QueryLLM`), commentary
    line, ask answer, `ToolSay` text, `ToolRemember`, `RememberFact`, vision
    caption
  - `RunTool` catch: "Illegal byte sequence"/"String conversion" messages are
    replaced with stable ASCII `bad_encoding_from_llm` so the toxic text never
    enters state
- Structured default system prompt
  - Reorganized the LLM system prompt from prose into labeled JSON-style
    sections: RESPONSE CONTRACT (exact act-JSON shape + strict-schema note),
    WORLD, YOUR BODY, GOAL, PERCEPTION, NAVIGATION, INTERACT, THINGS, SOCIAL,
    TOOLS, LEGEND
  - The model reply was already JSON-constrained server-side via
    `response_format: json_schema` (ActSchema); the prompt now states the
    contract explicitly up front so the model emits the conforming object
- A* pathfinding for `go_to` navigation
  - New `src/Pathfinding.cs`: `FindPath` builds a bounded walkability grid
    around the start->goal segment (projected onto the kobold's floor plane,
    so it stays on the same level; cells on other floors are blocked) and runs
    8-connected A* with diagonal corner-cut prevention, octile heuristic,
    binary-heap open set, and a node budget (any failure → `null` → fallback
    to the old direct straight-line steering)
  - Walkability: ground ray within 1.1m of the floor plane + chest-height
    `OverlapSphereNonAlloc` (reusable `_pathColliderBuf`); ignores own
    colliders and closed `GenericUsable` doors (they open on approach)
  - String-pull smoothing via chest-height `RayClear` LOS between wayposts
  - Runs on main thread (`RunOnMainThread`, 8000ms) because it uses Physics
  - `ToolGoTo` (Tools.cs): plans when enabled and `stopAt > 1.5m`; reuses an
    active path when the goal is within 1.5m and the plan is <6s old (no
    replan spam); falls back to direct steering otherwise
  - `Movement.cs` `FixedUpdateSafe`: steers waypoint-to-waypoint, advancing
    `_pathIdx` when within 0.8m of a post instead of declaring arrival, then
    stops on the final one
  - Path/state teardown added in `Body.cs` where `_navTarget` is cleared
  - Config (`Movement`): `PathfindingEnabled` (true), `PathfindingCellSize`
    (0.5), `PathfindingWindow` (20), `PathfindingNodes` (9000)
- Stimulus attribution: NPCs no longer miscredit machines/eggs as "the player"
  - Root cause: perception reported arousal (`horniness`) and bare `in_station`
    without naming the source, so when pleasure came from a station/machine or
    lay the model defaulted to "the player is making me feel good"
  - New `stim_from` perception field naming who/what is responsible: live
    partner (`being_filled_by:`/`inside:`) from `_pen`/`_dickIn`, else the
    machine/station last used while (or within 8s of being) mounted
  - Source recorded at the known moments: `ToolInteract` after `LocalUse`
    (station/machine), `OnPenetrationTick` entry (partner or machine part),
    `NotifyDickIn` entry (partner name); cleared on `exit_station` and body
    teardown
  - System prompt (INTERACT): stim_from credit rule — eggs/nests/machines are
    objects, not people; never attribute pleasant feelings to the player by
    default
- Station identification + movement fixes:
  - `ClassifyUsable` reworked: "play" grouping now catches PlayStation/
    PlayTogether/ActionStation + sex/erotic names; "bed" catches sleep/cot/
    mattress/nap/rest. Order matters — "play" checked before "bed" so a
    "PlayBed"-style object is a pleasure station, not a bed (that mislabel
    was why the model thought play stations were for resting)
  - New `PurposeFor(kind)`: one phrase per category appended to each nearby
    'i' field, e.g. `play (pleasure station)`, `bed (resting - can also be
    used for play)` so perception self-documents and the model never guesses
  - System prompt (GOAL/THINGS): explicit play-station-is-for-pleasure /
    bed-is-for-rest rule; play stations never for sleeping
  - System prompt (NAVIGATION/TOOLS): go_to is the only reliable travel tool
    (grid pathfinding); move_to/walk explicitly demoted to short post-
    arrival nudges, never cross-room travel
- 3D layered pathfinding (Pathfinding.cs rewritten):
  - Was: a flat 2D grid projected on the kobold's starting floor; any cell
    on another floor was blocked, so multi-level routes were impossible and
    ramps/stairs/stacks collapsed onto one elevation
  - Now: each cell is sampled with one downward multi-hit ray for up to 4
    DISTINCT floor elevations; every layer gets its own chest-clearance check
  - A* nodes are (cell, layer); two adjacent cells connect when the target
    floor is at most `ClimbStep` (0.5m) above the current one — steps/ramps/
    stairs are traversed naturally, climbing taller is forbidden, and dropping
    DOWN any distance stays allowed (walking off ledges still works). Up-steps
    carry a small extra cost so the search prefers a level route
  - Waypoints carry each cell's real ground height (CellPos/CellPos), so paths
    rise/fall with the level; string-pull smoothing now raycasts 0.75m above
    each waypoint's own floor (sloped segments stay above the ground)
  - Robustness: start cell force-grounds at the body height if the sampler
    can't match it (own colliders skipped, cramped spawns still route); the
    exact goal cell is resolved to the walkable floor nearest the current
    elevation, and if the exact cell is unwalkable a 6-cell ring near-miss
    search lands the path at the nearest passable approach instead of failing
  - Same call-site (FindPath(start, goal)); ToolGoTo fallback unchanged
  - Same config (PathfindingCellSize/Window/Nodes) still bounds the budget

## 2026-09-04

### Completed
- Dynamic ThinkInterval based on activity
  - `Llm.cs:GetDynamicThinkInterval` + `UpdateActivity` halving interval on moving/in-station/penetrated; idle doubles to max 0.8s
  - Logging added: `think interval: Xs (active/idle)`
- Pool Texture2D in CaptureImageBytes
  - `NPCInstance.cs` added `_texPoolL/_texPoolR/_texPoolStereo`
  - `Senses.cs:CaptureImageBytes` reuses pooled textures instead of `new Texture2D`
  - Logging added: `texture pool created/reused: L WxH`
- Merge ProbeWallProximity + FanClearance
  - `ProbeWallProximity` reads from `FanClearance` results
  - `BuildPerceptionSafe` updates `_bumpInfo` from clearance dict
- Cooldown DescribeNearby to every 2 ticks
  - `NPCInstance.cs` added `_cachedNearby/_lastNearbyTick`
  - `Senses.cs:DescribeNearby` returns cached list for 2 ticks
- Move plan stepping off LLM thread
  - `Llm.cs:ExecuteToolCalls` spawns background `RunPlanSteps` for plan array
  - LLM loop no longer sleeps on plan waits
- Perception throttling
  - `NPCInstance.cs` added `_lastFullPerceptionTick`, `_cachedPerception`
  - `Senses.BuildPerception` throttles full raycast build: active=every tick, idle=every 2 ticks
  - Cached ticks refresh yaw/needs only
  - Logging added: `perception cache hit` / `perception full build`

### Next 2 Moves
1. Offload plan step delays & verify dynamic interval and texture pool
   - Verify `RunPlanSteps` runs correctly, no race on `_running`, logs plan cap
   - Instrument interval changes: log when interval changes due to activity
   - Verify texture pool reuse: ensure `Release` not needed, no leaks, check `ReadPixels` size match
   - **Done**: Added logging to `GetDynamicThinkInterval` and `CaptureImageBytes` to confirm reuse
2. Awareness / perception hardening
   - Cache `DescribeNearby` results for 2 ticks (done) → verify stability of IDs
   - Reduce `BuildPerception` frequency when idle
   - Optional: add early-out for static scenes
   - **Done**: Added perception throttling with `_cachedPerception` and `_lastFullPerceptionTick`; active=1 tick, idle=2 ticks; lightweight refresh of yaw/needs on cached ticks

## Notes
- Performance leftovers list still has awareness items pending.
- Keep changes minimal per lean-ctx rules.
