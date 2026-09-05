# Changes Log

## 2026-09-05

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
