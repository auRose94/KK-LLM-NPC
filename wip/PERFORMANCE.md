# Performance Work

## 2026-09-04

### Completed Optimizations

- **Dynamic ThinkInterval**
  - File: `Llm.cs`
  - `GetDynamicThinkInterval()` returns base interval when active (moving/in-station/penetrated), else `min(base*2, 0.8s)`
  - `UpdateActivity()` updates `_lastMoveTime` on movement
  - Log: `think interval: Xs (active/idle)`

- **Texture Pooling**
  - Files: `NPCInstance.cs`, `Senses.cs`
  - Fields: `_texPoolL`, `_texPoolR`, `_texPoolStereo`
  - `CaptureImageBytes()` reuses textures, creates only on size mismatch
  - Log: `texture pool created/reused: L WxH`

- **Probe Merging**
  - `ProbeWallProximity` now reads from `FanClearance`
  - `_bumpInfo` updated from clearance dict in `BuildPerceptionSafe`

- **DescribeNearby Cooldown**
  - Cache `_cachedNearby` for 2 ticks
  - Reduces Physics.OverlapSphere calls

- **Plan Offload**
  - `ExecuteToolCalls` spawns background thread for plan steps
  - `RunPlanSteps` respects `_cfgMaxPlan` and `_running`
  - LLM loop no longer blocked by plan sleeps

- **Perception Throttling**
  - `_lastFullPerceptionTick`, `_cachedPerception`
  - Active: full build every tick
  - Idle: full build every 2 ticks, cached ticks refresh yaw/needs
  - Logs: `perception cache hit` / `perception full build`

### Verification Logs
- `think interval: Xs (active/idle)`
- `texture pool created/reused: L WxH`
- `perception cache hit (active=..., throttle=...)`
- `perception full build (active=...)`

### Next
- Verify logs in play
- Confirm raycast reduction via cache hits
- Optional static scene early-out
