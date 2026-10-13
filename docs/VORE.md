# Swallow-vore & spectate — the full mechanics

This is an opt-in feature set in `[LittleTreats]` — **all of it is OFF by default**, and
it only exists because the game's own swallow mechanic (you can already swallow kobolds
as the player in vanilla) deserves a real mind on the other side. Nothing here is
required for the main mod; see the [README](../README.md) for everything else.

Everything here was built as: the swallowed person is a PERSON — narrated to, honest per
outcome, never lost, and no way to cheat their way out silently.

## The player eats an NPC (`SwallowEnabled`, opt-in)

- **Trigger**: grab a kobold, aim steeply up (`SwallowLookUpDot`, default ≈70°+, near
  max-up so a casual drop can never trigger it), drop — you swallow them whole instead
  of dropping.
## Where they go in (`SwallowDestination` — off/mouth/anal/pussy/nipple)

- **`Off` is the default and the accident switch**: with it, no release-triggered
  swallow arms at all — a held kobold dropped near another head (precision-grabber
  bump while carrying included) just drops like normal.
- A picked hole arms the gestures and **aims the pull at that hole** (bone-anchored on
  the eater's body); narration names the entry everywhere ("taken in through the ass",
  unbirth phrasing for pussy, the breast for nipple) and the stomach outcome/fate is
  unchanged.
- **Dick (cock vore / urethral entry)** anchors on the EATER's actual dick (the game's
  penetrator object), falling back to a groin point for the dickless — a gained-dick
  eater uses their new equipment from the moment it grows.
- Cycle in-game with **F7** (off → mouth → anal → pussy → dick → nipple → off) — the
  toast lands in chat — or set it from the WebUI settings tab.

- **Digestion**: they ride into your mouth (~2 s) and are **hidden once fully swallowed**
  (no sticking-out clipping). Their LLM keeps narrating from inside, steering gated; the
  digestion is narrated to them as events.

## `SwallowEffect` — what happens at the end

| Effect | Outcome |
|--------|---------|
| **Egg** (default) | an egg pops out at the player's feet, mixing their genes with yours — plant it in soil and the hatched body wakes as the SAME person, changed genes, in control again |
| **BellyEgg** | the genetics go inside YOUR belly instead — sit on a nest and lay, then plant that egg |
| **Grow** | they absorb into you (bigger/stronger, no egg) — their saved memories pour into you: your identity stays yours, places and things they knew become yours; a vanilla eater wakes up remembering the person it ate |
| **Destroy** | they're simply gone (memories erased) |

Rebirth detail: a planted egg's fresh body claims through the ordinary path, so persona
restoration rides the existing MemoryStore keys (and **skills** transfer too — on
absorb into the eater, and through a child egg to the hatched new body). Absorbed
people's memory files are cleaned: once a person is absorbed (Grow) or rebirth-copied,
their origin record goes — no ghost wakes up later.

## The no-cheat escape hatch

**Type "swallow abort"** (or "spit out", "cough up", "regurgitate", "swallow stop") in
the game chat: everyone mid-digest is heaved back up alive and whole — scale, colliders,
renderers, ragdoll and their LLM session restored — set down in front of you; the
swallowed NPC gets the release as an event and remembers it. The line is consumed by
the mod so the other NPCs never hear it.

## Force-feeding (`SwallowForceFeeding`)

Any kobold — NPC or player — dropping a held person right onto another kobold's head
makes the receiver swallow them. The receiver walks around with the digest running
(outcome per `SwallowEffect`), the dropped one gets mode-honest narration naming who
fed them, and the dropper's `drop` result says `force_fed` instead of "dropped".

## Grief guards

- Player bodies can never be swallowed by the NPC swallow gestures or force-feeding —
  the only player-victim path is spectate (below).
- An eater already digesting someone refuses another.

## Eater awareness (NPC eaters too)

Even when the EATER is an AI NPC, it's a real mind in the loop:

- a first-person "you just swallowed X WHOLE…" event at the start,
- digest-progress lines ("your stomach churns around X…"),
- a live stomach-guest state line every turn/console round,
- a first-person completion per `SwallowEffect`,
- the mid-swallow abort reported from their side.

Other NPCs get the third-person spectator line. And the state machine keeps the two
worlds apart: a stomach guest is **a meal, never sex** — the guest's body parts don't
register as penetration, no climax, and the guest's own mind keeps talking (see the
swallowed-mind tools note) instead of reading its own digestion as sex.

## Spectate: the agent can eat YOU (`SwallowAgentEat`, opt-in)

The reverse. Your AI NPC gains `eat_player`: when they're within reach
(`SwallowAgentEatRange`, ~2.5 m), they swallow the local player whole — no grab needed —
and your camera is **forcibly taken over** for the rest of the cycle:

