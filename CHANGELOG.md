# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
