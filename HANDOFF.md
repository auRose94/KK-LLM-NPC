# KK-LLM-NPC handoff notes for another agent — 2026-09-03

Session state after a long iteration. Read **README.md** for the project description,
**SPLIT-PLAN.md** for the planned split. Everything below is what's actually in the
codebase right now and what to watch out for on resume.

## Build & deploy

- `./build.sh` — single instance (`KKLLMNPC.dll`).
- `./build.sh N` — instances 1..N (`KKLLMNPC.dll`, `KKLLMNPC2.dll`, …) — multi-kobold today.
- All DLLs deploy into `<game>/BepInEx/plugins/`. Per-instance config is
  `BepInEx/config/com.kk.llmnpc{,2,3,4}.cfg` (GUID `com.kk.llmnpc` + optional suffix).

## Game state assumptions (verified)

- `GameManager.InLevel()` — scene-name check (`!= "MainMenu" && != "ErrorScene"`); use it
  to gate the loop. `MainMap` is the playable world — do NOT block it.
- `PlayerPossession.TryGetPlayerInstance(out pp)` gives your player; `pp.kobold` is the body.
- The local player owns their body via `PhotonView.IsMine`. For AI-driven bodies the plugin
  claims ownership (`TransferOwnership(LocalPlayer)`, then `RequestOwnership()`), re-asserted
  every 3s (other mods can steal it).
- **Kobold rotation is NOT driven by the plugin.** `CharacterControllerAnimator.Start` sets
  `lookEnabled = true`; the `LookAtHandler` rotates the head toward `SetEyeRot(absolute world
  yaw, pitch)`. The body's rotation comes from the controller's own locomotion path (through
  `inputDir`) and the game's own system, not rigidbody writes. **Hands off the rigidbody
  rotation — we already broke the game twice doing that.**
- Interactions: `GenericUsable.LocalUse(Kobold)` drives machines. `User.Use()` only works on
  the player's tagged kobold — DON'T call it on our possessed body. `OvipositionSpot` is the
  egg-laying station; readiness = belly `Egg` reagent volume > 5 AND energy > 1.
- Station lock: `CharacterControllerAnimator.IsAnimating()` is the in-station flag; exit via
  `photonView.RPC("StopAnimationRPC", RpcTarget.All)` — same as player jump/cancel in a station.
- Reagent flow: `Kobold.bellyContainer: GenericReagentContainer`, `OnChange(contents, injectType)`
  — the FIRST fire after possession is a baseline snapshot (existing contents), DO NOT report
  it as a "drank X" event (that's the "gifted something" hallucination we fixed).
- Penetration: every `PenetrationTech.Penetrable` on the body exposes `penetrationNotify`
  (a delegate) — subscribe to get `(penetrable, penetrator, worldSpaceDistanceToPenetrator, …)`
  per frame + on pull-out. For the penetrator side add a `PenetrationTech.PenetratorListener`
  subclass into `Penetrator.listeners` (it's `System.Object`, not a Unity component —
  just `list.Add(listener)`).
- `Database<ScriptableReagent>.TryGetAsset(short id, out ScriptableReagent)` maps reagent id →
  asset; reagent name = `sr.name` (Localization assemblies are NOT referenced).

## Plugin architecture (current, working)

- `LLMNPCPlugin` (one instance per DLL). Fields prefixed `_cfg*` are config.
- Threads per instance:
  - `LLMLoop` — main decision loop on its own background thread, does calls via
    `RunOnMainThread` for perception/camera (Unity main-thread-only) and direct HTTP on the
    LLM thread. Catches everything and self-heals via `Update`'s watchdog.
  - `VisionWorker` — separate thread per pass; renders on main, captions on worker.
  - `CreativeCommentaryWorker` — free-voice call (higher temp) per `CommentEveryNTicks`.
  - `AnswerQuestionWorker` — `ask` tool answers on its own thread.
  - BepInEx main thread — `Update` handles: file watchers (config/DLL hot-reload), the
    LLM watchdog relaunch, and the chat-listener registration once Photon is ready.
- `FixedUpdate` drives `inputDir = Quaternion.Euler(0, _yawDeg, 0) * forward * fwdOut`,
  `inputJump`, `inputWalking`, `SetInputCrouched`, gaze (`SetEyeRot`), wall-probe, bump/ledge
  detection, idle damping of rigidbody velocity + angular velocity, and camera-clip auto-crouch
  — all with the "turn only when there's intent" guard so no drift.
- Perception splice injects `me`, `body`, `pos`, `yaw`, `blocked`, `walls`, `ground`,
  `clearance`, `vis_go`, `needs.{energy,horniness,eggs,crouch}`, `consumed`, `in_station`,
  `penetrated`, `penetrating`, `heard`, `asked`, `answered`, `rays`, `nearby` plus the memory
  block `last_thought / memory / facts / last_action / scene / history`.
- Actions come back through `response_format: json_schema` (NOT tools/tool_choice — Gemma/
  other templates 400 on tool_choice). The reply's `content` is the act-args JSON, which
  `ExtractActArgs` parses (handles proper tool_calls, content-JSON, `{"walk":…}`, markdown
  fences, truncations via regex salvage).

## Config of note (hot-reload supported — file watcher calls Config.Reload())
- `[LLM]`: Endpoint, Model, ApiKey, SystemPrompt, ThinkInterval, SendImage, ImageEveryNTicks,
  ImageOnBump/ImageOnTurn, MaxTokens, Temperature, PlanStepDelay/PlanMaxSteps,
  CommentEveryNTicks / CommentTemp.
- `[Vision]`: Enabled, EveryNTicks, Prompt, DebugDumpFrames.
- `[VisionModel]`: Model/Endpoint/ApiKey/MaxTokens (blank = fall back to `[LLM]`).
- `[Senses]`: RayCount, RayRange, AutoFindRange, ImageSize, CameraNearClip, CameraForward.
- `[General]`: BlockedScenes.
- Debug frames dump to `BepInEx/plugins/KKLLMNPC_frames/vNNNN_<yaw>.jpg`.

## Current state (built, deployed, green)

- 2 instances in plugins folder (`KKLLMNPC.dll`, `KKLLMNPC2.dll`). Both move, look, chat,
  and interact. The drift from earlier today is fixed (no rigidbody rotation by us; idle
  damping only on horizontal velocity).
- `KKLLMNPC.cs` is now split into `src/*.cs` (partial classes) — see SPLIT-PLAN.md.
  **SPLIT-PLAN.md** documents the completed split and the follow-up single-plugin
  `MaxKobolds` refactor. Both are safe next steps in a fresh session.
- The only warning: `CS0414 _user assigned but never used` — deliberate (reserved for future
  use); leave it.

## Known caveats / next candidates

- The `interact` path drives `target.LocalUse(kobold)` directly; if a station still refuses,
  log shows `interact -> cannot_use` — extend `ClassifyUsable` to categorize it and the model
  will route around.
- Hole classification in `ClassifyPenetrable` is name-based; modded penetrables land on
  the "hole" fallback. Extend synonyms as they come up.
- `_visionSteer` parses `go:<deg>:<reason>` from the vision caption. If your vision model
  ignores the format, either tighten `[Vision] Prompt` or set `[Vision] Enabled=false`.
- The vision caption *auto-remember*s "X is <dir>" lines into `facts` — if captions start
  noise-filling facts, restrict the heuristic in the vision worker.
