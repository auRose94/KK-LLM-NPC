# Improvement Backlog & Progress

Working tracker for the improvement sweep started 2026-10-02, born from a full
code survey (code health + feature/UX gap analysis). Every item below was
verified against the code before being listed.

Status legend: `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` decided against (reason noted)

Build/test after every wave: `./build.sh` then `./run_tests.sh`.
All gameplay math avoids `Mathf.PI` / `Random.insideUnitCircle` (unavailable under mcs).

---

## Wave 1 — thread safety & quick wins

- [x] **CH-02 — Unify `_stateLock` on a plain monitor.** `NPCInstance._stateLock` was a
  `ReaderWriterLockSlim` used as a Monitor by ~30 `lock()` sites while only `ResetMind`
  spoke RWLS — two independent guards over the same fields. Now a plain `object` monitor
  everywhere, with a comment warning against re-mixing mechanisms (verified: IdentityBot
  has its own, correct, plain-object lock; the reported "unlocked yaw writes" in Tools.cs
  were a false alarm — locked all along). *DONE 2026-10-02.*
- [x] **CH-04 — Cross-thread field discipline.** `_followMode` was already volatile and
  ToolLook's writes were already locked. Made `volatile`: `_blockedInfo`, `_bumpInfo`,
  `_lastVisionCaption` (main/vision-thread writes, LLM-thread reads, confirmed against
  call graphs). *DONE 2026-10-02.*
- [x] **CH-13 — Honor `sleep [secs]`.** `CmdSleep` now parses its argument (capped at
  `Consts.SleepMaxSeconds` = 300 s) and sets a volatile `_sleepUntilTime`; the console
  branch of the LLM loop defers turns (and vision) until it passes. Wake-early triggers:
  player chat (`HandleChat`), body events (`PushWorldEvent` — grabbed/thrown/danger),
  ask answers landing; `ResetMind` clears it; state anchor shows "sleeping: Ns left".
  Bare `sleep` still just ends the turn. Help text updated. *DONE 2026-10-02.*