- **POV**: `SwallowAgentEatPOV`, default FirstPerson — the same eye placement the AI's
  vision uses, so you see literally what the AI sees as it plays. ThirdPerson is a
  wall-safe follow orbit behind them.
- Your input is suppressed (game chat, menus and the abort command still work); your
  body parks inside theirs (pinned, hidden — the mod never destroys the player body),
  and an always-on HUD line explains where you are and how you get out.
- **Return**: `Egg` — the eater lays your egg and planting it in soil puts you back on
  your feet at the hatch spot with the egg's mixed genes (persona/saves untouched; the
  duplicate plant-spawned body is despawned). `BellyEgg` — same, but the egg waits for
  the eater's nest lay. `Grow`/`Destroy` — there is no hatch: you watch until you type
  **"swallow abort"** in game chat (released alive and whole) or quit to menu.
- If the eater's body is destroyed mid-watch you're put back down at their last spot;
  scene changes and DLL restarts release cleanly.

## Swallowed minds can talk but not act

While a body is inside a stomach (any swallow flow), the mod's body-attached commands
(`get`/`grab`, `drop`, `throw`, `use`, `cd`, `run`, `jump`, …, and cumming) are refused
with an honest explanation; speech (`echo`/`emote`), memory, goals/plan and read-only
polling still work — the swallowed one can talk to their keeper mid-digest. The state
anchor says the situation every turn, so a swallowed model isn't left guessing why
nothing moves.

## Config — all in `[LittleTreats]`, all OFF unless stated

| Key | Default | What it does |
|-----|---------|--------------|
| `SwallowEnabled` | false | the player-side swallow gesture + digestion flow |
| `SwallowDigestSeconds` | 45 | digest duration |
| `SwallowLookUpDot` | ≈0.94 | how steep the drop-look-up must be to trigger |
| `SwallowForceFeeding` | true | the head-shove release path |
| `SwallowDestination` | Off | entry hole + master switch (F7: off/mouth/anal/pussy/dick/nipple); Off disarms all release triggers; Dick anchors on the eater's own groin |
| `SwallowEffect` | Egg | Egg / BellyEgg / Grow / Destroy |
| `SwallowGrowLarger` / `SwallowGrowStrength` | on | Grow-mode takes |
| `SwallowGrowSex` | on | Grow-mode curves/groin take |
| `SwallowGrowPercent` | 10 | % of the meal's genes taken |
| `SwallowAgentEat` | false | the spectate flow (`eat_player`) |
| `SwallowAgentEatRange` | 2.5 | eat_player reach (m) |
| `SwallowAgentEatPOV` | FirstPerson | FirstPerson / ThirdPerson |

"## Multiplayer (both clients run the mod)

The vore flows run over the game's own RPC channel, so they **work across
clients**: the ownership transfer + pin/hide/restores are PUN RPCs — a remote
victim's client sees all of it visually.

- **Player eats another player** (`[LittleTreats] SwallowPlayerVictims`, default OFF):
  the grief guard lifts for player-into-player — the eat gesture and force-feeding
  both allow player victims and player receivers. A swallowed REMOTE player is
  **never destroyed**: at digest end their body is released at the eater's feet and
  their ownership returned — no body loss either way.
- **Narration travels**: the mod carries swallow events on a photon event channel
  (begin / digest milestones / end) so the remote victim's mod shows what's
  happening on THEIR machine, both to their console and to the NPCs their client
  owns.
- **Aborts travel both ways**: a swallowed remote player types "swallow abort" in
  THEIR chat — their mod sends the abort event; the EATER's mod runs the same
  honest abort locally. Nobody is stuck in someone else's stomach.
- Unmodded players are unaffected: with `SwallowPlayerVictims` off (default), all
  player-body guards hold exactly as before.

## Handshake: sharing duty + surviving crashes

`[Multiplayer] Handshake` (default ON): every modded client publishes presence,
claimed-body viewIds and a beat into their own photon player properties every ~4 s
— the room's player properties ARE the handshake channel, nothing external:

- **Shared duty**: each client claims up to its *share* of AI kobolds
  (`ceil(MaxNPCs / moddedClients)`) instead of every client claiming everything —
  no doubled LLM load in a room.
- **Crash mitigation**: a peer whose beat is stale but is still in the room (a
  frozen/hung client) has its claimed bodies marked reclaimable — the survivors'
  claim scan adopts the orphaned bodies. Human player bodies are never
  reclaimable (their viewIds live outside the claimed lists), so a human's body
  can't be possessed by a peer's LLM under any circumstance.

## Tests

`tests/test_swallow.cs` (57 asserts, pure `src/SwallowLogic.cs`): trigger rules,
conversion phases, the abort command, fingerprint format — the rebirth match is
fingerprint-exact, so a swallowed person always comes back as THEMSELVES.