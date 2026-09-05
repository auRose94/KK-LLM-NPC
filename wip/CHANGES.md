# Changes Log

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
