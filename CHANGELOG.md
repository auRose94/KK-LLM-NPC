# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **North-up sonar (playtest feedback, "the map slides around").** The ASCII radar was
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
- **Watched, not memorized: the player's current activity (playtest feedback, "know the
  objective without a fact").** The game has no quest list to read, so the AI now reads the
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
- **Hands the AI can finally see (playtest feedback, "the AI can't let go of a player").**
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