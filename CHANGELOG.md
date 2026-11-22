# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.13.0] — 2026-11-21

### Fixed (2026-11-21 — phantom sex after eating someone; Rose's field report)

- **The meal is never a lover — vore-first contact signaling.** The swallow
  system rides the game's PenetrationTech (the "victim as a large penis"
  insert registers in `_pen` like any penetrator), so the touch field's old
  first two checks lied: a body that had just EATEN someone read
  `IsPenetrated()==true` and reported **"being_penetrated"** — the model
  believed it was having sex with its meal. New pure `ContactCore.cs` decides
  the phrase with the ordering as the honesty: vore states are named vore
  (`carrying_a_guest — a VORE INSERT`, `vore_insert_sliding`,
  `meal_machinery`, `inside_a_stomach`) and only a NON-meal penetrator may say
  "being_penetrated" / "penetrating_something" (a real partner coexisting with
  a swallowed guest gets the sex phrase with the guest explicitly disclaimed).
  New tests: `test_contact.cs`.
- **Penetrator-listener meal-machinery gate** (`DickInIsMealMachinery`): the
  swallow system's virtual pen (`vore:<meal>`) re-parented under a body — the
  "insertion treats the whole body as a penetrator" — plus anything the body
  does while IT is a swallow session's victim (pre-`Swallowed`-flag pull
  included) and our dick sliding past the meal's mouth on the way down are
  purged at registration; they never become `_dickIn` state, never fire [sex]
  events, never enter the rhythm graph.
- **Perception honesty split**: `penetrated`/`penetrating` now report only
  REAL sex (`HasRealPenetration`/`HasRealDickInside`); the labeled meal
  surfaces under a new `vore_insert` key instead — the prompt taught models
  "penetrated = you're mid-play with someone", which is exactly the
  misunderstanding the old mixed key fed. Both act-mode and console prompts
  gained the explicit vore-insert exception (plus the `touch` phrase list
  update).

### Added (2026-11-21 — ModHooks: the public event API for other mods)

- **`ModHooks`** (`src/ModHooks.cs`, docs/MODHOOKS.md): the in-process C#
  event surface for other BepInEx plugins, built for Rose's VR mod — every
  signal passed honestly: `VoreStarted`/`VoreFullyInside`/`VoreEnded`
  (outcomes: thrown_up/poured_out/hold_ended/converted_<fate>/…),
  `SpectateStarted`/`SpectateEggLaid`/`SpectateEnded`/`SpectateHatched` (the
  local-player perspective hand-off — start pairs with end OR hatch),
  `SexStarted`/`SexEnded` (REAL sex only — the labeled vore insert never
  fires these), `PossessionStarted`/`PossessionEnded`. Payload classes carry
  the game Kobold refs + stable ids; all fires are main-thread, state
  transitions (no per-frame spam), and exception-isolated through pure
  `ModHooksCore` (a throwing subscriber is counted and skipped — the plugin
  lives; tests: `test_modhooks.cs`).
