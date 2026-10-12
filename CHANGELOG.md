# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Model list: HauhauCS Gemma4-12B-QAT Uncensored (Balanced) enters the compatibility
  list as a user-recommended 🟢 pick** (`HauhauCS/Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced`);
  auto-classifies as the gemma family (no code change). Card-backed: uncensored (0/465
  refusals), native tool-calling template, vision mmproj, 256K context, Q4_K_M sweet
  spot, MTP speculative decoding for ~60% faster generation; noted: single-GPU only
  (LM Studio tensor-split crashes it), card sampling values recorded. Field notes to
  follow a real playtest per the doc's convention.
- **The watering crash is fixed and the mod is hardened against its own reflection.**
  The crash-on-watering: the can-free watering assist ran its Photon RPC (and the
  plant-resolution scene scans) on the LLM thread — Photon RPCs must stay on the main
  thread. Everything inside `WaterPlantDirect` is marshalled now (the tool's assist
  resolution included), so the whole watering path is main-thread-correct.
- **A skill system: agents author their own reusable procedures, and skills travel.**
  `skill add <name> | <step> ; <step>` saves a named procedure OVER REAL COMMANDS —
  each step is validated against the shell vocabulary at save time (junk steps are
  dropped silently; an add with none is refused with the teaching line). `skill <name>`
  executes the steps as one batch (one history entry, budget-exempt — a skill IS
  budget), `cat skills` lists them with step counts and use counts, re-adding
  redefines, `skill forget` drops. Where they live:
  * saved per agent — the body's MemoryStore record carries the skills (restored when
    the same body wakes),
  * transferred on ABSORB — when a body is digested in Grow mode, the eater inherits
    the victim's skills (name collisions suffix "(absorbed)" instead of silently
    losing either), with a fact + world event so the eater knows it has them,
  * transferred through a CHILD EGG — the rebirth copy moves the skills list onto the
    hatched body's record: the hatched child wakes with its parent's skills.
  Parser (ConsoleShell.SkillParseAdd) is pure + unit-tested (the console suite again
  caught the edge cases: leading-keyword tolerance, junk-step drops, cap trims).
  The state anchor and whoami surface "skills: N" when the agent has any, so skills
  are discoverable without reading docs.
