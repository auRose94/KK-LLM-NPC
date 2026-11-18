# KK-LLM-NPC

LLM-driven Kobold NPCs for [KoboldKare](https://store.steampowered.com/app/1102930/KoboldKare/).

Possesses unoccupied AI kobolds in the world and plays them through an OpenAI-compatible
chat-completion endpoint: each NPC perceives, plans, moves, interacts, talks in chat,
and reacts to the world continuously, as if a player was at the wheel.

## Requirements

- **BepInEx 5.4x** — pre-bundled in every release zip (BepInEx 5.4.22.0), so you
  don't install it separately.
- **An OpenAI-compatible chat-completions server** — LM Studio, Ollama, llama.cpp,
  KoboldCpp, vLLM — serving whatever LLM should play the NPC. Where it runs is
  your call, three good options:
  - **A second GPU in the same machine** — the game and the server each get one.
  - **A second machine on your network** with a GPU — point `[LLM] Endpoint` at it
    (`http://<its-LAN-address>:<port>/v1/chat/completions`); set `[LLM] ApiKey` if it
    needs one. This is the roomy option: game performance untouched, full-size models.
  - **A cloud hosting service** — any hosted OpenAI-compatible endpoint (a rented GPU
    VM running vLLM/llama.cpp, LM Studio's remote, or a commercial inference host)
    works the same way: set `[LLM] Endpoint` (+ `ApiKey`) to its URL. Latency is the
    cost — the closer (LAN/nearest region), the livelier the NPCs.
  There is no "local-only" lock: the mod works with any endpoint it can reach.
- **(Optional) vision**: a multimodal model for the vision pass / first-person images —
  either the same server ([VisionModel] left blank uses it) or a separate endpoint
  (a dedicated small vision model on the second GPU/machine/cloud, if the action model
  is text-only).
- The game itself needs a GPU as usual; sharing the one GPU with a small LLM (4–9B,
  quantized) works but both run slower — that's what the second-GPU/second-machine/
  cloud options are for.

## What it does

Each possessed kobold becomes an autonomous agent. Every "think" tick it gets:

- **Possession pool** — every AI kobold the world produces gets an agent (a body that appears later, e.g. from a delivery or breeding, is claimed as it shows up). With `SpawnMissingKobolds` on, it can also spawn extra AI kobolds near the player until `MaxNPCs` is met.
- **Pickup awareness** — knows when its body is grabbed, tracks a position trail while carried, and reports where it was moved (thrown is flagged as startling); it also auto-recovers if ragdolled next to the mail/sell machine and will never interact with that machine, since climbing in starts the sell timer.
  - **Swallow-vore & spectate (opt-in, in [LittleTreats], all OFF by default)** — two opt-in flows, each a complete loop: the player can swallow a kobold whole (digestion narrated to a real mind, ending per `SwallowEffect`: egg / belly-egg / absorb / destroy, with a no-cheat chat abort and force-feeding variant), and an agent can `eat_player` — taking over your camera to spectate its whole game session from inside until you hatch or abort. Grief guards keep player bodies safe everywhere except the explicit spectate act; a swallowed person is always kept alive as a mind and restored on their terms. Swallows are double-gated: **SwallowDestination** (F7 in-game, WebUI settings) picks where a held person goes in — off/mouth/anal/pussy/nipple — and OFF (the default) disarms the release triggers entirely, so a precision-grabber bump mid-walk can never start a digestion by accident. **Full mechanics, config and tests: [docs/VORE.md](docs/VORE.md).**
- **Adult content by default, consent by choice** — the base prompt asks for nothing beyond the game's own (already adult, already fantastical) content: kobolds hatch as grown adults and the game's other mechanics — fantastical bodies, surviving falls, optional eating and sleeping, unrealistic intimacy systems — read plainly as fantasy with no real-world victim. The default prompt never pushes past that. What the **[NonConsent] system** (below) is for: not every model agrees to play the NSFW/kink content, some refuse it, moralise about it, or take the request personally. The mod *detects* a model declining the adult content, flags it, and — when `NonConsent.SFWOnly` is on — quietly steers it back to ordinary (non-sexual) roleplay instead of repeating an NSFW ask a model has already declined. Respecting a model's refusal is the point; by default a flagged model is simply *observed* in the browser, not changed.
- **Multiplayer handshake (mod-to-mod)** — with two modded clients in one room, the room's own player properties carry presence + claimed-body lists + beats: the kobold-LLM duty is SHARED (each client claims its share, no doubling), and a frozen peer's bodies become reclaimable by the survivor — the crash mitigation. See [docs/VORE.md](docs/VORE.md) for the multiplayer vore channel the same infrastructure carries.
- **Station departures are events** — a per-second occupancy watcher narrates "[station] the player left the bed station — you are on it alone now" when a partner walks away from a shared station within perception range, so the one left behind notices without re-diffing slot text.

- **Frustum raycasts** around the head (down/level/up rows) — what's where, with distances.
- **8-direction clearance** — which way is open, what's a wall/sill/window.
- **Ground** — supported / step / sill / ledge / drop distance.
- **Nearby** — kobolds, the player, and usable stations within 14m, with bearing ("front-right") and category (`bed`/`nest`/`play`/...).
- **Whole-map pathfinding** — background A* on a cached 3D walkability grid; time-budgeted with per-frame expansion caps so the main thread never hitches.
- **First-person JPEG** (optional, on a bump / after turns / periodically).
- **Body** — equipment (penis / penetrables), energy, horniness (with trend arrow), egg amount, stimulation, penetration state.
- **State** — in-station, being penetrated (depth/hole/thrust per penetrator), penetrating someone.
- **Reagent events** — drank/sprayed/metabolized ("drank Water 5ml") when the belly changes.
- **Player chat** — what you typed into the game chat; it answers.
- **History** — its last 10 actions with outcomes, recent goals, and a `facts` list it extends via
  `remember` and prunes via `forget` (stale facts also decay out over time).

By default the NPC sits at a **bash-like console** instead of receiving all of that as a
JSON blob every tick: it *polls* the game with short shell commands, reads the output, then
sends its next command (command → data → read → command, in a stream). It asks for exactly
what it needs — `ls` for what's nearby, `status` for a one-line snapshot, `cat
facts|goal|history` for memory — so the payload stays small, works on small local models, and
never overloads the context. It **speaks with `echo`**. (Set `[Console] Enabled=false` to
restore the legacy push-perception / `act`-JSON loop.)

### Console commands (bash-like)

The model types these at the shell prompt, one per line, and reads each result before the
next. Bash-style names are chosen so even small models already know them from shell data.
Legacy `act` names still work as aliases (`say`→`echo`, `go_to`→`cd`, `interact`→`use`,
`walk`→`run`, `exit_station`→`exit`, `grab`→`get`, `set_goal`→`goal`, …).

**Tiered command surface.** Smaller models get a lean ~15-command core in the system
prompt every turn (READ / LOOK / MOVE / SPEAK / GOALS / LEARN); the full ~60-command
table is one command away via `help all` or `man <topic>`. This keeps the small-model
context budget light where confusion actually happens, instead of dumping the whole
table on every model.

**Failure escalation.** When a command fails or a model repeats the same failing command,
the terminal escalates: the stuck detector escalates its hint (try different approaches /
`cat places` / `ls` / `help` / `man`), and on a hard cap it ends the turn so the model
can't burn the whole cycle repeating itself. Unknown commands get an escalating "type
`help`" nudge instead of silence.

| Group | Commands |
|-------|----------|
| **Read (poll the game)** | `ls` (nearby), `ps` (who's around — players AND fellow agent NPCs), `pwd` (where + facing in compass words), `whoami` (me), `cat facts\|goal\|chat\|needs\|history\|stations\|map\|body\|hold\|player\|quest`, `status` (one-line snapshot incl. coins + awake time + holding + quest), `find <place\|deg>`, `sonar [filter]` (north-up ASCII map — never rotates), `look left\|right\|up\|down\|around\|<deg>` |
| **Act** | `echo <words>` (**speak**), `emote <what you do>` (**body language**, renders `*like this*`), `cd <place\|id:N>`, `use <thing\|id:N>`, `run [secs] [left\|right]`, `turn [deg]`, `jump`, `crouch [0..1]`, `exit`, `get` (grab what's ~1m in front of the face — result names it), `drop`, `throw` (hurl held item / spray the bucket / fire tools in view direction), `hold` (what's in your hands), `follow on\|off`, `stop`, `sleep [secs]` (ends the turn; with secs it *pauses* that long — chat/events wake it early) |
| **Memory & goals** | `remember <fact>`, `forget <fact>`, `goal <text>\|done\|drop`, `plan <step>\|\|next\|clear` (goal's step ladder — see Planning) |
| **Economy** | `buy [name\|id:N]` — purchase whatever's within reach: machine contracts, the kobold dispenser, shop items. The NPC spends its OWN coins (shown in status/economy perception). |
| **Module tools** | `thrust`, `erection`, `mount`, `unmount`, `orgasm`, `plant`, `water`, `harvest`, `plant_egg`, `feed_blender`, `grind`, `rename` (positional args: `target`, `amount`) |
| **Misc** | `screenshot` (on-demand vision), `skill add <name> | <step> ; <step>` (reusable procedure — see Skills), `report <what's broken>` (files a dev bug note — AI feedback), `help` (list commands), `clear` |

### Speech adaptation

- **Response blocks** — an RP model can simply write its dialogue/actions as a roleplay
  block; the mod detects the block shape (quoted dialogue, `*actions*`, an {{user}}-style
  address) and sends it to the chat window verbatim — no `echo` required. Commands and a
  block can share one reply. Solo-voice (no one spoke, nobody around) is throttled to
  about one block per 90 s so it can't monologue, and identical repeats keep ending the
  turn instead of stacking.

- **RP placeholder heads** — SillyTavern-trained models speak in `{{user}}`/`{{char}}`;
  say/emote expand them at the moment of speaking: the live engagement partner first,
  then the nearest person within 14 m (player avatar before sibling NPCs), then the host
  player. `{{char}}` = the NPC's own name; the placeholder stays literal only when
  nobody at all is known.

### Perception keys

| Key | Description |
|-----|-------------|
| `body_control` | erection (0–1), stimulation, penetration state, hip animation status |
| `farm` | nearby plants (name, growing, watered, stage, produce) |
| `cooking` | nearby blenders/grinders, held seed/watering can flags |
| `identity` | orientation, trans status, persona, gender, pronouns |
| `economy` | the body's own coins + nearby purchasables (name/kind/cost/distance) |
| `mail_atm` | nearby mailbox/ATM machines |
| `swap_recent` | last 10 body-swap events |
| `my_swap` | whether the NPC is currently in a body it doesn't own |
| `holding` | what's in its own grabber: name + kind (fruit/seed/water bucket/tool/**person**) + distance + a warning line when it's holding a person |
| `player_activity` | the host player watched LIVE (no storage): what they're doing (station/heading/speed), what they hold, distance/bearing — how the AI "just knows" another player's business |
| `quest` | the game's objective letter scroll (DragonMail): title with live progress, text, stars earned, mail_waiting — letters arrive at the mailbox; the AI can `use mailbox` too |
| (facts) `player_wants:` | the player's latest request, auto-remembered when said — the durable copy of the player's live ask |

## Behavior highlights

**Walks by default** and obstacle-aware; whole-map A* pathfinding on a background
thread; remembers facts and lets stale ones decay; farms, cooks and hauls; names
itself; talks in game chat; sleeps when told; keeps `skill` procedures that even
survive being swallowed and absorbed. The long version — every behavior, with the
reasoning behind it — lives in [docs/BEHAVIOR.md](docs/BEHAVIOR.md).

## Install

Each release zip is a **complete drop-in** — BepInEx 5.4.22.0 is pre-bundled, so you
don't install it separately.

1. Get the prebuilt zip for your platform from the GitHub **Releases** page
   (`KKLLMNPC-<version>_win.zip` / `_linux.zip` / `_macos.zip`; the payload is the same
   IL plugin on every OS — the platforms differ only in which BepInEx build your
   KoboldKare host needs, and each zip carries an `INSTALL-<platform>.md`).
2. **Unzip it over your `KoboldKare` folder.** It drops a full `BepInEx/` tree
   (core, managed, doorstop) plus `BepInEx/plugins/` (the mod DLL + prompt files) into
   the game root.
3. **Launch the game.** First run writes `BepInEx/config/com.kk.llmnpc.cfg`; the debug
   console opens at `http://127.0.0.1:8338/`.

You also need an OpenAI-compatible chat server reachable from the game machine (Ollama,
LM Studio, llama.cpp, vLLM, a hosted endpoint — see the AI server section below).

### Building from source

`./build.sh` compiles the plugin against your game install and deploys
(`KKLLMNPC.dll` + the prompt files) into `BepInEx/plugins/`; `./make_release.sh` builds
the per-platform release zips (with BepInEx bundled) + release notes into `dist/`.

```bash
./build.sh                       # compile + deploy into your game's BepInEx/plugins/
./make_release.sh                # package dist/KKLLMNPC-<version>_*.zip (BepInEx bundled)
# offline release (no BepInEx download):
BUNDLE_BEPINEX=0 ./make_release.sh --no-build
```

`build.sh` also ships the prompt files (`system_prompt_*.txt`) into `BepInEx/plugins/`.
There is no build-time model download — the external host holds its own models locally
(see below). On Windows, see the PowerShell build notes under **Build**.

## The AI server (external host)

The mod talks to any OpenAI-compatible chat-completions endpoint via `[LLM].Endpoint`
(see the Requirements list above). For a local box the recommended host is
**Ollama**, which ships its own model library, an `/api/chat` + `/v1/chat/completions`
interface, and `--jinja` chat-template support so fine-tuned bases keep their base
template. The mod **adopts** the running Ollama as its live endpoint by default (v1.2.3)
instead of the hard-coded `[LLM].Endpoint`, and hot-swaps the served model without a
config edit. With no Ollama running it just idles, so a fresh install is unaffected.

**Bring-up on a fresh machine:**

```bash
ollama pull tev1:4b             # pull a model (default host picks one)
ollama run  tev1:4b             # sanity check it runs; Ctrl-D to quit
./build.sh                      # dll + prompt files into BepInEx/plugins/
```

Everything lives in the WebUI's **AI server** tab (http://127.0.0.1:8338): the status
line shows whether Ollama has been adopted as the live endpoint (and its health,
context length, current model); **disable adoption** makes the mod fall back to
`[LLM].Endpoint` while Ollama stays running, and **refresh models** re-reads what's
installed. The **installed models** picker lists every local model (`ollama pull` to
add one) and **use this model** hot-swaps the live served model — it also writes
`[LLM].Model`, so it takes effect on the next turn (the model must be running in Ollama
to serve).

Adoption is on by default and reversible: `[LLM].Endpoint` is left untouched, so
pointing back at any other endpoint is a one-line config change. Steam Proton runs Ollama the same way
it runs any local server (native python outside Proton, or Wine inside it); the mod
adopts whatever answers its endpoint.

## Config (per instance)

Configuration is documented in full — every section, key, default and range — in
[docs/CONFIG.md](docs/CONFIG.md). The shape of it:

- **`[LLM]`** — the planner: endpoint/model, think cadence, token + image budgets,
  the `ParallelCalls` concurrency gate
- **`[Console]`** — the bash-like REPL the agents drive (default ON; `Enabled=false`
  restores the legacy push-perception loop)
- **`[Ollama]`** — external-host adoption + the keep-alive heartbeat (default `30m`)
  + optional **replicas** (`Replicas`, default 1): N loaded instances of the model,
  one per agent — parallel turns with a full context window each
- **`[LMStudio]`** — the same adoption for LM Studio: live model list with load state
  and real context windows, a TTL keep-warm heartbeat, a picker that *loads*
  the model for you, and the same **replicas** support (`model:2`, `model:3`… —
  one loaded instance per agent; Ollama wins when both are running)
- **`[Senses]` / `[Vision]` / `[Movement]`** — perception and navigation knobs
  (incl. the **3D nav core**: volumetric sparse-voxel pathfinding built from the
  cached world map — caves, shafts and over/under floors route properly)
- **`[Memory]` / `[Farming]` / `[Autonomy]`** — long-run behavior
- **`[LittleTreats]`** — opt-in oddities (nothing on by default), plus
  `[Multiplayer]`, `[Web]`, `[Debug]`, `[DataRecord]`

Sanity-check a live config file against the documented ranges (it also flags
keys the docs no longer mention, in either direction):

```bash
tools/lint_config.py BepInEx/config/com.kk.llmnpc.cfg
```
## Build

### Linux / macOS

```bash
./build.sh        # build + deploy instance
# Or specify your game path:
KOBOLDKARE_DIR=/path/to/KoboldKare ./build.sh
```

### Windows (PowerShell)

The `build.sh` script requires bash. On Windows, use:

```powershell
# Option 1: Git Bash / WSL
bash build.sh

# Option 2: Manual mcs compilation
mcs -target:library -out:KKLLMNPC.dll src/*.cs \
  -r:"C:\path\to\KoboldKare\BepInEx\core\BepInEx.dll" \
  -r:"C:\path\to\KoboldKare\BepInEx\core\0Harmony.dll" \
  -r:"C:\path\to\KoboldKare\KoboldKare_Data\Managed\*.dll"
```

## Tests

All suites are pure C# + mono, zero external dependencies — 23 of them plus a
python config-linter selftest, wired and audited by one script (CI runs it on
every branch push, nightly with the soak scaled 50x):

```bash
./run_tests.sh                 # everything
./run_tests.sh json soak       # subset by suite name (exact or prefix)
```

Per-suite descriptions live in [tests/README.md](tests/README.md).

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture overview.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

MIT — see [LICENSE.txt](LICENSE.txt).

## Documentation

- [CONFIG.md](docs/CONFIG.md) — Complete configuration reference
- [BEHAVIOR.md](docs/BEHAVIOR.md) — Every agent behavior in detail, with the design reasoning
- [VORE.md](docs/VORE.md) — The full swallow-vore / spectate mechanics (opt-in, all OFF by default)
- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — Architecture overview
- [MODELS.md](docs/MODELS.md) — Ranked model compatibility list + benchmark plan
- [training/README.md](training/README.md) — Fine-tune a game-native model: record your own sessions (`[DataRecord]`), extract the dataset, eval + train (Qwen / Gemma4-QAT dual-track)
- [CHANGELOG.md](CHANGELOG.md) — Release history (older rounds: [changelog-archive.md](docs/changelog-archive.md))