- **The VR hand-off, both directions** (controls/configs/patches can fight
  either way — full contract in docs/MODHOOKS.md "Coexistence with
  controls / VR mods"): `ModHooks.ExternalSpectateCamera` — a VR rig that
  claims the camera on SpectateStarted silences the built-in last-writer
  spectate camera entirely (checked per pre-cull before any write);
  `ModHooks.ExternalPlayerControl` — the second claim: the spectate input
  lock (`PlayerPossession`/`CameraSwitcher` disable + per-tick input-field
  zeroing) stands down so a VR locomotion rig owns the player's controls
  (the honest belly-parking still runs — it's state, not a control fight).
  Both claims are cleared by the manager on release/hatch. Device-agnostic
  feeds so the rig reads instead of re-implements: `SpectateHudLine()` (HUD
  text), `SpectateStruggle01()` (escape meter), `SubmitPlayerStruggle()`
  (one press from ANY device through the same meter/narration/vomit/remote-
  abort logic as the flat SPACE poll — `PlayerStruggleHit` extracted in
  SwallowManager), and `SubmitPreyPoint(worldPos, hitName)` (v1.11.0's prey
  pointing from any device — the flat mouse click now routes through the
  same core in `Main.HandleSpectatePoint`, one path, no drift).
  `ModHooks.PatchOwnerIds` (`com.kk.llmnpc.patches`) + a full patch
  inventory table give the VR mod conflict detection before patching the
  same engine methods (the `Grabber.TryDrop` vore trigger is the one that
  bites a "release held item" VR gesture). Polling helpers
  `ModHooks.PlayerSpectating` / `ModHooks.EaterOfPlayer` survive scene loads.

### Added (2026-11-21 — the in-game options menu tab)

- **KKLLM categories in the game's Options screen** (`OptionsMenuTab.cs`,
  Rose's ask: "a tab for the LLM system with categories like the WebUI").
  Research first: decompiled the game + read Rose's KoboldKare source port —
  the options menu runs on naelstrof's UnityScriptableSettings framework
  (`ScriptableSettingSpawner` renders every `Setting` ScriptableObject whose
  group matches a view's `targetGroup`; `SettingsManager.AddSetting` is the
  framework's own public registration API). The mod rides that machinery with
  ZERO Harmony and no prefab edits: per category it clones a `View - Graphics`
  (ScrollRect + widget templates come free) and a `Button - Graphics`, points
  the spawner at a runtime `SettingGroup`, clears the inherited rows, and
  registers config-backed Setting proxies — BepInEx ConfigEntry stays the
  source of truth (SetValue writes the entry live, Load reads it, the game's
  own Save button persists, Reset-to-default maps to the entry's default).
  Six tabs next to Graphics/Audio/Controls: **KKLLM** (model/params,
  general, memory, autonomy), **KK Console**, **KK Senses**, **KK Movement**,
  **KK Vore** (incl. the consent gates + spectate POV), **KK Servers**
  (Ollama/LM Studio/vision model/NonConsent/Web). Bools and enums render as
  dropdowns, ranged floats/ints as sliders, wide ints + strings as text
  fields; labels resolve through the game's own localization table ("GUID")
  via runtime table entries; `ApiKey` deliberately NOT exposed (typed
  secrets on stream); keys needing a restart are labeled. `[OptionsMenu]
  Enabled` (default true) turns the whole thing off; failure logs once and
  the WebUI remains. New build references: Naelstrof.UnityScriptableSettings,
  Unity.Localization, Unity.TextMeshPro.

### Added (2026-11-21 — localization: the mod speaks the player's language)

- **L10N framework + speech steering** (Rose: "localization support for the
  whole mod"). Two honest layers, because the mod's text serves two masters:
  (1) **`[LLM] Language`** — the kobolds' SPOKEN language: `auto` (default)
  matches the humans, anything else becomes a hard directive appended last to
  every persona (act, compact, console) — all say/echo lines in that
  language, the command protocol stays English, proper nouns unchanged. (2)
  **Player-facing UI** follows the game's OWN Unity Localization locale:
  pure `L10nCore.cs` (+ tests) implements English-text-key lookup with a
  exact→base→en→identity fallback chain over flat per-locale JSON files at
  `BepInEx/plugins/KKLLMNPC/lang/<code>.json`; the options-menu tabs/labels
  inject one runtime entry per string into the game's "GUID" localization
  table for EVERY available locale (translated where a file provides one) so
  the game's own LocalizeStringEvent re-renders on language switch; the
  spectate HUD grew a translator overload (English templates are the keys,
  `{0}` placeholders survive). `en.json` (122 keys) is auto-written as the
  translator template; shipped seeds: German (complete), French, Spanish,
  pt-BR, Russian, Japanese, zh-hans (tabs + HUD). NOT localized on purpose:
  LLM-facing protocol text (English is the protocol), config descriptions,
  logs — docs/L10N.md explains the split and how to contribute a language.
  l10n suite added; spectate suite now compiles L10nCore.

### Changed (2026-11-21 — prompts read from files, not the config)

- **Prompt provenance cleanup** (Rose: "have a path to the prompt file
  instead in the config"). The full act persona used to live as a BepInEx
  config DEFAULT — every install's config file carried a ~23KB blob, and the
  prompt text existed in three places (config default, repo `.txt`, shipped
  copy). Now: the `system_prompt_*.txt` + `prompt_extras/*.txt` files are
  compiled into the DLL as embedded resources (`KKLLMNPC.prompts.*`, mcs
  `-resource:` + csproj EmbeddedResource), the on-disk copies remain the
  editable override, and `[LLM] SystemPrompt` / `[Vision] Prompt` defaults
  are EMPTY — the config holds only paths (`SystemPromptFile`,
  `[Console] SystemPromptFile`, `[Vision] PromptFile`, `NamePromptFile`).
  Pure `PromptSource.cs` (+ tests) owns the decision: genuinely-custom
  inline wins; an inline value equal to the file/embedded copy is the
  untouched auto-written default and is ignored; file beats embedded;
  everything missing → the caller's minimal last resort. A lost or
  unreadable prompt file now falls back to the DLL's embedded copy instead
  of a broken persona, and `prompt_extras` fills embedded copies for
  whatever the disk doesn't provide — still in filename order. Migration:
  old installs keep their stored blob (behavior unchanged) — **delete the
  `SystemPrompt` / `Prompt` lines from the config** to switch to the file
  flow; the config descriptions say so. No breaking config changes (the
  keys remain, inline overrides still honored).

### Changed (2026-11-21 — eggs and seeds are physical goods; no parthenogenesis)

- **Prompt lore** (act + console personas, default facts, farmcook extras):
  SEEDS are store goods — buy at the shop or grab strays near farm areas;
  they are never conjured. EGGS to plant come from the shop/dispenser, from a
  STUD breeding someone at a two-slot breeding station, or from a vore
  egg-fate conversion — **a female cannot make an egg alone (no
  parthenogenesis)**: a penisless room means no bred eggs, so buy an egg,
  wait for a stud, or spend the time on something else (grinding at an
  impossible egg soft-locks you for nothing).
- **Studs are findable**: `ps` lines and perception `people` entries now list
  each kobold's `gender` and `penis` (equipment-derived — same rules as
  InferGender, via new static `BodyHasPenis`/`BodyGender`), so an agent
  hunting a breeder can scan the room instead of interrogating everyone.

## [1.11.0] — 2026-11-18

### Added (2026-11-18 night — prey pointing + rolled vore personas)

- **Prey click-to-point** (Rose's design): while swallowed and spectating,
  clicking the world = the guest POINTING something out. The HUD raycasts the
  click, skips the pred's own body, and turns the hit into a pred-facing
  event: what it is, bearing relative to the pred's own facing, distance,
  height annotation, and the world coords (the gazetteer/go_to can route to
  it). The guest sees a gold circle at the click point + a confirmation line.
  Prey inside a pred finally has EYES WITH AGENCY.
- **Rolled vore personas** (RimVore-2 quirks parity + RimWorld identity):
  every body rolls a stable persona from its identity key — gluttonous/
  yearning/even-keeled/picky/reluctant (appetite trait scales the vore-drive
  climb: some agents yearn, others mostly ignore the loop), plus style
  (gentle/fierce/playful/shy) and curiosity (adventurous/curious/cautious)
  as self-knowledge in the vore state ("color HOW you feel, not WHETHER you
  obey"). Deterministic per body — same body, same persona, survives
  restarts with zero storage. Pure `VorePersonaCore.cs` + tests (spectate
  suite grew PointText cases: bearings, height annotations, coords).

## [1.12.0] — 2026-11-18

### Added (2026-11-18 night — AgentFSM: LLM-programmed state machines, the token economy)

Rose's architecture idea, built: **the model programs, the machine performs.**
Each agent owns a behavior STATE MACHINE stored as its `fsm` skill (seeded
with a standard one; edited line-by-line via `skill add fsm | …`), and the
turn engine runs it BEFORE the LLM every beat:

- **DSL** (`AgentStateMachineCore.cs`, pure + tested): `state` blocks with
  `enter` / `on chat <pattern>` / `on event <pattern>` / `timer <seconds>`
  triggers, and `say` / `cmd <command>` / `emit` / `goto` actions chained
  with `then`. Wildcard patterns, one-rule-per-tick, a 4-action budget and
  state/rule caps — a machine can loop forever without spinning the runtime.
- **Turn-engine pre-stage**: the machine gets first bite of every turn's
  drained events, executing say/cmd/emit through the SAME pipeline the model
  uses (repeat guards, command dispatch). Matched events are consumed;
  unmatched ones flow to the model untouched.
- **Modes** (`[AgentFSM] Mode`, default `assist`): assist = machine acts,
  LLM still polls (safe rollout). **full** = a machine that consumed every
  event SKIPS the LLM turn — the multi-avatar token economy — with a forced-
  poll ladder: direct chat never skips, and a periodic check-in hands the
  model a digest of what its machine said so it can refine it.
- **Machines inherit**: stored as skills, so saved lives and bodies that
  grow from the agent's genes carry the behavior culture forward. Fresh
  agents are seeded with the standard machine and add to it over time —
  "the one thing agentic models are good at, programming."
- `prompt_extras/fsm.txt` teaches the DSL; suites grew `agentfsm` (29
  total); the turn-engine seam is config-gated and fake-env tested.

## [1.10.0] — 2026-11-18

### Added (2026-11-18 night — Endo: non-fatal vore, RimVore-2 Hold parity)

Studied Rose's local RimVore-2 install (its Hold goal: "temporarily store…
the pawn inside is intact, not contributing anything to the pred other than
a bulge") and built the parity mode:

- **`SwallowEffect.Endo`** — the non-fatal hold. Nothing converts, nothing
  harms: the prey stays intact in the pred's belly (bulge only, full
  LivePrey narration), digest marks read "held safe" instead of converting,
  and at the end of the hold they walk out gently (`HoldRelease` — same
  cleanup as the vomit release, none of the violence: whole, warm, exactly
  the person they were). Kicks and the player's SPACE-mash remain early
  exits. In the F8 cycle after held-cum; per-agent picks accept
  `vore_mode: endo|hold|safe|non-fatal`.

### Fixed (2026-11-18 night — regrow gate follows the game, not our assumptions)

- **`regrow_player`'s destination gate loosened** (Rose: "the game's
  original feature supersedes the mod — male pregnancy routes eggs
  anally"): any player-chosen SwallowDestination now passes except mouth
  (excluded from the egg ritual by design) and off (nothing goes in
  anywhere). The destination stays the PLAYER's consent setting either way.
- Gate signature simplified to take the destination (`RegrowGate(masterOn,
  agentEat, dest)`); tests updated to the permissive matrix.

## [1.9.0] — 2026-11-18

### Added (2026-11-18 night — `regrow_player`: the player-regrowth ritual)

Rose's design, built the same night: an agent can take the player by
UNBIRTH, convert them to an egg, plant it, and the PLAYER wakes grown from
it — gene-mixed with the carrier, identity and memories riding the egg's
rebirth link. `regrow_player` composes systems that all ship (agent-takes-
player, egg fate, rebirth links, planting) into one consent-gated act:

- **Consent gates composed and pinned** (`SwallowLogic.RegrowGate`, pure +
  tested): SwallowEnabled + SwallowAgentEat arm it, and the unbirth framing
  requires the PLAYER's own `SwallowDestination = pussy` — the destination
  is theirs to choose, so the agent can never steer around it.
- **The fate is the flow**: the acting agent's vore_mode is pinned to egg
  through the same validated setter `vore_mode` uses (persists — the
  conversion reads it at completion; change it back afterwards).
- **The agent gets taught**: success pushes the full recipe as a guidance
  event (grab the egg → find soil → drop → use to plant → narrate every
  stage → greet them by name on hatch). Manual + prompt text updated in
  sync.

## [1.8.0] — 2026-11-18

The scene dump becomes the agents' map. Plus directional walk.

### Added (2026-11-18 evening — SceneKnowledge: the world gazetteer)

Rose's port tooling produces per-scene `*.full.json` dumps (every object,
its worldPosition, 3k+ nodes per scene). The mod now ingests them as a
**static world gazetteer**:

- **Map-wide `find`** — "find my egg" resolves against the dump registry
  when (and only when) live lookup misses, returning the nearest matches
  with distance, bearing, and a ready `go_to x:… z:…`. Long-distance search
  works; no more blind treks.
- **`go_to` to static locations** — name resolution falls back to the dump
  before answering "unknown_place", so `go_to egg`/`go_to blender`/`go_to
  <any structure>` creates a REAL path across the map.
- **Grounding** — once the dump lands, the agents get a one-shot "world
  intel" event ("this area holds 42 eggs, 6 nests, 155 stations…"), so the
  world's shape is known, not guessed.
- Files: `[SceneKnowledge] DumpDir` points at the dumps folder; the file
  whose `scene` field (or filename) matches the running scene loads once
  per scene on a background thread (24MB → 2682 entries in ~170ms, search
  ~6ms, verified live). Off or no dump = live-only behavior, unchanged.
- **Directional walk** — `walk left 3` now means 3 METERS left (direction +
  bare number = distance; `5m`/`5s` suffixes explicit; left/right strafe
  without turning, back turns first). `walk <dir> <m>` ends the
  turn-guessing hassle from tonight's session.

### Queued design (from the same session)

- **Player regrowth by egg** (Rose's idea): agent can unbirth the player →
  convert them to an egg → plant it → the player regrows from it. Composes
  the shipped systems (agent-takes-player, egg fates, belly-gene rebirth
  links, planting) into a new consent-gated chain — design in docs/VORE.md.

## [1.7.0] — 2026-11-18

New project direction: **stability + quality of life** — dissecting the whole
playthrough log (game log + Unity player log) and fixing what it shows, in the
mod and in the game itself via monkey-patching.

### Added (2026-11-18 — log forensics: engine stability guards + faster loading)

- **JigglePhysics stability guards** (the log's biggest engine offender): the
  game's embedded jiggle-physics library threw NullReferenceExceptions at two
  sites around modded-model churn — `JiggleSkin.UpdateMesh` fired PER FRAME
  while a modded player model was mid-swap (each throw "Uploading Crash
  Report"-ed), and `JiggleSkin.OnDisable`'s stale-rig deref aborted the game's
  `EnsureModsAreLoadedThenChangePlayer` coroutine mid-flight (why a restart
  could leave the player model broken until a manual re-pick). Both are now
  Harmony-finalized with the same throttled-swallow idiom as the station
  guard: one log line per 30s, the swap continues, the renderer is rebuilt
  by the model change anyway.
- **Faster loading: the startup probe went async.** `ModelProbe.Probe`'s
  retry ladder (3 attempts × 1s sleep × four probe endpoints) ran INLINE in
  the plugin Awake — blocking the whole BepInEx chainloader up to ~12 seconds
  when the endpoint was slow or absent (the largest load-time cost under our
  control; the chainloader itself is inherently one-at-a-time). It now runs
  on the thread pool; consumers are lazy and null-tolerant, so the mod uses
  defaults for its first turns and upgrades itself when the probe lands.

### Fixed (2026-11-18 evening — the marshal hang, named at last)

Second session, same day: the game froze mid-play again — but this time the
hang watchdog caught it live: main thread no beat for 26+ seconds, phase
`marshal-callback` (a RunOnMainThread callback never returned), LLM loops
timed out and backed off exactly as designed, and — notably — **zero kernel
GPU events** (the amdgpu ring-timeout receipts from the morning were a
separate, once-only event). Two shipped changes:

- **Marshal flight recorder**: every main-thread callback now carries its
  name (`Type.Method`, cheap reflection); the hang warning names the
  callback in flight, marshal timeouts name it too, and any callback taking
  >2s logs its duration once. The next hang names its culprit on its own.
- Verified today's freeze left no driver trace — it is ours (or the game's)
  to fix, and now it self-reports where.

### Known remaining (from the same log, queued)

- 82 `referenced script (Unknown) is missing!` + one `Unknown managed type
  referenced: Assembly-CSharp SettingPostProcessor` — content-authored prefabs
  referencing scripts that don't resolve (workshop content vs game version
  drift; one is the GAME's own scene). Logged by Unity itself, not catchable
  at the managed layer — roadmap: a fan-patch module that suppresses the
  per-instance `GameEventSanitizer` LogError spam and surfaces a per-mod
  summary instead.
- Unity's crash reporter uploads every swallowed-by-us exception ("Uploading
  Crash Report") — roadmap: bootstrapper-level opt-out.

## [1.6.0] — 2026-11-18

### Added (2026-11-18 — on-demand replicas: instances scale with the agent population)

Rose's morning verdict on v1.5.1: **A+ — "this version is the best so far"**,
with the first fully autonomous farm chain observed (walked to the mailbox,
took the starter egg, planted and watered it — following the letter's
instructions, unprompted). The one gap: instances loaded even when no agent
needed them (4× a multi-GB model black-screened her machine mid-play).

- **Replicas now scale ON DEMAND on both hosts** — instance N loads only when
  a Nth agent actually exists (`min(Replicas, live agents)`), which is also
  the load-balancing behavior: the second agent's instance spawns at its
  thread creation (a fresh cold load lands while it shares the base; its next
  query migrates), and mod-loaded extras release VRAM when agents leave
  (3-poll hold so a body death/respawn flicker doesn't churn). A genuinely
  different configured model still overrides everything, as before.
- **LM Studio user loads stay sovereign**: hand-loaded instances are always
  adopted, never auto-unloaded, and never counted against scale-down — only
  the mod's own extras release.
- **Hardware guidance, learned the hard way**: both `Replicas` descriptions
  and a one-shot startup warning at ≥3 now say it plainly — every instance
  takes the model's FULL VRAM on the same GPU that may be driving your
  display; 4× a multi-GB model has black-screened a machine here. 2 +
  per-model server concurrency is the safer shape; use a second machine or a
  compute-only GPU for more.
- Pure scaling math `ReplicasForPopulation` (+9 assertions in test_ollama).

## [1.5.1] — 2026-11-18

### Fixed (2026-11-18 — octree path straightening: the zig-zag pass)

Rose's verdict on v1.5.0 navigation: "a lot better, little zig-zaggie but
functional." Two causes, both fixed:

- **The straight-leg voxel march over-blocked** — twice. First, a latent typo
  packed the sample's Z from its X coordinate (`VoxZ(x)`), so the march
  consulted the wrong column entirely; second, ANY sample touching a wall
  voxel vetoed the whole leg, even when that voxel's center was half a meter
  off the line while the physics rays passed. A wall voxel now vetoes only
  when its center is within the body radius (`SegmentVetoRadiusM` 0.45m —
  the same notion of "near the line" as the ±0.35m lateral ray lanes and the
  simplifier below). Every false veto used to split a leg, and the A*
  staircase survived as zig-zag.
- **Deviation-tolerant simplification pass** (`WorldMap.SimplifyDeviating`):
  after string-pulling, a post is dropped when the physics rays clear the
  segment over it, its horizontal deviation from that segment is within a
  body width (+margin), and its height stays within a step-and-a-half of the
  interpolated line (steps/ramps straighten; cliffs don't). Iterative, so
  dropping one post can enable the next.
- Tolerance pinned in `test_octreenav`: a parallel lane one column off the
  wall stays open; a 0.15m graze still blocks.

## [1.5.0] — 2026-11-18

Volumetric pathfinding: the world map's sampled data now navigates as true 3D.

### Added (2026-11-17 — OctreeNav: sparse-voxel 3D pathfinding, built from the cached map)

- **`OctreeNav` — the 3D nav core** (`src/OctreeNav.cs`, pure C#). Every sampled
  surface becomes a floor voxel in per-column Y-sorted sparse sets; A* runs over
  true 3D nodes with (a) horizontal edges that pick the neighbor column's best
  walkable floor within [−2.6m drop, +0.5m climb] — the edge the old layered
  grid could never express across columns, (b) same-column fall edges for
  drop-through shafts/holes, (c) door tolls. This is what routes cave systems,
  fall shafts, over/under passes, and floor stacks of any depth. (A literal
  Morton-merge octree was deliberately skipped: we sample surfaces, not solid
  interiors, so uniform-interior merging had nothing to compress — the column
  map IS the sparse form of one.)
- **The `.kkmap` cache IS the 3D map now.** The core builds from the same
  arrays the cache stores, on a background thread, the moment the grid is
  ready — a cached scene gets volumetric routing in milliseconds with zero
  re-sampling (`map: ready (…, 3D N vox)` in the heartbeat).
- **Deeper sampling for caves** — `[Movement].WorldMapRayDepthM` (default 150,
  was a fixed 60): each column ray now reads 2.5× deeper under the terrain, so
  cave floors under tall geometry get sampled at all. Cache loader accepts up
  to 24 stacked layers (was 8). Needs one re-sample to apply to a cached scene.
- **3D straight-leg checks** — string-pull fusing uses a volumetric
  wall-voxel march (chest height, matching the physics rays) instead of the
  per-layer 2D quadtree, so a leg from a ramp onto an overpass is checked
  against both levels' walls. The quadtree stays as fallback.
- **Fallback ladder unchanged** — every query tries the 3D core first and
  falls back to the legacy layered A* (`[Movement].WorldMap3D=false` restores
  the old behavior entirely); walk-proof patches mirror into the 3D core.
- `tests/test_octreenav.cs` — 30 assertions: stacked tunnels + overpass
  asymmetry (drop off routes, climb back doesn't), fall shafts vs over-drops,
  30m stepped-cave descent, doors-as-tolls, patch-by-proof, budget, and a
  200×200 long-trek smoke. 26 C# suites total.

### Fixed (2026-11-17 — live-session salvage: fenced act-JSON with phrase actions + plan queues)

Dissected from the running gemma-nsfw session: the model replies in fenced
act-JSON whose action is a PHRASE ("look around") — the salvage marker-scan
died on `Canonical("look around") == null` and passed the blob through
untouched, so every turn became a silent "console thought". Worse, the
act schema queues its real intent in `"plan":[...]` (speech included), which
args-flattening strips as meta — the kobold tried to answer the player's
hello for six turns and never said a word.

- **Whole-reply fenced JSON now salvages**: fence bookends are stripped before
  the bare-JSON path, so ` ```json\n{...}\n``` ` (single- or multi-line)
  flattens to command lines.
- **Phrase actions resolve**: when the action name isn't canonical but its
  first word is ("look around" → look, "go to bed" → go/cd), the whole phrase
  becomes the command line instead of the salvage giving up.
- **Plan queues become command lines**: each `"plan"` entry is recovered as
  its own line (one level deep) — `{"action":"say","text":"Hi!"}` finally
  reaches `echo Hi!`.
- Four regression cases pinned in `test_console_shell.cs` (94 assertions).

### Added (2026-11-17 — per-agent model replicas: parallel turns on one Ollama)

- **`[Ollama].Replicas` (default 1)** — load N total instances of the adopted
  model and give each agent its own: true parallel turns, each with a FULL
  context window (the `OLLAMA_NUM_PARALLEL` alternative splits one window
  across slots). Ollama loads by tag, so the watcher creates N−1 zero-disk
  copies via `/api/copy` (`model`, `model.kk2`, … — same blobs, separate
  loaded runner), prewarms them (cold loads run on the watcher thread, same
  trade the keep-alive makes), pings EVERY replica in the keep-alive
  heartbeat, unloads the old set on a model change or a return to 1, and
  skips (with one log) any copy the server can't provide — those agents fall
  back to the shared instance instead of failing turns. Agents claim a
  replica round-robin at their first turn, and only while the Ollama
  hand-off is actually live (a shard name sent anywhere else is a 404).
  Each loaded copy takes the model's FULL VRAM — the knob is opt-in and
  documented accordingly. LM Studio cannot do this (one loaded instance per
  model key, no duplicate-key API) — `[LMStudio]` deliberately has no
  Replicas key, noted in its docs.
- Pure naming/assignment in `OllamaArgs.ShardName/ReplicaSet/ReplicaFor`
  (+17 assertions in `test_ollama.cs`).
- **`[LMStudio].Replicas` (default 1)** — the same capability for LM Studio,
  which DOES support multiple loaded instances of one model (the first fix of
  this round: an earlier claim that it couldn't was wrong — Rose loaded the
  same model twice live to prove it). The Nth loaded instance is addressed as
  `<model>:N`; the watcher loads the extras via `/api/v0/models/load` (TTL from
  `KeepAlive`) and — verification-driven — only routes an agent to an instance
  after a `/api/v0/models` poll confirms it `loaded`, pings every instance in
  the keep-alive heartbeat, strips the `:N` suffix when picking the shared
  base, and best-effort unloads our instances on model change / return to 1.
  Pure naming in `LMStudioArgs.InstanceName/InstanceBase/ReplicaSet/ReplicaFor`
  (+14 assertions in `test_lmstudio.cs`). Same VRAM math as Ollama replicas.
- **LM Studio replicas are ADOPTION-first** (same evening, from Rose's live
  dual-instance report): every LOADED `<model>:N` instance joins the pool —
  manual UI loads included, verified against her live server (base + `:2`,
  both `state:loaded`, parsed through the real code). `Replicas` now means
  "ensure at least N": missing indices load one-per-poll and are adopted only
  after a poll confirms them; only mod-loaded instances auto-unload (hers are
  never touched); pool formation is always logged (the silent-failure gap that
  made the first attempt undiagnosable). Pure adoption scan:
  `LMStudioArgs.LoadedInstancesOf` (+11 assertions).
- **Replica routing actually routes** (2026-11-18, the "4 loaded, none used"
  report): a WebUI pick writes the concrete model name into `[LLM].Model`, and
  a concrete name short-circuited `EffectiveModel` BEFORE the per-agent
  replica claim — every agent piled onto instance #1 while the adopted
  `:N` replicas idled. Resolution now claims the agent's replica whenever the
  configured model is the placeholder OR names the adopted base / one of its
  instance (`:N`) or shard (`.kkN`) keys; a deliberately different configured
  model still passes through untouched. The resolution log is now per
  DISTINCT name (with replicas, each agent may differ — "which instance is
  this kobold on" is a log query now).
- **LM Studio server-auth note**: with API auth enabled (0.4+), every probe
  401s unless the token is in `[LLM].ApiKey` — documented in the section.

### Fixed (2026-11-17 — gemma round 3: the XML tool dialect)

The Scamper session: gemma-nsfw switched formats AGAIN — now XML tool markup
(`<thought>…</thought>` + `<action>find('water')</action>`, close tags often
truncated to `</>`). None of it salvaged: every reply became a silent console
thought, the identical-repeat guard ended turns, and one block-speak leaked
raw markup into chat.

- **`<action>`/`<command>`/`<tool>` unwrap pass** in `ConsoleSalvage.Clean`:
  tag bodies become command lines (the lenient parser then handles
  `find('water')`/`man()` call-style natively), `<thought>` framing drops,
  malformed closes (`</>`, newline, none) tolerated, and the `<tool` open
  refuses to eat `<tool_call|>` (the JSON dialect's marker). Speech heads
  before a mid-line close tag survive as narration.
- **Markup is never voice**: `IsRpResponseBlock` rejects action/command-tag
  replies — quoted args in that dialect were being spoken in chat verbatim.
- **Name flow**: trailing parenthetical annotations ("Name: **Kizzy** (5
  letters)") no longer discard a completed answer, and the name prompt now
  says the taken list is NOT a checklist (gemma re-derived the whole list on
  all four budget re-rolls, then timed out into a flavor fallback — ~4 minutes
  per fresh body).
- Regressions in `test_console_shell` (+7), `test_textutil` (+2),
  `test_modelfamilies` (+2).

## [1.4.0] — 2026-11-18

LM Studio gets the same adoption treatment Ollama has — and one power Ollama
doesn't: the model picker actually LOADS the model for you.

### Added (2026-11-17 — LM Studio adoption: watcher, hand-off, TTL keep-warm, loader)

- **`[LMStudio]` adoption (ON by default)** — a new watcher adopts an
  externally-running LM Studio through its native `/api/v0/models`: one
  payload carrying every model with its **load state**, **real context
  window** (feeds context compaction), arch, and quantization — no
  `/api/show` round-trip needed. Idles harmlessly (`healthy=false`) when
  no LM Studio is running; strict parsing means any other server on that
  port is never mis-adopted.
- **Endpoint hand-off (`AutoEndpoint`, ON)** — with an adopted + healthy
  LM Studio the mod routes LLM traffic to it regardless of
  `[LLM].Endpoint`, and the `local-model` placeholder resolves to the
  actually-loaded model. **Precedence: an adopted Ollama still wins** (it
  shipped first — nobody's working setup flips). Optional LM Studio auth
  is honored via the existing `[LLM].ApiKey` Bearer.
- **Keep-alive heartbeat (`KeepAlive`, default `30m`)** — LM Studio
  auto-evicts JIT-loaded models after their idle TTL (default 60m) and the
  TTL resets on every request, so the watcher sends a 1-token completion
  roughly every half-window; `-1` pins for the session (15m cadence),
  `off` disables. GUI-loaded models have no TTL and stay resident on
  their own.
- **The picker loads the model** — the WebUI **AI server** tab now shows
  LM Studio alongside Ollama; **use this model** writes `[LLM].Model` AND
  loads the model in LM Studio itself (`/api/v0/models/load`, TTL from
  `KeepAlive`), only sending the load for not-loaded models so a resident
  model never pays a reload. With JIT loading enabled LM Studio even
  loads the model the mod names on the fly.
- **ModelProbe** now speaks `/api/v0/models` too, so a config pointed at
  LM Studio (port 1234) gets real context-window discovery at startup
  even without adoption.
- New pure module `src/LMStudioArgs.cs` + `tests/test_lmstudio.cs`
  (36 assertions — parser, the OpenAI-`/v1/models` false-positive guard,
  loaded-model pick, cadence math). 25 C# suites total.

### Fixed (2026-11-17 — gemma playtest dissection: swap staleness, vision hammer, JSON muttering, replan storms, the burger-bucket)

First `unseen_gemma_4_26b_nsfw` session, dissected from LogOutput — five root
causes behind "repeats commands, bad navigation, doesn't understand buckets,
shop confusion, barely talks":

- **Model swaps now re-profile the agent.** Family + tier were resolved ONCE
  per instance from the startup probe — a mid-session LM Studio swap ran the
  gemma-26B under a stale `deepseek`/`small` profile with every gemma
  mitigation absent and wrong token headroom. The profile now re-classifies
  when the live model name changes (adopted-host watcher → probe → config),
  tier re-resolves with a `<n>b` name heuristic (26b → medium; bands match
  ModelProbe's), and the startup probe's stale tier verdict is ignored after
  a swap.
- **Vision circuit breaker.** A text finetune behind a vision-shaped endpoint
  returned empty captions for the WHOLE session — each attempt costing a
  16–40s serial-LLM slot every ImageEveryNTicks, while the agent "checked
  screenshots" that never came. 3 consecutive empty captions now pause vision
  for 10 minutes and tell the agent: eyes offline, plan from sonar/`ls`.
- **JSON is never voice.** Gemma's legacy act-JSON replies (```json
  {"action":…}```) counted as "quoted dialogue" and were SPOKEN in chat as
  fragments. `TextUtil.IsRpResponseBlock` now excludes JSON-shaped blocks
  (fence-stripped `{`/`[` openers); the command-salvage path is unchanged.
- **Stuck-breaker storms end.** An unmapped target (TableStation) ground 30+
  same-route replans into the agent's terminal, each inviting a `go_to`
  re-issue. Replans now cap at 5 per goal (then drop the route and say ONCE:
  pick a different target or walk by hand), and only the FIRST reroute per
  goal reaches the agent.
- **Buckets tell the truth.** Every `BucketWeapon` was "water bucket —
  contents never run out"; but buckets are the game's FOOD vessels
  (`BucketWeapon/FoodCreateAction`). Krumble threw away a Burger on that
  line. Held buckets now read their real mix (`container` →
  `NoTouchGenericReagentContainer.GetContents()`): food buckets describe
  their contents in perception and the grab event, and keep the endless-water
  story only when they actually hold water (or can't be read).
- **`find <unknown>` lists the real places** (up to 6, nearest-first) instead
  of a dead-end "not found" — the shop spiral ended in "try: cat stations".
- Lifecycle OnDisable/OnDestroy at t=0 are Info now (normal host-GO churn),
  mid-session disables stay Warnings.

Watch item (not fixed): gemma twice returned SSE completions with empty
`delta` and `finish_reason=stop` — likely reasoning-channel emission under a
stale profile; re-check after a clean gemma-family session.

## [1.3.1] — 2026-11-16

The vore-drama mega-release: the churn system, reincarnation in three
flavors, drunk roleplay, and the fixes from the second playtest.

### Fixed (2026-11-16 — v1.3.1 hardening: safe drops + jumpy hosts)

- **Safe drop** (`[LittleTreats].SwallowSafeDropKey`, default LeftShift):
  hold the key while releasing a held kobold and NO swallow trigger fires
  for that drop — they just land. The accidental-vore fix from the
  playtest: placing kobolds at work stations near each other (or below
  yourself) no longer feeds anyone to anyone.
- **Jump-escape**: the swallow launch brake (the anti-physics-break
  release) fired on ordinary hard jumps (0.25 s above 2.5 m/s of climb);
  it now needs a real sustained climb (24 ticks above 3.4 m/s) — jumpy
  hosts keep their meals.

### Added (2026-11-16 — offspring reincarnation: the charged load's grown body wakes as the victim)

### Added (2026-11-16 — offspring reincarnation: the charged load's grown body wakes as the victim)

The last queue item, and it turned out to be one registration: when a held
Cum charge releases into a KOBOLD's belly, the belly-gene rebirth link is
registered (the same mechanism BellyEgg has used all along). The game's
pregnancy feeds on those belly genes; when the carrier nest-lays,
`OnOviposition` matches the belly fingerprint exactly and the egg inherits
the rebirth link — so the body that grows from the load (a fully grown
adult, gene-mixed with the carrier) hatches and the **victim wakes in it**:
name, facts, skills, vore picks, and their last waking moments — remembering
being eaten. Milk charges into a belly register the same link (milk-rebirth:
if the game lets a body grow from it, the same loop applies). The holder's
release narration now says so ("if a body grows from this and is planted,
THEY may wake in it").

### Added (2026-11-16 — episodic memory transfer: victims pass their last waking hours)

Memory records now carry an EPISODE — the last 8 timestamped short-term
history entries snapshotted at save time. On grow-absorb and drink-absorb it
arrives as one origin-labeled line ("from Mochi's last hours: 00:12 nav->water
the farm · 00:14 say->these tomatoes are coming along · …" — the final moments
are kept when capping), so an absorbed or drunken person passes what they were
DOING, not just their facts. On rebirth/soul-egg restore the same episode is
injected first-person ("your last waking moments, before this body slept: …")
— a reborn victim remembers being eaten. Legacy records without an episode
are unaffected; `MemoryCompaction.EpisodeLine`/`WakingMemoryLine` are pure
with suite coverage.

### Added (2026-11-16 — drunk roleplay: agents that drink MUST play it)

New `[Intoxication]` section (Enabled default ON, Reagents = Wine,Beer,Vodka,
Mead resolved against the game's reagent database — uninstalled names skip and
log; Potency multiplier). The belly postfix feeds a per-agent blood-alcohol
tracker: a RISE in the belly's alcohol volume is a drink (~13 units ≈ one
tier step; one event caps at half a bar), metabolism is slow (0.013/min — a
full drunk sobers across ~45 minutes of play). Three tiers — tipsy, DRUNK,
WASTED — ride the state anchor EVERY turn with an explicit PLAY IT contract
("heavy tongue, grand wrong ideas that feel BRILLIANT, leaning on things"),
tier-hit events on the way up, and honest sobering on the way down (coming
off WASTED is a hangover: "quiet, slow, suspicious of light and of
yesterday's decisions"). A new prompt-extra makes the contract absolute: the
body says it, the mind plays it — never narrate yourself sober while the
line says otherwise. Pure math in `DrunkCore` (25th suite).

### Added (2026-11-16 — the ghost-milk variants: held charges released by the body's own acts)

`SwallowEffect.HeldMilk` / `HeldCum` (`vore_mode(heldmilk|udder|heldcum|load|charged…)`
, F8 cycle extended): the held churn — the victim converts into a CHARGE the
holder carries, no fluid poured. Release happens through the body's own acts,
discovered via a Harmony postfix on `GenericReagentContainer.AddMixRPC` (every
milk spray and climax in the game routes through it carrying the source's
viewID): when a charged holder's milk lands somewhere (the milking machine's
spray into a bucket/pump/mouth) or their cum lands in a receiver, the charge
goes with it — the receiver takes the victim's genes + the substrate
registration, so the existing drink-absorb chain fires downstream and a
cum-into-kobold can express the victim's genes in the child (mind-to-child
reincarnation noted as future polish). Honest held-variant narration for
eater, victim, and bystanders; suite covers the extended cycle, labels, and
words.

### Added (2026-11-16 — the substrate principle, v1: pour-liberation + drink-absorb + origin-labeled memories)

- **Pour-liberation**: the prey reagent IS the digestion's grip — drain a
  pred's belly mid-digest (pump, bucket, mouth) and when most of the LivePrey
  volume leaves unexpectedly (below half the metabolism curve), the
  conversion loses hold: the meal spills free, half-melted and ALIVE, with
  honest narration for everyone watching.
- **Drink-absorb**: churned fluid carries the person — their genes ride the
  container (and the game's own transfers carry container genes with the
  fluid, so the fingerprint follows through buckets and pumps). Whoever's
  belly next holds the churn gets one-shot absorption of the victim's
  memories: "you taste someone in what you drank — <name>'s substance, and
  with it their MEMORIES pour into you." Churned records are never
  forgotten now — they ride the fluid (and the soul egg's rebirth link).
- **Origin-labeled memories** (the provenance ask): absorbed facts land as
  individual "from Mochi: <fact>" lines (capped) alongside the summary —
  the eater can finally attribute what they know. Applies to grow-absorb
  AND drink-absorb; pure `MemoryCompaction.OriginLabeledFacts` with suite
  coverage.

### Added (2026-11-16 — churn phase 2: the Milk and Cum fates)

Two new digestion fates (Rose's design — "stuffing them into breasts churns
them into milk"): `SwallowEffect.Milk` and `Cum`, cycled by F8
(egg→bellyegg→grow→destroy→**milk→cum**→egg) and pickable per-agent with
`vore_mode(milk|churn|cum|seed…)`. The meal converts at completion into real
`Milk`/`Cum` reagent in the eater's belly (where the game's own Cum lands),
volume matched to the meal, **the victim's genes riding the fluid** —
drinkable, milkable, spillable. Honest narration on all sides ("a person,
becoming a drink"); churned ≠ integrated, so the **soul-egg second chance
applies** (an egg shaped like them can slip free with the churn). Player-
victim fate hints and the eat_player outcome notes teach the new modes —
and the stale "swallow-abort chat command" mentions were rewritten to the
SPACE-mash escape. Suite covers the extended cycle, labels, and word
parsing.

### Fixed (2026-11-16 — dildo models are not people: owner-aware penetrator naming)

The game's penetrators are named after their MODEL — 'BDDiego', 'KDEFuta',
'WhorseDick', 'dild 1' are Bad-Dragon-style dick attachments — and penetration
events used the raw object name, so agents believed a stranger named Diego or
KDEFuta was inside them ("I'm being penetrated by KDEFuta", field log). Every
penetrator is now resolved to its OWNER: another kobold's part narrates as
"<their name>'s KDEFuta-model penis" (agent name > player nick > body name);
an ownerless penetrator is labeled a TOY ("a shaped dildo/prop with no body
attached — an OBJECT, never a person"); our own is caught by the own-appendage
guard or named as our own equipment. `whoami`/`body` now lists the body's own
dick MODEL NAMES ("equipment names, never people"), and the system prompt
teaches the pattern with examples.

### Added (2026-11-16 — the churn system phase 1 + reincarnation: the soul egg)

- **LivePrey reagent**: a swallowed victim now rides the eater's belly as
  REAL reagent — registered at runtime (clone of Egg: safe LocalizedString,
  warm pink color/emission, ~31s metabolization half-life via reflection;
  AddAsset invoked reflectively so the build never references
  naelstrof.Modding), poured at Begin (units scale with the meal, capped to
  the stomach), slow-cooked by the game's OWN metabolism — the reagent
  level IS the digestion, visible to any reagent scanner, and the belly
  bulge becomes NATIVE (the reflection shim retires to fallback whenever
  the reagent took). Clean removal on every release path (vomit, abort,
  end): OverrideReagent-to-zero on the live contents + a 0.01 spill to fire
  the container's change events.
- **Reincarnation** (`[LittleTreats].SwallowReincarnateChance`, default
  0.15): the Destroy fate's rare second chance — a SOUL EGG carrying the
  victim's OWN genes slips free, their saved life linked through the same
  RebirthLink machinery the egg fate uses: plant it in soil and they return
  as themselves, memories intact. The eater is told exactly what they hold:
  "their SECOND CHANCE; yours to deliver or devour." Grow absorptions stay
  integrated — no second chance for those. Honest narration either way
  (the victim's final line reflects whether an egg came of them).

### Added (2026-11-16 — stretch stomachs, kick training, and the player escape mini-game)

- **The stretch band**: a meal slightly over the ceiling (up to +0.6 ratio)
  still goes down — the stomach STRETCHES to compensate: pull ×1.5,
  digest ×1.35, the meal's kicks hit ×1.25 harder against the tight gut,
  and the bulge shows ×1.2 more. Only beyond the band is the meal refused.
- **Kick training**: HOLD a real fighter (struggle ≥ 4) all the way through
  digestion and the eater's `bellySize` gene grows (+2, live-applied via
  `SetGenes` — a genuinely roomier stomach, persisted by the game's save).
  Surviving kicks IS training: preds who keep eating fighters become able
  to swallow anyone.
- **Player escape mini-game**: the "swallow abort" chat command is GONE. A
  swallowed PLAYER earns freedom the honest way — MASH SPACE (spectate HUD
  shows the struggle meter `[#-----] N%`; each press = one struggle hit,
  same size-scaled math as agent kicks; fill it and the pred throws you up
  whole). When the eater runs on another client, the meter lives on the
  victim's machine and success fires the same cross-machine abort RPC the
  chat command used. NPC victims keep the `kick` path (which now shares one
  `VomitRelease` with the mini-game).

### Added (2026-11-16 — vore size mechanics: capacity gates, scaled time, throw-up, belly bulge)

Meals scale with the bodies involved, per the playtest design:
- **Capacity gates** (new `[LittleTreats].SwallowSizeGates`, default ON): a
  meal too big for the receiving stomach is refused before any pin — the
  ceiling rises with the eater's `bellySize` gene (the game's own stomach
  capacity), so big-stomach predators eat big prey as a build choice. New
  `InsertStatus.TooBig` with honest notes. Off restores anything-goes.
- **Scaled timing**: pull-in takes 0.75–2x and digestion 0.5–3x the base,
  by victim-to-eater `baseSize` ratio (per-state `PullSeconds`; the old
  constant is now only the seed).
- **Throw-up**: every `kick` from inside accumulates struggle — bigger
  meals churn far more (4 kicks at the capacity ceiling vs ~24 for a tiny
  snack). Past the threshold the stomach HEAVES: the meal is thrown up
  whole, alive and furious, with escalating warnings to the predator
  before it happens. A fighting meal is one you must pacify or finish
  fast — or don't swallow fighters.
- **Belly bulge**: a swallowed meal finally SHOWS — the game's belly
  visual is reagent-volume-driven, so the meal-term is added on top
  (reflection onto `bellyInflater`; a Harmony postfix keeps it through
  drinks; begin/milestones/ends refresh it), scaled to meal size (0.45
  inflatable units at an even match, monstrous at the ceiling) and easing
  down as digestion progresses.
All curves are pure `SwallowLogic` (SizeRatio/DigestScale/PullScale/
FitsInBelly/KickStruggle/BellyBulge) with 30 new assertions in the
swallow suite; prompts (manual/console/vore extra) teach the size rules.

### Added (2026-11-16 — eat-by-name: `swallow target='Name'`)

The playtest's "couldn't be ordered to eat by name": the swallow tool took
no target — the model had to find the victim, walk to them, GRAB them and
face them through four fragile steps, and failed more often than not on a
moving person. `swallow` now takes `target` (an agent's chosen name, a
player's chat name, a body name, or `id:N` from nearby): resolved at
hugging range (~3m) it inserts directly — no grab dance; too far and it
answers with the distance + bearing so the approach is one step, not a
guess; unresolved it points at `ps`. Every existing gate is unchanged
(master switch, one meal at a time, player-body refusal, already-inside,
Force-mode receiver hunt) via the new `TryInsertForAgentTargeted`. Bare
`swallow` keeps the classic held-and-facing form; manual + console prompt
and the vore prompt-extra teach both.

### Fixed (2026-11-16 — vore playtest round: swift silence, own-dick guard, voluntary greetings)

- **Swift silence.** A fully absorbed mind (Grow absorb, Destroy consume, or
  vanished mid-swallow) now goes quiet the moment the conversion completes —
  `MarkGone` stops the LLM loop, drops every further `say`, and retires the
  instance. Previously the victim kept chatting from inside the eater
  ("Zynx... I'm all yours now. I accept it.") or, once the body was
  destroyed, asked "where's my body?" until the reconcile pass caught up.
  Same for the sell box: the mail machine (KoboldKare's actual seller —
  LocalUse → mail animation → PhotonNetwork.Destroy) is now Harmony-prefixed,
  so a claimed body being mailed is silenced the instant the machine takes
  it, not seconds later.
- **Own-appendage guard (the WhorseDick bug).** A penis GAINED by grow-vore
  absorb spawns re-rooted outside the existing own-body checks and
  registered against the eater's own penetrables — the log shows "absorbed
  Mochi (dick=gained)" and five lines later "penetrated by WhorseDick
  (vagina)": the agent reacting to its own new equipment as "something is
  inside me". A penetrator that is one of the body's own appendages
  (component search, inactive included) can never penetrate it; only new
  contacts pay the search.
- **Voluntary greetings.** The canned "oh, hi!" / "hey again" auto-speech on
  noticing the player is gone — the agent now receives a proximity signal
  ("you notice <player> nearby — whether to greet them is entirely up to
  you") and decides for itself. First-sight and cooldown behavior preserved
  so the signal can't spam.

## [1.3.0] — 2026-11-15

### Fixed (2026-11-15 — who's-who: identity churn made legible; ps ids; record name pollution)

Field log from the v1.3.0 playtest: every agent's body was destroyed/sold
at least once, and look-alike prefab clones (three genetically identical
Porcupines distinguished only by viewID) recast the roles silently —
survivors met strangers wearing familiar faces with no idea the person
they knew was gone, and `ps` rows were indistinguishable (same prefab
name, no id, plus 30–120 s of prefab-name limbo while qwen3.8 burned
naming attempts on chain-of-thought). Three fixes:

- `ps`/people entries now carry a stable `id` (look-alikes never share
  one), and a claimed-but-not-yet-named agent says so in `who`: "a NEW
  mind waking in this body — don't call them by the body name."
- `SaveBodyMemory` no longer overwrites a previous life's real name with
  the unfinalized prefab placeholder when a save lands in the naming
  window (the record temporarily said 'Porcupine' before 'Nugget' landed;
  a body lost mid-limbo would have kept the placeholder forever).
- A destroyed/sold body now announces the departure to the other agents
  ("you watched <name>'s body be destroyed or taken — <name> is GONE; a
  new mind may appear later — greet them fresh"), so the recast is
  narrated, not silent. Vore absorbs keep their own richer exit and stay
  quiet here.

Identity still dies with the body by design (a new clone is a new
person); persistent-personality reincarnation remains future work.

## [1.3.0] — 2026-11-15

### Fixed (2026-11-08 — phantom intimacy + per-frame station crash)

Perception gained a `touch` field (`Body.ContactText`): an honest
physical-contact signal — `being_penetrated` / `penetrating_something` /
`mounted_in_station` / `carrying_a_guest` / `inside_a_stomach`, or
`none — nothing is touching you`. Arousal rises on its own with no contact
(the game pumps stimulation continuously), and without this signal the model
mistook a merely-aroused body standing near the player for intimacy
("we're sharing something") and got tender with someone who wasn't touching
it. The system prompt now teaches: real contact is only ever stated by
`touch`/`penetrated`/`penetrating`/`feel`, never inferred from proximity.
Also: a Harmony finalizer on `User.SortGrabbables` swallows the game's
per-physics-frame `BreedingStation.CanUse` NullReferenceException (a station
whose `AnimationStation.info` went null after a body was destroyed/sold
mid-mount — observed 3800+ log spams), logged once per 30 s, game
interaction menu left intact.

### Added (2026-11-15 — config linter; README/CHANGELOG slimmed)

New `tools/lint_config.py`: validates a BepInEx `.cfg` against
`docs/CONFIG.md` — values outside documented ranges are errors, keys the
docs no longer mention are warnings (stale docs are a bug too; `--strict`
escalates). Conservative by design: range text it doesn't recognize is
skipped, never guessed at. `--selftest` runs the parser fixtures and is
wired into `run_tests.sh` as the python gate (CI needs no extra setup);
first run against the live config found 24 undocumented keys — the
`[DataDump]`, `[Decision]` and `[NonConsent]` sections among them (doc
gap, now visible). README slimmed 42 KB → 19 KB: behavior highlights
moved to `docs/BEHAVIOR.md`, the per-instance config tables (a drifting
subset of CONFIG.md) replaced with a section map + linter pointer, and
the stale per-suite test counts replaced with the runner. CHANGELOG
slimmed 168 KB → 23 KB: pre-2026-10-14 rounds (waves, kk-serve
companion) moved verbatim to `docs/changelog-archive.md`.

### Fixed (2026-11-15 — run_tests.sh suite filtering actually works)

The usage header has always promised `./run_tests.sh json path` (run only the
named suites) but the argument list was silently ignored — every invocation
ran everything. Args are now honored (exact or prefix match), which is what
the new nightly deep-soak CI step relies on.

### Added (2026-11-15 — long-horizon soak suite + nightly deep-soak CI; ParallelCalls goes live-editable)

New `soak` suite (23rd): three fake agents driven through `SOAK_ROUNDS`
whole turns (default 40) over a cycling pathological reply stream — normal,
empty, whitespace, prose-as-thought, a 120KB flood, an identical parrot
line, a JSON tool call, broken JSON, a null endpoint failure — with
cross-agent chat events landing in each other's queues. Asserts the
invariants that only show up over distance: nothing escapes
`ConsoleTurnCore.Run`, the round budget holds (the historical empty-reply
runaway class), transcript/history growth stays linear, wall clock stays
bounded. CI gained a nightly schedule (04:17 UTC) + `workflow_dispatch`
that re-run the full suites and then scale the soak to 2000 turns per
agent — 50x the per-push distance. `[LLM] ParallelCalls` now follows live
config edits (re-clamped and pushed into the SafeHttp gate on
`SettingChanged` — previously restart-only), and its guidance gained the
Ollama note: Ollama queues gracefully but serves only
`OLLAMA_NUM_PARALLEL` slots, each splitting the model's KV window — 2–4 is
the sweet spot there.

### Added (2026-11-15 — Ollama keep-alive heartbeat: no more mid-session model unloads)

Ollama unloads the served model after its default ~5m idle, so any quiet stretch
in play (exploring, reading chat, a long station scene with no decision due)
evicted it — and the next NPC turn stalled behind a multi-gigabyte reload. That
stall was the "model_unloaded" failure the v1.2.2 retry only patched around;
this removes the cause. The Ollama watcher now sends Ollama's documented
keep-warm call — `/api/generate` with an empty prompt — carrying an explicit
`keep_alive`, roughly every half of the configured window (60 s for the
default). New `[Ollama].KeepAlive` (default `30m`; durations, bare seconds,
`-1` to pin for the session, `off` to disable and keep Ollama's own behavior).
Pings stop with the game, so the default also unloads shortly after you quit;
`-1` holds the RAM until the server recycles it. The heartbeat rides the
existing watcher thread (no new thread), only fires while an Ollama is adopted
and a current model is known, throttles to one attempt per interval even on
failure (a timed-out ping usually means the server is mid-load — the one time
hammering hurts), and logs once when it activates. Parse/cadence math lives in
pure `OllamaArgs.KeepAliveSeconds` / `KeepAlivePingSecs` with new tests in the
ollama suite; the WebUI AI-server snapshot gains a `keepAlive` field.

### Added (2026-11-06 — headless agent sim: the console turn engine is unit-testable)

The console REPL's turn loop — rounds, guards, nudges, the anchor dedup — was
the least-tested code in the plugin exactly where it mattered most: it is where
the historical console bugs lived (the idle-ENTER transcript runaway, the "now
events" anchor flood, the parrot loop), and it was Unity-coupled, so no suite
could reach it. It now follows the pure-core pattern (`ContextCore`,
`PathCore`).

- **`src/ConsoleTurnCore.cs` (pure) holds the whole turn engine.**
  `ConsoleTurnCore.Run(state, env)` is a port of `ConsoleTurn`'s control flow;
  `ConsoleTurnState` carries the cross-turn streaks/throttles/anchor-dedup
  state (wiped by `ResetMindNow` via `Reset()`); `IConsoleTurnEnv` is the seam
  the game supplies (LLM query, command execution, transcript,
  events/anchor, clock, logging).
- **`src/Console.cs` keeps only the game wiring**: `ConsoleTurn()` is now a
  try/catch adapter plus a nested `ConsoleTurnEnv` mapping the seam onto the
  real Unity-coupled members. Behavior is unchanged — the engine is a
  mechanical port, quirks included (a limit-cut turn still ends without
  sending its `[limit]` line; only the next tick's fresh budget tells the
  model).
- **`TextUtil.Sanitize` / `TextUtil.Shorten`** moved out of `Llm.cs` /
  `Console.cs` (same behavior, pure home) so the engine can use them
  headlessly.
- **`tests/test_turncore.cs` — the scripted-LLM agent sim.** A fake
  OpenAI-style replier plus a fake world terminal drive WHOLE console turns
  through the real engine: anchor dedup/bare poll, ENTER frames, the
  prose-nudge ladder, identical-repeat and terminal-copy guards, roleplay
  blocks + the star-narration nudge, the command budget (`sleep` honored when
  spent, suppressed echoes refund their slot), unknown-command escalation, the
  stuck ladder, scratch commands riding free, pipes/`tee`, multipart
  screenshots, endpoint gates, the turn clock, and salvage/`<think>`-strip
  integration — 99 checks, with the real `ConsoleShell` parser, `ConsolePipe`
  filters, `ConsoleSalvage` and `ModelFamilies` running inside the simulated
  turns. Suites: 21 → 22.

## [1.2.3] — 2026-11-05

### Changed (2026-11-05 — new default profile: rapid, immersive, Ollama-first)

The shipped defaults now reflect a real, well-played setup — tuned for rapid
response, deep immersion, better tool-calling/roleplay, and general helpfulness.
**No existing config file is touched** (BepInEx only writes a key on first
launch or when missing), so this only changes what a fresh install boots with.

- **Ollama is now the default host.** `[Ollama] Enabled` and `AutoEndpoint`
  ship ON: the mod watches your Ollama for models and routes the action model
  through it (the hand-off the WebUI's AI-server tab has always offered). With
  no Ollama running it just idles (`healthy=false`), so a fresh install without
  Ollama is unaffected — `[LLM].Endpoint` remains the fallback. The default
  `Model` string is still the neutral `local-model` (not pinned to any one
  model); see the model ranking below.
- **Faster, livelier turns.** `ThinkInterval` 0.4 → **0.1 s** (rapid response),
  `Temperature` 0.3 → **0.8** (more varied roleplay), `PlanStepDelay` 0.35 →
  **2 s**, `PlanMaxSteps` 8 → **2** (decisive single-step plans).
- **More immersive perception.** `MaxTokens` 1024 → **18096**,
  `ImageEveryNTicks` 6 → **2**, `[Vision] Stereo` off → **on** (harmless on a
  text-only model — the image is dropped when the server can't see),
  `RayCount` 9 → **18**, `RayRange` 25 → **32**, `RadarSize` 10 → **30**,
  `RadarScale` 1.2 → **0.25 m/cell** (finer, wider sonar).
- **Console + concurrency.** `[Console] MaxRounds` 3 → **5**,
  `[Console] MaxTokens` 256 → **4096** (more back-and-forth, roomier command
  output for better tool-calling), `ParallelCalls` 2 → **8** (multi-NPC /
  multi-slot servers; lower it to ~2 on a single-GPU serial box).
- **`MaxNPCs`** 1 → **4** (more kobolds on the LLM at once).
- **The opt-in adult features stay OFF by default.** `SwallowEnabled`,
  `SwallowAgentEat`, and `SwallowPlayerVictims` are unchanged (still false) —
  that OFF-by-default safety pillar is deliberate and untouched.
- **Ornith recommended higher.** `ornith-1.5:35b` moved up to the 🟡2 tier in
  [docs/MODELS.md](docs/MODELS.md) as the default-recommended model — a
  user report of rapid responses, deep immersion, strong tool-calling +
  roleplay, and notably good handling of the game's adult subject matter. It's
  text-only (no vision), so pair a vision model via `[VisionModel]` for
  first-person frames.
- `IdentityBot` stays OFF by default (multiplayer "safe default" — unrelated to
  the qualities above).

### Fixed (2026-11-04 — server stability, `ps` target visibility, "now" event flood)

- **`ps` now lists every other kobold in the room, not just players and
  named agent siblings.** `DescribePeople` (Senses.cs) previously `continue`'d
  past any wild/unclaimed kobold and any claimed-but-nameless body, so a tasked
  target that was a wild NPC (or an agent whose name hadn't registered yet) was
  invisible to `ps` — "go eat Flint" failed because Flint wasn't on the list.
  The list is now built in two passes (addressable agent siblings first so the
  8-entry cap can't starve them behind a crowd of wild bodies), and every other
  kobold is listed and classified — player / agent / wild — never silently
  dropped. The old `[ps census]` debug re-scan (which existed only to diagnose
  this very drop) is removed since an empty result now genuinely means no one
  else is present.
- **Commentary worker can no longer stack threads under a slow server.**
  `MaybeCreativeCommentary` (Llm.cs) started a fresh worker thread every due
  tick with no busy guard — a slow model let commentary threads accumulate,
  each holding an HTTP call and a full payload, pushing concurrent requests
  past the `ParallelCalls` gate (the field "second-kobold freeze"). It now
  mirrors the vision worker's `_visionBusy` guard with a 2-minute wedged-flag
  reset.
- **The `model_unloaded` retry no longer hammers a reloading server.** The
  retry loop re-POSTed the identical payload up to 3× with 4s sleeps, holding a
  gate slot for up to 12s and re-stalling a server mid-reload (a reload takes
  seconds — each retry extended it). Now: one quick in-call retry covers a fast
  reload; if that still fails the tick defers to the next one and the endpoint
  breaker backs off the whole NPC, freeing the gate slot.
- **Console "now events" no longer flood the log/transcript.** Every tick,
  `ConsoleTurn` re-pasted the entire state anchor (clock + goal + needs + …) as
  a fresh `user` message even when nothing changed — the per-tick "now" dump.
  The `now:` clock string is now rebuilt only every 5s (its per-second advance
  was defeating any dedup), and the anchor is re-emitted only when it or fresh
  events actually changed since the last turn; an idle tick sends a bare poll
  so the model is still prompted to act while the last real anchor stays in the
  transcript for context.

### Fixed (2026-11-04 — \uXXXX escape parsing, name-prompt echo)

- **`Json.Parse` now decodes `\uXXXX` string escapes.** The hand-rolled string
  parser handled `\n \r \t \" \\ \/` but not `\u` — and Go-based
  OpenAI-compatible servers (Ollama, OpenAI) HTML-escape `<`, `>`, `&` as
  `\u003c`/`\u003e`/`\u0026` in every response string. Those arrived as literal
  "u003c" text: a model echoing the name prompt's mandated "Name: <the name>"
  line got registered as the NAME "u003cthe" — clean alphanumerics to the
  salvage regex, so it validated and stuck. The same gap hid inline `<think>`
  tags from `StripThinkBlocks` ("u003cthu003e") and broke `Json.Write` →
  `Json.Parse` round-trips of control characters, which the writer emits as
  `\u00xx`. Escapes now decode (including surrogate pairs); malformed hex stays
  literal, per the parser's lenient convention.
- **The name prompt's mandated final line no longer shows a placeholder.**
  With the parser fixed, an echoed `Name: <the name>` fails validation and
  re-rolls — but the echo still burned the attempt. The mandated line (builtin
  template, shipped `system_prompt_name.txt`, TIME-IS-UP reminder, and the
  retry-with-memory instructions) now shows a concrete example —
  `Name: Nyxa (invent your own name — never the example)` — so no
  angle-bracket placeholder remains anywhere in the name flow. Echoes of the
  example itself can't collide: `NameRegistry.TryReserve` re-rolls duplicates.
- 7 new parser tests in `tests/test_json.cs` (`\u003c/\u003e/\u0026`, mixed
  hex case, surrogate pair, malformed-escape literal fallback, `\b`/`\f`
  decode, writer→reader control-char round-trip, and the Ollama response
  shape that produced "u003cthe").

### Fixed (2026-11-04 — swallow direction + thinking-word names)

- **The predator is no longer told it is the meal.** `SwallowManager` flagged
  the EATER's NPC with `Swallowed` during a conversion (a grab-suppression
  hack) — but `Swallowed` is the victim state: the eater's console refused
  every body command with "you are deep inside someone's body", and the state
  anchor said "YOU ARE INSIDE SOMEONE'S BODY (a swallowed guest…)" every turn
  while it was DIGESTING someone else. The states are now separate: a new
  `NPCInstance.Digesting` flag carries the one gate the eater actually needs
  (`ModGrab`'s per-frame grabber must not steal floor items mid-conversion),
  while the eater's console, movement, camera, arousal and anchor stay
  first-person honest — its meal awareness comes from the correctly-worded
  `[conversion]` events and the anchor's belly-guest line. End/abort teardown
  clears the new flag (the old hack's stuck-flag risk is preserved in reverse).
- **Names are no longer picked out of the thinking.** The name-flow's first
  salvage scanned the WHOLE reply for the last `[a-zA-Z0-9-]` token — the last
  word of an unmarked reasoning monologue ("…maybe I should check the list")
  became the kobold's name: field-observed names "no", "maybe", "idea",
  "kobold", "list". That scan is gone: a completed reply earns its `Name: X`
  contract line (last match wins), unmarked-CoT mining stays with
  `SalvageNameFromCoT` (end-anchored + stopword-guarded, complete replies
  only), and a TRUNCATED reply (finish_reason=length) now earns nothing from
  the early passes — only a `Name:` line FOLLOWED by a newline (provably
  complete before the cut) via `strictFinal`; everything else re-rolls with a
  bigger budget. The playtest words joined `CoTStopwords`. 8 new regression
  tests in `tests/test_modelfamilies.cs` lock the garbage words → null while
  real conclusions still land.

### Fixed (2026-11-04 — tool-result JSON serialization)

- **Tool responses no longer degrade to .NET type names.** `Json.Write` — the
  one choke point every tool result, WebUI op, and training record flows
  through — only handled `Dictionary<string, object>`; any other map shape
  fell into the generic `IEnumerable` branch, where each entry's
  `KeyValuePair`/`DictionaryEntry` struct failed the class-only reflection
  check and serialized via `ToString()` — `Dictionary<string,string>` came out
  as `["[a, 1]","[b, 2]"]`, `Dictionary<string,List<string>>` as
  `["[k, System.Collections.Generic.List`1[System.String]]"]`, `Hashtable` as
  `["System.Collections.DictionaryEntry"]`. Downstream, `ToDict`'s
  write→parse round-trip then recovered a list instead of a dict, so the agent
  saw mangled text or raw type names where it expected JSON data. The writer
  is now a proper depth-capped map/array/object builder: any `IDictionary`
  (generic and non-generic, keys stringified), `KeyValuePair<K,V>` as
  `{"key":…,"value":…}`, public-field structs (Unity `Vector3` → `x/y/z`), and
  explicit `char`/enum/`DateTime` handling. Cyclic shapes terminate at a depth
  cap instead of stack-overflowing.
- **The remaining `ToString()` leak sites at the formatter edges got the same
  guarantee** via a new `Json.Text` single-value helper: the console result
  formatter's perception-dict getter `G` and scan-value loop, the tool-log
  args brief, and the legacy act-parser's `CleanValue` — a dict/list value
  there used to render the type name into terminal output and the tool log.
- **The playtest's "echo System.Collections.Generic.Dictionary`2[…]" parrot
  loop is traced to its real root and fixed.** The salvage path
  (`ConsoleShell.ExpandArgDict`) flattens a recovered tool call's arguments
  into a command line; act-schema replies put the arguments in a `params`
  DICT (`{"action":"say","params":{"text":"Hello! I am No."}}`), and that
  dict hit `ToString()` — the recovered line became
  `say System.Collections.Generic.Dictionary`2[System.String,System.Object]`,
  entered the transcript as an assistant message, and the model copied it for
  turns. `params` now flattens one level into real arguments
  (`echo Hello! I am No.`), and any other dict/list arg value renders as JSON
  data instead of a type name. 4 regression tests in
  `tests/test_console_shell.cs` lock the shapes.
- **The WebUI terminal's "user: System.Object[]" rows are fixed** — a display
  bug, not a model-facing one. Screenshot-attached messages carry multipart
  content (a text part + an image part in an `object[]`); the web terminal's
  `Convert.ToString(content)` rendered the array's ToString — the bare type
  name — exactly where `use` had just turned the body and the auto-screenshot
  fired. The terminal now flattens through `ConsoleMsgText` (the same
  text+`[N image(s)]` shape the in-game Mind overlay uses), so the debug view
  shows what the model actually read. The LLM request path was already
  correct (the fixed `Json.Write` serializes the parts array properly).
- 12 new writer tests in `tests/test_json.cs` (string/int value dicts, nested
  lists, Hashtable, KeyValuePair, arrays, enum/char, struct fields, cyclic
  depth cap, round-trips, `Json.Text`) — all compile-only against `Json.cs`.

### Added (2026-10-14 — behavior-hardening round)

- **Quest lifecycle is closed out.** QuestSense now tracks the objective OBJECT
  (not just the title string), so `autoAdvance` chain quests — which used to
  read as "objective progress" for the next letter forever — are announced as
  `OBJECTIVE COMPLETED` the moment the scroll hands off; scene teardown can no
  longer masquerade as a completion (error-streak guard + scene-stability
  window). A completion fanout sheds quest-scoped facts from every instance and
  auto-retires a goal that was that quest (recorded as `game-cleared` in the
  goal history). `cat quest` now answers with the quest scroll (it used to open
  the plan display).
- **Facts finally age in wall-clock time.** `FactRec` carries a UTC stamp that
  survives save/restore (`fact_utc` per record; old saves fall back to the
  record's save stamp), so ancient notes stop waking up brand-new at every
  session. `[Memory] FactMaxAgeHours` (default 72h, 0=off) prunes facts not
  re-remembered in that window — during the prompt render AND before every
  save. Identity notes (`me:`/`name:`) and the latest `player_wants:` are
  exempt; compaction-3 merges carry the newest member's age instead of
  resurrecting old lines as fresh.
- **Agent↔agent chat is named as such.** An addressed sibling line reaches the
  addressee as `X (another AI kobold) said TO YOU: …` (players stay
  `player: …`), and the prompt surfaces (builtin + console/default files, in
  sync) now teach that a named-sender line is another agent to answer by
  name+comma — agents had been taught that every `[chat]` is the player. The
  victim's swallow-begin promise text is second-person throughout ("you went
  in through X's mouth"), removing the eater/victim pronoun blur.
- **The body-swap machine rebind works again** (regression fix). The machine
  re-fires `bodySwapped` from stale pod timers with both bodies control-flagged
  mid hand-off; those re-fires used to hit silent early-returns in
  `RebindAfterSwap`, leaving two minds driving one body and the AI "never
  swapping". Every guard exit now logs with its reason; a STALE claim on the
  incoming body (no live sibling instance behind it) is cleared and the
  machine's hand-off taken; a LIVE sibling claim keeps the normal both-minds-
  move resolution; and a 12s swap-grace keeps the reconcile claim race out of
  the hand-off window.
- **Wall-through interaction is gated three ways.** `interact` requires
  proximity (InteractRange+1.2m; the id-route used to fire from 14m away) and
  a clear body→target line (`wall_between` teach-on-refusal otherwise), the
  touch-bubble selector ignores usables on the far side of a thin wall, route
  arrival requires clear sight of the original goal on the final leg
  (throttled replan — "arrived outside the building, interacted in" is over),
  and both path pipelines end a SNAPPED goal at the landing CELL instead of
  re-pinning the raw goal X/Z through the wall.
- **Movement lost the fly act.** Arrival braking was a `Mathf.Max` (could only
  raise the throttle — bodies charged every waypoint at full speed, overshot,
  tripped the bump-breaker); it's a cap now. `go_to` no longer snaps the yaw
  (a model re-issuing it every think-tick whipped the ragdoll 90–180° per
  tick) and its results stop telling the model to "re-issue go_to" — the
  route course-corrects itself; only new destinations need a call.
- **Glued echo tails run.** `echo I will go check cat quest` speaks "I will go
  check" and EXECUTES `cat quest` (it used to speak the whole line; the model
  believed it had checked and asked again). Deliberately narrow with
  false-positive guards (articles before `cat` refuse the split; pipes/quotes
  stay the payload rule's business) + prompt teaching + unit tests.
- **The empty mailbox stops reading as busy.** A mailbox `CanUse=false` now
  returns `mail_empty` with "no mail inside right now — come back when the
  space-beam glows" instead of the generic "maybe busy/occupied" hint that
  had agents camping an empty box.
- **WebUI settings page is organized.** `/api/config` sorts by canonical
  section order (LLM→Vision→…→Debug) then key and dedupes ids; the accordion
  groups by a keyed map instead of section-change detection (one drawer per
  category — interleaved reflection order used to spawn duplicate LLM/Vision/
  General drawers), and drawer open-state survives filter reshuffles by
  summary text instead of index.

### Changed (2026-10-14 — repo layout: training + serving split out)

- **Training lives outside the mod repo now** — the whole `training/` tree
  (pipeline code + 85 GB of checkpoints/datasets) moved to
  `Work/KK-Training`. The MiMo fine-tune run was stopped mid-flight and the
  MiMo track dropped (safety-RL base + thin data; see `KK-Training/
  PLAN-PASS2.md`): pass-1 audited with the new `curate.py` (coverage holes +
  zero think traces + 8 raw-think-tag episodes, since stripped to
  `episodes.curated.jsonl`), and pass-2 records with thinking ON from the
  current build for the new event vocabulary.
- **The python AI server moved to its own `kk-serve` branch** — `serve/` (the
  OpenAI-compatible torch server + fetch assets + python contract tests)
  rides there for separate refinement; master keeps the mod-side launcher,
  which degrades to external-endpoint adoption when the python dir is absent.
  `build.sh` and the test runner skip the serve steps cleanly either way.


---

Older entries (pre-2026-10-14: wave rounds, kk-serve companion): [docs/changelog-archive.md](docs/changelog-archive.md)
