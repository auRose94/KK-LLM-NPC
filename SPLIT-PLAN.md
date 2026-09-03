# Split KKLLMNPC.cs into partial-class files — DONE (2026-09-03)

The split is complete. `KKLLMNPC.cs` (3,440 lines) now lives as nine files under
`src/`, each a `public partial class LLMNPCPlugin` (plus the standalone `Json.cs`).
`build.sh` compiles `src/*.cs`. Build is green (`build.sh 4`) with only the
pre-existing benign `CS0414 _user` warning. Behavior is byte-for-byte identical.

## What changed
- `src/Main.cs` — `[BepInPlugin]` decl, config fields, Unity lifecycle, main-thread
  marshalling, scene gating, memory (history/facts)
- `src/Body.cs` — possession/teardown, camera, belly/egg + penetration awareness
- `src/Senses.cs` — perception build, ray fan, clearance, nearby, equipment
- `src/Movement.cs` — `FixedUpdate`, move/stop, gaze, camera-clip auto-crouch
- `src/Llm.cs` — decision loop, HTTP, act-schema parsing, ask/commentary workers
- `src/Vision.cs` — background vision caption thread
- `src/Tools.cs` — `Tool*` actions + `RunTool` switch
- `src/Chat.cs` — Photon chat listener, `ToolSay`, naming
- `src/Json.cs` — `Json` + `JsonObj` (independent helpers)

## Notes / deviations from the original plan
- The plan assumed an `InstanceSuffix` in the `[BepInPlugin]` GUID; the actual
  source uses a fixed `"com.kk.llmnpc"` GUID. Kept as-is (pure motion).
- `Config.cs` was folded into `Main.cs` (config fields live with the lifecycle).
- The split was done with a brace/string/comment-aware Python script
  (`/tmp/opencode/split.py`) and verified line-faithful against the original.

## Deferred — next session after split
Multi-kobold **as one plugin** (option 2 from 2026-09-02):
- Introduce `NpcBody` container class (fields = everything movement/body/senses-state)
- `LLMNPCPlugin` keeps a `List<NpcBody>`; spawns `MaxKobolds` bodies on free AI kobolds
- Each body: own LLM+vision threads, own memory/name; shared config + endpoint
- Plugin `Update`/`FixedUpdate` dispatch per body; Photon chat fans out to all bodies
- Add `[General] MaxKobolds` (default 1).
Do it only after the partial-class split is green.