- **Fresh bodies wake up in a bed — never at spawn.** A brand-new possession (wild
  spawn, dispenser delivery, a rebirth hatched from a soil-planted egg — anything that
  is not a bodyswap's identity-reuse) now places the body beside the nearest bed in the
  scene (same map-wide list `cat places` reads; skipped when the body already lies
  within 1.5 m of one or no bed exists). Beside, not inside: the sleeper engine would
  grab a body placed into the bed into `in_station`, and nobody should wake up already
  locked in a machine. The console boot line opens "you just woke up in the bed 'Name'"
  so the model's first scene is waking up, not standing in a field.
- **Star-text is words, and the model is told so at the moment it matters.** RP models
  narrate "*walks to the kitchen*" and wait — nothing moves, because only command lines
  act. When a spoken response block narrates a physically-actable starred action
  (`TextUtil.StarsWantRealActions`, pure + unit-tested: walk/step/climb/enter/grab/sit/
  mount/…), the mod speaks the block AND adds one throttle-gated reminder row: "the game
  did not move — send the real command". Prompts (built-ins + shipped console file)
  state the rule outright: star text is narration for the reader; the world changes
  through commands; back a starred act with its command the same turn.
- **Response blocks: roleplay prose is the voice now, not noise.** The playtest loop
  that motivated the identical-repeat guard is solved the other way around — an RP
  model answering in a roleplay **block** (dialogue in quotes, `*action*` asterisks,
  or an {{user}}-style address) is no longer thrown away as a "thought" and nudged to
  wrap itself in `echo`; the whole block is DETECTED (`TextUtil.IsRpResponseBlock`,
  markers are objective: paired asterisks with words between, paired quotes, or an RP
  placeholder) and sent to the chat window verbatim, exactly as written — mixed replies
  speak the block AND run the commands, all-prose replies speak and end the turn. So
  the models that were "barely speaking" because their voice landed in the thought
  bucket just talk. Cadence guards stay: with a [chat]/[event] trigger the block speaks
  freely; without one it rides the idle-chatter throttle (one block per ~90 s) so a
  block never becomes a monologue, ToolSay's repeat suppression plus the identical-repeat
  guard remain the loop breakers, and the "REPLY WITH COMMAND LINES ONLY" nag stands
  down for a turn that spoke a block. Taught in all prompt surfaces (built-ins + the
  shipped `system_prompt_console.txt`); `echo` is still there for a quick plain line.
- **{{user}}/{{char}} resolve to real people at speak time.** RP-template models
  (SillyTavern trained) write their placeholders straight into speech — observed live:
  "nudges against {{user}}'s entrance" went to the chat window verbatim. Say and emote
  now expand them against the actual world at the moment of speaking: the live
  engagement partner first (who's inside this body / whose is inside it — the same
  sources the partners perception reads), else the nearest person in radius (14 m,
  player avatar before sibling NPCs), else the host player's chat name. {{char}} is the
  NPC's own name; unknown placeholders pass through; with nobody known at all the
  placeholder stays literal rather than inventing a name. Pure + unit-tested
  (`TextUtil.SubstituteRpPlaceholders` in the textutil suite).
- **The "garbled tool-call" mystery is solved and fixed: it wasn't tool calls at all.**
  The raw-reply samples (every 10th salvage fire, added last round) show the fires were
  LONG ROLEPLAY PROSE paragraphs, and the old glue-splitter was the butcher: its
  "2 command words anywhere in the line" rule hit prose command words scattered one at
  a time — "let me **use** you~" manufactured a bare `use`, "who am I"-style fragments
  ("`me`" → `whoami` alias, "**burn**" → `turn` via fuzzy) turned paragraphs into junk
  command lines that then executed. The glue scanner now splits only on a DENSE
  command cluster (two command words in a three-token window, matched tight — exact
  table + aliases, no Levenshtein, and function-word aliases `me`/`id`/`self` never
  count), while the loose matcher still recovers mangled commands from real junk
  segments (`goall` → `goal`; that path only runs inside already-cut markup). Salvage
  also never manufactures bare dangerous commands (`use`/`get`/`drop`/`throw` with no
  target) out of prose debris, and the markdown fence was dropped from the junk-marker
  list (fenced JSON is the tool-call salvage's job; as a junk marker the fence cut RP
  paragraphs).
  Regression-tested with the actual playtest shapes: RP prose stays prose, glued
  queues still split.- **Tool-call formats the model actually speaks: native tools + full-decode salvage.** The
  console request now declares the whole command vocabulary as an OpenAI tools array
  (`[Console] NativeTools`, default on): servers with function-calling (llama.cpp --jinja,
  LM Studio, vLLM, Ollama, KoboldCpp) parse the model's output into real `tool_calls`
  deltas — deterministic command + arguments, and ALL calls in a reply execute in order as
  a multi-command turn (ParseChatResponse now surfaces every call, not just the first).
  Servers without tool support get the field ignored or are fenced off by a new `tools`
  rung of the 400 ladder (`ApplyRequestFix`, 300 s pause), and two text fallbacks cover the
  rest: the reply parser, and a new bracketing salvage (`ConsoleTools.ToolCallMarkupLines`)
  that brace-matches the tool-call JSON models stream as content (`<tool_call>`-wrapped,
  `<|tool_call|>`, plain fenced) — narration preserved, arguments honored — instead of the
  old word-scavenger that mangled them. Legacy act mode grew the same muscle: extra `tool_calls`
  beyond the first execute as plan steps, and console replies from native calls land as the
  model's own command lines.
- **The `plan` ladder — persistent HOW under the stored WHAT.** `goal` keeps holding the
  goal; `plan <step>` now lays out the ordered steps once (max 12, `plan`/`cat plan` list
  them, `plan next` marks the front step done, `plan clear` wipes). The state anchor carries
  the current step every turn, so the how-to survives trims; the ladder clears automatically
  when the goal completes or drops. Prompts (built-ins + `system_prompt_console.txt`) teach:
  goal once → plan the steps → one step per turn.
- **Conversation memory that no longer cliff-edges.** `TrimConsoleConversation` replaces the
  dropped span with one compact `[session memory]` digest (first-three/last-two highlights,
  `ConsoleShell.ConsoleCompact.Digest`, pure + unit-tested) instead of deleting it, and the
  state anchor opens with an identity heartbeat line ("you: Name — persona") so the persona
  survives long consoles for template-heavy models. Legacy act-mode caps rose across the
  board (history 5/10/15 → 10/18/40, thoughts 3/6/8 → 6/10/14, facts 10/20/30 →
  12/24/40, chat 5/15/30 → 8/20/40) — the old floors started small models from amnesia
  while big models kept 15 lines for the same reasoning.

- **Roleplay data the NPCs can query — including their own body.** The game gives a
  possessed kobold no mirror and no spawn description, so the model couldn't truthfully
  describe itself or where it woke. New/extended surfaces: `whoami` gains a gene-derived
  appearance line (scale color named from the game's hue/saturation/brightness body
  paint — "pale green", "dark grey" — size/build from the inflate/fat genes, curves and
  groin from breast/dick genes, main-thread built); `cat here` (alias `area`) is a full
  look-around in words (the 360° spatial prose + what's near) — the console previously
  had no way to read any of it, it only lived in the legacy perception payload; and the
  wake line + a durable `spawn:` fact now describe WHERE the body woke on every map —
  in the bed when one exists (the wake-in-bed feature), otherwise "lying near <named
  places> — around you: <prose>", so maps without SleepyHeadStations still wake with a
  scene instead of "Scene: unknown". The fact carries named anchors only (the fact store
  refuses viewer-relative prose and the area text is full of it); the full surroundings
  live in the transcript. Taught in the prompts: "you have no mirror — `whoami` is your
  look; `cat here` is your eyes."
- **`cat` help/tools now list `plan` and `here`** (the command table and the NativeTools
  function description were two revisions behind the vocabulary).

- **Finer pathfinding: the body fits through what the path says it can.** Three linked
  fixes for the the door-frame/fence playtest report. (1) Path smoothing
  and local A* string-pulling cleared a single CENTER ray — which slides through door
  gaps the half-meter capsule can't, leaving the body humping the frame post forever.
  Every cleared segment now also checks two lateral lanes at ±0.35 m (body half-width),
  in the shared clear-check both the world-map and local paths use; fan-steering does
  the same three-lane check, so steering only picks headings the BODY fits through.
  (2) Fences, sills and low walls are HOPPED, not braked at: when the forward ray's
  obstacle top is within ~1 m of the feet, the body is grounded, the collider isn't a
  kobold or a usable machine, and landable ground exists beyond (not a cliff), the
  movement drives a one-shot jump over it (1.4 s cooldown) instead of reporting
  "no clear way". (3) A stuck-breaker for everything else: a first bump reports
  "turn"; a second rapid bump throws a sidestep shove toward the last fan-steer lean;
  a third drops the whole plan and re-plans around the spot from the stored goal
  (world-map first, local grid fallback) — the A* then routes around whatever kept
  getting ground into. (4) The world-map cell default went 1.0 m → 0.5 m for door-
  width granularity (big maps still auto-inflate to the hard cell cap), and a cached
  map that is COARSER than the configured cell is now discarded instead of silently
  winning.

### Fixed
- **The LateUpdate stutter is gone — the grab patches no longer reflect per frame.**
  The body-scoped grab patches ran String-keyed `GetField` reflection for every
  grabber on every LateUpdate frame (AI-owned and player alike) — `type.GetField`
  scans metadata each call, and a scene with a few grabbers hitched every frame.
  Grabber handles (`view`/`player`/`offset`/list) and AI-ownership now come from a
  per-grabber cache: FieldInfo resolved once per grabber instance, ownership checked
  ~once per second, per-frame work reduced to dictionary reads and cached
  `FieldInfo.GetValue`. The claim scan's rebirth check also gained a hot-path guard —
  gene fingerprinting only runs while an egg is actually pending (registry
  non-empty); the common case costs one dictionary read, not a photon genes read
  per candidate per tick.
- **The egg-plant freeze is contained and the next one will name itself.** The plant
  flow gained full phase instrumentation: every main-thread marshal in the planting
  path (soil resolve → teleport item → LocalUse → verify → water assist) sets the
  live `MainPhase` label the hang watchdog already reads — a frozen game now leaves
  "main thread appears HUNG ... last phase: plant: LocalUse" in the log instead of a
  blank stare. The watering assist stopped nesting marshals too: `WaterPlantDirect`
  split into a main-only worker (no marshals inside; the auto-water inside the plant
  macro calls it directly) and a public marshalled wrapper (the tool path).
- **A jump pushes into the facing now — no more hop in place.** Two layers: `jump`
  (and `walk(jump)`) now carries a real forward burst into the arc (the game's
  controller ignores move input mid-air, so a plain inputJump hopped vertically and
  landed exactly where it started), and at jump-start the movement applies a
  horizontal impulse along the current facing — auto-hops over fences/sills push
  harder (3 m/s, they aim at a landing) while command jumps scale with their walk
  speed (≥0.9 m/s minimum). Exiting a station is untouched (the game's cancel path).
- **A hatched rebirth can't be stolen by a claim race.** The player was swallowed,
  the egg planted, cycles done — and instead of the player spawning, ANOTHER agent
  possessed the fresh body first. The claim scan now skips any body currently in the
  rebirth registry (`SwallowManager.IsAwaitingRebirth` — a fingerprint match against
  pending hatches): those bodies belong to the hatch flow, which revives the person
  (player or NPC) in the fresh body and despawns the duplicate; claims are unlocked
  only once the registry releases the fingerprint.
- **A bucket pickup could crash the game — the grab patches are now fail-safe.** The
  mod's body-scoped TryGrab/LateUpdate prefixes returned "handled" even when their own
  reflection work threw, so the original code got skipped after partial failure; a
  bucket pickup (its GrabInfo shape) hit that path. Now ANY reflection or shape
  problem falls back to the ORIGINAL game code (the patch only claims the takeover
  when its full job completed), the grabbed-objects list is read as a non-generic
  IList (no generic-cast trap), bookkeeping is ordered add-before-RPC, and failures
  log instead of crashing. Human grabbers were never patched.
- **The growth ladder is explicit: plant → water → stage 2 → water → stage 3 → fruits
  or spawning.** The player clarified the mechanic after the first round: ONE watering
  advances exactly ONE stage — two waters total, no more, no less. So the direct-
  injection dose went back to a fixed moderate amount (capacity-filling was a wrong
  guess and buys nothing), the `water` result spells the ladder verbatim ("watered →
  next stage … water N more time(s)"), and the fully-grown branch now says what
  actually happens next: for normal plants FRUITS (harvest), for an egg it spawns a
  fresh empty body. Prompts on all surfaces carry the ladder.
- **Growth is visible stage by stage, and the plot is named.** "Couldn't get it to the
  third stage" was invisible: `water`/`plant` results now carry the plot's name, id
  and addr (the model learns the egg/seed IS on a valid soil tile instead of hunting
  another plot), plus `stages_left` / `mature` — "each watering matures it one stage;
  water again until stages_left: 0, then harvest". And the direct-injection dose
  FILLS THE PLANT'S CAPACITY (the old 60 ml fixed dose only advanced a stage part
  way — growth drinks the whole container) for a guaranteed one-stage bump per
  watering. Prompts teach the cycle: planted on a valid tile → water until mature →
  harvest.
- **Watering needs no can anymore: `water` falls through to direct injection.** The
  can-free path the user asked for: when no watering can/bucket is within reach, the
  water tool (and the post-plant auto-water) injects the `Water` reagent straight into
  the plant's own `GenericReagentContainer` via its photon RPC (`AddMixRPC`, `Inject`) —
  the exact effect the can's spray produces, with no equipment and no carrying. The
  resolve covers plants and soil tiles (a tile's `planted` plant is fetched directly);
  the result says `how: "direct (no can — the mod applied the water)"` so the model
  knows. Config-gated: `[Farming] WateringAssist` (default ON) — off restores
  "watering requires real equipment". The bucket/can keep working as the physical way;
  prompts now say "WATERING NEEDS NO CAN".
- **The water bucket can't be drunk dry anymore, and watering reaches seeds AND planted eggs.** Agents were `use`-ing the bucket/can — the drink path empties the water — and then had nothing to spray with. Now a water tool NEVER routes to use: held, `use bucket` fires the spray at the nearest plant (contextual use), on the ground it teaches "get it first"; in hand, `water` fires the can→target spray `water id:N`-style at a PLANT or at a SOIL TILE's photon view (a planted seed/egg may have no Plant entity yet — the spray reaches them in the dirt). Misses are honest: "nothing in reach" now names the nearest plot's distance instead of a bare no_plant, and a map-wide can hint was already pointed at. Prompts on all surfaces: "THE WATER BUCKET/CAN IS A TOOL, NOT A DRINK — `use` on it is refused".
- **A newly-gained penis can't be misread as penetration.** When `SwallowEffect=Grow`
  granted a body a dick (`SetGenes` live-spawns the penetrator at the crotch), the first
  frames registered it against the OWNER'S OWN penetrables: the agent read "someone is
  inside me", believed a phantom or their own body was fucking them, and begged not to
  stop about an organ attached to themselves. Three gates: the penetration handler
  refuses any penetrator whose owner is the body itself; the dick-in watchers ignore
  targets on the body's own hierarchy (with a silent purge of any listener that fired);
  and the grow step purges the self-side state immediately after `SetGenes`. The
  gained-dick body event now also says it outright: "It is attached to YOU and can
  never penetrate you — if you feel 'someone inside you' it is a misread of your own
  body (`feel` reports holes truthfully)".
- **A digested body is no longer narrated as sex.** The swallowed person's body parts
  are live penetration machinery: their dick is a penetrator prop that "entered" the
  eater's mouth on the way down, so the eater received a `[sex] BEING PENETRATED` event,
  computed a thrust rhythm for it, and focused the whole meal on that rhythm ("let us
  enjoy this rhythm") — while the victim's own side reported penetrating. Three gates,
  per side: the eater's penetration table skips any entry whose owner is a currently
  digesting victim (`SwallowManager.IsDigestingVictim`); a swallowed body's own
  dick-in watcher is suspended while `Swallowed`; and a mid-play swallow purges both
  sides' stale entries at begin (the victim was penetrating the eater when swallowed —
  that entry never sees a "pull out"). The eater's anchors already carry the STOMACH
  GUEST (meal, not sex) line; with the false events gone, the two can no longer
  contradict. Real parallel sex with an unrelated partner still registers normally.
- **Planting teleports the item onto the soil and waters the plant.** Farming was too
  painful to do the player way — the walk-aim-drop loop burned retries and the flow
  still depended on being on the dirt. The plant macro now: releases the held item
  (guarded `ToolDrop`, no force-feed risk looking down), takes ownership of its photon
  view, TELEPORTS it onto the plot tile (velocity zeroed), plants through the seed
  component, verifies the tile took, and then AUTO-WATERS the fresh plant by firing
  the nearest watering can's spray directly at it (any can within 5 m of the plot; the
  result says `watered: no` with a distance hint when none is close — `water` still
  handles the far case). The eye follows the item onto the plot so the model SEES where
  it landed. Walk-and-approach remains only as the fallback for a plantable without a
  photon view. Results now carry `planted` / `teleported` / `watered`; every refusal
  is the honest one. Prompt surfaces updated: "say plant — it teleports, plants and
  waters; you don't have to stand on the dirt or aim anything".
- **Carrying is anchored to the agent's own eyes — the grab works, and nothing circles.**
  The game's carry system reads the LOCAL PLAYER'S CAMERA twice a frame: held items
  spring to `viewPos + player-camera-rotation * offset` in `Grabber.LateUpdate`, and
  `TryGrab`'s sphere + sort ray sit wherever the player looks — so an AI body's carried
  items swung around it when the human player turned (the "flys around the thing it's
  targeting" report) and floor items/water buckets were outside any agent's real cone.
  Body-scoped Harmony prefixes (`src/ModGrab.cs`) now REPLACE `LateUpdate` and `TryGrab`
  for grabbers whose owner is this mod's AI body: anchors use the agent's own
  `_yawDeg/_pitchDeg`, candidate search uses the agent's eye cone (radius/count sorted
  like the original, CanGrab + grab-count respected, GrabInfo + OnGrabRPC replicated),
  and the human player's grabber runs the original code untouched. The plant macro's
  earlier "planted after many retries" flakiness traces to this too — the aiming was
  right while the game's own grab geometry ignored it.
- **"Stuck on a fence" and orbiting both trip breakers.** A THIRD stuck breaker:
  the anti-circling watch samples the goal bearing + distance every ~1.2 s; when the
  bearing has rotated ~110°+ while the distance stopped shrinking (`NavCircleWatch`),
  the body is orbiting an obstacle steering can't open — the route hard-re-plans and
  the turn reports "circling the target — re-planning". Progress decays the watch, and
  a route change resets it.
- **`deliver <place|id>` — carry it THERE and put it down.** The walk-it-to-the-location
  macro the user asked for: empty hands are refused ("get <item> first"), the target
  resolves from the nearby id registry / soil words (nearest plot) / place names,
  the route re-uses the go_to A* machinery, arrival is polled (40 s cap), then the eye
  aims down at the spot and the item is dropped ON it. Registered as a module tool in
  both modes, taught in every TOOLS/carry prompt surface (built-ins + shipped files).
- **Planting looks at the dirt, stands on the dirt, and tells the truth.** The game plants
  a seed where THE SEED is within its soil-overlap radius — and a held item floats ~1 m
  in front of the face, so a body 3 m off the plot "used" it, nothing grew, and the tool
  reported ok. The new plant macro (`PlantWithPositioning`): finds the target soil from
  the item's own footprint (the game's exact rule) or the nearest farm plot (map-wide
  cache, addr + bearing in the refusal), AUTO-APPROACHES when out of reach (bounded walk
  bursts, re-resolving the nearest plantable plot between attempts), aims the eye DOWN
  at the dirt before the use, and VERIFIES the tile stopped being plantable — `planted:
  true` only when it took, with an honest too_far/`used-but-nothing-grew` otherwise.
  Held-first: a seed OR AN EGG in hand goes through the macro (the laid egg item carries
  a `Seed` component — eggs plant through the same chain), free seeds still work; and
  `use soil`/`use egg`/`use dirt` with a plantable in hand routes to the macro while a
  bare `use` never hijacks station uses. Prompts teach it: planting is positional — be
  on the plot; the mod walks, looks and verifies for you.
- **The bee-in-a-window movement is tamed and the paths are planned, straightened through a quadtree.** Three movement-quality fixes for the the bumble-stutter playtest report: fan-steering gained HYSTERESIS (a heading that cleared stays preferred for 1.2 s instead of the ±20° flip-flop), corner approach switched to PURE PURSUIT (near a waypoint, the aim blends forward toward the next post — no more stop-turn-go-stop), and steering keeps a trickle of forward drive instead of full-stop braking cycles. The planner's string-pull now also runs against a QUADTREE over the walkability bitmap (`src/VoxelQuadtree.cs`, pure, with its own 57-assert test suite): uniform-open blocks collapse upward so a straight run across open field is proven against a handful of coarse nodes (zigzag legs fuse into one planned straightaway), mixed regions drill to the exact 0.5 m sampled truth, and every skip is verified at its landing cell — the first revision skipped phantom shifted blocks (slab-exit treated a block index as a corner) and clipped wall pixels through the gaps; both are fixed and regression-tested.
- **Small items are grabbed on purpose now: the grab AIMS.** The grab cone follows the camera view, so floor items (seeds, eggs) were simply never in it — the eye stayed level while the model "grabbed at" them. `grab`/`get` now resolves an optional target (`get seed`, `get id:3` via the nearby registry) or falls back to the nearest grabbable, turns the eye toward it including the downward pitch floor items need, waits a beat for the swing, then grabs; the result names what it aimed at. `use` already aimed — the asymmetry that made people grabbable but seeds not, is gone.
- **Item uses are taught contextually — an egg is either a body or a meal.** The prompts (built-ins + shipped console/act files, same lesson) say it plainly: the same item can have several uses, pick by what you want — a seed plants in bare soil; an EGG either plants in soil (`use` facing dirt / `plant_egg`) and grows a fresh empty kobold body, or is EATEN for the protein/strength buff by holding it and USING it (the game eats held edibles through use — GenericEdible is a GenericUsable). `get <name>` aims for itself; no manual head choreography.
- **The ability to cum is OFF while being digested.** The orgasm tool returns an honest
  refusal for a swallowed body — "no climax exists in a stomach; the walls churn, that's
  all" — checked inside `ToolOrgasm` itself (module pass-through dispatch included), on
  top of the round's general swallowed-gate; and `feel`, when swallowed, reports the
  digestion instead of the holes and drops the "orgasm when the peak lands" teaching line.
- **A stomach guest is no longer readable as penetration.** The belly anchor used to
  open "you have someone INSIDE YOU" — which RP-template models, with their priors,
  parse as sex, sitting one line away from the penetration anchor whose fallback said
  "someone is inside you". All guest phrasing is now stomach-and-meal: the anchor leads
  "STOMACH GUEST (you swallowed them whole — a MEAL, not sex: nothing entered your
  holes, and [sex]/feel lines never mean the guest)"; the penetration anchors name their
  holes explicitly ("being PENETRATED sexually — a dick is inside one of your holes…
  a stomach guest is NOT this"); swallow-begin events tell the eater "in your STOMACH
  (a meal, not sex)" and the victim "a MEAL — no sex is happening"; `feel` appends the
  guest as its own line marked separate from the holes; and the EATING prompt section
  (built-in + shipped console + act prompt, same sentence) states the rule once, plainly.
- **A swallowed mind can no longer act on the world.** Its LLM session keeps running (by
  design — it talks from inside), but every body-attached command it tried (`grab`/`get`,
  `drop`, `throw`, `use`, `cd`, `run`, `turn`, `jump`, `crouch`, `exit`, `follow`, and the
  module tools) now returns an honest refusal — "you are inside a stomach — hands, legs
  and machines do nothing from here" — on both the console path and the legacy act path.
  Speech, memory, goals/plan and read-only polling still work, and the state anchor says
  the same thing every turn so the model stops attempting instead of learning from
  failures alone.
- **Memory files are deleted once absorbed.** In `SwallowEffect=Grow`, the digested
  person's facts pour into the eater (unchanged) — but their own MemoryStore file was
  left behind, so a later body with the same serialized object name would wake up as a
  ghost of someone the world had eaten. Absorb now reads the records first and deletes
  the victim's file after the pour (same end state as Destroy), and the egg-rebirth copy
  deletes the old body's key once the life is restored onto the hatched body's key (it
  was a duplicate of someone already reborn). Egg/BellyEgg keep their records until the
  actual rebirth, as before.
- **Roleplay-locked repetition loops break themselves.** The debug terminal kept
  re-adding a roleplay model's identical prose ("*Ember blinks open heavy eyelids…")
  every single round — the transcript became a wall of the same paragraph. Consecutive
  identical replies are now counted: the first executes (a legitimate retry), the second
  gets one corrective row, the third ends the turn instead of re-executing and
  re-storing. Also, when a reply mixes unmarked reasoning prose with real command lines,
  the prose line is preserved in history as the thought behind the commands instead of
  vanishing.
- **The "recovered command lines from garbled tool-markup" log line finally shows WHAT
  the model wrote** — sampled raw (≤300 chars, every 10th fire) into the log, so the
  remaining garbled shapes name themselves in the next playtest instead of the log only
  counting them.

### Removed
- **The `ask` tool.** It re-asked the model about its own perception through a SECOND LLM
  call: answers were long self-narrating meta-prose ("I'm Wispette — a small kobold woman
  living in this world as my own inner model and monologue…"), landed as near-duplicate
  facts/history four turns running, and crowded the real event history out of the small
  window. The model already answers any question it cares about by polling (`ls`,
  `find`, `cat`, `sonar`, `status`); the tool's worker thread, console command, alias,
  perception keys (`asked`/`answered`), `[ask]` event drain, prompt mentions and schema
  entry all went with it, everywhere (act tools, fuzzy lists, salvage targets, ConsoleShell
  table + aliases, `help` text, docs).
- **The nest lay gate now names the REAL blocker — the swallowed-egg birth loop closes.**
  The game's lay rule is `egg > 5ml AND energy > 1` (OvipositionSpot.CanUse), but every
  refusal channel used to phrase the whole thing as "belly not full": a nest rejected an
  eater carrying a poured guest-egg (20ml, plenty) only because ENERGY was ≤1, and the
  model was told it wasn't full — so it decided the check was broken, the player saw the
  same and assumed a bug ("use the OviStation anyway"), and the eater drifted to a play
  station to "make eggs" — which produced nothing, ever. Fixes, so a swallowed person
  actually comes back: the go_to/interact nest gates split into `belly_not_full`
  ("you carry Xml egg — a nest needs >5ml; egg reagent comes ONLY from eating/swallowing")
  and `out_of_energy` ("you carry Xml egg — that IS enough — but energy is Y and a nest
  lays only above 1: eat food (or bed-rest) first; nothing else raises energy"); the
  `needs.eggs` perception reports which side is missing ("egg is enough, but ENERGY too
  low to lay") instead of a blanket "belly not full"; the BellyEgg completion event and
  the eat_player outcome note now spell out the pour → NEST LAY (needs energy too) →
  plant → hatch protocol at the moment it starts; the nest station purpose and every
  prompt line say "needs >5ml egg AND energy >1" instead of "belly is full"; and one
  wrong log line ("poured into the player's belly" for any eater) follows the truth now.
- **Sex stations can't be talked into energy or egg duty, and a two-slot station isn't
  a solo machine.** Every prompt surface (act built-ins in Main.cs + `system_prompt_default.txt`,
  console built-ins in Console.cs + `system_prompt_console.txt`, in exact pairs) now says
  plainly: stimulation/sex/play NEVER create energy or egg reagent — food (metabolized)
  and bed rest are the ONLY energy sources; egg reagent only arrives from eating/swallowing;
  and a TWO-SLOT station (play/breeding) does NOTHING with one body in it — no stimulation,
  no egg, no energy — so enter one alone only on purpose (waiting to be topped) and exit
  when the partner isn't coming instead of camping. Same-day fix for the eaten-player
  session where the eater hopped between a play station and the nest on faith alone.
- **Grow-mode gene absorption — curves and groin (`SwallowGrowSex`, on with the other Grow toggles).**
  Absorbing someone in `SwallowEffect=Grow` now also takes their secondary sex genes, not
  just size/strength: the eater's `breastSize` += `SwallowGrowPercent`% of the meal's; a dick
  the eater already has grows by the same % (length + thickness together); and a victim's dick
  on a dickless eater is GAINED whole — their equip id plus their dick/ball sizes copied
  exactly (10% of a dick is invisible, so a gained groin transfers without sampling).
  Everything flows through the game's own `SetGenes`, so new curves or a newly grown dick
  render on the body the moment digestion completes — on AI kobolds and on the player's own
  body (self-eat) alike. The pure decision table lives in `SwallowLogic.GrowSexGenes` (tested
  in the `swallow` suite); every narration channel was made honest about it: the eaten one's
  swallow promise now lists what is actually being taken (the three toggles), the eater's
  completion world event names its own body changes (the generic fanout deliberately skips
  that instance), and the digest-awareness `belly` anchor line says "size, curves, groin".
- **The [limit] STALL — the real "won't navigate" bug, diagnosed LIVE through the new
  web UI.** Watching an actual spectating agent's terminal showed the whole failure loop:
  the turn's command budget (12/cycle, shared across reply rounds) got eaten by repeated
  `echo`+`sonar`+`ls`, then the model's `sleep` was refused by the same budget while the
  message it just received said "sleep to continue later" — a self-contradiction the weak
  model fell into FOREVER (turn after turn of "[limit] … sleep": zero `cd`s, zero
  walks, goal never cleared). Four fixes: (1) `sleep` is now ALWAYS honored at the limit
  (it's the turn terminator the prompts tell the model to send); (2) a suppressed
  echo/emote returns its budget slot (repeating speech no longer burns actions);
  (3) once the limit hits, the turn ENDS that round instead of asking the model again
  inside the same exhausted turn (the old behavior printed "[limit]" over and over);
  (4) `FmtResult` renders arrays/lists as joined text — the mysterious `System.Object[]`
  the model received for some command output can no longer reach it in that form.
  Also visible in that live session and NOT yet fixed: the spectate-session goal was
  never `goal done`-ed after the swallow (the agent literally cannot act until this
  build) — expect the new build to unstick it. A second live-session failure
  shape — `cat facts` returning "error: Method has zero rva" (a mono invoke error with no
  code-level cause in the CatFacts path — it deterministically blocked the agent from
  reading `player_wants`) — is now INSTRUMENTED: the console's catch logs the exception
  TYPE + STACK to the BepInEx log (tail visible in /api/state), so the next occurrence
  names its real culprit.
- **DLL hot-reload NEVER fired on this setup — fixed with a write-time poll.** The game
  install is an SMB network drive (`S:\`), and `FileSystemWatcher` does not report
  changes on shares: `build.sh` deploys silently never hot-reloaded. Update now polls the
  plugin file's LastWriteTimeUtc (>1s delta → same queued-restart path as the watcher);
  deploys hot-reload from the NEXT game launch onward. (The current session predates this
  build and still needs one manual restart to pick up all of the above.)
- **Think-only STALL auto-retry (round 4 — the 'stuck standing in thought with no action').**
  The playtest's dominant failure: glm-4.6v-flash streamed reasoning_content ("Wait"/"Okay")
  with EMPTY content, burned its whole token budget on hidden CoT, and the turn did
  nothing — repeatedly. Now `PostChatPayload` detects the shape (200-OK + reasoning_content
  + empty content) and re-posts ONCE with the token budget QUADRUPLED (cap 16384) in the
  same tick (act + console paths share the POST code); the next-turn hint remains as the
  second line of defense.
- **Fact hygiene round 2 — the junk classifier went full-spectrum (stale goals, stale
  object states, stale landmark orientation data, and still-queryable body states were
  filling the fact ring).** `TextUtil.JunkFactReason` (pure, suite `textutil` → 63 asserts)
  classifies now: `goalish_fact` (goal:…/my goal/… — the goal SLOT owes the plan;
  goal-facts are why cleared goals lingered), `self_state_fact` (i am in/near/at, i am
  hungry/tired/horny, i have 3 eggs — ALL live-queryable via ls/status/sonar/cat needs/
  feel), `stale_state_fact` (the door is open / the can was empty — transient; read live),
  `stale_relative_fact` (round 1). The remember tool refuses per-class with a note naming
  the live surface; `RememberFact` drops stragglers from every writer (ask answers,
  chat-derived). Persona/lessons/durable places/past-tense events all still pass.
- **Egg delivery narrated as the JOB it is (an egg failed to travel from the station to
  the plot).** The `[lay]` completion event no longer says 'a real egg item now' — it
  hands over the concrete flow: the egg sits at your feet → get/grab → cd a farm plot
  (`cat places` lists plots map-wide w/ ids) → set on dirt facing it → `use id:N` / `plant`,
  never sell/eat/leave it, and a swallowed-PLAYER egg = deliver promptly.
- **WebUI — the debug console is now a browser page.** `src/WebUI.cs`: loopback HTTP
  (`[Web] Enabled` default ON, `[Web] Port` 8338 — 127.0.0.1 ONLY, read-only):
  `/` = a single-file dark UI (vanilla JS, 2s polling; one tab per instance: goal/
  facts/history/terminal/thread state; boxes for spectate status, mod-log tail,
  AI-reports tail), `/api/state` = the JSON the page polls, `/api/reports` = the report
  log tail. Everything the handler thread reads is lock-guarded strings/volatiles — NO
  Unity object reads — so it answers even while the main thread is WEDGED (the freeze
  forensics case). The dev assistant can fetch it too: `curl http://127.0.0.1:8338/api/
  state` mid-session. The F6 IMGUI overlay stays as the in-game glance.
  - *Fix (same day, before first successful boot):* the original `WebUI.Ensure` call sat
    in Awake BEFORE its `[Web]` config binds — null entry, silent bail, server never
    started. The call moved into Update (after binds): idempotent, config-guaranteed, and
    it now also stops/re-binds within a frame when `[Web] Enabled/Port` changes at runtime.
- **Key places: the map's survival machines known from boot (playtest round 3).** User:
  the agent can't find the egg-laying station/bed/farm patches/water because they're far
  from spawn and a small model "can't navigate the scene well enough" on its own. Three
  root fixes:
  - *The resolver's range wall was the real blocker for travel:* `FindPlaceByName`
    (`cd nest` / `go_to name`) searched a **60m OverlapSphere** — beyond it,
    "unknown_place". It now resolves names SCENE-WIDE against the existing `StationList`
    (every GenericUsable) **plus** `SoilTile` farm plots and `WateringCanWeapon` cans
    (which were never searchable at all), with kind aliases (`cd farm`/`soil`/`plot`,
    `cd water`/`watering`/`can`) so a kind word finds its machine however far. The old
    collider-scratch buffer is gone (it also truncated silently in busy scenes).
  - *New `places` directory (perception + `cat places` + BOOT LINE):* every nest (egg
    laying), bed (rest), farm plot (planting) and watering can (water source) on the map
    with addr (Kind#SW22 style), distance, world bearing, landmark hints for stations,
    live slot text, a stable `id`, and `:busy` — sorted nearest-first, 5s-cached shared
    scan (`FindObjectsOfType<SoilTile/WateringCanWeapon>` sits alongside the existing
    GenericUsable cache; both are map-wide). The console BOOT LINE now carries
    `key places: ...` so the knowledge exists the moment the body does — the "from the
    beginning" ask — and `cat places` reprints it on demand.
  - *Dead-end notes now navigate:* `water` with no can in reach reports the nearest can
    MAP-WIDE (addr + distance + bearing, "cd water, then try again near it") instead of
    "no watering can nearby"; `unknown_place` says to check `cat places`/`ls`. Prompts
    teach it in all four sites (act TOOLS/perception/NAVIGATION × built-in + default.txt;
    console persona + console.txt NAVIGATION rewritten: `too_far` is answered by
    `cat places`, not by giving up).
- **Spectate: the agent can EAT THE PLAYER — and the player is forced to watch.**
  New opt-in pair `[LittleTreats] SwallowAgentEat` (default OFF) + `SwallowAgentEatRange`
  (default 2.5m). The feature inverts the swallow-vore direction:
  - *Playtest round 2 fix — THE CAMERA HOOK WAS DEAD: KoboldKare renders under URP, and
    `Camera.onPreCull` is not invoked under the Scriptable Render Pipeline — both playtests
    left the taken-over view frozen at a degenerate configless OrbitCamera pose (the user's
    "spectate still sends me to the far corner of the map"). The writer now hangs on
    `RenderPipelineManager.beginCameraRendering` (per-camera pre-cull under URP; verified by
    reflection probe: CoreModule has ScriptableRenderContext + begin/endCameraRendering
    `Action<ctx, Camera>`), with `onPreCull` kept as built-in-pipeline insurance, and a
    one-shot "spectate: camera override live" log line so the hook's health is visible on
    the very first spectate.*
  - *Fact hygiene — ONLY USEFUL FACTS (the facts machine filled with 'object to
    left'-style junk that goes stale the moment anything moves). Three layers:* (1) the
    mod's own landmark auto-remember (`"kind is front-right here"` on every spotting,
    Senses.cs) is REMOVED — it was the junk factory, and its text had no category dedupe
    so it churned the 24-slot fact ring; (2) the `remember` tool (act `remember(mem=)`,
    console `remember <fact>`) now REFUSES viewer-relative facts — `TextUtil.IsViewer-
    RelativeFact` (pure, suite `textutil` +22 asserts) flags "(to/on/at/from)(my/the)?(left|
    right)", "left/right/behind/ahead of me", "next to me", "my left side", bare "ahead/
    behind" and "is/are here" — while name-anchored facts pass ("in front of the house",
    "left of the fireplace", "bed is upstairs", lessons, past-tense events); the refusal
    note teaches the restatement ("facts are STICKY knowledge — name the thing and its
    durable place"); (3) `NPCInstance.RememberFact` silently drops any stragglers from the
    other writers (ask answers, chat-derived text) with a debug log. Prompts teach it too
    (act TOOLS remember description in Main.cs + system_prompt_default.txt; console
    remember Help line + console.txt WORLD paragraph).
  - *The act.* Every mod-driven NPC gains the **`eat_player`** module tool (console mode:
    `eat_player`; act mode: a JSON tool in the schema). When the local player is within
    range and the eater's mouth is empty, they swallow the player WHOLE through the normal
    swallow pipeline — pull-in (~2.2s), hidden once fully swallowed, digest
    (`SwallowDigestSeconds`), settle — exactly like a kobold victim. The tool result, the
    eater's first-person event, the world fanout, and the shared `belly`/console anchors
    are all player-aware ("you just swallowed the PLAYER WHOLE — their camera is gone,
    they WATCH everything through your body's eyes…"). The `swallow abort` chat commands
    now honor the feature even when `SwallowEnabled` is off, and also release a **parked**
    player (below).
  - *Camera takeover.* From the swallow's first frame the player's camera is taken over
    and never released until the cycle ends: `SpectateManager` writes the follow pose on
    **`Camera.onPreCull`** — last-writer before render, so OrbitCamera configurations,
    camera hotkeys, mouse look and pause can never win a frame back, and restoring costs
    nothing (the game's own config stack is never touched). First frame snaps; after that
    it settles smoothly. NEW default after the first playtest: **FirstPerson — through the
    eater's own eyes** (`SwallowAgentEatPOV`): position = the eater's head bone at the AI-vision eye offset
    (`[Senses] CameraForward`, TransformPoint so grown eaters scale cleanly), rotation =
    the head's, nearClip swapped to `[Senses] CameraNearClip` and restored on release —
    you watch literally what the AI watches. ThirdPerson keeps the size-scaled orbit and
    gained a wall-safe boom (linecast head→wanted-spot pulls the camera in when
    furniture/walls sit between you — the playtest's eater sat in a cramped bathtub).
  - *The parked body.* The player body is NEVER destroyed (that hits the game's death
    machinery and breaks `TagObject`): after consume it parks inside the eater — pinned,
    hidden, colliders off — and FOLLOWS the eater's belly position each tick, so sonar/
    `cat player`/trail-watching stay honest while they watch. Input is suppressed
    (`SetMovementEnabled(false)` + reflection-clearing the grab/rotate/hip/switch flags
    every tick); game chat, ESC menus and the abort command still work. An
    **always-on HUD** (independent of the F6 overlay) tells the spectator who they're
    inside, what they're waiting for, and the escape command.
  - *Hatch (= rebirth).* Egg mode: the eater's lay pops the egg exactly as before and the
    rebirth registry is marked `IsPlayer`; when the plant chain spawns that body,
    `SpectateManager.HatchRevive` copies the hatched genes onto the parked player body
    (`SetGenes`), revives it at the hatch spot, schedules the duplicate NPC body for
    despawn (deferred ~0.5s — destroying mid-`OnPhotonInstantiate` is unsafe), and hands
    the camera + movement back. BellyEgg mode rides the `OnOviposition` relay so the
    nest-laid egg's link keeps `IsPlayer`. Grow/Destroy park without a hatch pending —
    the HUD says so and the only exits are abort or leaving the world.
  - *Lifecycle safety.* Eater body destroyed mid-watch → the player is released at the
    eater's last known position; scene leave (`LeavingWorld`) and DLL-restart
    (`RestartPlugin`) release spectate; Photon-refused despawns retry up to 20×.
  - *Pure core + tests.* `src/SpectateLogic.cs` (gate with machine-readable reasons,
    azimuth math, follow distance/pose, smoothing, HUD lines) is Unity-free — new suite
    `spectate` (48 asserts; run_tests.sh + tests/README updated). `check_build.sh` was
    also missing the `com.naelstrof.orbitcam`/`com.naelstrof.inflatable`/`NetStack`
    references that `build.sh` already had (drifted during the swallow work) — fixed so
    the semantic check compiles clean again: 45 files, 0 errors, 0 warnings.
  - *Prompts in sync* (Main.cs built-ins + system_prompt_default.txt + Console.cs
    built-in + system_prompt_console.txt): act TOOLS list gained `eat_player`, both modes
    gained an EATING THE PLAYER paragraph — the eater is told the player is a person in
    their care, to keep living their ordinary life (that IS the show) and to talk to them.
    Playtest gap closed: GLM small-tier misread "try to eat me" as an invitation to play
    (bath stations, "you mount me") — all four prompt sites now say explicitly that a
    player ASKING to be eaten means `eat_player`, get close and run it, NOT an invitation
    to play.
- **Swallow: the EATER is aware now — and stations narrate WHO left.** Two awareness
  gaps closed.
  - *Eater awareness (swallow flow).* `SwallowState` now carries the EATER's own mind
    (`EaterNpc`): a swallow BEGIN delivers a direct first-person line to a driven eater
    ("you just swallowed X WHOLE — they are inside you now…") — self-swallows previously
    reached the eater only as a third-person fanout that read like watching yourself from
    outside; digest milestones mirror to them ("your stomach churns around X — they are in
    there, alive… ~Ns left"); every outcome lands first-person (Egg: "you laid an EGG —
    plant it in soil…"; BellyEgg: "egg reagent + genes inside YOUR belly — sit on a
    nest"; Grow: the existing `[absorb]` line; Destroy: "nothing left of them"), the
    mid-swallow ABORT is reported from the eater's side too (placement corrected to
    "at the eater's feet" everywhere — narrator now uses `BodyLabel(st.Eater)` instead of
    assuming the player), and a live `you have someone INSIDE YOU: …` line rides the
    state anchors every turn (console anchor line next to the sex line; act perception
    `belly` key) via `SwallowManager.BellyGuestText`. `FanoutWorldEventExcept` is new:
    the generic "you watch X…" spectator copies now skip the eater/victim minds that get
    dedicated first-person lines.
  - *[station] departure events.* `StationLeaveWatch` (Main.Update heartbeat, ~1/s, main
    thread) snapshots the occupants of every `IAnimationStationSet` within the 14m
    perception radius; a snapshot diff narrates the transition ("[station] the player
    left the bed station — you are on it alone now" / "… near you"). Joins are
    deliberately not evented (the per-turn slots text already shows them the same turn);
    the NPC's own exits aren't narrated to themselves.
- **`[LLM] Thinking=off` — disable the hidden reasoning pass so the NPC acts faster.**
  Reasoning families burn their entire token floor on invisible chain-of-thought before
  every visible answer (the struggled-trio note in docs/MODELS.md: thinking families take
  "slow/lost turns"). The new config sends server-native suppression fields into EVERY
  chat payload — `think=false` (Ollama), `chat_template_kwargs {enable_thinking:false}`
  (llama.cpp / vLLM / SGLang, Qwen3-style templates), `reasoning_budget=0` (llama.cpp
  hybrid models) — wired into all six call sites (act loop, console REPL, comment invite,
  self-ask, name selection, vision caption). Fields a server doesn't recognize are
  skipped server-side, so the shapes coexist; LM Studio / KoboldCpp users get the same
  effect from their per-model GUI. `auto` (default) never touches the payload. The
  family token-headroom floors and the client-side think-strip stay in place, so an
  ignored knob degrades to exactly today's behavior; leftover reasoning markers are
  still stripped silently. Fields table is the pure `ModelFamilies.ThinkingOffPayloadPart`
  (unit-tested) — kept in sync with this entry.
- **Per-mode chat-template files.** Each LLM call mode ships and reads an editable
  chat-template file next to the DLL. The act loop (`[LLM] SystemPromptFile` →
  `system_prompt_default.txt`) and console REPL (`[Console] SystemPromptFile` →
  `system_prompt_console.txt`) already did; the remaining two call modes join them:
  the VISION CAPTION pass (`[Vision] PromptFile` → `system_prompt_vision.txt`,
  placeholder `{stereo}` expands live from `[Vision] Enabled`/`StereoIPD` — an
  explicitly customized `[Vision] Prompt` still wins) and the one-shot NAME prompt
  (`[LLM] NamePromptFile` → `system_prompt_name.txt`, placeholders
  `{gender}`/`{species}`/`{traits}`/`{taken}`). build.sh ships both new files and the
  code built-ins must stay byte-equal with them (the standing prompt-sync-trap rule,
  noted at both built-ins). Expansion is `TextUtil.ExpandTemplate` (pure, tested) —
  unknown `{tokens}` pass through untouched so a hand-edited template degrades
  gracefully instead of corrupting the prompt.
- **Player swallow-vore — in the new opt-in [LittleTreats] config category (ALL OFF by
  default).** A Harmony prefix on `Grabber.TryDrop` turns the drop gesture into a
  swallow when enabled, the view camera points up (forward.y ≥ 0.55) and a PERSON is in
  hand: the drop is canceled, the victim is ownership-taken, any station animation is
  canceled, and it is released on the game's throw path (limp ragdoll — no
  dropped-on-a-station auto-use). Then a session pins it: ~2.2s pull-in to the mouth
  while shrinking (1 → 0.3), it sits at the belly for `SwallowDigestSeconds` (default
  45, clamped 5–600) with narration milestones ("the stomach walls churn around
  you…"), then ~6s compacting to nothing — and `SwallowEffect` decides what comes of
  it: **`Egg`** (default) — an `Egg` room object lands at your feet carrying
  `KoboldGenes.Mix(player genes, victim genes)` (the game's own oviposition
  recombination), ScrambledEgg-filled like vanilla eggs so it can also be eaten;
  **`BellyEgg`** — instead the victim's `Egg` reagent (20 units, nests need ≥5) plus
  THEIR genes pour into YOUR `bellyContainer` — sit on a nest and lay them yourself
  (vanilla `OvipositionSpot` flow): a `OvipositionSpot.oviposition` relay matches the
  lay against the belly genes we poured in and registers the LAID egg's seed genes for
  the rebirth link (the lay-time Mix rolls randoms and can't be predicted); **`Grow`**
  — no egg; `SwallowGrowLarger`/`SwallowGrowStrength` pick what you absorb (baseSize
  and grabCount/maxEnergy, each +`SwallowGrowPercent`% of the meal — grab is flat +1,
  cap 8; applied live via `Kobold.SetGenes` `With()` mutation) and nothing comes back
  out; **`Destroy`** — nothing gained, nothing laid, they're just gone. A
  claim-managed victim's LLM session STAYS ALIVE through all of it: steering is gated
  (`NPCInstance.Swallowed`) but speech/console keeps running — it experiences the
  digestion — and the onboarding narration is HONEST per mode (Grow/Destroy are told
  there's no coming back). Other NPCs get "you watched the player swallow NAME WHOLE"
  fanout events. On completion the body is Photon-destroyed; the eaten NPC's
  persona/facts/name are snapshotted to MemoryStore BEFORE the destroy
  (`SaveBodyMemory` skips destroyed bodies — the sold-kobold teardown has the same
  gap). Plant an Egg-or-BellyEgg egg in soil (vanilla Seed → Plant → KoboldSpawnPlant
  chain): when the reborn body hatches, `Kobold.spawned` matches its genes against a
  fingerprint registry — every gene float round-tripped through the same HalfPrecision
  quantize→dequantize the `AddKoboldGenes` serialization applies, so the plant chain's
  quantization can't break the match — and copies the eaten body's saved life onto the
  new body's key. The claim scan then wakes the SAME person in the new body: same
  name, persona, facts and pronouns, in control again, genes changed. Config keys
  (all `[LittleTreats]`, default OFF): `SwallowEnabled`, `SwallowDigestSeconds`,
  `SwallowEffect` (Egg|BellyEgg|Grow|Destroy), `SwallowGrowLarger`,
  `SwallowGrowStrength`, `SwallowGrowPercent`. Held PLAYER bodies can never be
  swallowed (grief guard); anything else in hand still drops normally even when a
  swallow starts. New build references: `com.naelstrof.orbitcam.dll` (camera look-up
  check) and `NetStack.dll` (BitBuffer/HalfPrecision). Tests: new pure `SwallowLogic`
  suite (trigger rule, phase/scale math, digest milestones, fingerprint format).

  Playtest round 2: the trigger angle is now `SwallowLookUpDot` (default 0.94 ≈ 70°+,
  deliberate near-max-up — it was 0.55 ≈ 33° and a raised face ate people on a normal
  drop); the swallowed body's renderers shut off the moment the pull-in completes —
  the shrunk rig doesn't scale cleanly with the root and kept visibly sticking out of
  the stomach (a consumed NPC's speech still works through the chat channel); and the
  no-rebirth modes (Grow/Destroy) erase the eaten one's saved AI life via the new
  `MemoryStore.Delete(bodyKey)` so the persona records can't undercut the effect.

  Playtest round 3: a no-cheat digest ABORT — the player types "swallow abort"
  (aliases "abort swallow", "swallow stop", "stop swallowing" — letters-only
  normalized, "cough up", "spit out" too) into the game CHAT: every in-flight session
  releases its victims (`ReleaseSwallowed` un-does the whole pin — scale/colliders/
  renderers/kinematics/controller restored, ragdoll popped, each person set down in
  front of the eater) and `NPCInstance.Swallowed` lifts so the LLM session resumes
  with a release event + a fresh persisted snapshot. The interception sits in
  plugin `OnEvent` right before NPC chat distribution: only the LOCAL player's lines
  are honored, matched lines are consumed (the NPCs never hear the escape hatch),
  and with the feature disabled chat is never hijacked; empty-stomach typing gets a
  "your stomach is empty" bubble. The matcher lives in the pure `SwallowLogic`
  (`NormalizeLetters`/`MatchesAny`/`AbortCommands`) — same suite.

  Playtest round 4 — FORCE-FEEDING (`[LittleTreats] SwallowForceFeeding`, default on,
  master-gated by `SwallowEnabled`): the drop gesture generalizes beyond the player's
  self-eat. `TryDropPrefix` now runs two gestures in precedence order — (1) the
  unchanged player steep-look-up self-swallow; (2) ANY kobold's drop (the mod's NPC
  `drop` tool and the player alike) landing a held PERSON right on/at another kobold's
  HEAD (`IsOnOrAboveHead` within 0.85m of the head bone; receiver must be alive, not a
  player body (grief guard), not held, not already digesting someone) makes the
  RECEIVER swallow them. `Begin` carries the ACTOR now, so every narration names who
  did what: the fanout says "you watch X shove NAME into Y's face — Y swallowed them
  whole!", the eater's LLM gets a direct force-fed briefing (a NAME in THEIR stomach),
  the dropped one's swallow promise names their feeder, and the mod's `drop` tool
  result gained a `force_fed` field (captured inside the main-thread drop marshal via
  `SwallowsBegunBy`) so the dropper's model never believes a plain "dropped" when the
  shove became a swallow. All completion narrations (egg at feet / belly pour / grow /
  destroy / abort) name the ACTUAL eater via `BodyLabel`, and the egg-rebirth
  registry/fact now carry the eater's label (`RebirthLink`) since the stomach owner
  isn't always the player — the reborn person's fact no longer claims "the player"
  swallowed them.

  Playtest round 5 — laying-misread + hip rhythm: (1) EGGS ARE PENETRATORS — the game
  pushes a laid egg out through the laying path (OvipositionSpot's spline push), and
  loose egg props can press against a body, so `OnPenetrationTick` fired sex events
  ("BEING PENETRATED by Egg(Clone)") and the model thought it was having sex with an
  egg. Egg-sourced penetrators (a `Seed` in the chain — eggs are the only
  Penetrator+Seed objects) are now routed to `HandleEggPassing`: never sex state
  (excluded from `_pen`, so the [sex] anchor, rhythm graph, `feel` and stimulation
  sourcing stay clean) and narrated as `[lay]` events instead — entry ("an egg is
  pressed into / emerging from your <hole>… NOT a partner, NOT sex… no thrusting, no
  panic"), 5s-rate-limited mid-pass, and a completion ("cleared you completely —
  plant it in soil"). State resets on body swap/teardown. Prompt teaching added in
  BOTH locations (Main.cs built-in + system_prompt_console.txt): [lay] = laying, not
  sex. (2) THE THRUST was a ~10 Hz sine — faster than the animator's own 0.05s hip
  `SmoothDamp`, which low-passed it into a tiny shiver: literally "too fast and not
  very deep" in one bug. Strokes/s now follows intensity (1.2 + 1.8×I ≈ 2.3 at the
  default 0.6, ~3.0 at max) and the amplitude has a felt floor (+30%) — the SmoothDamp
  fully tracks it, so the hips visibly stroke deep and slow. The `feel` "what moves
  it" line, the tool schema + result (`strokes_per_second`) and the prompts teach the
  new pace. Lesson for future hip work: keep any SetHipVector modulation under ~3 Hz
  or the animator's SmoothDamp eats it.

  Playtest round 6 — THE LOCKUP + blind forensics (2026-10-09 log): a session froze
  with 9 identical "LLM loop: System.TimeoutException: main thread timeout (5000ms)"
  lines and NOTHING from the hang watcher. The freeze began right after a successful
  chat turn whose LLM server hiccuped ("LLM raw response (empty content)" with
  reasoning chunks streaming, then `HTTP 400 — {"error":"Engine protocol predict
  request failed: fetch failed"}`) — LM Studio-side trouble in the same window; the
  wedge cause on the main thread UNNAMED because **THE HANG WATCHER WAS BLIND**: it
  compared two Time.unscaledTime reads, and unscaledTime only advances on the main
  thread, so when the game truly hung the watchdog's own readings froze with it and
  the gap never grew. Fixes: (1) main-thread heartbeats now stamp both game-time and
  WALL CLOCK (`MainClock`/`LastMainTickWallTick` via one `Beat()` helper) — the
  background watchdog compares wall clock, so a real hang reports the stuck PHASE
  every second; (2) `RunOnMainThread` timeouts throw with the diagnosis inline
  ("main thread timeout (5000ms) — main thread last beat 47.0s ago, in phase
  'Swallow.Tick'") so even the LLM loop's spam lines name the wedged section;
  (3) the LLM loop backs off in 10s cycles (+ one "stuck, waiting it out" line) after
  3 consecutive marshal timeouts instead of hammering every 2s; the counter un-arms
  on the first successful marshal; (4) the ladder classifies "predict request" /
  "fetch failed" as transient_engine (retry-unchanged) — the old matcher only knew
  "predict stream", so that engine-only failure was mis-filed as "context" and a
  perfectly good payload got shrunken for nothing. The next wedge names itself.

  Playtest round 7 — Grow mode ABSORBS memories: `SwallowEffect=Grow`
  no longer deletes the eaten one's saved life — it POOURS INTO THE EATER: facts
  (their knowledge — places, wants, dangers) transfer to whoever consumed them, the
  eater's own name/persona/pronouns never touched (absorbing is not becoming). A
  live LLM eater (force-fed NPC) gets the memories injected into their session this
  turn ([absorb] event + plain facts — RememberFact dedupes one-slot-per
  ':'-prefix category, so shared prefixes would collapse the set; one 'absorbed:'
  marker fact summarizes it), persisted by their own teardown. An unclaimed eater
  gets the merged record written under THEIR body key — a vanilla AI kobold later
  claimed by the mod wakes up remembering the person they ate. Destroy mode keeps
  the deletion; egg modes keep the records for rebirth. Narration updated (the
  eaten one is told their memories live on in the eater; fanouts say it too).
  DISCOVERED while wiring this — pre-existing key-collision risk: `BodyKey` =
  CleanName(object name), and freshly-spawned Photon kobolds are ALL "Kobold(Clone)"
  → they all share the "Kobold" memory key (observed: a `Kobold.json` + two "Soot"
  saves under different keys on disk); the live-name restoration guard
  masks double-claims, but plain-prefab bodies overwrite each OTHER's saved lives.
  Affects plain-Kobold spawns' memory saves (incl. this feature's) — needs a
  stable per-body key (game save identity) as a follow-up.
- **North-up sonar.** The ASCII radar was
  world-anchored in code but documented to the model as *facing-relative* ("row 0 = furthest
  forward") and only ever drew the camera-FOV cone, so every turn re-scrambled what the map
  showed and the model misread it. It is now a true sonar: a fixed 16-ray **compass sweep**
  (absolute world bearings) plus exact `nearby` positions, plotted on a grid whose top edge
  is ALWAYS north (+Z) and right edge east (+X) — it never rotates with facing; facing is
  the arrow beside `@` and a new `facing` field ("ENE (78°)" — `pwd` shows it too). The map
  carries its own legend header (the model no longer needs the buried prompt LEGEND), and
  window/glass geometry is classified as a new `V` (kind `v`) so "by the window" locates
  windows. Perception mode gets a `radar(filter=…)` act tool; console mode gets a `sonar
  [filter]` command (aliases `radar`, `radar_map`, `minimap`) — glyphs W/S/V/U/K/P can be
  filtered and the filter persists (applies to the perception sonar too until `all`).
- **Station IDs the AI can actually disambiguate.** Ornith's request: list stations by
  cardinal direction + distance. Each `stations` entry (and `cat stations`) now has an addr
  like `Blender#SW4` (kind + WORLD compass + meters — the ID Ornith wanted), a numeric `id`
  target, a world-fixed `bearing` next to the old facing-relative `dir`, landmark clues
  (`near window, mailbox` — scanned in a 4.5m radius around each station, memoized 15s),
  and per-kind `closest_you`/`closest_player` flags (with `player_d`) so "which one shall we
  use with me?" resolves to the station nearest the player. The model is taught to answer
  "the one by the window?" questions by matching addr + hints.
- **AI bug-report channel.** New `report` console command / `report_issue` act tool
  (aliases `report_bug`, `bug_report`, `complain`, `complaint`) — when tools keep failing or
  output contradicts reality, the model files one line for the human maintainers. With the
  new `[Debug] AIReportLog` option (default ON) it appends a block — timestamp, NPC, body,
  position, facing, goal, in-station state, the message, and the NPC's last ~10 tool calls
  (new ring buffer recorded from both the act loop and console executions) — to
  `BepInEx/config/kkllmnpc_ai_reports.log`. Reports always also surface as `[AIReport]`
  BepInEx console-log lines; with the option off the tool says so and suggests telling the
  player. Prose words (`problem`/`issue`/`bug`) are deliberately NOT aliases so thinking out
  loud can't file a report by accident.
- **ZDTaichu support + a vision-temperature knob + the living model list.** The
  playtest's `zdtaichu5.0-9b` was
  running as "generic"; its HuggingFace card (TaichuAI/ZDTaichu5.0-9B-GGUF) identifies it
  as a Qwen3.5-9B backbone + C-RADIOv4-H vision encoder with a dedicated
  spatial-reasoning/agentic training mix, Qwen-style think blocks, mmproj-vision, and a
  card recommendation of temp 0 + top_p 0.95 + top_k 20 for spatial/grounding work.
  `ModelFamilies` now classifies any "taichu" id as a qwen-class reasoner family
  (`taichu`: think-stripping, 512 token headroom, vision-capable, tier by size), and the
  spatial caption pass's hardcoded temperature became a `[VisionModel] Temperature` config
  (default 0.2 unchanged; set 0 to match the card). ZDTaichu joins the good list in the
  docs (Qwen-family models are consistently strong here — vision, spatial reasoning,
  console discipline). New **`docs/MODELS.md`** = the long-term model compatibility list:
  ranked per-family field notes/quirks/tiers now, plus the benchmark plan (reproducible
  obstacle-course checklist, metrics mined from the tool/report logs) so ranking can go
  from observational to measured over future playtests. Tests cover the classification.
- **`Compass.cs`** — pure-C# world-fixed compass helpers (north = +Z, east = +X) shared by
  the sonar, station bearings, facing text and the arrow glyphs, with a new `compass` unit
  suite (9 suites total now).
- **Watched, not memorized: the player's current activity.** The game has no quest list to read, so the AI now reads the
  player the way other players do — by looking. Every 0.4s (main thread, plugin tick) the
  host player is snapshotted: position, heading, speed, station (via the same
  `CharacterControllerAnimator.IsAnimating()` the NPC body uses), ragdolled state, and what
  their own Grabber holds. Perception carries `player_activity` (activity wording —
  "cooking/feeding the blender", "resting on a bed", "walking ESE at 2.1 m/s", holding
  list — plus distance/bearing relative to the NPC); console mode gets `cat player`.
  Nothing is stored: it's live observation, refreshed every moment. The `player_wants:`
  fact from the previous wave remains as the durable copy of *requests*.
- **Destiny-style route learning ("a player walked it, therefore it's a path").** The
  shared walkability grid was baked once, so a room behind a door closed at bake time (or
  geometry placed later) stayed "wall" forever. New `WorldMap.PatchWalkable`: any watched
  body standing/walking on a wall-verdict cell downgrades it to walkable — the plugin Watch
  polls the host player, and NPC bodies patch from their own movement loop — and patches
  persist in the cached `.kkmap` (the build and grid share the arrays).
  Plus `PlayerTrail`: the host player's actual walked path is sampled every 0.4s (points +
  jump flags, decimated, ~4000-point cap, persisted per scene to
  `BepInEx/config/kkllmnpc_maps/trail_<scene>.txt`; loaded fresh each session). When
  `go_to`/follow can't route (too_far, gaps, parkour, unreachable-by-A* rooms), the NPC
  copies the player's demonstrated route — including replaying flagged jumps as hop
  bursts — and go_to's "too_far" refusal now tries the trail before refusing. Route legs
  (>9m movement, door crossings, hops) fan out to every instance's world-event queue as
  ROUTE WATCH lines, so NPCs also understand HOW the player left a physical room.
  Prompts in both modes document the new capacity.
- **The objective scroll is real — QuestSense (correction of an earlier wrong claim).**
  The Wave-6 research mislabeled the game's quest system as nonexistent; a fuller pass
  found `Scripts/Objectives/`: `ObjectiveManager` (chain of `DragonMailObjective`
  letters; `GetCurrentObjective()`/`GetStars()`/`HasMail()`), the top-right paper scroll
  (`ObjectiveUIDisplay` — titles embed live progress like "Create 4 food 2/4"), and
  delivery through the **mailbox** (`MailboxUsable.Use → ObjectiveManager.GetMail()`).
  New `QuestSense` polls the manager 1/s, fans out change events to every instance
  ("A NEW OBJECTIVE LETTER is live ...", "objective progress: ...", "the scroll
  OBJECTIVE was COMPLETED (stars: N)", "MAIL is waiting ... YOU can fetch it for your
  player"), and exposes: perception `quest` {title, text, stars, mail_waiting}, console
  `cat quest` (aliases `cat objective`/`cat mail`), and a `quest:` line in status. The
  **mailbox is now a named kind** (`ClassifyUsable` → `mailbox`, purpose "the objective
  LETTERBOX — safe; NOT the sell machine") so prompts and hints distinguish the letter
  box from the `MailMachine` sell danger; the AI can `use mailbox` / `interact` to GET
  mail for its player. Prompts in all three locations describe both objective layers
  (scroll letters + player_wants + player_activity watching).
- **Hands the AI can finally see (an AI couldn't let go of a player).**
  Research into the game source settled the mechanics: `Grabber` holds grabs on a physics
  spring anchored ~1m in front of the view (DriverConstraint + a hip joint for kobold
  victims), grab takes whatever's in a ~1m bubble in front of the face, `TryActivate` is
  the throw (adds ~10 m/s in view direction and releases — weapons fire/spray instead),
  and `Release()` early-returns on a stale grab. New `HoldSense.cs`
  reads the private `grabbedObjects` list via reflection and gives the model: perception
  `holding` entries (name, kind, distance, person warning), world events on pickup/release
  ("you grabbed a PERSON — let them go"), `holding:` in status, and `hold`/`cat hold`.
  Tools reworked: `grab` now reports exactly what it caught (warning when it's a person),
  `drop` verifies the release and belt-and-braces `OnReleaseRPC` onto any person still
  joint-locked (the "can't let go" fix), and a new `throw` tool (console `throw`, aliases
  toss/hurl/yeet/lob/activate; act tool in the schema enum) does the real throw/activate —
  hurls items in the view direction, sprays the bucket, fires tools.
- **Player objectives persist now.** The latest player request auto-saves as a
  `player_wants:` fact the moment it's said (survives the 30s chat window and compaction),
  so the model can treat it as the player's live ask, offer help, take it on as its
  own goal, and forget the fact once handled.
- **Small-things physics are taught, from the game source.** Both prompt files (and the
  console persona/protocol) now explain with mechanics-grounded specifics: held things
  float ~1m ahead on a spring and lag turns/bump what you pass; fruit blends when it
  enters a blender's intake (carry it close — the machine pulls it from your grip); seeds
  plant at ≤~1m from bare soil + `use` (hold over the dirt, or drop it there first); the
  water bucket sprays ~10ml blobs on throw while its contents never run out; eggs laid via
  nests carry like seeds and plant in soil, growing fresh EMPTY kobold bodies (alive,
  nobody's home — candidate avatars; the game frames grown eggs as possibly your children)
  with a tenderly-not-sellables rule.

### Fixed
- **Name selection survives unmarked chain-of-thought replies.** Some templates print
  the whole reasoning pass as PLAIN text ("Here's a thinking process: 1. …") — no
  reasoning markers for the stripper to see — and with the old fixed 320-token budget
  every reply died mid-monologue, so a kobold burned all three name attempts and fell
  back to the mechanical name every time (observed 2026-10 across three consecutive
  logs). Three layers now: `ChooseNameWithLLM` reads `finish_reason` and retries with a
  bigger budget per attempt (320/960/1920 on thinking families, 128/384/768 otherwise)
  so the monologue can complete; the prompt forbids thinking out loud but allows a
  reasoning reply that ENDS with a `Name: <name>` line; and a completed non-name reply
  is mined for the conclusion (`ModelFamilies.SalvageNameFromCoT`: "Final Answer: X" /
  "Name is: X" / "I'll call her X" / a bare one-word final line, last candidate wins,
  scanned line backward). Constraint restatements can't slip through (every shape is
  end-of-line anchored) and candidates hitting prompt-echo stopwords ("female",
  "letters", "them"…), the taken-list, or the species unit label are dropped — the
  unit-label check also moved into `ModelFamilies.IsSpeciesUnitLabel` as the single
  source of truth.
- **"Not really aware they're having sex" — console mode now has a full intimacy
  channel.** Console mode drains only chat + reagent events: being entered, withdrawn,
  and filling up reached the model only as late belly/egg numbers ("they just notice
  they have an egg later"). Now: penetration entry/withdrawal push [sex] world events
  (and the console drain delivers world events at all — carries/thrown/danger too, which
  ALSO never landed in console before); the per-turn state anchor carries a live sex
  line when anything is happening ("being penetrated (Dick: steady, 1.2 strokes/s,
  depth varying 0.2m to 0.6m, deepening)"); and the new `feel` tool (BodyControl module;
  console: `feel`) is the on-demand detailed read — who is inside
  whom, the depth/rhythm curve in words (strokes/sec from the last 3s of the game's
  calculated depth values, depth range, deepening/pulling-out trend), stimulation, egg
  state, and WHAT MOVES the stimulation (your hips: thrust; the partner's pace; the
  machine). `thrust` teaching goes with it. Prompts say: respond to [sex] events in the
  moment; never ignore a penetration.
- **Name-selection emptiness: the logging was looking in the wrong place.** The previous
  fix logged the response's HEAD (which cut exactly before the content field) — now the
  message DICT is serialized into the log (300 chars), and content itself is recovered
  from every shape: string content, content-PART arrays (text parts joined), and
  'reasoning_content' — some servers put the real text somewhere else entirely. Between
  that, the JSON-answer parsing and the 128-token floor, a model that "wants to use json
  as a name" or answers in a preamble now lands the name anyway; what still fails gets a
  REAL reason in the log for the next round.
- **Farming flow verified from the game source + taught as LOOK-then-USE**: the seed
  only plants while it sits on/near the soil (Seed.CanUse scans a 1m plantable SoilTile
  radius from the SEED's position), so the prompt flow is now: grab seed → cd to the
  plot → hold it over the dirt (or drop it on the plot) → look/turn TOWARD it → THEN use
  its id — with the "interact fails when you're looking elsewhere" note. All three
  prompts.
- **Gemma's 500s never hit the 400-ladder — they stream INSIDE 200-OK responses.**
  The follow-up log: "event: error … does not match the expected peg-gemma4 format"
  arrives as an SSE event after HTTP 200, so SafeHttp sees success and the loop sees
  empty content — retrying never happened. New `StreamEngineError` detection
  (pure, tested): empty content + an engine-error event (peg-*/server_error/predict
  stream/nested 500) → retry the UNCHANGED payload up to twice, with the engine's
  message in the log. Also: terminal-turn glue salvage ("speech… ps whoami status go
  find bed echo Zzzzzz~ 15m" — the whole turn glued into one line/SAY) now splits into
  speech + real command lines (targeted commands hunt non-command targets: "go find
  bed" → cd bed); glued digits ("sleep1h05m" → sleep 1h05m); code-fence artifacts
  ("```json{ "turn": -9}") are junk markers now; "id_7"/"id-8"/bare-number targets
  normalize to numeric-id targeting everywhere; and the goal belt refuses
  fence/turn-artifact junk.
- **"Focused on the blender, thinks it's a goal" — the energy truth + an exit
  strategy.** The hungry NPC was reasoning fine under a lie it was told: the prompts
  claimed FOOD is the recovery while the game's OTHER energy source (verified in the
  source: `BedMachine.SleepRoutine` = +0.2 energy/s) — bed REST — was missing from the
  teaching, and the model had no protocol for "the blender contract costs more coins
  than I have". Prompts (all three) now teach: at 0 energy stations refuse you;
  food metabolizes into energy AND bed rest restores it steadily; plus a HUNGER
  PROTOCOL (ask once → eat/buy what's reachable → plant/water/wait/harvest → bed rest)
  and an anti-fixation rule: don't loop on a plan you can't afford — state plainly what
  you need (coins? fruit?) and switch route; a machine you pass is not a goal; drop
  stale goals instead of re-asserting them.
- **"Not really using the image system at all" — console now has eyes by default.**
  The whole session had ZERO image uses: console mode never auto-attaches frames and
  no model ever typed `screenshot` itself. New `[Console] AutoScreenshotTicks` (default
  6): every N turns a vision-capable model gets its current first-person view attached
  automatically (when it actually moved/turned since the last one — no repeated frames
  of the same spot), and right after a stuck streak (see WHERE you're stuck). Prompts
  tell it to decide from the images.
- **"Wanted to use json as a name" — the name call now parses JSON answers.** Models
  saturated on the mod's contract answer the one-shot name prompt with an act-shaped
  JSON object ({"name":"..."}) or code fences — both now parsed (plus
  "name: X"/"my name is X" shapes), and the name call's max_tokens floor rose 16→128
  (those shapes need room to land complete; 16-token cuts were half the empty-name
  stories). Raw-server logging stays for anything still empty.
- **Inline tool-markup salvage ("they wanted to use XML now") + the name-selection fix.**
  The Novalis log showed the Gemma-a4b model emitting its whole turn as speech with its
  command queue inside mangled tool markup:
  "Hello! I am Basil…!<br/>ls&lt;|whoami?&lt;k&gt;sleep]&lt;tool_call|&gt;" — plus invented
  fragments (---end-of-turn-, &lt;goall …&gt;, &lt;lsl-&gt;). Everything went into the
  SAY payload; none of the intended commands (ls / whoami / goal explore the house /
  sleep) executed. New `ConsoleSalvage` (pure, in ConsoleShell.cs + tests): lines are
  split at junk markers (tool_call/broken-tags/end-of-turn fragments), the clean head is
  kept as the line (speech reads correctly), and the markup segment's words are
  canonicalized against the command vocabulary — payloads collected for goal/remember/
  echo-style commands, next-token targets for cd/use/get/find/look. A garbled reply now
  becomes: speech + `ls` + `whoami` + `goal explore house` + `sleep` executing properly,
  with a log line when a salvage happens. Think-marker constants are fragment-assembled
  in this file too (the marker-hygiene rule).
- **Name selection was structurally broken for preambly models.** The one-shot name call
  ran at max_tokens **16** — ANY model that emits even a short preamble (or a stray
  reasoning line the template doesn't isolate) gets cut to empty content before a name
  ever lands ("LLM name rejected: '' x3" in two different models). Floor raised to 64
  (reasoners keep 320), and when content is still empty the server's RAW response
  (240-char snippet) is logged so the per-family quirk is diagnosable from the log
  instead of a silent triple-fallback.
- **Gemma glitch-say guard (the 40KB "see you's" bubble).** The Gemma-4-26B-QAT playtest
  produced a degenerate sampling loop — one reply was the phrase "see you's" repeated
  thousands of times (40KB to the bubble, chat window, ack comparer and the prompt). New
  `TextUtil.TrimDegenerateRepeat` (pure, 11th test suite): consecutive identical 1- and
  2-token windows repeated ≥6× collapse to two units with " […]" (content before AND
  after the run survives), same-char runs ("!!!!!…") cut when they dominate, and says are
  hard-capped at 600 chars. Wired into `say` before suppression/acks, with a log line
  showing the shrink.
- **A\* scratch pool — the second freeze lead.** The MainMap++night2 session's grid is
  1150×1119 cells × layers ≈ tens of millions of nodes; EVERY A* call allocated
  `_gScore`/`_cameFrom`/`_closed` fresh (~185MB) and zeroed them with a slow int-by-int
  loop (~0.2–0.4s main-thread + massive GC churn per `cd`, the same order as the hang).
  A* now has a grow-only scratch pool (one-time init; per-run touched-node restore on
  release; overlapping runs fall back to private arrays; small local-window grids skip
  the pool), and both call sites release in `finally`.
- **Hang forensics that work from OUTSIDE the wedged thread.** Both freeze logs showed
  only repeated "main thread timeout (5000ms)" from the LLM loop and NOTHING else —
  because the main thread was wedged and a per-frame WedgeWatch can't log from inside
  its own wedge. Now: the main thread records a heartbeat + phase marker
  ("ReconcileInstances" / "WorldMap.Tick" / "PlayerTrail.Tick" / "QuestSense.Tick" /
  "marshal-callback" / idle) and a background watchdog logs
  "main thread appears HUNG (Ns without a tick) — last phase: X" — the next freeze
  names the section it died in.
- **Fallback names are gendered + NPCs can change their pronouns.** Not every model
  completes name selection (3× "LLM name rejected: ''" in the log) — the flavor fallback
  pools are now split feminine/masculine/neutral and the fallback picks from the pool
  matching the body's inferred gender, so a she/her body isn't auto-named "Gnarl". And
  new `set_pronouns(pronouns='she/her')` tool (console: `set_pronouns she/her`): the
  NPC's own declaration wins over anatomy-derived inference everywhere (perception
  'pronouns'), persists in the per-body memory record, and is restored with the rest of
  the life.
- **HTTP 400 recovery ladder (the playtest's peg-native 400s).** The fresh log's 400 body
  was a SERVER-side streaming-decode hiccup: "Engine protocol predict stream … does not
  match the expected peg-native format" (type server_error wrapped in an HTTP 400) — the
  payload was fine, the server's grammar layer choked on the model's own stream, wasting
  a tick each time. New `HttpRetry.cs` (pure + 10th test suite) classifies 400/413/422
  error bodies: **transient_engine** (peg-native/nested-500) → retry the UNCHANGED
  payload up to twice after 1.2s; **context/size (incl. 413/422)** → shrink the payload
  in two staged degradations (stage 1: radar/rays dropped, chat_log halved, nearby/
  stations/people capped, image parts flattened; stage 2: history/memory/facts dropped +
  oldest half of long console conversations trimmed); **image** → drop image parts +
  pause attaching for 90s (legacy frames AND console screenshots skip attaching and say
  so, rather than burning a guaranteed 400); **response_format** → drop the schema + pause
  5 min; **max_tokens** → retry at 512. Non-payload errors (401/404/no-model) keep the
  existing guidance logging. Ladder is bounded (≤5 posts per tick) and every step logs
  which fix fired.
- **Station confusion (muse-glimmer playtest): occupancy, energy, follows.** Four issues,
  all grounded in the game source while fixing:
  1. *"It consumed one slot, hence busy — so it left thinking the station was full."*
     Stations are `IAnimationStationSet` machines with per-slot occupants
     (`AnimationStation.info.user`) — now queried and worded: stations/`ls` entries carry
     "slots k/N taken — YOU ARE in one (that's why the station itself can read busy);
     1 slot(s) FREE", in-station perception carries `station_slots` (via the animator's
     current set), and ':busy' can't be misread as 'no room for me' anymore.
  2. *cannot_use now says WHY*: `out_of_energy` when a body's energy is 0 (a game rule
     found in `KoboldAnimationUsable.CanUse` — 0-energy kobolds are refused by every
     station!), "you are ALREADY in this station", or the live slot text — no more silent
     "maybe busy/occupied".
  3. *Follow-mode*: it's the model's preferred travel once learned — but they never
     learned they could leave it. The `follow` perception field now carries
     "follow(on:false) frees your own movement; follow(on:true) re-attaches" whenever
     following is on, and both prompts teach the break-free ("feeling 'stuck in follow'
     is a mistake — the release is one call").
  4. *Energy economics taught*: at 0 energy stations refuse you; FOOD is the reliable
     recovery (consumption reagents write metabolizedContents — energy follows); beds =
     SleepyHeadStations and NOT every map has one — when worried about energy with no
     bed around, EAT. In all three prompt locations (Main built-in + both shipped txts).
- **Follow-mode main-thread freeze (muse-glimmer playtest) — main-thread file I/O on a
  network drive.** The log's smoking gun: three "main thread timeout (5000ms)" warnings
  from the LLM loop's top-of-loop marshal — Unity's main thread stopped servicing the
  marshal queue for 15s+ while the game hung hard. Every periodic save ran SYNCHRONOUSLY
  on the main thread: the player-trail file (every 20s while the player walks — new in
  the trail update) and the 3MB world-map file, into `BepInEx/config/kkllmnpc_maps/` —
  which lives on the machine's NETWORK VOLUME. One stalled SMB write inside Update =
  the freeze. Fixes: both saves now serialize their payload bytes on the main thread
  (µs–ms) and do ALL file I/O (write/delete/move) on the thread pool with a single-flight
  guard; the trail save also throttles to ≥30s AND ≥120 new samples (was: every 20s while
  walking); and a per-frame phase watchdog (`WedgeWatch`) times WorldMap.Tick /
  PlayerTrail.Tick / QuestSense.Tick and warns by name when any section takes >250ms —
  the next wedge reports itself. Also fixed the follow-trail jump replay (the appended
  player position lacked its parallel jump entry, silently disabling jumps for
  follow-trails).
- **"Lost in the console" — terminal-echo degeneration (ZDTaichu playtest).** The LM
  Studio payload dump showed the failure loop precisely: replies degenerated into COPIES
  of the terminal — leading `$` prompts, echoed `ls` output rows (re-executed as
  "unknown command" noise), and worst, the STATE-ANCHOR line copied back, which parsed as
  a `goal:` command and stored the literal garbage goal `(none yet) | awake: 0h03m` —
  that garbage then appeared in every later anchor and the model kept re-storing it,
  permanently. Fixes:
  1. *Transcript-echo filtering*: replies are checked line-by-line against the last user
     message's actual terminal lines; verbatim copies (with or without their `$` prompts)
     are dropped before parsing (with a "(skipped N echoed terminal lines)" note), and a
     reply that is ONLY a copy gets prose-style corrective feedback instead of executing
     anything. Legit plain re-issued commands (no `$`) still execute.
  2. *Bare prompt lines vanish*: "$" / "$ $" / "> >" lines are no-ops now (were
     "unknown command — try: help" noise + stuck-counter hits). Nested prompt stacks
     strip repeatedly; ParseLine also now tolerates glued prompt "$cd bed".
  3. *Goal belt*: `goal` refuses payloads that look like status lines (start with
     "(none yet", contain "awake:") — an anchor copy can never become a goal again.
  4. *Prompt rules*: console protocol/persona/txt now explicitly say: never begin a
     reply with '$', never copy the terminal output back, replies are new command
     lines only.
- **CRLF from the game's chat is stripped in `HandleChat`** (Windows chat lines carried
  trailing `\r` — corrupts exact "stay"/"leave" detection and pollutes the transcript).
- **"Use the station I'M in" now resolvable**: the watched-player snapshot records the
  station the player is inside or standing next to; `player_activity` (perception) and
  `cat player` (console) now include the station's `'ls'-namespace id` plus whether the
  partner seat is free — `use id:N to join them`. Also: the state anchor points at the
  goal: a `player_wants:` fact with an empty goal now reads "your player's ask is in
  facts — act on it or set it: goal <the ask>".
- **Self-reply sources, part 2 (offline-room edition, from a playtest log).** A playtest
  log (offline/single-player room) showed two live issues beyond the ack-loop fix below:
  1. *The identity bot restarted on every single say* — `[identity-bot] starting` ×4 in a
     short session. In an offline room ("offline room") there's no real server for a
     second Photon client to join, so the bot never reached `InRoom` and every `say`'s
     ensure call tore down and rebuilt the client. It now refuses to start in
     `PhotonNetwork.OfflineMode`, when the client isn't `IsConnectedAndReady`, or in an
     "offline room", and gives up for the session after 5 failed starts — the
     owner-attributed fallback (which was actually carrying all the chat) runs on clean.
  2. *Free-floating self-greetings* — a second/third intro posted with NO `[chat]` behind
     it: the model was conversing with its own past lines. Console turns that are purely
     echo/emote with no [chat]/[event] trigger now get a throttled nudge ("nobody spoke
     this turn — that message went to nobody; DO something: ls / cd / goal, or sleep"),
     and the prompts teach the recipient rule after the model called its player by its
     own name ("Hello Nib!"): the speaker's name is on the line / `ps`; never address the
     player by your own name. (Also: the supplied log was dated 9/25 — from BEFORE the
     ack-first / race-fix builds; those fixes weren't in that run.)
- **"The AI responded to a past message" — a real loop, found and closed.** `say` marked
  the player-chat conversation ACK *after* repeat-suppression — so when a reply was
  suppressed as a near-duplicate, the player's line stayed un-acked, the 30s `heard`
  window re-served the same old line next tick, the model answered it again, got
  suppressed again: an endless "responding to an old past message" cycle that also looks
  like self-conversation. Acks now land *before* suppression — conversation bookkeeping
  is decoupled from world anti-spam.
- **Thread/body racing hardened** (investigating "two threads fought over a body"):
  1. *Claim-after-shutdown hole*: the reconciler retires an instance by setting
     `_running=false` + tearing down its body WITHOUT joining the LLM thread (joining can
     deadlock on a parked main-thread marshal) — but a thread parked mid-turn could wake,
     run its queued `EnsureBody` marshal, and CLAIM A NEW BODY as an invisible orphan:
     already removed from the instance list, no watchdog/chat routing ever saw it again,
     and its claim leaked when it quietly exited. `EnsureBody` and `Possess` now re-check
     `_running` immediately before claiming — a retired instance can never claim again.
  2. *Duplicate LLM threads*: `ForceRestartThread` (overlay) and `MaybeRestartThread`
     (watchdog) both replaced `_llmThread` with no lock and no under-lock re-check — two
     restarts interleaving (or an overlay force-restart racing a watchdog relaunch) could
     leave one instance with TWO LLM threads interleaving ONE conversation and one body's
     tools: two minds fighting over one body, each possibly taking its own past lines as
     context. All restart paths now funnel through one lock, re-verify the thread is dead
     under it, and can never duplicate or drop a live thread reference.
  3. *Double-bind tripwire*: the possession claim is atomic, but as a final guard
     `BindBody` checks (via `Plugin.FindOtherInstanceHolding`) that no other live
     instance drives the same body and logs a loud "DOUBLE BIND — both names" error if
     one does — any residual same-body fight becomes diagnosable from the log. The benign
     designed claim-race log now says what it is ("expected race, winner keeps driving")
     so it isn't mistaken for a fight.
- **NPCs can now hear each other speak (and be addressed by name).** Chat routing previously
  blanket-filtered *every* plugin-owned line for *every* instance, so NPC↔NPC conversation
  was structurally impossible and each body saw its fellow agents as anonymous "wild/AI"
  kobolds. Routing is now per-instance: player lines addressed to a specific NPC
  (`"Ember, follow me"` / `"Ember: hi"` / `"@Fern hey"`) land on that NPC alone (with the
  address stripped so command words still parse); other agents' lines only reach a sibling
  when **addressed** and at most twice a minute (a duet cap — each reply is a stimulus for
  the next, which previously meant any shared channel looped forever). Fellow agents now
  show up named in `ps`/`ls`/the "people" perception with an "another agent kobold" note.
- **The state lock was unsynchronizing itself.** `NPCInstance._stateLock` is a
  `ReaderWriterLockSlim` that ~30 call sites used as `lock(obj)` (a Monitor) while only
  `ResetMind` spoke `EnterWriteLock` — Monitor and RWLS guard independent internal locks,
  so the "exclusion" everyone assumed protected nothing. Now a plain `object` monitor
  everywhere (hold times are nanoseconds), with a comment warning future readers not to
  re-mix the mechanisms.
- **Mind wipes could race the LLM thread mid-turn.** `ResetMind` mutated ~35 fields
  (transcript, facts, chat, target maps) from the main thread while the LLM thread was
  possibly inside `ConsoleTurn` reading exactly those. `ResetMind` is now a dispatcher: a
  live LLM thread queues a volatile pending-why, and the loop applies the wipe at the next
  turn boundary (`ResetMindNow`); with no live thread it applies immediately as before.
  The identity-bot stop inside the wipe is marshalled to the main thread, since the wipe
  can now run on the loop thread.
- **A dead LLM server cost a full retry run every think tick, forever.** SafeHttp retries
  *within* a query (3 attempts + backoff), but the think loop had no memory between ticks:
  an offline endpoint meant three connection attempts every ~0.8s indefinitely, with the
  player seeing nothing in-game. A cross-tick circuit breaker (`_endpointFails`,
  `_endpointBlockUntil`) now defers queries on an escalating idle (5/10/20/30s, logged),
  resets on first success, and surfaces state three ways: the state anchor tells the
  model itself (`endpoint: DOWN — retrying in Ns`), the Overlay State tab shows a
  color-coded row, and `ConsoleTurn` no longer churns prompts when no endpoint is
  configured at all.
- **`sleep [secs]` ignored its own argument.** The command documented (and help listed) a
  duration that was discarded — the loop repolled the model at full cadence anyway. It
  now pauses activity until the wake time (capped at 300 s), wakes early on player chat,
  body events (grabbed/thrown/danger) and ask answers, and the state anchor shows
  "sleeping: Ns left". Bare `sleep` still just ends the turn.
- **Physics work ran off the main thread and re-derived answers every frame.**
  `FindPlaceByName` ran a 60 m `Physics.OverlapSphere` (fully allocating) on the LLM
  thread; Farming/Cooking's `FindNearest*` helpers ran scene-wide `FindObjectsOfType` on
  the LLM thread too — both now marshal to the main thread. The wall-proximity scan (4
  rays) and camera-clip probe (1 ray) ran every physics tick even while idle — now 4 Hz
  and 5 Hz respectively, with the last scan persisting between runs. Ray-hit perception
  (`GetComponentInChildren<Renderer>` hierarchy walks per ray per build) is memoized per
  collider (5 s TTL, 512-entry cap, cleared on mind reset).
- **`Clear` in the console claimed "ok" and cleared nothing** (kept as no-op — but the
  mind-viewer now makes the transcript visible in-game instead; see Added).

### Added
- **Persistent per-body memory (`src/MemoryStore.cs`).** When a body is given up
  (sold/lost/world change), its facts, persona, name and active goal are written to
  `BepInEx/config/kkllmnpc_memory/<body>.json`, keyed by the body's own serialized
  object name (which survives the world save). When the same body is claimed again in a
  later session, it wakes as the same person — name restored (unless another copy
  claimed it), facts and goal loaded, LLM name re-pick skipped. A brand-new body still
  wipes everything (the context-retention guard); a bodyswap still keeps the mind
  in-memory. This is the fix for "world reload = total amnesia".
- **Money and buying (`src/Economy.cs`).** The prompts promised buying contracts and
  shop items, but no wallet perception or buy tool existed — the behaviour was
  unperformable. Now: each NPC body carries its own coins (`MoneyHolder`, the game's own
  per-kobold wallet) surfaced in `whoami`/`status`/`cat needs` and legacy perception as
  `economy` (coins + nearby purchasables with costs), and a `buy [name|id]` tool buys
  whatever is within reach — machine construction contracts, the kobold dispenser, shop
  items — through the game's own public `CanUse`+`LocalUse` path (identical to a human
  player's click; it spends the NPC's own coins). Contract/dispenser prices are
  read via reflection where the game keeps them private.
- **The `emote` command.** Body language distinct from speech: `emote curls up by the
  fire` renders as `*curls up by the fire*` through all three speech channels (bubble +
  real chat window + local echo), with repeat suppression still applying. Registered in
  the console vocabulary, the legacy dispatch, the salvage-path tool lists, and taught
  in both prompts.
- **Speech bubble duration scales with text length** (2.5 s + 1 s per 12 chars, 3–10 s)
  instead of a flat 4 s — long lines stop truncating, short ones don't overstay.
- **Session time (`awake: 2h07m`).** The game ships no day/night clock (verified in the
  game source: `DayNightCycle` is a vestigial metabolism ticker and its clock events are
  orphaned), so the honest stand-in — how long this body has been awake this session —
  rides in the state anchor, the needs block and `cat needs`, so a model can reason
  "I've been up 2h; time to nap".
- **Console-mode commentary.** Free unprompted musings (`CommentEveryNTicks`) only
  fired on the legacy path; console mode (the default) never got them. The musing
  worker now gets the console state anchor as context.
- **Overlay Mind tab (F6).** The NPC's console transcript — what it typed, what came
  back — plus the latest vision caption, mirrored live in-game. Previously the entire
  default-mode mind was visible only in the BepInEx log. The transcript refreshes via a
  monotonic version counter instead of copying per IMGUI frame.
- **Overlay Config tab covers every key.** Replaced the hand-coded 16-entry list (which
  silently clipped at the pane height and omitted all `[Console]`, `[General]`,
  `[Memory]`, `[Multiplayer]` keys) with reflection over the plugin's bound config
  fields — every key editable in-game, API-key masked. Also fixed the docs claiming the
  overlay hotkey was Insert (it's F6, in `Main.cs`).
- **`docs/IMPROVEMENTS.md`** — the working tracker for this improvement sweep: every
  finding, decision and completion note for the waves above (thread safety, resilience,
  performance, and the feature set listed here).

### Fixed (continued)
- **Horniness never dropped while station-play was energy-blocked.** The game only resets
  stimulation (`kobold.stimulation`, raw −20..+10) on an orgasm, which requires energy ≥ 1 —
  with low energy a play station saturated it near max forever, and the slow-burn `_horny`
  code holds (not drops) while stimulation is high, so the NPC's need stayed pinned and it
  looped in the station. Now: thresholds compare against stimulation normalized to 0-1 (the
  `HornyStim*` constants are normalized by design — the old code mixed raw values with
  normalized thresholds, so the "climax" trigger fired on tiny touches), and a sustained
  ≥90% saturation with no climax (≥60s, presumptively energy-blocked) bleeds the need down
  at 2× the climb rate. Climaxes and bed-rest relaxation still reset it to 0.05.
- **Context retention across possessions.** `BindBody` now wipes all runtime memory
  (`ResetMind`) whenever an instance takes a body it didn't previously inhabit — a sold /
  destroyed body can never leak its facts, goals, chat or console history into the next
  claim. Bodyswap-machine rebinds still keep the mind (that's the point of the swap).
- **LLM requests now actually retry.** `SafeHttp` built a single `HttpWebRequest` outside its
  retry loop and called `GetRequestStream()`/`GetResponse()` on it repeatedly. A submitted
  `HttpWebRequest` can never be resent — the second attempt threw `InvalidOperationException`
  ("request started") without touching the network — so every retry was a silent no-op that
  still paid its backoff sleep. A transient 503 cost the NPC 3 seconds of frozen decision
  loop and still failed.
- **Wired the retry layer into the real transport.** `SafeHttp` existed but had *no callers*:
  all four LLM call sites (`Llm.cs`), the vision call (`Vision.cs`) and the model probe
  (`ModelProbe.cs`) each hand-rolled their own single-shot `HttpWebRequest` with no retry at
  all. They now all go through `SafeHttp`. This is what makes the retry behavior real rather
  than theoretical.
- **Permanent HTTP errors no longer retried.** 400/401/403/404/413 fail after one attempt
  instead of three, so a bad API key or an over-long context no longer burns backoff sleeps
  and hammers the server. 5xx, 408, 429 and connect/timeout faults still retry with backoff.
- **Failed calls report the server's error body.** Previously a non-2xx reply was reduced to a
  status code and discarded. The log now carries the server's own complaint, which is what
  makes "context length exceeded" distinguishable from "no model loaded" without a debugger.

### Added
- **Model-family awareness (`ModelFamilies.cs`).** Classifies the loaded model id into a
  family profile (Qwen, Gemma, GLM, Muse Glimmer, Ornith, LFM, Bonsai, Nemotron, Granite,
  Mistral, SmolLM, Phi, Llama/GPT-OSS, DeepSeek-style reasoners, vision captioners; unknown →
  generic) and adapts behavior automatically — no new config keys:
  - Reasoning-marker scaffolding (`&lt;think&gt;…&lt;/think&gt;` and the `thinking` spelling,
    in all casing variants) is stripped from replies before line/JSON parsing at all three
    reply sites — console turn, legacy act loop, name selection. Stripping is unconditional
    (content-neutral cleanup); it also handles two failure shapes local servers actually
    produce: an opener the server consumed as a special token (closer leaks as text → keep
    what follows the closer) and a token-cap cut mid-reasoning (keep any salvable prefix,
    else the existing empty-reply retry paths take over).
  - Token budgets gain a reasoning floor for thinking families: effective
    `max_tokens = max(user value, default + headroom)` — Qwen/Bonsai/GLM +512,
    Nemotron/Granite +768, reasoners +1024, unknown families +256; no-reasoning families are
    untouched. A user's explicit higher value always stands.
  - Name selection raises its `max_tokens` from 16 to 320 for thinking families — 16 died
    inside the marker block (a likely historical cause of failed LLM name picks), and the
    extracted name is now stripped before validation regardless.
  - Tier auto-resolution falls back to the family's preferred tier only when the probe is
    offline: Nemotron/Granite/Ornith/LFM/Bonsai/vision-captioners → small; Muse Glimmer →
    medium. With probe data the probe still wins.
  Pure C#, unit-tested: `tests/test_modelfamilies.cs` (67 tests, suite `modelfamilies` in
  `run_tests.sh`).
- **Late bodies now get claimed (`ReconcileInstances` fix).** The pool sized instances to
  `min(claimable, MaxNPCs)` (currently *free* bodies). Once every instance was bound, a body
  that appeared LATER (delivery, breeding, another mod) incremented `claimable` but
  `want = min(1, 2)` < `instances.Count` — so no instance was ever created to take it. The
  pool now creates an instance whenever free bodies outnumber hungry (unbound) ones, and
  leaves the rest to the existing 20s unbound-instance retirement. This is the fix for
  "it wouldn't claim a body when it was an option".
- **Population top-up (`SpawnMissingKobolds`, default OFF).** A KoboldKare world only ever
  holds the starting pair of AI kobolds — more come only from paid deliveries or breeding —
  so `MaxNPCs` above 2 could never populate. When enabled, the plugin spawns fresh AI kobold
  bodies (room objects from the same "Kobold" Photon prefab the in-game dispenser uses,
  random genes, default AI control) near the player until the world reaches `MaxNPCs`, one
  per 8s. The spawned kobolds are ordinary room objects — they persist in the farm save just
  like delivered kobolds. Needs the master client (always the local player in single player);
  guests in multiplayer skip the top-up.
- **Pickup / carry / throw awareness (`src/CarrySense.cs`).** Polls `Kobold.grabbed` each
  physics tick; on grab it snapshots where the body was taken from and records a position
  trail while carried. On release it pushes a world event into the same `[event]` channel the
  LLM drains: gentle carry ("picked up and carried ~Xm <bearing>, path: … — your mental map is
  now off by that much"), throw (release speed > 3 m/s, the game's own `ThrowRoutine`
  threshold — flagged as startling/upsetting), or brief lift. Each relocation auto-`remembers`
  a fact so the NPC knows where it ended up across turns.
- **Mail/sell machine protection.** The mail machine is a sell trap: `use` starts a swallow
  timer that destroys the entered kobold, and its intake pulls in loose (ragdolled) bodies.
  `interact`/`use` now refuses it outright (`sell_machine` reason), and a physics-tick guard
  auto-recovers (game's own `PopRagdoll`) when the body is ragdolled within its suck radius,
  with a DANGER world event telling the NPC to run. Both prompts (console + legacy) explain
  the machine and the "never jump near it" rule.
- **Station partnering knowledge.** Both prompts now teach: some stations hold two kobolds
  (play/breeding) and some are solo-only (bed/nest/toilet/bath/seat); on two-partner stations
  the BOTTOM (penetrated) enters first and settles, the TOP (penetrator/stud) mounts second.
- **Flavorful name fallback.** When LLM name selection fails, the fallback is now a curated
  kobold-flavor name list (Ember, Moss, Tumble, …) before the mechanical numbered style, and
  the LLM answer is rejected when it is just the species name ± digits ("Kobold2") so the
  model re-rolls an actual name instead. Validation window aligned to the prompt (2–12).
- **`MaxNPCs` raised to 8** (was 1–4). The clamp is centralized in `Consts.MaxNPCs`.
- **`src/ContextCore.cs` + 109 tests.** The compaction policy — escalation ladder,
  de-escalation, per-budget dynamic limits and their floors, token estimation, fact merging,
  history summarization — is now a pure, Unity-free state machine (`ContextPolicy`,
  `ContextMath`, `ContextCompaction`). `ContextManager` is a thin adapter holding only the wall
  clock, the `ModelProbe` lookups and the admin-API model switch. Dynamic compaction was listed
  as a "Key Design Decision" with zero test coverage because it couldn't be reached without a
  game install; it now has the most thorough suite in the repo.
- **`GoalResolver` in `PathCore.cs` + 30 tests.** The goal-snapping ring search was inline in
  `Pathfinding.ResolveGoal`, coupled to `PathGridState` and therefore untestable. It now runs
  against an `IGoalGrid` interface that `PathGridState` already satisfied for free (no new
  members), and is covered headlessly. This pins down two behaviours that were previously
  implicit: the search is nearest-in-plane with a *height* tiebreak (a ring-1 cell at the wrong
  floor beats a ring-2 cell at the right one), and equal-height ties resolve to the first cell
  scanned.
- **`run_tests.sh`** — one command to compile and run every pure-C# suite, with a per-suite
  summary and a non-zero exit if anything fails. Previously there was no single entry point and
  no shared exit-code convention.
- **`tests/test_safehttp.cs`** — 23 tests driving the real transport against a loopback
  `HttpListener`. These count actual server-side hits, which is the invariant that broke: the
  suite asserts a 3-retry sequence reaches the server 3 times, and that a 400 reaches it once.
- **`check_syntax.sh` + real CI gates.** CI previously compiled nothing and ran nothing — it
  counted lines and printed file listings, so a syntax error in `src/` merged green. CI now
  runs the unit suites and a parse check over all of `src/` (a full build still needs a game
  install, since BepInEx/Unity/Photon aren't redistributable).
- **`check_build.sh`** — a real compile against a game install, reusing `build.sh`'s exact
  reference set, and treating warnings as errors. Skips cleanly when no install is present, so
  it works locally and is a no-op on CI. Added after the parse-only check proved it could not
  catch a renamed member: `ContextManager.DynamicMaxThoughts` was briefly renamed, the call site
  in `NPCInstance.cs` was left dangling, and every headless gate stayed green. Only `build.sh`
  caught it. The limitation is now documented in `check_syntax.sh` itself so it isn't
  rediscovered.
- **CI fails on stray sources.** `build.sh` compiles `src/*.cs`, so a `.cs` file dropped into a
  `src/` subdirectory would be silently excluded from the DLL. CI now rejects that.

### Removed
- **`tests/test_pathfinding.cs`.** Every one of its ten methods was a `// Placeholder:` comment
  with no assertion and no `Main()` — it was a TODO list wearing a test file's name. The one
  piece of real logic it was gesturing at (goal resolution) is now `GoalResolver`, tested.
- **`tests/test_contextmanager.cs`.** Same anti-pattern as `test_horniness.cs`: it re-implemented
  the compaction ladder inline ("simulate escalation without a real NPCInstance") and asserted on
  its own local variables. Superseded by the 109 real tests in `test_contextcore.cs`.
- **`tests/test_horniness.cs`.** It never referenced `NPCInstance` or any file in `src/` — it
  re-implemented the horniness arithmetic in local variables and asserted on its own locals.
  Two of its six cases failed (`HorninessBounds` computed -0.05; `EggLayReadiness` got false)
  and it still exited 0, so it reported success while red. Its failures described arithmetic
  production never performs: the real `UpdateHorniness` only ever clamps the upper bound and
  resets on climax, so `_horny` cannot go negative. Deleting it removes a false signal; the
  honest fix is to extract the horniness math into a Unity-free `HorninessCore.cs` and test
  that, the way `PathCore` was extracted. Not done here.

### Documentation
- Corrected the ambient-commentary claims. `EmitAmbient` has no live callers (all three call
  sites are commented out), so the README's "moans on stimulation" and ARCHITECTURE's
  "Ambient commentary (moan on stimulation spike)" described behavior the build does not have.
  The docs now say it is disabled. Restoring the calls or deleting the dead method is a
  gameplay decision, so neither was done unilaterally.
- `tests/README.md` now lists all 7 suites (326 tests). Previously documented as 4 test files, all
  described as pure C#, while 3 files existed that either needed a game install or asserted
  nothing at all.

### Known gaps (pre-existing, not addressed here)
Two compaction features are implemented and unit-tested but never called, so compaction level 3
advertises more than it does:
- `ContextCompaction.SummarizeHistory` has no caller — level 3 merges facts but never summarizes
  history, despite the log text and ARCHITECTURE both saying "fact merging + history summary".
- `ContextManager.DynamicMaxChatLog` has no caller, so `[LLM] ChatLogLines` is not reduced under
  compaction.

Both are behaviour changes to wire up (how many chat lines to keep at level 5 is a tuning
decision), so they were left for a deliberate choice rather than changed silently.

### Added (Console REPL and earlier work)
- **Console REPL (bash-like AI interface, default ON)** — the LLM now *polls* the game from a shell
  instead of receiving a perception JSON blob every tick: it sends command lines (`ls`, `ps`, `pwd`,
  `whoami`, `cat facts|goal|chat|needs|history|stations|map|body`, `status`, `find`, `look`; `echo
  <words>` to **speak**; `cd`, `use`, `run`, `turn`, `jump`, `crouch`, `exit`, `get`, `drop`, `follow`,
  `stop`, `sleep`; `remember`, `forget`, `goal`, `ask`), reads the terse output, then sends more
  commands — a command → data → read → command stream. Payloads stay small (works on small local
  models, no overload), and the model asks for exactly the data it needs. Bash-style names are
  chosen so small models already know them; legacy `act` names (`say`→`echo`, `go_to`→`cd`,
  `interact`→`use`, `walk`→`run`, …) are aliases, with a deliberately tight fuzzy matcher that
  recovers typos without turning prose into commands. Module tools (thrust, plant, water, harvest,
  plant_egg, feed_blender, grind, rename, …) register into the same vocabulary at startup.
  New `[Console]` config section (`Enabled`, `MaxRounds`, `MaxTokens`, `HistoryMessages`,
  `SystemPromptFile`); a `system_prompt_console.txt` persona ships with the plugin.
  `Console.Enabled=false` restores the legacy push-perception / `act`-JSON loop (kept intact).
  New files: `src/Console.cs`, `src/ConsoleShell.cs`, `system_prompt_console.txt`;
  tests: `tests/test_console_shell.cs` (90 tests).
- **Background path worker** — static daemon thread computes A* paths off the main thread;
  milestone-based replanning (only when target moved, path exhausted, or deviation exceeds
  threshold); time-budgeted expansion prevents frame hitches on large maps
- **Whole-map world map** — cached 3D walkability map shared by all agents; adaptive cell
  sizing (span/1200, clamped [0.5,4.0]); hard cell cap (~4M); auto floor detection
  (gap > 1.5m = new layer, cap 16)
- **Body control module** — `thrust` (hip animation), `erection`, `mount`, `unmount`,
  `orgasm` tools with consent-aware prompt guidance; perception `body_control` reports
  erection, stimulation, penetration state, hip animation status
- **Chat freshness** — timestamped chat log with ack tracking; say-repeat suppression
  (Jaccard > 0.65 OR Levenshtein ratio > 0.8); identity bot exponential backoff
  (2s → 30s); empty-content retry; de-duped log
- **Farming & cooking module** — `plant`, `water`, `harvest`, `plant_egg`, `feed_blender`,
  `grind` tools; perception `farm` (nearby plants) and `cooking` (nearby equipment)
- **Identity module** — per-NPC identity block (orientation, trans status, persona);
  `rename` tool with uniqueness validation; mailbox/ATM perception
- **PathCore.cs** — pure C# A* solver, path policy, adaptive cell sizing (Unity-free,
  compiles standalone for testing)
- **ChatSimilarity.cs** — Jaccard + Levenshtein similarity (pure C#, testable)
- **New tests** — `test_pathcore.cs` (33 tests: A*, ShouldReplan, adaptive cell, auto floors);
  `test_chat_similarity.cs` (19 tests: Jaccard, Levenshtein, fuzzy matching)
- **New prompt extras** — `body_control.txt`, `chat.txt`, `farmcook.txt`, `identity.txt`

### Changed
- **Replies are stripped of reasoning scaffolding before all parsing** — the console loop,
  the legacy act loop and name selection no longer see hidden CoT runs; the empty-content
  retry path and console empty-streak nudges now fire on reasoning-only replies instead of
  choking on them. See the ModelFamilies entry under Added.
- **Pathfinding** — moved from inline FixedUpdate A* to background worker; added time budget
  and expansion cap; world map with auto-layers and adaptive cells
- **Movement config** — added `PathTimeBudgetMs`, `PathMaxExpansions`, `WorldMapEnabled`,
  `WorldMapCellSize`, `WorldMapMaxSpan`, `WorldMapMaxCells`, `WorldMapAutoLayers`,
  `WorldMapLayers`, `WorldMapCellsPerFrame`
- **Farming config** — new `[Farming]` section with `ScanRadius` and `ScanMax`
- **Architecture** — 26 partial class files (up from 15); new `ModuleRegistry` for
  reflection-based feature discovery
- **Warning count** — reduced from 19 to 0 (removed dead locals, fields, and assignments)

### Added
- **Goal machine** — `set_goal` / `complete_goal` / `drop_goal` tools with a persistent goal in perception (`goal` block + `nudge` when stuck); a thought-repetition guard for models that ignore the tools; completing/dropping a goal sheds its scratch facts (`src/GoalMachine.cs`)
- **`forget` tool** — drop a fact by category, text, or substring (no argument = drop the oldest fact); the explicit half of forgetting
- **Fact decay** — `Memory.FactDecayTicks` (default 900; 0 = off): facts the model hasn't re-asserted age out of context; re-`remember`ing refreshes a fact's age; cap eviction now removes the oldest fact
- **Chat baseline** — the game chat log is snapshotted when the NPC acquires its body; `chat_log` only feeds lines added after that, so the NPC never "reads" pre-spawn conversation
- **NPC room identity (opt-in)** — `Multiplayer.IdentityBot` + `IdentityAppId`: a second Photon client joins the room under the NPC's name so its chat renders `KoboldName: text` to everyone; auto-falls back to owner-attributed chat whenever the bot is down (`src/IdentityBot.cs`)
- **`LLM.SystemPromptFile`** — full system prompt from a file (resolved relative to game dir, plugin dir, or CWD) overriding the built-in; `system_prompt_default.txt` ships in the repo
- **SafeHttp utility** — retry wrapper with exponential backoff (3 attempts, 1s/2s/4s delays), proper resource disposal, and TLS validation for all LLM HTTP calls
- **Constants.cs** — named constants for all magic numbers (HTTP timeouts, perception ranges, movement values, etc.) — replaces scattered literals throughout the codebase
- **Thread pool semaphore** — caps concurrent `Task.Run` calls to prevent thread pool starvation under heavy multi-NPC load
- **Improved error logging** — all bare `catch (Exception) { }` blocks now log the exception message via `Logger.LogWarning("...: " + e.Message)`
- **Cross-platform build support** — build.sh includes dotnet SDK fallback alongside mcs
- **docs/ARCHITECTURE.md** — complete architecture overview with component breakdown, data flow, and thread model
- **docs/CONFIG.md** — complete configuration reference with all keys, defaults, ranges, and purposes
- **CONTRIBUTING.md** — contribution guidelines, code style, testing, and debugging instructions
- **CODE_OF_CONDUCT.md** — Contributor Covenant Code of Conduct
- **GitHub Actions CI** — `.github/workflows/build.yml` for automated build verification
- **tests/test_json_standalone.cs** — standalone Json parser/writer test specifications (no Unity/BepInEx deps)
- **Improved README** — complete config table with ranges, Windows build instructions, architecture doc links

### Changed
- **Own-speech filtering** — `Main.cs` now also matches on the sender's nickname, so a bot-attributed chat line (identity-bot path) isn't heard back by the sending NPC (no feedback loop)
- **System prompt resolution** — explicit `SystemPrompt` > `SystemPromptFile` > built-in; the full prompt now lives in `system_prompt_default.txt` and the compact small-model prompt includes the goal tools
- **Config duplication eliminated** — config entries are now bound in one place (`Main.cs`) and read via a shared config accessor, removing the duplicate field copies in `NPCInstance.cs`
- **HTTP resource management** — `HttpWebRequest` streams are now properly disposed via `using` blocks and `SafeHttp`'s finally blocks, preventing connection pool exhaustion
- **Magic numbers replaced** — all magic numbers (timeouts, ranges, thresholds) replaced with named constants in `Constants.cs`
- **Vision hung timeout** — reduced from 2 minutes to a configurable value with logging

### Fixed
- **Goal flapping** — `set_goal` now refuses a goal that was dropped/completed within the recent window (the `recently_dropped` ring is enforced, not just displayed), so weaker models can't loop set→drop→set
- **Fact-shed safety** — `ShedFactsForGoal` no longer wipes a world-map fact (e.g. `nest: upstairs`) when a goal shares its category prefix; a fact is only shed if the goal is category-tagged AND shares ≥2 significant words with it
- **Identity-bot staleness** — the bot now restarts when the game's room changes (it used to stay bound to the old room and chat into it), recovers after exhausting its reconnect budget (rate-limited restart instead of giving up forever), and `Stop()` no longer blocks the game's main thread for up to 1s
- **Standalone Json tests** — `tests/test_json_standalone.cs` `Main` called test methods that didn't exist, so the documented no-deps test path never compiled; it now delegates to `JsonTests.RunAll()`, which exercises the real `src/Json.cs` (22 tests). `Json.cs` no longer references `LLMNPCPlugin` (parse-error logging moved to a pluggable `Json.ErrorLog` sink wired in `Main.cs`)
- **Inverted test assertion** — `test_json.cs` asserted `Has("x")` should be false (message said "should be true"); corrected
- **HTTP connection leaks** — `HttpWebRequest.GetRequestStream()` is now always disposed in a `using` block, even when `GetResponse()` throws
- **Silent exception swallowing** — removed 15+ bare `catch (Exception) { }` blocks across Senses.cs, Movement.cs, Body.cs, Vision.cs, and Tools.cs
- **Door toggle-fluttering** — `MaybeOpenDoorAhead` cooldown now uses a named constant (`Consts.DoorCooldown`) for clarity
- **Camera clip detection** — improved camera-clip fix timing with configurable thresholds
- **Build.sh** — added mcs availability check with installation instructions

## [1.0.0] - 2026-09-05