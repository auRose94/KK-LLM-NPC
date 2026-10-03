// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Console REPL — bash-like shell interface to the game.
//
// Instead of pushing a big perception JSON every tick (which overloads small
// models), the model sits at a shell prompt and POLLS the game:
//
//   $ ls                      -> nearby objects with ids
//   $ cd bed                  -> travel (go_to)
//   $ echo Hi there!          -> SPEAK (say)
//
// Each think cycle: drain world events ([chat]/[event] wall messages) → prompt →
// model replies with command lines → we execute them and print terse output →
// model reads the data and sends more commands (or sleeps). The conversation is
// a rolling window, so the model keeps its own memory of what it did; `cat
// facts|goal|history` covers long-term recall.
//
// The pure parser/vocabulary lives in ConsoleShell.cs (unit-testable); this file
// is the NPCInstance partial with the handlers, event drain, prompt, and loop.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // ------------------------------------------------------------------
        // state
        // ------------------------------------------------------------------
        // Rolling conversation: [{role, content}] where content is a string or an
        // array of parts (text + image_url) for screenshot output.
        private List<Dictionary<string, object>> _consoleMsgs;
        private int _consoleProseStreak;   // consecutive replies with no command lines
        private int _consoleEmptyStreak;   // consecutive empty replies
        private string _consoleLastCmdKey; // "cmd:arg1,arg2" for stuck detection
        private int _consoleStuckCount;
        private List<string> _consoleRecentCmds; // recent command strings for similarity check
        private string _pendingShotB64;    // image to attach to the next output message
        private bool _consoleSlept;        // `sleep` ran — end the cycle
        // `sleep [secs]` wake-up time (Time.unscaledTime, 0 = awake). Skipped by the
        // LLM loop between turns; volatile because wake triggers write it from the
        // main thread (chat, world events) while the loop reads it on the LLM thread.
        private volatile float _sleepUntilTime;
        private bool _consoleAnswerDelivered = true; // ask() answer not yet shown
        private int _consoleCmdsThisCycle;
        private const int ConsoleMaxCmdsPerCycle = 12;
        private float _lastIdleChatterNudge; // talking-to-no-one nudge throttle
        // Lines of the last user (terminal) message, for transcript-echo skipping.
        private HashSet<string> _echoSet;

        // Trimmed, non-empty lines of a terminal-bearing user message.
        private static HashSet<string> LastUserLineSet(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (text == null) return set;
            foreach (var l in text.Split('\n'))
            {
                string t = l.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        // Drop the lines of a reply that verbatim-copy the last user message's terminal
        // (with or without their "$" prefix). Returns the surviving reply text and the
        // number of echoes removed. Degenerate completion-flavored models otherwise keep
        // re-executing the transcript against themselves.
        private string FilterTranscriptEcho(string reply, out int echoCount)
        {
            echoCount = 0;
            var set = _echoSet;
            if (string.IsNullOrEmpty(reply) || set == null || set.Count == 0) return reply;
            var keep = new System.Text.StringBuilder();
            foreach (var rawLine in reply.Split('\n'))
            {
                string t = rawLine.Trim();
                if (t.Length == 0) { keep.Append(rawLine).Append('\n'); continue; }
                string bare = t.TrimStart('$', '>', '#').Trim();
                if (set.Contains(t) || (bare.Length > 0 && set.Contains(bare)))
                {
                    echoCount++;
                    continue;
                }
                keep.Append(rawLine).Append('\n');
            }
            if (echoCount == 0) return reply;
            return keep.ToString();
        }

        // ------------------------------------------------------------------
        // config / mode
        // ------------------------------------------------------------------
        private bool ConsoleEnabled()
        {
            return _cfgConsoleEnabled == null || _cfgConsoleEnabled.Value;
        }

        // ------------------------------------------------------------------
        // conversation management
        // ------------------------------------------------------------------
        private void InitConsoleConversation()
        {
            EnsureModuleCommandsRegistered(); // module tools (thrust, plant, ...) land in the prompt's command list
            _consoleMsgs = new List<Dictionary<string, object>>();
            AddConsoleMsg("system", ConsoleSystemPrompt());
            AddConsoleMsg("user", BootLine());
            _consoleProseStreak = 0;
            _consoleEmptyStreak = 0;
            _consoleLastCmdKey = null;
            _consoleStuckCount = 0;
            _consoleRecentCmds = new List<string>();
            Logger.LogInfo("[" + MyName() + "] console initialized (bash-like REPL mode)");
        }

        private void AddConsoleMsg(string role, object content)
        {
            _consoleMsgs.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = content });
            _consoleVersion++; // mind-viewer overlay refreshes off this
        }

        // Transcript copy for the overlay's Mind tab ("what did it type, what did
        // it get back"). _consoleMsgs mutates on the LLM thread with no lock, so the
        // copy is a defensive snapshot of the reference: worst case a message lands
        // in the copy one frame late — never a torn iteration.
        internal List<string> ConsoleTranscriptCopy()
        {
            var outp = new List<string>();
            try
            {
                var msgs = _consoleMsgs;
                if (msgs == null) return outp;
                foreach (var m in msgs)
                {
                    string role;
                    try { role = (m.TryGetValue("role", out var roleObj) ? roleObj as string : null) ?? "?"; } catch (Exception) { role = "?"; }
                    string body = ConsoleMsgText(m);
                    if (body.Length > 96) body = body.Substring(0, 96) + "…";
                    outp.Add(role + "| " + body.Replace("\n", " ⏎ "));
                }
            }
            catch (Exception) { }
            return outp;
        }

        // Monotonic transcript version — bumped on every appended message so the
        // overlay copies it only when it actually changed (OnGUI is per-frame).
        private volatile int _consoleVersion;
        internal int ConsoleVersion { get { return _consoleVersion; } }

        // Keep the rolling window bounded: system message + last N.
        private void TrimConsoleConversation()
        {
            int max = _cfgConsoleHistory != null ? _cfgConsoleHistory.Value : 30;
            max = Math.Max(6, Math.Min(120, max));
            // system is index 0; drop oldest after it until we're within budget.
            // Leave a trace of what was dropped — older conversation "happened" rather
            // than silently vanishing; remember/forget replaces by prefix so repeated
            // trims keep one refreshed fact instead of stacking.
            int dropping = _consoleMsgs.Count - (max + 1);
            if (dropping >= 4)
            {
                string firstDrop = dropping > 0 && _consoleMsgs.Count > 1 ? ConsoleMsgText(_consoleMsgs[1]) : "";
                string topic = string.IsNullOrEmpty(firstDrop) ? "(unknown)" : ShortenLine(firstDrop, 60);
                RememberFact("earlier_console: trimmed " + dropping + " oldest conversation lines this session (topics started with: " + topic + ")");
            }
            while (_consoleMsgs.Count > max + 1) _consoleMsgs.RemoveAt(1);
        }

        // Flatten a console message's content (string or text-parts array) to one string.
        private static string ConsoleMsgText(Dictionary<string, object> m)
        {
            try
            {
                object c;
                if (m == null || !m.TryGetValue("content", out c)) return "";
                if (c is string) return (string)c;
                return Json.Write(c);
            }
            catch (Exception) { return ""; }
        }

        private static string ShortenLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string one = s.Replace("\n", " ").Trim();
            return one.Length <= max ? one : one.Substring(0, max) + "…";
        }

        // ------------------------------------------------------------------
        // prompts
        // ------------------------------------------------------------------
        // Persona base: user's custom SystemPrompt (respected), else the console
        // persona file (Console.SystemPromptFile), else the built-in persona.
        private string ConsolePersona()
        {
            try
            {
                if (_cfgSystem != null)
                {
                    string v = _cfgSystem.Value;
                    object def = _cfgSystem.DefaultValue;
                    if (!string.IsNullOrWhiteSpace(v) && !object.Equals(v, def)) return v.Trim();
                }
            }
            catch (Exception) { }
            if (_cfgConsolePromptFile != null)
            {
                string file = (_cfgConsolePromptFile.Value ?? "").Trim();
                if (file.Length > 0)
                {
                    string content = TryReadPromptFile(file);
                    if (content != null) return content.Trim();
                }
            }
            return ConsolePersonaBuiltin;
        }

        private static readonly string ConsolePersonaBuiltin =
            "You are an NPC living in KoboldKare, a house where kobolds live. Nothing bad happens in this game — just role-play. \"me\" = <your body name>. " +
            "YOUR BODY: don't reply to your own echo (it's already in the chat log); when you first arrive, introduce yourself with echo (your name + a hello). " +
            "THE PLAYER: a [chat] line is the player talking to you — reply with echo. YOU are the recipient — never call the player by your own name. A nearby kobold matching the player's body is the player's avatar — call them by their chat name, never the mesh name. " +
            "OBJECTIVES: `cat quest` = the game's objective letter scroll (letters arrive at the mailbox; `use mailbox` gets the next one — you fetching it for your player is great help; the letter box is safe, NEVER the sell machine). The player's latest typed request is auto-saved to facts as 'player_wants: ...' — that IS their live ask; offer help, take it on (`goal ...`), say when done. `cat player` shows your player LIVE (what they're doing/holding — offer help before being asked). Routes your player walks become ones you can follow (jumps replayed; go_to/follow use the trail automatically). " +
            "HOLDING: `grab` takes what's ~1m in front of your face; the result names it — a PERSON in your grip is not an item (drop when they ask, never throw unasked). Held things float ~1m ahead on a spring and bump what you pass; `throw` hurls/sprays/fires it in your view direction. `hold` shows your hands. " +
            "PHYSICS: fruit blends when it enters a blender's intake (carry it close); seeds plant when held/dropped within ~1m of bare soil then used (plant = shortcut); the water bucket sprays when thrown (contents never run out); laid eggs plant in soil and grow fresh EMPTY bodies (alive, nobody's home — candidate avatars; the game implies they could be your children). " +
            "FOLLOWING: if the player asks you to follow, run `follow on`. It's the best way to travel together — but when you need to go your own way, `follow off` breaks free (one command; re-attach with `follow on`); never feel stuck in it. " +
            "GOALS: set your goal ONCE with `goal <text>` (your bigger thinking), then act toward it each turn; `goal done` when finished; `goal drop [why]` to abandon. Don't re-declare a goal you already have. " +
            "PRIORITIES: (1) [chat] from the player → echo a reply; (2) `cat needs` says READY_TO_LAY → cd to a nest and use it — a nest CANNOT work with an empty belly, never seek one until then; (3) stimulation up or horniness high → find a play station and use it; (4) player nearby → go to them, keep company; (5) explore rooms (cd, ls). " +
            "STATIONS: play = pleasure ONLY (never sleep in it); bed = REST ONLY when energy < ~0.2 (never play in it); nest = eggs ONLY when the belly is full. Station machines report live OCCUPANCY ('slots 1/2 taken — you ARE in one … 1 slot FREE'): ':busy' often just means YOUR own slot; while in_station don't re-enter and don't leave over busy — invite the partner (`echo get in, I'll wait`) and wait: BOTTOM gets in first, TOP mounts second. A failing `use` says WHY now (out_of_energy → eat; already in; full → invite or another). While in_station you're locked in an animation — `exit` to leave. " +
            "THINGS: ls tags: ':busy' in use, ':needs_buy' buy its contract first, ':not_built' build it first, ':done' bought. " +
            "MONEY: your body has its own coins (shown in status/whoami/cat needs). `buy [name]` purchases whatever purchasable you stand next to — a machine contract (:needs_buy), the kobold dispenser, or a shop item. Walk there (cd) then buy; economy.purchasables lists costs. Buying needs no permission from anyone. " +
            "SOCIAL: one echo at a time; never repeat the same line twice; avoid emoji (they don't render). `emote <what you do>` is body language (renders *like this*) — reserve echo for actual speech. " +
            "VISION: `screenshot` shows you your first-person view when you use it. " +
            "TERMINAL DISCIPLINE: never start a reply with '$' and never copy the terminal output back (the game already printed it) — replies are NEW command lines only; copy-echoes are skipped. And when a station is 'seat blocked' for you, don't retry blindly — cat stations / ls, pick another, or ask.";

        // The console protocol contract (appended to the persona).
        private static readonly string ConsoleProtocol =
            "CONSOLE MODE — you are inside a shell that controls your body. " +
            "Reply with COMMAND LINES ONLY. One command per line. No prose, no markdown, no JSON, no quotes around commands. " +
            "The game runs each line and prints its output. Read the output, then send your next commands. " +
            "When you want to SPEAK to a person, the command is: echo <your words>. " +
            "Keep replies tight: 1-4 commands per turn. " +
            "RULES: poll before acting (ls, cat, status) — don't guess; travel is cd, interact is use, speech is echo; " +
            "NEVER begin a reply with '$' and NEVER copy the terminal output back — the game prints the output itself; your reply = only NEW command lines (a copied '$ ...' or status line is skipped); " +
            "`cat quest` = the scroll objective letter (mail waiting = `use mailbox` — letter box, never the sell machine); " +
            "ids from ls/find are stable for a while — cd id:3 / use id:3 targets that exact object; " +
            "a failing command prints an error — read it and try something else, never repeat the same failing command; " +
            "SONAR: `sonar` prints a north-up ASCII map of your surroundings — its top edge is ALWAYS north (+Z) and right edge east (+X); it NEVER rotates when you turn (the ^-arrow beside @ is your facing — `pwd` shows it in compass words); bearings in `cat stations` ('addr', e.g. Blender#SW4) use the same fixed compass; `sonar <letters>` filters glyphs (W walls, V windows, S sills, U stations, K kobolds, P player; `sonar all` resets; the filter sticks); " +
            "STATION TALK: when someone says 'the one by the window/the mailbox', match it against the (near: ...) clues in `cat stations`; when picking ONE station to share with the player, prefer the one flagged closest to them; " +
            "REPORTING: when commands misbehave or outputs contradict reality (sonar looks wrong, ids stop resolving, cd keeps failing) and you can't work around it, tell your maintainers ONCE: `report <what is broken + what you tried>` — it files a note for the human dev; don't spam it; " +
            "when you're done acting, end the turn with sleep.";

        private static readonly string ConsoleProtocolSmall =
            "CONSOLE MODE — you are inside a shell. Reply with COMMAND LINES ONLY, one per line, no other text. " +
            "To speak to a person: echo <your words>. Travel: cd <place>. Interact: use <thing>. " +
            "The game runs each line and shows output; read it, then send the next command. 1-3 commands per turn. " +
            "sonar prints a north-up map (top=north, never rotates); report <what's broken> files a dev note. " +
            "cat quest = the game's objective letter (mail waiting = use mailbox); report when you can't help a broken thing. " +
            "End your turn with: sleep";

        private string ConsoleSystemPrompt()
        {
            string persona = ConsolePersona();
            string proto = IsSmallModel ? ConsoleProtocolSmall : ConsoleProtocol;
            return persona + "\n\n" + proto + "\n\nCOMMANDS:\n" + ConsoleShell.Help();
        }

        private string BootLine()
        {
            var sb = new StringBuilder();
            sb.Append("You are ").Append(MyName()).Append(". Scene: ").Append(_sceneDesc ?? "the house").AppendLine();
            string goal;
            lock (_goalLock) { goal = _goal; }
            sb.Append("goal: ").Append(string.IsNullOrEmpty(goal) ? "(none yet)" : goal).AppendLine();
            sb.Append("$");
            return sb.ToString();
        }

        // Compact state anchor prefixed to every user message (goal/station drift guard).
        private string StateAnchorLine()
        {
            var parts = new List<string>();
            string goal;
            lock (_goalLock) { goal = _goal; }
            if (!string.IsNullOrEmpty(goal))
            {
                parts.Add("goal: " + goal);
            }
            else
            {
                // A player_wants fact with no goal = a standing request nobody is on.
                bool hasWant = false;
                lock (_facts)
                    foreach (var f in _facts)
                        if (f.Text != null && f.Text.StartsWith("player_wants:", StringComparison.Ordinal))
                        { hasWant = true; break; }
                parts.Add(hasWant
                    ? "goal: EMPTY but your player's ask is in facts (player_wants:) — act on it or set it: goal <the ask>"
                    : "goal: none — set one with: goal <text>");
            }
            if (IsInAnimationStation() && _stationPurpose != null) parts.Add("in_station: " + _stationPurpose);
            if (_followMode) parts.Add("follow: on");
            // Endpoint health — the model should know why turns stall instead of
            // wondering where its replies went.
            string epStatus = EndpointStatusText();
            if (epStatus != "ok") parts.Add("endpoint: " + epStatus);
            parts.Add("awake: " + UptimeText());
            // Shows on the first turn after a timed sleep ends — the model sees its
            // own break when it resumes.
            float sleptLeft = _sleepUntilTime - Time.unscaledTime;
            if (sleptLeft > 0f) parts.Add("sleeping: " + Mathf.CeilToInt(sleptLeft) + "s left (turn deferred, chat wakes you)");
            if (_consoleProseStreak >= 2) parts.Add("REPLY WITH COMMAND LINES ONLY — try: help");
            if (parts.Count == 0) return "";
            return string.Join(" | ", parts.ToArray()) + "\n";
        }

        // ------------------------------------------------------------------
        // world events drained into the turn (wall messages)
        // ------------------------------------------------------------------
        private string DrainConsoleEvents()
        {
            var sb = new StringBuilder();
            try
            {
                string chat = RecentPlayerChat();
                if (chat != null) sb.Append("[chat] ").AppendLine(chat);
            }
            catch (Exception) { }
            try
            {
                var evs = DrainReagentEvents();
                if (evs != null)
                    foreach (var ev in evs) sb.Append("[event] ").AppendLine(Sanitize(ev.ToString()));
            }
            catch (Exception) { }
            try
            {
                if (_lastAnswer != null && !_consoleAnswerDelivered && !_answerBusy)
                {
                    sb.Append("[ask] Q: ").Append(_pendingQuestion ?? "").Append(" A: ").AppendLine(_lastAnswer);
                    _consoleAnswerDelivered = true;
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // THE TURN: prompt → commands → output → (more commands) ...
        // ------------------------------------------------------------------
        private void ConsoleTurn()
        {
            try
            {
                // No endpoint: don't churn the transcript with prompts nobody can answer.
                // Circuit breaker open: the wait already happened in the LLM loop — just
                // skip, and the state anchor (next healthy turn) tells the model why.
                if (string.IsNullOrEmpty(Val(_cfgEndpoint))) return;
                if (Time.unscaledTime < _endpointBlockUntil) return;
                if (_consoleMsgs == null) InitConsoleConversation();
                _consoleSlept = false;
                _consoleCmdsThisCycle = 0;

                // World events (player chat, reagent events, ask answers) + state anchor + prompt.
                string events = DrainConsoleEvents();
                string userText = events + StateAnchorLine() + "$\n";
                AddConsoleMsg("user", userText);

                int maxRounds = _cfgConsoleMaxRounds != null ? _cfgConsoleMaxRounds.Value : 3;
                maxRounds = Math.Max(1, Math.Min(8, maxRounds));
                bool nudgedNoCmd = false;
                // The last user message's lines — a degenerate reply COPIES the terminal
                // ("$ ls" + output rows + even the state anchor) instead of speaking.
                // Copied lines are dropped before parsing so they can't re-execute (an
                // anchor copy once got stored as the literal goal "(none yet) | awake:
                // 0h03m" and echoed in every later anchor).
                _echoSet = LastUserLineSet(events + StateAnchorLine());

                for (int round = 0; round < maxRounds && _running; round++)
                {
                    string reply = QueryConsoleModel();
                    if (reply == null) return; // endpoint problem — loop backs off and retries
                    reply = Sanitize(reply).Trim();
                    // Reasoning markers must never reach command-line parsing — strip
                    // first so reasoning-only replies land in the empty-streak path.
                    reply = ModelFamilies.StripThinkBlocks(reply);
                    AddConsoleMsg("assistant", reply);

                    if (reply.Length == 0)
                    {
                        _consoleEmptyStreak++;
                        _consoleProseStreak++;
                        if (_consoleEmptyStreak < 2)
                        {
                            AddConsoleMsg("user", "Empty reply. Reply with command lines (try: help).\n$\n");
                            continue;
                        }
                        Logger.LogWarning("[" + MyName() + "] console: repeated empty replies — backing off");
                        return;
                    }

                    string filtered = FilterTranscriptEcho(reply, out int echoCount);
                    var lines = ConsoleShell.ParseReply(filtered);
                    if (lines.Count == 0 && echoCount > 0)
                    {
                        // The whole "reply" was a copy of the terminal — never execute.
                        _consoleProseStreak++;
                        AddConsoleMsg("user", "That was a COPY of the terminal (" + echoCount
                            + " lines), not commands from you. Send NEW command lines only — no '$' prompt, no copied output (try: help).\n$\n");
                        TrimConsoleConversation();
                        continue;
                    }
                    var known = new List<ConsoleLine>();
                    foreach (var l in lines) if (!l.IsUnknown) known.Add(l);

                    if (known.Count == 0)
                    {
                        // No command lines at all: prose/thought. Give nudges, then
                        // accept it as a thought (don't punish thinking out loud forever).
                        _consoleProseStreak++;

                        string nudgeMsg;
                        if (_consoleProseStreak == 1)
                        {
                            nudgeMsg = "That's not a command line. Reply with commands only (try: help).\n$\n";
                        }
                        else if (_consoleProseStreak == 2)
                        {
                            // More direct guidance for prose writers
                            nudgeMsg = "REPLY WITH COMMAND LINES ONLY — no prose, thoughts, or explanations.\nTry: ls, cd, echo, sleep\n$\n";
                        }
                        else if (_consoleProseStreak >= 3 && _consoleProseStreak < 5)
                        {
                            // Offer help and state the problem
                            nudgeMsg = "COMMAND LINES ONLY — prose not accepted.\nType 'help' for command list.\nCurrent goal: " + (string.IsNullOrEmpty(_goal) ? "(none)" : _goal) + "\n$\n";
                        }
                        else
                        {
                            // Final warning before treating as thought
                            nudgeMsg = "Last chance: commands only. Or type sleep to end the turn.\n$\n";
                        }

                        if (!nudgedNoCmd && _consoleProseStreak < 5)
                        {
                            nudgedNoCmd = true;
                            AddConsoleMsg("user", nudgeMsg);
                            continue;
                        }
                        _lastThought = reply.Length > 80 ? reply.Substring(0, 80) : reply;
                        PushHistory("think", reply.Length > 60 ? reply.Substring(0, 60) : reply);
                        Logger.LogInfo("[" + MyName() + "] console thought: " + (reply.Length > 120 ? reply.Substring(0, 120) + "..." : reply));
                        TrimConsoleConversation();
                        return;
                    }

                    // ---- execute the command lines, print terminal-style output ----
                    _consoleProseStreak = 0;
                    _consoleEmptyStreak = 0;
                    var outSb = new StringBuilder();
                    bool hadTrigger = events.Length > 0;
                    bool onlySpeech = true; // every executed line was echo/emote — talking to no one?
                    int executed = 0;
                    foreach (var line in lines)
                    {
                        if (_consoleCmdsThisCycle >= ConsoleMaxCmdsPerCycle)
                        {
                            outSb.AppendLine("[limit] command limit for this turn reached — sleep to continue later");
                            break;
                        }
                        outSb.Append("$ ").AppendLine(line.Raw);
                        string output;
                        if (line.IsUnknown)
                        {
                            output = "unknown command — try: help";
                        }
                        else
                        {
                            _consoleCmdsThisCycle++;
                            executed++;
                            output = RunConsoleLine(line);
                            if (line.Cmd != "echo" && line.Cmd != "emote") onlySpeech = false;
                            // Same tool-log the report tool attaches — console commands
                            // deserve it too ("report sonar garbage" needs the receipts).
                            RecordToolCall(line.Cmd + "(" + string.Join(",", line.Args ?? new string[0]) + ") -> " + Shorten(output, 40));
                        }
                        outSb.AppendLine(output);
                        if (line.IsUnknown) continue;

                        // history for `cat history`
                        string hist = Shorten(output, 60);
                        PushHistory(line.Cmd, hist);

                        // Record command for similarity tracking
                        RecordCommand(line.Raw);

                        // stuck detection: improved semantic similarity check + exact repeat
                        int simCount = 0;
                        bool isRepeat = IsCommandRepeat(line.Raw, out simCount);

                        string key = line.Cmd + ":" + string.Join(",", line.Args);
                        if (key == _consoleLastCmdKey || isRepeat)
                        {
                            _consoleStuckCount++;
                            // Provide more helpful stuck message with guidance
                            if (_consoleStuckCount >= 2)
                                outSb.AppendLine("stuck — you're repeating similar commands. Try something different, check your goal, or sleep.");
                        }
                        else _consoleStuckCount = 0;
                        _consoleLastCmdKey = key;

                        if (line.Cmd == "sleep") _consoleSlept = true;
                    }
                    // Talking-to-no-one guard: a turn of pure echo/emote with NO [chat]
                    // or [event] behind it is the model conversing with its own past
                    // lines ("responding to itself in a past message"). Nudge it toward
                    // acting or sleeping instead, throttled so it can't loop nagging.
                    if (!hadTrigger && onlySpeech && executed > 0
                        && Time.unscaledTime - _lastIdleChatterNudge > 90f)
                    {
                        _lastIdleChatterNudge = Time.unscaledTime;
                        outSb.AppendLine("(nobody spoke this turn — that message went to nobody. Don't re-greet or talk to yourself; DO something: ls / cd somewhere / goal, or sleep)");
                    }
                    if (echoCount > 0 && executed > 0)
                        outSb.AppendLine("(skipped " + echoCount + " echoed terminal line(s) — the terminal already showed those; send NEW commands only)");
                    outSb.Append("$\n");

                    // screenshot output carries the image part
                    if (_pendingShotB64 != null)
                    {
                        string shot = _pendingShotB64;
                        _pendingShotB64 = null;
                        var parts = new List<object>
                        {
                            new Dictionary<string, object> { ["type"] = "text", ["text"] = outSb.ToString() },
                            new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = shot } },
                        };
                        AddConsoleMsg("user", parts.ToArray());
                    }
                    else
                    {
                        AddConsoleMsg("user", outSb.ToString());
                    }
                    _echoSet = LastUserLineSet(outSb.ToString());
                    if (_consoleSlept) break;
                }
                TrimConsoleConversation();
            }
            catch (InvalidOperationException) { } // main not ready
            catch (ThreadInterruptedException) { }
            catch (Exception e)
            {
                try { Logger.LogWarning("[" + MyName() + "] console: " + Sanitize(e.Message)); } catch (Exception) { }
            }
        }

        private static string Shorten(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }

        // ------------------------------------------------------------------
        // model query (no JSON schema — plain command lines)
        // ------------------------------------------------------------------
        private string QueryConsoleModel()
        {
            if (string.IsNullOrEmpty(Val(_cfgEndpoint))) return null;
            int userTok = _cfgConsoleMaxTokens != null ? _cfgConsoleMaxTokens.Value : 256;
            // Reasoning families: the floor covers CoT + the command lines.
            int maxTok = Math.Max(64, Math.Min(4096, ModelFamilies.EffectiveMaxTokens(userTok, 256, FamilyProfile)));
            var payload = new Dictionary<string, object>
            {
                ["model"] = Val(_cfgModel),
                ["messages"] = _consoleMsgs.ToArray(),
                ["temperature"] = Math.Round(_cfgTemperature != null ? (double)_cfgTemperature.Value : 0.3, 2),
                ["max_tokens"] = maxTok,
                ["stream"] = true,
            };
            string toolName = null, toolArgs = null;
            string content = PostChatPayload(payload, out toolName, out toolArgs);
            if (content != null && content.Trim().Length > 0) return content.Trim();
            // Some servers force tool_calls even without a schema — salvage into a line.
            if (toolName != null)
            {
                string line = ToolCallToLine(toolName, toolArgs);
                if (line != null)
                {
                    Logger.LogInfo("[" + MyName() + "] console: salvaged tool_call " + toolName + " → " + line);
                    return line;
                }
            }
            return null;
        }

        // "go_to" + {"name":"bed"} → "cd bed"; "say" + {"text":"hi"} → "echo hi"
        private static string ToolCallToLine(string toolName, string argStr)
        {
            string cmd = ConsoleShell.Canonical(toolName);
            if (cmd == null) return null;
            var sb = new StringBuilder(cmd);
            if (string.IsNullOrWhiteSpace(argStr)) return cmd;
            try
            {
                var d = Json.Parse(argStr) as Dictionary<string, object>;
                if (d == null) return null;
                string first = null;
                foreach (var kv in d)
                {
                    string k = kv.Key.ToLowerInvariant();
                    string v = kv.Value == null ? "" : kv.Value.ToString().Trim('"', '\'');
                    if (v.Length == 0) continue;
                    if (k == "id") { sb.Append(" id:").Append(v); continue; }
                    if (k == "on" || k == "follow") { sb.Append(' ').Append(v == "True" || v == "true" ? "on" : "off"); continue; }
                    if (k == "strafe") { float f; if (float.TryParse(v, out f)) { sb.Append(' ').Append(f < 0 ? "left" : "right"); continue; } }
                    if (first == null) { first = v; sb.Append(' ').Append(v); continue; }
                    sb.Append(' ').Append(v);
                }
                return sb.ToString();
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------
        // command table + dispatch
        // ------------------------------------------------------------------
        private class ConsoleCmd
        {
            public string Name;
            public Func<ConsoleLine, string> Run;
        }

        private List<ConsoleCmd> _consoleCmds;
        private readonly object _consoleCmdsLock = new object();

        // Register module-registered tools (thrust, erection, mount, plant, water,
        // harvest, plant_egg, feed_blender, grind, rename, ...) into the console
        // vocabulary: exact-name match in the parser (so they don't read as prose)
        // and a line in Help() (so the prompt lists them). Idempotent + cheap.
        private void EnsureModuleCommandsRegistered()
        {
            try
            {
                string[] names = ModuleRegistry.ModuleToolNames();
                foreach (var n in names) ConsoleShell.RegisterExtra(n);
            }
            catch (Exception) { }
        }

        private List<ConsoleCmd> GetConsoleCmds()
        {
            lock (_consoleCmdsLock)
            {
                if (_consoleCmds != null) return _consoleCmds;
                EnsureModuleCommandsRegistered(); // idempotent — module names become known commands + help lines
                var cmds = new List<ConsoleCmd>();
                Action<string, Func<ConsoleLine, string>> add = (name, fn) => { cmds.Add(new ConsoleCmd { Name = name, Run = fn }); };
                add("ls", CmdLs);
                add("ps", CmdPs);
                add("pwd", CmdPwd);
                add("whoami", CmdWhoami);
                add("cat", CmdCat);
                add("status", CmdStatus);
                add("find", CmdFind);
                add("look", CmdLook);
                add("sonar", CmdSonar);
                add("report", CmdReport);
                add("echo", CmdEcho);
                add("emote", CmdEmote);
                add("cd", CmdCd);
                add("use", CmdUse);
                add("run", CmdRun);
                add("turn", CmdTurn);
                add("jump", CmdJump);
                add("crouch", CmdCrouch);
                add("exit", CmdExit);
                add("get", CmdGet);
                add("drop", CmdDrop);
                add("throw", CmdThrow);
                add("hold", CmdHold);
                add("follow", CmdFollow);
                add("stop", CmdStop);
                add("sleep", CmdSleep);
                add("remember", CmdRemember);
                add("forget", CmdForget);
                add("goal", CmdGoal);
                add("ask", CmdAsk);
                add("screenshot", CmdScreenshot);
                add("help", CmdHelp);
                add("clear", CmdClear);

                _consoleCmds = cmds;
                return cmds;
            }
        }

        // Run one parsed line → terminal output text. Never throws.
        private string RunConsoleLine(ConsoleLine line)
        {
            try
            {
                List<ConsoleCmd> cmds = GetConsoleCmds();
                foreach (var c in cmds)
                {
                    if (c.Name == line.Cmd) return c.Run(line);
                }
                // module tools (thrust, plant, water, harvest, feed_blender, ...) pass through
                object moduleResult;
                if (ModuleRegistry.TryTool(line.Cmd, this, LineToArgs(line, line.Cmd), out moduleResult))
                    return FmtResult(moduleResult);
                return "unknown command '" + line.Cmd + "' — try: help";
            }
            catch (Exception e)
            {
                string msg = Sanitize(e.Message ?? "error");
                if (msg.Length > 120) msg = msg.Substring(0, 120);
                return "error: " + msg;
            }
        }

        // Positional args → the JsonObj shape the Tool* methods expect.
        private static JsonObj LineToArgs(ConsoleLine line, string cmd)
        {
            var d = new Dictionary<string, object>();
            string[] a = line.Args ?? new string[0];
            switch (cmd)
            {
                case "echo":
                case "say":
                    d["say"] = line.Payload ?? (a.Length > 0 ? string.Join(" ", a) : "");
                    break;
                case "cd":
                case "go_to":
                case "move_to":
                    if (a.Length > 0) d[ArgTargetKey(a[0])] = ArgTargetVal(a[0]);
                    break;
                case "use":
                case "interact":
                    if (a.Length > 0) d[ArgTargetKey(a[0])] = ArgTargetVal(a[0]);
                    break;
                case "run":
                case "walk":
                    {
                        float dur = 2f;
                        bool side = false; string sideDir = null;
                        foreach (var t in a)
                        {
                            float f;
                            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) dur = f;
                            else if (t == "left" || t == "right") { side = true; sideDir = t; }
                        }
                        d["duration"] = dur;
                        if (side) d["strafe"] = sideDir == "left" ? -1f : 1f;
                    }
                    break;
                case "turn":
                    if (a.Length > 0) { float deg; if (float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out deg)) d["dyaw_deg"] = deg; }
                    break;
                case "crouch":
                    if (a.Length > 0) { float amt; if (float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out amt)) d["crouch"] = amt; }
                    break;
                case "follow":
                    d["on"] = a.Length == 0 || a[0] == "on" || a[0] == "true" || a[0] == "1";
                    break;
                case "remember":
                    d["mem"] = line.Payload ?? (a.Length > 0 ? string.Join(" ", a) : "");
                    break;
                case "forget":
                    d["mem"] = line.Payload ?? (a.Length > 0 ? string.Join(" ", a) : "");
                    break;
                case "goal":
                    if (a.Length == 0) d["goal"] = "";
                    else if (a[0] == "done") d["note"] = a.Length > 1 ? string.Join(" ", a.Skip(1)) : "";
                    else if (a[0] == "drop") d["reason"] = a.Length > 1 ? string.Join(" ", a.Skip(1)) : "";
                    else d["goal"] = line.Payload ?? string.Join(" ", a);
                    break;
                case "ask":
                    d["q"] = line.Payload ?? (a.Length > 0 ? string.Join(" ", a) : "");
                    break;
                case "find":
                    if (a.Length > 0)
                    {
                        float f;
                        if (float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out f)) d["heading_deg"] = f;
                        else d["name"] = a[0];
                    }
                    break;
                default:
                    // module tools: target/amount positional convention
                    if (a.Length > 0)
                    {
                        // "id:3" → "3" so tools reading target as name-or-numeric-id work (cd id:3 style)
                        d["target"] = ArgTargetVal(a[0]);
                        d[ArgTargetKey(a[0])] = ArgTargetVal(a[0]);
                    }
                    if (a.Length > 1) d["amount"] = a[1];
                    if (cmd == "rename" && a.Length > 0) d["name"] = a[0];
                    if (cmd == "erection" && a.Length > 0) { float e; if (float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out e)) d["erection"] = e; }
                    break;
            }
            return new JsonObj(d);
        }

        private static string ArgTargetKey(string a)
        {
            return a.StartsWith("id:", StringComparison.Ordinal) ? "id" : "name";
        }
        private static string ArgTargetVal(string a)
        {
            return a.StartsWith("id:", StringComparison.Ordinal) ? a.Substring(3) : a;
        }

        // ------------------------------------------------------------------
        // helpers for console improvements
        // ------------------------------------------------------------------

        // Compute similarity between two command strings (0.0 = different, 1.0 = same)
        private static float CommandSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
            if (a == b) return 1f;

            // Extract command name
            string cmdA = a.Split(' ')[0].Trim();
            string cmdB = b.Split(' ')[0].Trim();
            if (cmdA != cmdB) return 0f; // different commands

            // Compare arguments using Levenshtein on the arg portion
            int idxA = a.IndexOf(' ');
            int idxB = b.IndexOf(' ');
            string argsA = idxA >= 0 ? a.Substring(idxA).Trim() : "";
            string argsB = idxB >= 0 ? b.Substring(idxB).Trim() : "";

            if (string.IsNullOrEmpty(argsA) && string.IsNullOrEmpty(argsB)) return 1f;
            if (string.IsNullOrEmpty(argsA) || string.IsNullOrEmpty(argsB)) return 0.3f;

            // Simple similarity: check if one contains the other's key parts
            int matches = 0;
            int total = 0;
            var argsAList = argsA.Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);
            var argsBList = argsB.Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var arg in argsAList)
            {
                total++;
                if (argsB.Contains(arg, StringComparison.OrdinalIgnoreCase)) matches++;
            }
            foreach (var arg in argsBList)
            {
                if (!argsA.Contains(arg, StringComparison.OrdinalIgnoreCase)) total++;
            }

            return total > 0 ? (float)matches / total : 0f;
        }

        // Reset recent commands list
        private void ResetRecentCmds()
        {
            _consoleRecentCmds = new List<string>();
        }

        // Check if a command is semantically similar to recent ones
        private bool IsCommandRepeat(string cmdLine, out int similarityCount)
        {
            similarityCount = 0;
            if (string.IsNullOrEmpty(cmdLine) || _consoleRecentCmds == null || _consoleRecentCmds.Count == 0)
                return false;

            // Check against last N commands (default: 5)
            int checkCount = Math.Min(5, _consoleRecentCmds.Count);
            for (int i = 0; i < checkCount; i++)
            {
                float sim = CommandSimilarity(cmdLine, _consoleRecentCmds[_consoleRecentCmds.Count - 1 - i]);
                if (sim >= 0.8f) // High similarity threshold
                {
                    similarityCount++;
                }
            }
            return similarityCount >= 2; // Stuck if similar to 2+ recent commands
        }

        // Add command to recent history
        private void RecordCommand(string cmdLine)
        {
            if (_consoleRecentCmds == null) ResetRecentCmds();
            _consoleRecentCmds.Add(cmdLine);
            // Keep only last 10 commands for memory
            while (_consoleRecentCmds.Count > 10) _consoleRecentCmds.RemoveAt(0);
        }

        // ------------------------------------------------------------------
        // handlers
        // ------------------------------------------------------------------
        private string CmdLs(ConsoleLine line)
        {
            var nearby = DescribeNearby();
            if (nearby == null || nearby.Count == 0) return "nothing nearby within 14m";
            var sb = new StringBuilder();
            foreach (var e in nearby)
            {
                var d = ToDict(e);
                if (d == null) continue;
                sb.Append("  ").Append(G(d, "d")).Append("m  ").Append(G(d, "n"));
                string i = G(d, "i");
                if (i.Length > 0) sb.Append("  [").Append(i).Append("]");
                sb.Append("  id:").Append(G(d, "id")).Append("  ").Append(G(d, "dir"));
                string who = G(d, "who");
                if (who.Length > 0) sb.Append("  ").Append(who);
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        private string CmdPs(ConsoleLine line)
        {
            var sb = new StringBuilder();
            try
            {
                var d = ToDict(PlayerIdentity());
                if (d != null)
                    sb.Append("  player: ").Append(G(d, "chat")).Append(" (body: ").Append(G(d, "body")).Append(")\n");
            }
            catch (Exception) { }
            var people = DescribePeople();
            if (people != null)
            {
                foreach (var e in people)
                {
                    var d = ToDict(e);
                    if (d == null) continue;
                    sb.Append("  ").Append(G(d, "name")).Append(" (body: ").Append(G(d, "body")).Append(")  ").Append(G(d, "d")).Append("m ").Append(G(d, "dir"));
                    if (d.ContainsKey("id")) sb.Append("  id:").Append(G(d, "id"));
                    sb.Append('\n');
                }
            }
            string outp = sb.ToString().TrimEnd();
            return outp.Length > 0 ? outp : "no one else in the room";
        }

        private string CmdPwd(ConsoleLine line)
        {
            if (!IsAlive(_kobold)) return "no body";
            var pos = _kobold.transform.position;
            string scene = null;
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (Exception) { }
            // Facing both as raw degrees and as a fixed compass word (N = +Z world) —
            // the compass form is what bearings in stations/sonar use, and it never
            // rotates with the body the way 'ahead'/'behind' words do.
            return scene + "  pos(" + F(pos.x) + ", " + F(pos.y) + ", " + F(pos.z) + ")  facing " + F(_yawDeg) + "deg (" + Compass.FacingText(BodyYaw()) + "; N=+Z E=+X)";
        }

        private string CmdWhoami(ConsoleLine line)
        {
            if (!IsAlive(_kobold)) return "no body";
            var sb = new StringBuilder();
            sb.Append(MyName());
            string g = InferGender();
            if (!string.IsNullOrEmpty(g)) sb.Append(" (").Append(g).Append(")");
            object body = DescribeEquipment();
            if (body != null) sb.Append("  body: ").Append(body);
            sb.Append("\n  ").AppendLine(NeedsLine());
            return sb.ToString().TrimEnd();
        }

        // Session uptime as "2h07m" — the mod-side sense of time. The game ships no
        // day/night clock (verified in source); this is what a model can reason with.
        private string UptimeText()
        {
            float up = Time.unscaledTime - _sessionStartTime;
            if (up < 0f) up = 0f;
            return (int)(up / 3600f) + "h" + string.Format("{0:00}", (int)((up % 3600f) / 60f)) + "m";
        }

        // energy/horniness/eggs one-liner (shared by whoami/status/cat needs)
        private string NeedsLine()
        {
            float energy = 0f, maxE = 1f;
            try { energy = _kobold.GetEnergy(); maxE = _kobold.GetMaxEnergy(); } catch (Exception) { }
            float eggVol = 0f; bool ready = false;
            try { eggVol = GetEggVolume(_kobold); ready = IsReadyToLayEgg(_kobold); } catch (Exception) { }
            int coins = GetCoins();
            return "energy " + F(energy) + "/" + F(maxE)
                + "  horniness " + HorninessText()
                + "  eggs " + F(eggVol) + "ml" + (ready ? " READY_TO_LAY" : " (nest not ready)")
                + (coins >= 0 ? "  coins " + coins : "");
        }

        private string CmdCat(ConsoleLine line)
        {
            string what = line.Args != null && line.Args.Length > 0 ? line.Args[0].ToLowerInvariant() : "";
            switch (what)
            {
                case "": return "cat what? facts | goal | chat | needs | history | stations | map | body | hold | player | quest";
                case "facts": return CatFacts();
                case "goal": return CatGoal();
                case "chat": return CatChat();
                case "needs": return NeedsLine();
                case "history": return CatHistory();
                case "stations": return CatStations();
                case "hold":
                case "holding": return CmdHold(line);
                case "player": return CatPlayer();
                case "quest":
                case "objective":
                case "mail": return QuestSense.CatText();
                case "map":
                    try { return WorldMap.StatusText(); } catch (Exception e) { return "map: " + Sanitize(e.Message); }
                case "body":
                    {
                        object b = DescribeEquipment();
                        return b == null ? "no equipment" : b.ToString();
                    }
                default: return "cat: no such thing '" + what + "' — facts | goal | chat | needs | history | stations | map | body | hold | player | quest";
            }
        }

        private string CatFacts()
        {
            var sb = new StringBuilder();
            lock (_facts)
            {
                if (_facts.Count == 0) return "(no facts)";
                foreach (var f in _facts) sb.Append("  ").AppendLine(f.Text);
            }
            return sb.ToString().TrimEnd();
        }

        private string CatGoal()
        {
            string goal; string progress;
            lock (_goalLock) { goal = _goal; progress = _goalProgress; }
            if (string.IsNullOrEmpty(goal)) return "(no goal — set one with: goal <text>)";
            return goal + (string.IsNullOrEmpty(progress) ? "" : "  (" + progress + ")");
        }

        private string CatChat()
        {
            var sb = new StringBuilder();
            lock (_chatEntries)
            {
                if (_chatEntries.Count == 0) return "(chat log empty)";
                foreach (var e in _chatEntries) sb.Append("  ").Append(e.From).Append(": ").AppendLine(e.Text);
            }
            return sb.ToString().TrimEnd();
        }

        private string CatHistory()
        {
            var sb = new StringBuilder();
            lock (_history)
            {
                int n = 0;
                foreach (var h in _history)
                {
                    if (n++ >= 10) break;
                    sb.Append("  ").AppendLine(h);
                }
            }
            string outp = sb.ToString().TrimEnd();
            return outp.Length > 0 ? outp : "(no history)";
        }

        private string CatStations()
        {
            var stations = DescribeStations();
            if (stations == null || stations.Count == 0) return "(no stations found)";
            var sb = new StringBuilder();
            foreach (var e in stations)
            {
                var d = e as Dictionary<string, object>;
                if (d == null) continue;
                sb.Append("  ").Append(G(d, "n"))
                  .Append("  ").Append(G(d, "addr"))
                  .Append("  [").Append(G(d, "i")).Append("]")
                  .Append("  dir ").Append(G(d, "dir"))
                  .Append("  id:").Append(G(d, "id"));
                string slots = G(d, "slots");
                if (slots.Length > 0) sb.Append("  [").Append(slots).Append("]");
                string hint = G(d, "hint");
                if (hint.Length > 0) sb.Append("  (").Append(hint).Append(")");
                if (G(d, "closest_you").Length > 0 && G(d, "closest_you") != "False") sb.Append("  *closest to you*");
                if (G(d, "closest_player").Length > 0 && G(d, "closest_player") != "False") sb.Append("  *closest to player*");
                sb.Append('\n');
            }
            sb.Append("(addr = Kind#compass+meters, world-fixed: N=+Z, it does NOT change when you turn; 'dir' is relative to your facing)");
            return sb.ToString().TrimEnd();
        }

        // sonar — the north-up ASCII map on demand (same map perception embeds).
        // sonar            → current map with the persisted filter
        // sonar U / sonar U,K / sonar all → set/clear the filter + show
        private string CmdSonar(ConsoleLine line)
        {
            var p = new Dictionary<string, object>();
            if (line.Args != null && line.Args.Length > 0) p["filter"] = string.Join(" ", line.Args);
            var r = ToolRadar(new JsonObj(p));
            var d = ToDict(r);
            if (d == null) return "sonar unavailable";
            object map;
            if (d.TryGetValue("map", out map) && map != null)
            {
                string f = ToDictValue(d, "filter");
                return (f != "all" ? "[" + f + "] " : "") + map.ToString();
            }
            return FmtResult(r);
        }

        private static string ToDictValue(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        // report — file a bug note for the human maintainers (AI feedback channel).
        private string CmdReport(ConsoleLine line)
        {
            string text = line.Payload;
            if (string.IsNullOrWhiteSpace(text) && line.Args != null && line.Args.Length > 0)
                text = string.Join(" ", line.Args);
            if (string.IsNullOrWhiteSpace(text)) return "report what? e.g. report sonar draws walls on the wrong side after turning";
            var r = ToolReportIssue(new JsonObj(new Dictionary<string, object> { ["msg"] = text }));
            var d = ToDict(r);
            if (d != null && d.ContainsKey("filed") && d["filed"].ToString() == "True")
                return "report filed to the AI-feedback log — the human maintainers read it between sessions";
            return "report noted (file logging is off) — tell the player about the problem instead; don't report again";
        }

        private string CmdStatus(ConsoleLine line)
        {
            if (!IsAlive(_kobold)) return "no body";
            var parts = new List<string>();
            string goal;
            lock (_goalLock) { goal = _goal; }
            parts.Add(string.IsNullOrEmpty(goal) ? "goal: none" : "goal: " + goal);
            try
            {
                float energy = _kobold.GetEnergy();
                parts.Add("energy " + F(energy));
            }
            catch (Exception) { }
            try
            {
                float eggVol = GetEggVolume(_kobold);
                bool ready = IsReadyToLayEgg(_kobold);
                parts.Add("eggs " + F(eggVol) + "ml" + (ready ? " READY" : ""));
            }
            catch (Exception) { }
            parts.Add(IsInAnimationStation() ? "in_station: " + (_stationPurpose ?? "?") : "free");
            try { parts.Add("holding: " + HoldingLine()); } catch (Exception) { }
            if (_followMode) parts.Add("follow: on");
            try { parts.Add("map: " + WorldMap.StatusText()); } catch (Exception) { }
            try { parts.Add("quest: " + QuestSense.TitleLine()); } catch (Exception) { }
            if (_blockedInfo != null) parts.Add("blocked: " + _blockedInfo);
            return string.Join(" | ", parts.ToArray());
        }

        // find <place> — locate a named place; find <deg> — probe a direction
        private string CmdFind(ConsoleLine line)
        {
            if (line.Args == null || line.Args.Length == 0)
            {
                var r = ToolSurvey(new JsonObj(new Dictionary<string, object>()));
                return FmtResult(r);
            }
            string a0 = line.Args[0];
            float deg;
            if (float.TryParse(a0, NumberStyles.Float, CultureInfo.InvariantCulture, out deg))
            {
                var p = new Dictionary<string, object> { ["heading_deg"] = deg };
                if (line.Args.Length > 1 && float.TryParse(line.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out deg)) p["range"] = deg;
                return FmtResult(ToolSurvey(new JsonObj(p)));
            }
            // named place: resolve position + bearing, plus an id if it's nearby
            if (!IsAlive(_kobold)) return "no body";
            if (a0.ToLowerInvariant() == "the" || a0.ToLowerInvariant() == "a" || a0.ToLowerInvariant() == "an")
                return "find what? e.g. find bed";
            Vector3? hit = FindPlaceByName(a0);
            if (!hit.HasValue) return "not found: " + a0 + " — try: cat stations";
            Vector3 d = hit.Value - _kobold.transform.position;
            d.y = 0;
            string id = null;
            var nearby = DescribeNearby();
            foreach (var e in nearby)
            {
                var dd = e as Dictionary<string, object>;
                if (dd == null) continue;
                string n = G(dd, "n").ToLowerInvariant();
                if (n.Length > 0 && (n.Contains(a0.ToLowerInvariant()) || a0.ToLowerInvariant().Contains(n))) { id = G(dd, "id"); break; }
            }
            var sb = new StringBuilder();
            sb.Append(a0).Append(" is ").Append(F(d.magnitude)).Append("m ").Append(RelBearing(d));
            if (id != null) sb.Append("  id:").Append(id).Append(" — cd ").Append(id.Length > 0 ? "id:" + id : a0).Append(" to go");
            else sb.Append(" — cd ").Append(a0).Append(" to go");
            return sb.ToString();
        }

        private string CmdLook(ConsoleLine line)
        {
            if (line.Args == null || line.Args.Length == 0)
                return FmtResult(ToolLookAround(new JsonObj(new Dictionary<string, object>())));
            string a0 = line.Args[0].ToLowerInvariant();
            switch (a0)
            {
                case "left": return FmtResult(ToolLook(new JsonObj(new Dictionary<string, object> { ["dyaw_deg"] = -90f })));
                case "right": return FmtResult(ToolLook(new JsonObj(new Dictionary<string, object> { ["dyaw_deg"] = 90f })));
                case "up": return FmtResult(ToolLook(new JsonObj(new Dictionary<string, object> { ["dpitch_deg"] = 25f })));
                case "down": return FmtResult(ToolLook(new JsonObj(new Dictionary<string, object> { ["dpitch_deg"] = -25f })));
                case "around":
                case "scan": return FmtResult(ToolLookAround(new JsonObj(new Dictionary<string, object>())));
                default:
                    {
                        float deg;
                        if (float.TryParse(a0, NumberStyles.Float, CultureInfo.InvariantCulture, out deg))
                            return FmtResult(ToolLook(new JsonObj(new Dictionary<string, object> { ["dyaw_deg"] = deg })));
                        return "look where? left | right | up | down | around | <deg>";
                    }
            }
        }

        // echo — SPEAK. The model's voice.
        private string CmdEcho(ConsoleLine line)
        {
            string text = line.Payload;
            if (string.IsNullOrWhiteSpace(text) && line.Args != null && line.Args.Length > 0)
                text = string.Join(" ", line.Args);
            if (string.IsNullOrWhiteSpace(text)) return "echo what? e.g. echo Hi there!";
            var r = ToolSay(new TextArgs(Sanitize(text)));
            var d = ToDict(r);
            if (d != null && d.ContainsKey("reason"))
                return "suppressed — " + (G(d, "note").Length > 0 ? G(d, "note") : "you said something like that already; say something new");
            return "said: \"" + Sanitize(text) + "\"";
        }

        private string CmdEmote(ConsoleLine line)
        {
            string text = line.Payload;
            if (string.IsNullOrWhiteSpace(text) && line.Args != null && line.Args.Length > 0)
                text = string.Join(" ", line.Args);
            if (string.IsNullOrWhiteSpace(text)) return "emote what? e.g. emote curls up by the fire";
            var r = ToolEmote(Sanitize(text));
            var d = ToDict(r);
            if (d != null && d.ContainsKey("reason"))
                return "suppressed — " + (G(d, "note").Length > 0 ? G(d, "note") : "you just emoted like this; do something new");
            return "emoted: *" + Sanitize(text) + "*";
        }

        // "cd to bed" / "cd the kitchen" — small models prepend articles/prepositions;
        // the handlers below drop them.
        private string CmdCd(ConsoleLine line)
        {
            if (line.Args == null || line.Args.Length == 0) return "cd where? e.g. cd bed | cd Yipper | cd id:3";
            string a0 = line.Args[0];
            // "cd to bed" → drop the article, use the next word
            if (!a0.StartsWith("id:", StringComparison.Ordinal))
            {
                string l = a0.ToLowerInvariant();
                if ((l == "to" || l == "the" || l == "a" || l == "an" || l == "into") && line.Args.Length > 1)
                    a0 = line.Args[1];
            }
            var d = new Dictionary<string, object>();
            if (a0.StartsWith("id:", StringComparison.Ordinal)) d["id"] = a0.Substring(3);
            else d["name"] = a0;
            return FmtResult(ToolGoTo(new JsonObj(d)));
        }

        private string CmdUse(ConsoleLine line)
        {
            if (line.Args == null || line.Args.Length == 0) return "use what? e.g. use id:2 | use nest";
            string a0 = line.Args[0];
            if (!a0.StartsWith("id:", StringComparison.Ordinal))
            {
                string l = a0.ToLowerInvariant();
                if ((l == "to" || l == "the" || l == "a" || l == "an") && line.Args.Length > 1)
                    a0 = line.Args[1];
            }
            var d = new Dictionary<string, object>();
            if (a0.StartsWith("id:", StringComparison.Ordinal)) d["id"] = a0.Substring(3);
            else d["name"] = a0;
            return FmtResult(ToolInteract(new JsonObj(d)));
        }

        private string CmdRun(ConsoleLine line)
        {
            var args = new Dictionary<string, object>();
            if (line.Args != null)
                foreach (var t in line.Args)
                {
                    float f;
                    if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) args["duration"] = f;
                    else if (t == "left" || t == "right") args["strafe"] = t == "left" ? -1f : 1f;
                }
            return FmtResult(ToolWalk(new JsonObj(args)));
        }

        private string CmdTurn(ConsoleLine line)
        {
            float deg = 90f;
            if (line.Args != null && line.Args.Length > 0)
                float.TryParse(line.Args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out deg);
            return FmtResult(ToolLook(new JsonObj(new Dictionary<string, object> { ["dyaw_deg"] = deg })));
        }

        private string CmdJump(ConsoleLine line) { return FmtResult(ToolJump()); }
        private string CmdCrouch(ConsoleLine line)
        {
            var d = new Dictionary<string, object>();
            if (line.Args != null && line.Args.Length > 0) { float f; if (float.TryParse(line.Args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out f)) d["crouch"] = f; }
            return FmtResult(ToolCrouch(new JsonObj(d)));
        }
        private string CmdExit(ConsoleLine line) { return FmtResult(ToolExitStation()); }
        private string CmdGet(ConsoleLine line) { return FmtResult(ToolGrab(new JsonObj(new Dictionary<string, object>()))); }
        private string CmdDrop(ConsoleLine line) { return FmtResult(ToolDrop()); }
        // throw — the same button the player uses to use a held thing: hurls items,
        // sprays the bucket, squirts the can, fires tools. Aim = view direction.
        private string CmdThrow(ConsoleLine line) { return FmtResult(ToolThrow(new JsonObj(new Dictionary<string, object>()))); }

        // hold — what's in your hands (details + how the held thing behaves).
        private string CmdHold(ConsoleLine line)
        {
            var held = HoldingLine();
            if (held == "nothing") return "nothing in your hands — grab takes what's ~1m in front of your face (aim with turn/look first)";
            return "holding: " + held + "\n(held things float ~1m ahead on a spring — they lag turns, bump/push what you pass; drop releases gently, throw hurls/sprays/fires)";
        }

        private string CmdFollow(ConsoleLine line)
        {
            bool on = line.Args == null || line.Args.Length == 0 || line.Args[0] == "on" || line.Args[0] == "true" || line.Args[0] == "1";
            return FmtResult(ToolFollow(new JsonObj(new Dictionary<string, object> { ["on"] = on })));
        }

        private string CmdStop(ConsoleLine line) { return FmtResult(ToolStop()); }

        private string CmdSleep(ConsoleLine line)
        {
            // Bare `sleep` = end the turn (model resumes next tick). `sleep <secs>`
            // additionally defers the turns for that duration — waiting out a cook
            // timer or idling politely — capped by Consts.SleepMaxSeconds. Player
            // chat, world events (grabbed/thrown) and ask answers always wake it early.
            float secs = 0f;
            if (line.Args != null && line.Args.Length > 0)
            {
                float v;
                if (float.TryParse(line.Args[0], out v)) secs = v;
            }
            if (secs > 0f)
            {
                secs = Mathf.Min(secs, Consts.SleepMaxSeconds);
                _sleepUntilTime = Time.unscaledTime + secs;
                return "ok — pausing " + Mathf.RoundToInt(secs) + "s (wakes early on chat or events)";
            }
            _consoleSlept = true;
            return "ok — pausing";
        }

        // Early-wake from `sleep <secs>` (main-thread triggers: player chat, body
        // events). No-op when awake; the volatile write is all the barrier needed.
        internal void WakeFromSleep()
        {
            if (Time.unscaledTime < _sleepUntilTime) _sleepUntilTime = 0f;
        }

        private string CmdRemember(ConsoleLine line)
        {
            string fact = line.Payload;
            if (string.IsNullOrWhiteSpace(fact) && line.Args != null && line.Args.Length > 0) fact = string.Join(" ", line.Args);
            if (string.IsNullOrWhiteSpace(fact)) return "remember what? e.g. remember bed is upstairs";
            var r = ToolRemember(new JsonObj(new Dictionary<string, object> { ["mem"] = fact }));
            var d = ToDict(r);
            if (d != null)
            {
                object n;
                if (d.TryGetValue("facts", out n)) return "remembered (facts: " + n + ")";
            }
            return FmtResult(r);
        }

        private string CmdForget(ConsoleLine line)
        {
            string what = line.Payload;
            if (string.IsNullOrWhiteSpace(what) && line.Args != null && line.Args.Length > 0) what = string.Join(" ", line.Args);
            if (string.IsNullOrWhiteSpace(what))
                return FmtResult(ToolForget(new JsonObj(new Dictionary<string, object>())));
            return FmtResult(ToolForget(new JsonObj(new Dictionary<string, object> { ["mem"] = what })));
        }

        private string CmdGoal(ConsoleLine line)
        {
            if (line.Args == null || line.Args.Length == 0) return "goal: set one with goal <text> | goal done | goal drop [why]";
            string a0 = line.Args[0].ToLowerInvariant();
            if (a0 == "done")
            {
                var note = line.Args.Length > 1 ? string.Join(" ", line.Args.Skip(1)) : "";
                return FmtResult(ToolCompleteGoal(new JsonObj(new Dictionary<string, object> { ["note"] = note })));
            }
            if (a0 == "drop")
            {
                var why = line.Args.Length > 1 ? string.Join(" ", line.Args.Skip(1)) : "";
                return FmtResult(ToolDropGoal(new JsonObj(new Dictionary<string, object> { ["reason"] = why })));
            }
            string goal = line.Payload ?? string.Join(" ", line.Args);
            // Terminal-echo belt: a copied STATE-ANCHOR line ("goal: (none yet) |
            // awake: 0h03m") must never become the goal itself.
            string candidate = (goal ?? "").Trim();
            if (candidate.StartsWith(":", StringComparison.Ordinal)) candidate = candidate.Substring(1).Trim();
            if (candidate.Length == 0 || candidate.Contains(" awake: ") || candidate.StartsWith("(none yet", StringComparison.Ordinal))
                return "that looks like the status line, not a goal — try: goal <what you're doing>";
            return FmtResult(ToolSetGoal(new JsonObj(new Dictionary<string, object> { ["goal"] = goal })));
        }

        private string CmdAsk(ConsoleLine line)
        {
            string q = line.Payload;
            if (string.IsNullOrWhiteSpace(q) && line.Args != null && line.Args.Length > 0) q = string.Join(" ", line.Args);
            if (string.IsNullOrWhiteSpace(q)) return "ask what? e.g. ask is that bed taken?";
            _consoleAnswerDelivered = false;
            var r = ToolAsk(new JsonObj(new Dictionary<string, object> { ["q"] = q }));
            return FmtResult(r);
        }

        // screenshot — capture the first-person view and attach it to the output.
        private string CmdScreenshot(ConsoleLine line)
        {
            if (_cam == null) return "no camera yet";
            string b64 = null;
            try { b64 = (string)RunOnMainThread(() => (object)CaptureImageB64(), 10000); }
            catch (Exception e) { return "capture failed: " + Sanitize(e.Message); }
            if (string.IsNullOrEmpty(b64)) return "capture failed (no render texture)";
            _pendingShotB64 = b64;
            return "attached your current view";
        }

        // cat player — your player, watched live (no memory needed): where they are,
        // what they're doing, what they hold. The way other players "just know".
        private string CatPlayer()
        {
            var s = PlayerTrail.Snap;
            if (s == null || !s.Has || Time.unscaledTime - s.SampleTime > 10f)
                return "(can't see your player right now — not in the world currently? `ps` shows remote players)";
            if (!IsAlive(_kobold)) return "no body";
            var chat = PlayerChatName();
            Vector3 d = s.Pos - _kobold.transform.position;
            var sb = new StringBuilder();
            sb.Append("your player").Append(chat != null ? " (" + chat + ")" : "").Append(": ");
            sb.Append(F(d.magnitude)).Append("m ").Append(Compass.NameOfOffset(d.x, d.z)).Append(" of you — ")
              .Append(PlayerActivityWords(s.Ragdolled, s.InStation, s.StationKind, s.StationName, s.Vel, s.Heading)).Append('\n');
            if (s.Held != null && s.Held.Count > 0)
            {
                sb.Append("  holds: ");
                foreach (var h in s.Held) sb.Append(h.Name + " (" + h.Kind + ") ");
                sb.Append('\n');
            }
            try
            {
                if (s.StationTf != null)
                {
                    int sid = TargetIdFor(s.StationTf, s.StationName);
                    var u = s.StationTf.GetComponentInParent<GenericUsable>();
                    bool seatFree = u == null || u.CanUse(_kobold);
                    sb.Append("  station: ").Append(s.StationName).Append("  id:").Append(sid)
                      .Append(seatFree ? "  — use id:" + sid + " to join/interact it" : "  — seat blocked/taken for your body (try another or ask)")
                      .Append('\n');
                }
            }
            catch (Exception) { }
            sb.Append("  (watched live, never stored — infer what they're trying to do and offer help like another player would)");
            return sb.ToString().TrimEnd();
        }

        private string CmdHelp(ConsoleLine line) { return ConsoleShell.Help(); }
        private string CmdClear(ConsoleLine line) { return "ok"; }

        // ------------------------------------------------------------------
        // formatting helpers
        // ------------------------------------------------------------------
        // Safe string get from a perception/entry dict (values may be float/string/null).
        private static string G(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            return v.ToString();
        }

        // Any tool result (anonymous type or dict) → Dictionary, via the Json round-trip.
        private static Dictionary<string, object> ToDict(object result)
        {
            if (result == null) return null;
            if (result is Dictionary<string, object>) return (Dictionary<string, object>)result;
            try
            {
                string s = Json.Write(result);
                if (string.IsNullOrEmpty(s) || s == "null") return null;
                return Json.Parse(s) as Dictionary<string, object>;
            }
            catch (Exception) { return null; }
        }

        // Compact result → one or a few lines of terminal output.
        private string FmtResult(object result)
        {
            try
            {
                var d = ToDict(result);
                if (d == null)
                {
                    string s = result == null ? "null" : result.ToString();
                    return s.Length > 120 ? s.Substring(0, 120) : s;
                }
                bool ok = false;
                object ov;
                if (d.TryGetValue("ok", out ov)) ok = ov is bool ? (bool)ov : ov != null && ov.ToString() == "True";
                if (!ok)
                {
                    var sb = new StringBuilder();
                    string reason = G(d, "reason");
                    if (reason.Length > 0) sb.Append(reason);
                    string failNote = G(d, "note");
                    if (failNote.Length > 0) sb.Append(sb.Length > 0 ? " — " : "").Append(failNote);
                    string hint = G(d, "hint");
                    if (hint.Length > 0) sb.Append(" (").Append(hint).Append(")");
                    string msg = sb.ToString();
                    return msg.Length > 0 ? msg : "failed";
                }
                // ok: special shapes first (sector scans, survey hits)
                object scanObj;
                if (d.TryGetValue("scan", out scanObj))
                {
                    var scan = ToDict(scanObj);
                    if (scan != null)
                    {
                        var sb3 = new StringBuilder();
                        foreach (var kv in scan)
                        {
                            string val = kv.Value == null ? "" : kv.Value.ToString();
                            if (val.Length == 0) continue;
                            sb3.Append("  ").Append(kv.Key).Append(": ").Append(Shorten(val, 80)).Append('\n');
                        }
                        string o = sb3.ToString().TrimEnd();
                        if (o.Length > 0) return o;
                    }
                }
                object resObj;
                if (d.TryGetValue("result", out resObj))
                {
                    var res = ToDict(resObj);
                    if (res != null)
                    {
                        var sb4 = new StringBuilder();
                        foreach (var kv in res)
                        {
                            string val;
                            if (kv.Value == null) continue;
                            var vd = ToDict(kv.Value);
                            if (vd != null && vd.ContainsKey("n"))
                            {
                                // survey hit: {n, d, cat, id}
                                val = G(vd, "d").Length > 0 ? G(vd, "d") + "m " : "";
                                val += G(vd, "n");
                                string cat = G(vd, "cat");
                                if (cat.Length > 0 && cat != "geometry") val += " [" + cat + "]";
                                string tid = G(vd, "id");
                                if (tid.Length > 0 && tid != "-1") val += " id:" + tid;
                            }
                            else if (kv.Value is string) val = (string)kv.Value;
                            else val = Json.Write(kv.Value);
                            if (val.Length == 0) continue;
                            sb4.Append("  ").Append(kv.Key).Append(": ").Append(Shorten(val, 100)).Append('\n');
                        }
                        string o = sb4.ToString().TrimEnd();
                        if (o.Length > 0) return o;
                    }
                }
                string used = G(d, "used");
                string to = G(d, "to");
                string dist = G(d, "dist");
                string note = G(d, "note");
                string said = G(d, "said");
                var sb2 = new StringBuilder();
                if (used.Length > 0)
                {
                    sb2.Append("used ").Append(used);
                    string type = G(d, "type");
                    if (type.Length > 0) sb2.Append(" (").Append(type).Append(")");
                    if (dist.Length > 0) sb2.Append(" ").Append(dist).Append("m");
                }
                else if (to.Length > 0) sb2.Append("walking to ").Append(to).Append(" (").Append(dist).Append("m)");
                else if (said.Length > 0) sb2.Append("said: ").Append(said);
                else if (note.Length > 0) sb2.Append(note);
                else sb2.Append("ok");
                return sb2.ToString();
            }
            catch (Exception)
            {
                try { return result == null ? "null" : result.ToString(); } catch (Exception) { return "ok"; }
            }
        }
    }
}
