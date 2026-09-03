# Split KKLLMNPC.cs into partial-class files (planned — do in a fresh session)

## Current state
Single file `KKLLMNPC.cs` (~3,250 lines): one `LLMNPCPlugin` class containing every
feature end-to-end, plus shared helpers `Json` and `JsonObj` at file bottom. It works;
the failure mode of splitting it *in-place* with scripted edits is member-fragmentation
at continuation lines and stale-context edits.

## Why
The plugin class has grown into ~90 members with cross-cutting state
(`_kobold`, `_head`, `_yawDeg`, `_stateLock`, `_vision*`…) referenced by dozens of
methods. A fresh rewrite as partial classes groups by responsibility and makes the
multi-kobold refactor viable as a follow-up (each concern becomes its own file; only
`Movement/Body/Senses` need per-body fields the rest share).

## Target structure (same assembly, same GUIDs, new files)
```
KKLLMNPC/               (currently: KKLLMNPC.cs at repo root)
  Main.cs               // [BepInPlugin] decl + partial-class shell + InstanceSuffix
                        // + config field declarations + Awake/OnDestroy/Update + watchers
                        // + main-thread marshalling (RunOnMainThread*, SafeRun)
                        // + scene gating (IsPlayableScene, MarkScene)
  Config.cs             // all ConfigEntry<> fields moved from Main? (keep with Main)
  Chat.cs               // ToolSay, OnEvent, RecentPlayerChat, PickName/MyName, greeting
  Body.cs               // EnsureBody/Possess/TeardownBody/SetupCamera + belly/egg/penetration
                        // listeners (Subscribe/Unsubscribe*, OnBellyReagentsChanged,
                        // ClassifyPenetrable, *Info) + equipment awareness (DescribeEquipment)
  Senses.cs             // BuildPerception(Safe), ray fan, FanClearance, DescribeNearby,
                        // RelBearing, ClassifyUsable, Probes (ProbeGround/ProbeWallProximity/
                        // CastBlocked/ProbeSurfaceTop), StimTrend, CleanName
  Movement.cs           // FixedUpdate(+Safe), SetMove/StopMove, IsOwnCollider/FindClearHeading,
                        // gaze wander + partner-locked gaze, camera clip auto-crouch,
                        // movement/nav state fields
  Llm.cs                // LLMLoop, QueryLLM, ActSchema/ActionParamProps, ExtractActArgs/Unwrap/
                        // TrySalvageTruncated, ExecuteToolCalls/ExecuteStep/SummarizeResult,
                        // Ask/AnswerQuestionWorker
  Vision.cs             // MaybeStartVisionPass, VisionWorker, DumpVisionFrame, CaptionImage
  Tools.cs              // Tool* methods + RunTool switch + ToolSchemas/ActSchema shared schema
  Json.cs               // Json (Write/Parse) + JsonObj — already independent
```
`Json.cs` separates cleanly today; all others become `public partial class LLMNPCPlugin`.

## Constraints
- **Attribute GUIDs must not change**: `[BepInPlugin("com.kk.llmnpc" + InstanceSuffix, ...)]` exact string concat; each compiled instance must produce identical semantics to current dlls (KKLLMNPC.dll, KKLLMNPC2.dll, …).
- build.sh: change `mcs … KKLLMNPC.cs` to `mcs … *.cs` (or explicit list) in each instance's invocation; keep `-define:KK_INSTANCE_N` plumbing.
- Preserve behavior byte-for-byte: don't "improve" anything during the split. Pure motion.
- All fields keep their current names/types/initializers; methods move verbatim.

## Safe split procedure (fresh session)
1. `git init && git add -A && git commit -m "pre-split"` if you want a rollback point.
   (or just keep /tmp/opencode/KKLLMNPC.bak3.cs as the rollback).
2. Move `Json.cs` first — builds immediately; sanity compile.
3. Move `Chat.cs` → `Vision.cs` → `Llm.cs` → `Tools.cs` (`Tools.cs` needs `Senses`/`Movement` methods; move late).
4. Move `Senses.cs`, then `Movement.cs`, then `Body.cs`.
5. Finally collapse `Main.cs` to just the shell + config + Unity lifecycle + marshalling.
6. After **each** file move: `bash build.sh N` must compile with only the pre-existing
   benign warnings (currently CS0414 on `_user`). Don't proceed past a red build.
7. Final: `bash build.sh 4` and confirm all instance DLLs deployed.

## Acceptance
- `wc -l` of the largest new file < 900.
- `build.sh 2` produces both DLLs green.
- In-game launch log shows both instances: `Loading [KKLLMNPC 1.0.0]`, `Loading [KKLLMNPC2 1.0.0]`, then "this kobold calls itself …" per instance — same as today.

## Deferred — next session after split
Multi-kobold **as one plugin** (option 2 from 2026-09-02):
- Introduce `NpcBody` container class (fields = everything movement/body/senses-state)
- `LLMNPCPlugin` keeps a `List<NpcBody>`; spawns `MaxKobolds` bodies on free AI kobolds
- Each body: own LLM+vision threads, own memory/name; shared config + endpoint
- Plugin `Update`/`FixedUpdate` dispatch per body; Photon chat fans out to all bodies
- Add `[General] MaxKobolds` (default 1).
Do it only after the partial-class split is green.
