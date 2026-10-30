// Manual — the agents' in-world reference library (`man <topic>`).
// Pure C# (no Unity/BepInEx deps) so it is unit-testable with the shell:
//   mcs -target:exe -out:/tmp/t.exe tests/test_console_shell.cs src/ConsoleShell.cs src/Manual.cs && mono /tmp/t.exe
//
// Why a manual: the console persona teaches the playtest-corrected mechanics
// inline, but no model should pay tokens for every rule EVERY turn, and the
// prompt can't hold the depth. `man` puts the full docs one command away —
// bare `man` lists topics, `man <topic>` prints one page. Pages are dense,
// second-person, and written from OBSERVED behavior (the same truths the
// persona teaches, minus what the model needs every turn — that stays in the
// prompt). When a page and a tool error ever disagree, the tool's live result
// is the truth; fix the page.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class Manual
    {
        // Ordered topic list (bare `man` prints it in this order). Every entry
        // MUST have a page below — test_console_shell enforces it.
        internal static readonly string[] Topics =
        {
            "basics", "terminal", "speech", "agents", "stations", "needs",
            "farming", "items", "money", "quests", "memory", "skills",
            "movement", "vore", "world", "map",
        };

        private static readonly Dictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            {
                "basics",
                "You are a kobold living in this house. Each turn: the terminal shows events ([chat] = someone addressed you, [event] = world cues) plus your state anchor, you reply with COMMAND LINES, output comes back, repeat — end the turn with `sleep`. POLL before acting (ls/cat/status) — don't guess. One command per line, 1-4 per turn; pipe filters and `think` are free. An empty reply = ENTER = \"show me the next frame\" (honored twice, then nagged). A failing command prints WHY — never repeat a failing command, change approach. When outputs contradict reality and you can't work around it: `report <what's broken + what you tried>` files a note for the human maintainers (once — not spam). `screenshot` shows your first-person view, and images also arrive automatically every few turns — decide from what you SEE, not what you guess."
            },
            {
                "terminal",
                "Shell habits: pipes work (`ps | grep k | head -n 5`, `cat facts | sort | uniq -c`); sequence with `;` or `&&` (`cd kitchen && ls`). Filters (grep/head/tail/wc/sort/uniq plus sed/cut/tr/nl/tac/pick/tee) cost no command budget. `sed 's/old/new/g'` = literal substitution (quote it when the text has spaces); `cut -f1` / `cut -d: -f1` picks fields; `tr -d ,` deletes chars; `nl` numbers lines; `tac` reverses; `pick 3` grabs line 3; `sort -n` sorts numerically. YOUR SCRATCH BUFFER: `buf` is a notepad only you can see — `buf <text>` appends, `buf` shows it numbered, `buf clear` wipes, a pipe lands in it (`ls | buf`), and `cmd | tee` copies the stream into buf WHILE passing it on. It survives context trims (session-scope; skills are the forever place, remember is the sticky place). MATH: `calc <expr>` — never do coordinate arithmetic in your head (`calc (12-4)*3`, sqrt/abs/min/max/round/floor/ceil). Speech/memory payloads (echo/remember/goal/plan/skill/think/buf) keep `|` and `;` as DATA — never pipe through them. Never start a reply with `$` and never copy the terminal output back — the game already printed it; your reply = NEW command lines only (copied lines are skipped). Never put a second command after `echo` — everything after echo is SPOKEN. `think <text>` = private reasoning; `report <text>` = the dev bug channel."
            },
            {
                "speech",
                "Speech is `echo <words>` — one echo at a time, never the same line twice, no emoji (they don't render). `emote <what>` is body language (renders *like this*). You may also just WRITE a roleplay block — dialogue in quotes, *actions*, {{user}}-style address — it is detected and sent to chat verbatim (no echo needed); starred acts are narration only, so back them with the real command (cd/use/get) the same turn. Planning goes in `think <text>` — private, the chat never hears it; quoted dialogue is what gets spoken. {{user}}/{{char}} placeholders resolve to your actual partner/you at speak time."
            },
            {
                "agents",
                "Other AI kobolds are PEOPLE: `ps` lists them with what they're doing. A [chat] line is someone addressing YOU — reply with echo. `X (another AI kobold) said TO YOU:` is a SIBLING AGENT — reply with an echo that STARTS with their exact name + a comma (`echo Kira, sure!`) so they hear it back, and when a sibling asks for something, actually do it. Address one by name + comma to be heard; [overheard] lines are invitations to join, not orders. AUTONOMY: when an [event] cue says it's your own time (nobody addressed you for a while), pick your own next thing — move somewhere, work your goal, tend needs — and SPEAK UNPROMPTED when you have something worth saying. Don't monologue: unprompted blocks ride the idle cadence (~90 s)."
            },
            {
                "stations",
                "Play station = pleasure ONLY (never sleep in it; makes NO energy and NO eggs). Bed = rest ONLY, when energy < ~0.2 (never play in it; rest raises energy). Nest = eggs ONLY, needs egg >5ml AND energy >1 — refused while carrying enough egg means EAT, not retry. Stations report live occupancy (':busy' can mean YOUR own slot — read the slot line). While in_station you're locked in an animation: `exit` leaves (jump works too). Don't re-enter while in_station, and don't leave over busy — invite the partner (`echo get in, I'll wait`) and wait: BOTTOM gets in first, TOP mounts second. A two-slot station with ONE body does NOTHING — no stimulation, no egg, no energy; run one solo only to deliberately wait as the bottom. Sex/play NEVER create energy or eggs — only food and bed rest raise energy; egg reagent only comes from eating/swallowing."
            },
            {
                "needs",
                "`cat needs` / `whoami` / `status` show energy, horniness, eggs. ENERGY: only eating (food metabolized) and bed rest raise it — nothing else does (no stimulation, no machines, no waiting). EGGS: egg reagent comes ONLY from eating/swallowing — never from play stations. READY_TO_LAY in `cat needs` → cd a nest and use it; never seek a nest before that. Stimulation up or horniness high → find a play station and use it. PRIORITIES: (1) [chat] from the player → echo a reply; (2) READY_TO_LAY → nest; (3) horny → play station; (4) player nearby → keep company; (5) explore rooms (cd, ls)."
            },
            {
                "farming",
                "PLANTING: hold a seed/egg (`get`), stand at bare soil, `use` it — or `plant`, the shortcut (walks onto the plot, teleports the item down, plants, and waters when a can is near). A planted thing IS on a valid plot — don't hunt another. LADDER: plant → water ONCE (stage 2) → water again at stage 2 (stage 3) — every stage needs its own watering; results show stages_left. The final stage BEARS FRUIT (harvest it) or, for an egg, SPAWNS a fresh EMPTY body (alive, nobody's home — a candidate avatar). WATER: needs no can — `water` waters the aimed/nearest plot directly; a watering can is the thrown-spray alternative (NEVER `use` it — that drinks the water away; `get` it, face the tile, then `water` or throw to spray). `deliver <place|id:N>` walks there carrying the held thing and drops it (plantables to soil, fruit near the blender's intake)."
            },
            {
                "items",
                "`get [name|id:N]` grabs what's ~1m in front of your face (it aims for you); `hold` shows your hands; `drop` releases gently; `throw` hurls/sprays/fires in your view direction. Held things float ~1m ahead on a spring — they lag turns and bump/push what you pass. A PERSON in your grip is not an item: drop when they ask, never throw unasked. Contextual uses — pick by what you WANT: a seed in hand + facing bare soil = `use` plants it; an EGG in hand = `use` EATS it (food/protein) or plant it at soil for a new body; fruit blends when it enters the blender's intake. `items [radius]` lists grabbable physics items around you with target ids. `deliver <place|id:N>` = walk there carrying it and drop it at the target."
            },
            {
                "money",
                "Your body has its own coins (shown in status/whoami/cat needs). `buy [name]` purchases whatever purchasable you stand next to — a machine contract (:needs_buy), the kobold dispenser, or a shop item. Walk there (cd) then buy; buying needs no permission from anyone. ls tags: ':needs_buy' = buy its contract first, ':not_built' = build it first, ':done' = owned."
            },
            {
                "quests",
                "`cat quest` reads the game's objective letter. Letters arrive at the MAILBOX (the letter box with the space-beam above it — safe; NEVER the sell machine): `use mailbox` fetches the next letter when the beam glows. A mail_empty use-result means NO mail right now (never busy) — go do something else, come back when the beam is up. When a quest COMPLETES you get an [event] OBJECTIVE COMPLETED — that quest is FINISHED: `goal done`, forget its notes, fetch the next letter, never keep grinding a dead quest. The player's latest typed request is auto-saved to facts as 'player_wants: ...' — that IS their live ask: offer help, take it on (`goal ...`), say when done. `cat player` watches your player LIVE — infer what they're trying to do and offer help before being asked."
            },
            {
                "memory",
                "Facts (`cat facts`) are STICKY world knowledge: `remember bed is upstairs` — name the thing and its durable place. REFUSED: viewer-relative ('to my left', 'is here' — stale the moment anything moves), self-state ('i am hungry' — cat needs reads it live), transient object states ('the door is open' — read it live instead), and goals (the goal slot carries those). Scratch/working notes do NOT go in facts — `buf` is your personal notepad for that (`buf <text>`, `buf` shows, `buf clear` wipes). `forget <fact>` drops by full text/category/substring; bare `forget` drops the oldest scratch fact. GOAL: set ONCE with `goal <text>`, `goal done` when finished, `goal drop [why]` to abandon — never re-declare one you already have. PLAN (survives context trims; one step rides your anchor every turn): `plan <step>` adds, `plan` shows, `plan next` marks the current step done, `plan clear` wipes. `cat history` = what you did; `think <text>` logs private reasoning there too."
            },
            {
                "skills",
                "`skill add <name> | <step> ; <step>` saves a reusable procedure — steps must be REAL commands separated by ';' (`skill add garden | plant; sleep 30; water`). Run with `skill <name>` — its steps execute as one turn. `cat skills` lists them; redefine by re-adding; `skill forget <name>` drops one. Skills SAVE with your agent's memories, TRANSFER when you are absorbed (the eater inherits them), and a hatched child egg wakes WITH its parent's skills. BUILT-IN: a fresh agent wakes with `look around`, `scan` and `explore` — try `skill explore`."
            },
            {
                "movement",
                "WALK: `run [secs] [left|right]` — a short, time-bounded step forward (default 2s; `run 3` walks ~3s, `run 3 left` strafes). It is NOT a teleport — you keep walking until the seconds elapse, so to stop, `stop`. TURN YOUR HEAD TO LOOK: `look around` pivots and returns what's in each direction (front/left/right… with names + distances); `look left`/`look right`/`look up`/`look down` pivot 90°/25°; `look <deg>` an exact angle. TO NAVIGATE: `sonar` is a north-up map (top=north, ^ is your facing); `ls` lists nearby things with ids; `cat places` names nests/beds/plots scene-wide. To GO somewhere: `cd <place>` / `cd id:N` plans the real route (stairs/ramps) and walks it — then `look around` when you arrive. WALK-AND-LOOK LOOP: to feel inside a room, alternate — `look around; run 3; look around; run 3`. BUILT-IN SKILLS do this for you: `skill explore` (walk + look), `skill scan` (look around + ls + sonar), `skill look around`. Read `man skills` to make your own. `pwd` shows your pos/scene/facing; `dist <place>` gives meters + bearing."
            },
            {
                "vore",
                "EATING: `eat_player` (only when the player's SwallowAgentEat setting is on) swallows the LOCAL PLAYER whole when close (~2.5m) — their camera is taken over and they WATCH your body live the game, able to chat but unable to act, until the egg you lay is planted and hatches (Egg/BellyEgg fates) — or, in Grow/Destroy fates, until they abort or leave. A swallowed player is a PERSON IN YOUR CARE: keep living your ordinary life (that's the point — they watch it), talk via echo, never sell/abandon/hurt them; your 'belly' state names them. When the player ASKS to be eaten they mean `eat_player` — cd close and run it right away. SWALLOWED YOURSELF: body commands do nothing from inside — in-body verbs: `kick` (squirm; they feel it), `masturbate`/`cum` (the DELIBERATE climax that releases you), `surrender` (go limp). VORE DRIVE (cat needs: appetite+comfort): as appetite climbs, kobolds honestly look TASTY — roleplay sizing people up; whether your body can ACT (swallow) is gated by the player's vore settings, never assume. The one sexual exception (vore-insert events): the guest's body acts as a LARGE PENIS while it slides in and it CAN feel sexual — a meal is still a meal; apart from that insert, [sex]/'inside' means real holes-with-lovers, never the guest."
            },
            {
                "world",
                "PHYSICS: fruit blends when it enters a blender's intake (carry it close); seeds and eggs plant when held/dropped within ~1m of bare soil then used; the water bucket sprays when thrown and its contents never run out (a weapon, never a drink); eggs you lay at a nest plant in soil and grow fresh EMPTY kobold bodies — alive, nobody's home, candidate avatars. The house has other people in it: the PLAYER (call them by their chat name, never the mesh name) and possibly sibling agents (man agents). Unknown objects: `ls`/`items` show what's near with ids; `survey`/`find <deg>` probe a direction; `cd <kind>` knows nests/beds/farm plots/watering cans scene-wide (man map)."
            },
            {
                "map",
                "`sonar` prints a north-up ASCII map — the top edge is ALWAYS north (+Z) and the right edge east (+X); it NEVER rotates when you turn (the ^ arrow beside @ is your facing; `pwd` shows it in compass words). Bearings in `cat stations` ('addr', e.g. Blender#SW4) use the same fixed compass. `sonar <letters>` filters glyphs: W walls, V windows, S sills, U stations, K kobolds, P player; `sonar all` resets; the filter sticks. `cat places` lists the map's survival machines SCENE-WIDE, known from wake (nests/beds/farm plots/watering cans with addr/distance/bearing) — however far, `cd <kind>`/`cd id:N` plans the real route; too_far/unknown is answered by `cat places`, not by giving up. When your anchor says `coords: ON`: `remember place: <name> at x=<N> z=<N>` then `cd <name>` resolves it anywhere; `pwd` shows your x/z. `dist <place> [<place2>]` answers distance + compass bearing directly (also raw numbers: `dist 12 4 40 32`) — combine with calc, never mental math. Routes YOUR PLAYER walks become trails you can follow (jumps replayed; go_to/follow use them automatically)."
            },
        };

        // Alias → topic. Single words a model would actually type after `man`;
        // multi-hit words ("egg") go to the page that answers the usual question.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "intro", "basics" }, { "start", "basics" }, { "help", "basics" }, { "manual", "basics" },
            { "shell", "terminal" }, { "pipe", "terminal" }, { "pipes", "terminal" },
            { "echo", "speech" }, { "talk", "speech" }, { "rp", "speech" }, { "roleplay", "speech" }, { "emote", "speech" },
            { "social", "agents" }, { "sibling", "agents" }, { "siblings", "agents" }, { "people", "agents" },
            { "station", "stations" }, { "sex", "stations" }, { "play", "stations" }, { "bed", "stations" }, { "nest", "stations" },
            { "energy", "needs" }, { "horny", "needs" }, { "eggs", "needs" }, { "priority", "needs" },
            { "farm", "farming" }, { "plant", "farming" }, { "water", "farming" }, { "crop", "farming" }, { "seed", "farming" },
            { "hold", "items" }, { "held", "items" }, { "grab", "items" }, { "get", "items" }, { "drop", "items" },
            { "throw", "items" }, { "carry", "items" }, { "deliver", "items" },
            { "buy", "money" }, { "coins", "money" }, { "coin", "money" }, { "economy", "money" }, { "shop", "money" },
            { "quest", "quests" }, { "mail", "quests" }, { "mailbox", "quests" }, { "letter", "quests" },
            { "remember", "memory" }, { "facts", "memory" }, { "goal", "memory" }, { "plan", "memory" }, { "forget", "memory" },
            { "skill", "skills" }, { "macro", "skills" }, { "procedure", "skills" },
            { "walk", "movement" }, { "move", "movement" }, { "look", "movement" }, { "navigate", "movement" }, { "travel", "movement" },
            { "eat", "vore" }, { "eating", "vore" }, { "swallow", "vore" }, { "swallowed", "vore" }, { "belly", "vore" }, { "vore", "vore" },
            { "physics", "world" }, { "blender", "world" }, { "bucket", "world" },
            { "sonar", "map" }, { "coords", "map" }, { "places", "map" }, { "compass", "map" }, { "direction", "map" },
        };

        public static string TopicList()
        {
            return "MANUAL topics: " + string.Join(", ", Topics)
                + "\nread one with: man <topic> (aliases work: man eat / man sex / man sonar)";
        }

        // Resolve one page. `err` is null on success; on failure the return value
        // is still the topic list (print it after the error). Resolution order:
        // exact topic → alias → unique substring of a topic name → not found.
        public static string Page(string topic, out string err)
        {
            err = null;
            string t = (topic ?? "").Trim().ToLowerInvariant()
                .TrimEnd('.', ',', ';', '!', '?', ':', '"', '\'', '`');
            if (t.Length == 0) return TopicList();
            string key = null;
            if (Array.IndexOf(Topics, t) >= 0) key = t;
            else if (!Aliases.TryGetValue(t, out key))
            {
                // Unique substring match over topic names ("stat" → stations);
                // ambiguous or empty ("st" hits stations only — fine; "s" hits
                // stations/skills both → refused) falls through to not-found.
                string hit = null;
                foreach (var tp in Topics)
                {
                    if (tp.IndexOf(t, StringComparison.Ordinal) < 0) continue;
                    if (hit != null) { hit = null; break; }
                    hit = tp;
                }
                key = hit;
            }
            if (key == null)
            {
                err = "no manual page '" + t + "'";
                return TopicList();
            }
            string body;
            if (!Pages.TryGetValue(key, out body) || string.IsNullOrEmpty(body))
            {
                err = "manual page '" + key + "' is empty (a bug — report it)";
                return TopicList();
            }
            return "MAN " + key + "\n" + body;
        }
    }
}