- [x] **CH-08 — Demote hot-path logging.** Perception cache-hit/full-build lines moved
  to Debug (Senses.cs), legacy `act:`/`step action` telemetry to Debug (Llm.cs),
  say-suppression note to Debug (Chat.cs). Deliberately KEPT at INFO: the per-say
  `[NPC] name: text` line (documented as the log's entertainment channel). Ownership
  fight re-asserts every 3 s but warns only every 30 s now. *DONE 2026-10-02.*
- [x] **CH-11 — Console-mode commentary.** Second entry point
  `MaybeCreativeCommentaryConsole()` calls the musing worker with the console state
  anchor instead of a perception JSON; wired after each console turn. Fires while
  goal/in.station/follow states are live; fully-idle ticks stay quiet (anti-spam by
  design). Legacy path unchanged. *DONE 2026-10-02.*

## Wave 2 — resilience & correctness

- [x] **CH-14 — Endpoint health.** Cross-tick circuit breaker added: `_endpointFails` /
  `_endpointBlockUntil` (volatile), filled by `PostChatPayload` via `NoteEndpointFail()`
  (5/10/20/30 s escalating idle, logged at 1st and every 5th fail) and reset by
  `NoteEndpointOk()` when the server becomes reachable again. Both the console branch and
  the legacy loop skip turns while blocked; `ConsoleTurn` also returns early when no
  endpoint is configured (this also stops the old wart of appending a bare `$` prompt to
  the transcript forever). Surface: state anchor gains `endpoint: DOWN — retrying in Ns`
  and the Overlay State tab shows a color-coded endpoint row (box height raised for the
  extra line). *DONE 2026-10-02.*
- [x] **CH-03 — Thread-safe mind reset.** `ResetMind(why)` is now a dispatcher: with a
  live LLM thread it queues a volatile `why` string; the LLMLoop applies it at the top of
  each iteration (a turn boundary — the only place transcript/facts/lists aren't being
  read) via the renamed `ResetMindNow(why)`. The identity-bot stop inside the wipe is
  marshalled to the main thread (`RunOnMainThreadAsync`) since the wipe can now run on the
  loop thread. Dead-loop callers apply immediately as before. *DONE 2026-10-02.*
- [x] **CH-07 — OverlapSphere main-thread + NonAlloc.** `FindPlaceByName`'s 60 m scan now
  runs inside `RunOnMainThread` with a 64-slot NonAlloc buffer (`_toolColliderBuf`).
  Verified `FindBestUsable` (the second reported site) already runs inside
  `ToolInteract`'s `RunOnMainThread` — no change needed there. *DONE 2026-10-02.*
- [x] **CH-12 — Wire the tested compaction.** Survey correction: `MergeFacts` at level ≥3
  and the dynamic `MaxHistory/MaxFacts/MaxThoughts` limits were ALREADY wired; the real
  gaps were `SummarizeHistory` (zero callers) and `DynamicMaxChatLog` (zero callers).
  Now: `HistoryJson` at compaction level ≥3 collapses older entries into
  "(earlier: Nx action)" counts via `ContextCompaction.SummarizeHistory`; `ChatLogJson`
  caps to `DynamicMaxChatLog` under pressure; and console-mode trims
  (`TrimConsoleConversation`) leave an auto-refreshing `earlier_console:` fact behind
  instead of silent amnesia. *DONE 2026-10-02.*

## Wave 3 — performance & dedup

- [x] **CH-01 — Resolve PathWorker: DELETED.** The survey confirmed all real A* is
  main-thread by design (the local window grid samples Physics per cell, and post
  smoothing raycasts) — the background worker could never be wired without a different
  grid architecture, and nothing ever enqueued to it anyway (`_receivedPath` had no
  writers either). Removed PathWorker (thread loop, Enqueue/GetResult, placeholder
  ComputePath), the polling sites in Movement/Tools, and the Start/Stop calls in Main.
  ~140 lines of dead scaffolding gone, one idle 10 ms-polling thread gone. Also removed
  the dead `EmitAmbient`-adjacent claim in ARCHITECTURE (done in final pass docs sync).
  *DONE 2026-10-02.*
- [x] **CH-05 — Gate per-tick raycasts.** Walk-probe/ledge rays were already gated on
  actually driving (`fwdOut > 0`); the real unconditional cost was `ProbeWallProximity`
  (4 rays + string building, now 4 Hz) and `CheckCameraClip` (1 ray, now 5 Hz). The last
  wall scan persists between runs (`result["walls"]` no longer nulls every frame).
  *DONE 2026-10-02.*
- [x] **CH-06 — Collider perception cache.** New per-collider memo (5 s TTL, 512-entry
  cap, cleared on mind reset) returns name/kind/renderer-bounds/facing for a ray hit;
  the per-hit hierarchy walk (`GetComponentInChildren<Renderer>` etc.) amortizes to once
  per collider per 5 s. The hit-point-dependent sill classification stays per ray.
  *DONE 2026-10-02.*
- [x] **CH-09 — Deduplicate shared helpers.** `Consts.Rad2Deg` replaces all 10 literal
  copies; `TextUtil.AsciiSafe` replaces 3 inline Linq strippers; Levenshtein now has one
  home (ChatSimilarity.LevenshteinDistance — Llm.cs and ConsoleShell.cs both delegate,
  console-shell test compiles it too); ray-clear smoothing (Pathfinding vs WorldMap
  byte-identical pair) unified as `WorldMap.SegmentClear(a, b, reject-predicate)` with
  each pipeline keeping its own collider rule. *DONE 2026-10-02.*
- [x] **CH-10 — Shared scene cache.** New `SceneCache.cs` (typed, TTL'd, main-thread
  only, cleared on world exit). Converted: Body/Main/Tools/Senses kobold scans, WorldMap
  anchors, Farming per perception, Cooking scans, CarrySense's bespoke mail cache.
  BONUS FIX found during conversion: Farming/Cooking's `FindNearest*` helpers ran
  `FindObjectsOfType` ON THE LLM THREAD (outside their tools' marshals) — they now
  marshal internally via one shared generic (Farming.FindNearestOf<T>). *DONE 2026-10-02.*

## Wave 4 — player-facing features

- [x] **FT-01 — Console "brain viewer" overlay tab.** New Mind tab (F6): the NPC's
  console transcript (role-colored, last 18 entries, refreshed off a monotonic
  `_consoleVersion` counter instead of per-IMGUI-frame copies) + the latest vision
  caption. The planned frame thumbnail died on arrival — this KoboldKare build's
  IMGUI module is stripped of GUI.DrawTexture AND GUIContent-based Box/Label overloads
  (verified against the DLL) — so the caption mirrors in-game and frames stay in
  `KKLLMNPC_frames/`. *DONE 2026-10-02.*
- [x] **FT-02 — Named addressing.** `"Ember, follow me"` / `"Ember: hi"` / `"@Fern hey"`
  lands on that instance ONLY, with the address stripped so command words still parse
  (word-boundary safe: "Ashtray" doesn't address "Ash"). Unaddressed lines still
  broadcast. Plus a per-instance say-box in the Overlay's Instances tab (click, type,
  Enter — reuses the custom IMGUI text-input stack, committing via
  `InjectPlayerChat`). *DONE 2026-10-02.*
- [x] **FT-03 — NPC↔NPC awareness.** The blanket `isOurs` filter is replaced by
  per-instance routing: an agent's own words never reach it; SIBLING speech reaches a
  sibling only when addressed and bounded by a 2-per-minute duet cap. A static
  body-id→agent-name registry (maintained on bind/unbind/finalize/bodyswap) makes
  fellow agents visible, named, in `ps`, `ls` and perception. Station-claim tags were
  considered and deferred — the reactive `:busy` already covers the common race, and a
  claim registry without exit-hook rigor risks lying to the model. *DONE 2026-10-02
  (claims deferred).*
- [x] **FT-04 — Coins + buy.** New `src/Economy.cs` module (ModuleRegistry pattern):
  `buy [name|id]` resolves machine construction contracts / the kobold dispenser /
  `GenericPurchasable` shop items within reach, honors the game's own
  `CanUse`+`LocalUse` public path (exactly what a human click does — it spends the NPC
  body's OWN `MoneyHolder` wallet and broadcasts the RPCUse), reports cost/coins paid.
  Private costs read via reflection. Perception key `economy` (coins + purchasables
  with prices), coins in `whoami`/`status`/`cat needs`, both prompts + console prompt
  file teach it. *DONE 2026-10-02.*
- [x] **FT-05 — Memory persistence per body.** New `src/MemoryStore.cs`
  (`BepInEx/config/kkllmnpc_memory/<body>.json`, keyed by the body's serialized object
  name which survives the game save). Teardown (`sold/lost/world change`) writes
  {name, persona, facts, goal}; a later fresh bind of the SAME body restores them and
  skips the LLM name re-pick (name collision with another copy → fall back to fresh
  wake). Bodyswap still keeps the mind in-memory; brand-new bodies still wipe. Save is
  fire-and-forget with defensive locks; restore is main-thread (BindBody). *DONE
  2026-10-02.*
- [x] **FT-06 — Scrollable config editor.** The hand-coded 16-entry list is replaced
  by reflection over the plugin's bound `_cfg*` fields → every key in every section
  editable in-game (this BepInEx build has no public `ConfigFile.Bindings`; the
  entry objects' Get/SetSerializedValue + Definition reflection suffice). Dynamic list
  height (the old one silently clipped at boxH). Hotkey doc fixed to F6. *DONE
  2026-10-02.*
- [x] **FT-07 — Time awareness (adapted).** Research verdict: KoboldKare ships NO
  day/night clock — `DayNightCycle` is a vestigial metabolism ticker and all clock
  events are orphaned in commented-out code. So the feature is honestly re-shaped:
  `awake: 2h07m` (session uptime of the body) in the state anchor, the legacy needs
  block and `cat needs`, so a model can reason "been up 2h → nap". A fake game clock
  would have lied to the model. *DONE 2026-10-02.*
- [x] **FT-08 — Speech richness.** Bubble duration now scales with text (2.5 s + 1 s
  per 12 chars, clamped 3–10 s: the game's own 2 s minimum plus length-extension made
  a flat 4 s truncate long/hold short lines oddly). `emote` command (FT-08's core)
  implemented: body language through the same three channels, asterisk-wrapped.
  Per-NPC voice packs (`Chatter.SetYowlPack`, public research-backed API with named
  packs available) — deferred deliberately: loading Addressables from a mod is a
  runtime-probability, not a certainty, and it isn't worth shipping blind. *DONE
  2026-10-02 (voice deferred).*

---

## Decisions & notes

- Item CH-12 and the CHANGELOG's own "Known gaps" section overlap — landing this
  retires that gap entry.
- Legacy push-perception mode stays intact behind `[Console] Enabled=false` through all
  of this; every change must keep both modes working.
- `EmitAmbient` stays commented-out (documented as deliberate); CH-11 covers aliveness.

## Post-wave review (independent diff audit, same day)

An independent review pass over the full sweep diff caught 7 regressions, all fixed
before wrap-up:
1. **Overlay tab routing was live-vs-dead split** — the real draw path lives in
   `Main.OnGUICallback()` (Main.cs), not the deleted `DrawOverlayWindow` clone in
   Overlay.cs; my new 4-tab switch only existed in the dead one, so "Mind"/"Config"/
   "Instances" tabs misrouted. Fixed Main.cs's live switch + removed the dead clone.
2. **Memory restore always refused**: the names file never releases reservations, so
   `IsTaken(savedName)` is permanently true — restore now RECLAIMS via `MarkTaken`
   (the NameRegistry-documented re-claim path) and only refuses when a LIVE body
   currently wears that name (`BodyIdForAgentName` lookup added to the registry).
3. **The deferred mind wipe would have erased a just-restored life one think tick
   later** — the fresh-bind wipe in `BindBody` now applies synchronously
   (`_pendingMindReset = null; ResetMindNow(...)`): safe, because BindBody runs inside
   the LLM thread's own blocked `RunOnMainThread` marshal — and restore passes the
   purified `BodyKey(target)` for save/load key symmetry (ASCII-only edge too).
4. **`emote` was missing from the legacy ActSchema's action enum** — schema-enforced
   models could never emit it (added, plus a `text` param prop everywhere).
5. **`buy`'s CanUse guard fell through to an unconditional LocalUse on any
   exception** (GenericPurchasable charges unconditionally) — now fails closed.
6. **`buy`'s `name` argument was dead code** (`S()` never returns null, so the `??`
   fallback never fired) — both keys checked explicitly now.
7. **`EndpointStatusText` "not configured" unreachable** (Val() substitutes a default)
   — cosmetic, noted, left as defensive.

## Wave 5 — playtest feedback (2026-10-02, evening)

Four issues surfaced in the first real playtest with Ornith live, fixed same day:

1. **"Agents get disoriented when the map slides around — don't rotate the ASCII map."**
   The sonar was a contradiction: the plot math was world-anchored, but it only sampled the
   camera-FOV cone and the prompt called row 0 "furthest forward". Fix: a fixed 16-ray
   compass sweep (absolute bearings, shared/memoized) + exact nearby positions plotted on a
   grid with top = north (+Z) — plus facing as an arrow beside @, a `facing` perception
   field / pwd compass words, a legend header (the model-facing "table of contents"), and a
   glyph filter (`radar(filter=…)` act tool / `sonar [filter]` console command). Windows got
   their own ray kind `v`/`V` so glass is nameable. New `Compass.cs` is pure C# + the
   `compass` test suite (convention: N=+Z, E=+X, world-fixed, defined in exactly one place).
2. **"They wanted listing options by compass direction + distance as an ID"** — station
   entries (perception `stations`, console `cat stations`) now carry addr `Blender#SW4`,
   world `bearing`, landmark hints scanned around the station (`near window, mailbox` —
   memoized 15s, 4.5m radius, fixture-keyword matching so windows/mailboxes are findable),
   an `id` target, and per-kind `closest_you`/`closest_player` flags for the "which station
   do we use together?" decision; prompts teach matching a player's "the one by the
   window" against the hints.
3. **"Tool calling isn't exactly perfect — add a complaint/bug-report tool."** New
   `report`/`report_issue` (console + act tool) writes an AI-feedback block (message +
   position/facing/goal + last ~10 tool calls from a new ring buffer) to
   `BepInEx/config/kkllmnpc_ai_reports.log` behind `[Debug] AIReportLog` (default on;
   off = BepInEx log only). Prose words are not aliases (no accidental reports from
   thinking out loud).
4. Console `pwd` now shows facing in compass words; console prompt and shell help document
   all of it; README/CONFIG/CHANGELOG updated. Both interface modes stay in sync — the
   old BuildRadarMap (cone-shaped, rotation-misdescribed) was deleted outright rather than
   patched, and perception `nearby` is computed before the radar so both share one scan.

## Wave 6 — held things & player objectives (2026-10-02, night)

Second playtest cluster: "does the AI know the player's objective? can it use the physics
props? and it picks up people without noticing / can't let go." Game-source research first
(`/mnt/matrix/.../Scripts/`): `Grabber` (grab bubble 1m in front of face, spring-anchored
hold ~1m ahead via DriverConstraint, hip-joint for kobold victims, `TryActivate` = throw
+10 m/s or weapon fire, release via `IGrabbable.OnReleaseRPC`), `PrecisionGrabber` (wheel
distance, rotate, freeze — player-only flair), `ElectricBlender : SuckingMachine` (suck
zone pulls/swallows props to blend reagents), `Seed : GenericUsable` (plants when ≤1m from
a plantable SoilTile, on use), `BucketWeapon` (throw = ~10ml sprays; contents never empty),
`EggSpawner` (laid eggs = penetrator props), and NO task/quest/bill system anywhere
(only the plant tutorial canvases) — the player's objective exists purely in chat.

1. **Hands**: new `HoldSense.cs` reflects `Grabber.grabbedObjects` (no public accessor
   exists) into held-identity (kind via Fruit/Seed/BucketWeapon/Weapon/Kobold components),
   pickup/release world events, perception `holding`, holding line in status, `hold` +
   `cat hold`. Grab result now names the catch (PERSON = warning + let-go guidance — the
   likely root of "picked up a player and didn't know/let go"), drop verifies and force-
   releases stuck victims via direct `OnReleaseRPC`. New `throw` tool + console command
   (toss/hurl/yeet/lob/activate aliases) exposes the real activate-button mechanics.
2. **Player objective**: latest local-player chat auto-saves a `player_wants:` fact
   (RememberFact's category-dedupe keeps exactly one live ask), prompts tell the model the
   game has no quest list and that this fact IS the player's objective to help with.
3. **Mechanics teaching**: both prompt files + console persona explain hold physics
   (spring, lag, bump/push, momentum), blender-intake blending, seed-on-soil-then-use,
   bucket spray / never-empty, and egg→soil→empty-body (treat-tenderly) lore, sourced from
   the code rather than guesswork.

## Wave 7 — watched objectives & learned routes (2026-10-02, night)

Follow-up on the same playtest: "can the AI know the player's objective WITHOUT a
fact/memory — like other players just know?" and "let players extend the pathfinding with
their own movement — Destiny 2 style: if a player's parkour got them somewhere, it's a
valid path for the AI to copy (and follow-mode should replay their real path)".

1. **Watching replaces remembering for objectives.** No game-side objective system exists
   (verified), so the durable `player_wants:` fact from Wave 6 got a live twin: a 0.4s
   main-thread snapshot of the host player (position/heading/speed/station/ragdoll/holds
   via the grabber reflection) rendered per-NPC as `player_activity` (perception) and
   `cat player` (console). Live observation only — nothing stored.
2. **Walkproofing the shared map** (`WorldMap.PatchWalkable`): a watched body (host player
   via the plugin tick; NPC bodies from their own movement loop) standing/walking on a
   cell the bake called "wall" downgrades it — covers rooms behind doors closed at
   bake-time and late-built geometry; patches persist (build arrays shared with the grid
   → next cache save).
3. **Trail copying** (`PlayerTrail`): host player's walked samples (0.4s, jump flags,
   ±0.5m decimation, 4000 cap) persisted per scene (`trail_<scene>.txt`) and reloaded —
   `go_to`'s too_far refusal now tries the trail first; when A* fails, the NPC copies the
   demonstrated route with jump replay (hop bursts at flagged posts), and follow-mode's
   no-path fallback does the same — following the player through parkour they showed.
4. **Route narration**: legs ≥9m (with door-crossing detection + hop summary) fan out as
   ROUTE WATCH event lines to every instance — "the player left the room, east, through
   the TopDoor" — the "understand when a player left a physical room, and how" ask.

## Wave 8 — correction: the quest system exists (2026-10-02, night)

The user knew better than the Wave-6/7 research: **the game does have a quest system.**
A fuller pass surfaced `Scripts/Objectives/` (my greps hit "objective" matches but I
skimmed the wrong hits, and `ls | grep -i task` obviously couldn't see a system named
"Objectives"): `ObjectiveManager` — a chain of `DragonMailObjective` letters with
`GetCurrentObjective()`, `GetStars()`, `HasMail()`, per-objective `completed`/`updated`
events, per-scene + saved — rendered by `ObjectiveUIDisplay` as the top-right paper
scroll (title embeds live progress: "Create 4 food 2/4"), DELIVERED through the mailbox
(`MailboxUsable.Use → ObjectiveManager.GetMail()`). 25 concrete objective classes
(breed/create-food/grind-fruit/plant-kobold/deliver-genes/…).

Landed: **`QuestSense.cs`** — 1/s polling + change-detection (the manager is
scene-scoped, so polling beats subscribing to static events owned by a destroyed object),
world-event fanout on new letters / progress / completions / waiting-mail, perception
`quest` (title+text+stars+mail_waiting), console `cat quest` (+ `cat objective`,
`cat mail`), and a `quest:` line in status. The letterbox itself is now named:
`ClassifyUsable` gained a `mailbox` kind with the purpose text "the objective LETTERBOX
— safe; NOT the sell machine" (the `MailMachine` sell-danger rules are class-based and
unchanged). The AI can fetch mail for its player (`use mailbox`). All "no quest system"
claims in the prompts (Main.cs built-ins, both shipped persona files, console persona +
protocol) were rewritten to describe both objective layers; the false claims in earlier
README/CHANGELOG rows were corrected. Lesson recorded: a grep that "verifies absence" is
weaker than a full file listing of the relevant game-source folder.