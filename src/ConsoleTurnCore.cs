// ConsoleTurnCore — the headless half of the console REPL turn engine. The
// console turn loop in src/Console.cs (rounds, streaks, guards, nudges, the
// event/anchor dedup) is where the historical console bugs lived (the idle-ENTER
// transcript runaway, the "now events" anchor flood, the parrot loop), but it was
// Unity-coupled and untestable. This file holds the whole control flow as pure
// C#: the game side supplies an IConsoleTurnEnv (LLM query, command execution,
// transcript, events/anchor, clock, logging) and tests supply a scripted fake —
// a scripted LLM "plays" the agent through whole turns with no game install.
// Console.cs maps the seam onto the real members (ConsoleTurnEnv adapter) and
// keeps the exception wrapper; behavior here must stay a port of that loop.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KKLLMNPC
{
    // Cross-turn console turn state (streaks, throttles, dedup anchors). The
    // instance holds one (`_turn`); ResetMindNow calls Reset() — the same fields
    // the mind wipe cleared when they lived directly on NPCInstance. Deliberately
    // NOT wiped by Reset(): the throttle timestamps and the salvage counter, which
    // also survived the wipe before the move.
    internal sealed class ConsoleTurnState
    {
        public int ProseStreak;            // consecutive no-command replies
        public int EmptyStreak;            // consecutive empty replies
        public string LastCmdKey;          // "cmd:arg1,arg2" for stuck detection
        public int StuckCount;
        public bool StuckBreak;            // hard cap reached — end the turn this cycle
        public bool Slept;                 // `sleep` ran — end the cycle
        public int CmdsThisCycle;          // acting budget spent this cycle
        public int UnknownLines;           // unknown-command lines seen this cycle
        public HashSet<string> EchoSet;    // lines of the last user (terminal) message
        public string LastAssistantReply;  // identical-reply guard
        public int RepeatStreak;
        public bool LastReplyWasBlock;     // last spoken reply was a response block (state-anchor hint)
        public float StarNudgeT;           // star-narration reminder throttle
        public float UnknownNagT = -999f;  // repeated identical unknown-command nag throttle
        public float IdleChatterT;         // talking-to-no-one nudge throttle
        public float LastTriggerT;         // last [chat]/[event] trigger (-999 = never)
        public float TurnStampT;           // previous turn's clock stamp
        public string ClockLine;           // "now: HH:MM:SS (+…)" — read by the state anchor
        public float ClockRebuildT;        // last clock-string rebuild (ClockRebuildInterval)
        public string LastEmittedAnchor;   // anchor dedup

        public ConsoleTurnState()
        {
            LastTriggerT = -999f;
            LastCmdKey = null;
        }

        // Exactly what ResetMindNow used to clear field-by-field.
        public void Reset()
        {
            ProseStreak = 0;
            EmptyStreak = 0;
            LastCmdKey = null;
            StuckCount = 0;
            TurnStampT = 0f;
            ClockLine = null;
            ClockRebuildT = 0f;
            LastEmittedAnchor = null; // fresh boot: emit a real anchor
            Slept = false;
        }
    }

    // Game-side seams the turn engine drives. Console.cs implements it with a
    // nested adapter over the Unity-coupled members; the headless tests
    // implement a scripted fake (fake LLM + fake world terminal).
    internal interface IConsoleTurnEnv
    {
        float Now();                               // Time.unscaledTime
        float SessionStart { get; }                // possession time (uptime clock)
        bool Running { get; }                      // instance loop flag
        string Name { get; }                       // MyName() — log prefixes
        int MaxRounds { get; }                     // [Console] MaxRounds (unclamped)
        bool EndpointConfigured { get; }
        bool EndpointBlocked { get; }              // circuit breaker open
        void EnsureConversation();                 // InitConsoleConversation when wiped
        string DrainEvents();                      // [chat]/[event] queues (consumed)
        string StateAnchor();                      // goal/needs/state line
        void AddMsg(string role, object content);  // transcript append (string or multipart)
        void Trim();                               // rolling-window trim
        string QueryModel();                       // one LLM call; null = endpoint problem
        bool LastReplyPromotedReasoning { get; }   // reply harvested from the reasoning channel
        bool SpeakBlock(string block, bool hadTrigger, bool endTurn, bool promotedReasoning, out bool recorded);
        string RunLine(ConsoleLine line, string pipeIn); // execute one console command
        void PushHistory(string kind, string text);
        void RecordToolCall(string entry);
        void RecordCommand(string raw);
        bool IsCommandRepeat(string raw, out int simCount);
        void BufSink(string text);                 // `tee` stream sink (scratch buffer)
        void AutoScreenshot();                     // MaybeAutoScreenshot hook
        string TakePendingShot();                  // pop the pending screenshot (or null)
        bool ImageCooldownActive { get; }          // server refused images recently
        void NoteSalvage(string rawReply);         // garbled-markup counter + sampling log
        string Goal { get; }
        string LastThought { get; set; }
        void LogInfo(string msg);
        void LogWarning(string msg);
    }

    internal static class ConsoleTurnCore
    {
        private const int ConsoleMaxCmdsPerCycle = 12;

        // One console decision turn: prompt (events + state anchor) → up to
        // MaxRounds of (model reply → terminal output → next prompt). Ends on
        // sleep, a guard trip, prose-as-thought, or the round budget. Throws
        // through — the caller owns the try/catch (main-not-ready, thread abort).
        public static void Run(ConsoleTurnState st, IConsoleTurnEnv env)
        {
            // No endpoint: don't churn the transcript with prompts nobody can answer.
            // Circuit breaker open: the wait already happened in the LLM loop — just
            // skip, and the state anchor (next healthy turn) tells the model why.
            if (!env.EndpointConfigured) return;
            if (env.EndpointBlocked) return;
            env.EnsureConversation();
            st.Slept = false;
            st.CmdsThisCycle = 0;
            st.UnknownLines = 0;   // fresh cycle: no unknown-command escalation yet
            st.StuckBreak = false;

            // TURN CLOCK: stamp this turn with the session time and the gap
            // since the previous turn's prompt. The anchor carries it, so the
            // model sees time passing between its ticks — wall-clock pressure
            // against getting lost in thought ("… +2m07s since your last turn —
            // think, then ACT").
            float stampNow = env.Now();
            float sinceLast = st.TurnStampT > 0f ? stampNow - st.TurnStampT : 0f;
            st.TurnStampT = stampNow;
            // Rebuild the clock string only every ClockRebuildInterval seconds — its
            // per-second advance otherwise defeats the anchor dedup below (the model
            // still reads the same time, just a few seconds stale).
            if (st.ClockRebuildT <= 0f || stampNow - st.ClockRebuildT >= Consts.ClockRebuildInterval)
            {
                st.ClockRebuildT = stampNow;
                float elapsed = stampNow - env.SessionStart;
                if (elapsed < 0f) elapsed = 0f;
                int hh = (int)(elapsed / 3600f), mm = (int)((elapsed % 3600f) / 60f), ss = (int)(elapsed % 60f);
                var clock = new StringBuilder().Append("now: ")
                    .Append(hh.ToString("00")).Append(':').Append(mm.ToString("00")).Append(':').Append(ss.ToString("00"));
                if (sinceLast > 20f)
                {
                    clock.Append(" (+");
                    if (sinceLast >= 90f)
                        clock.Append((int)(sinceLast / 60f)).Append('m');
                    else
                        clock.Append(sinceLast.ToString("0", CultureInfo.InvariantCulture)).Append('s');
                    clock.Append(" since your last turn — time is passing; think, then ACT)");
                }
                st.ClockLine = clock.ToString();
            }

            // Auto-view: give the model its eyes (config [Console] AutoScreenshotTicks).
            env.AutoScreenshot();

            // World events (player chat, reagent events) + state anchor + prompt.
            // events MUST be drained every turn (the queues are consumed) even when
            // we skip re-emitting the anchor — otherwise events would be lost.
            string events = env.DrainEvents();
            string anchor = env.StateAnchor();

            // ANCHOR DEDUP: only re-paste the full state anchor when it (or fresh
            // events) actually changed since the last turn. An idle tick where
            // nothing moved sends a bare poll so the model is still prompted to
            // act — the last real anchor stays in the transcript for context —
            // but the bulky anchor isn't re-sent every tick.
            string userText;
            bool anchorChanged = !string.Equals(anchor, st.LastEmittedAnchor, StringComparison.Ordinal);
            if (events.Length > 0 || anchorChanged)
            {
                userText = events + anchor + "$\n";
                st.LastEmittedAnchor = anchor;
            }
            else
            {
                userText = "$\n"; // bare poll: nothing new since the last turn
            }
            env.AddMsg("user", userText);
            env.Trim(); // tick-boundary trim: the idle ENTER loop (empty replies short-circuit every other trim call) must never outgrow the window

            int maxRounds = Math.Max(1, Math.Min(8, env.MaxRounds));
            bool nudgedNoCmd = false;
            // A trigger this turn = something spoken TO the NPC ([chat]/[event]).
            // Response blocks (rp prose) speak freely on a trigger; without one the
            // solo voice is throttled to the idle-chatter cadence so a block never
            // becomes a monologue.
            bool hadTrigger = events.Length > 0;
            if (hadTrigger) st.LastTriggerT = env.Now();
            // The last user message's lines — a degenerate reply COPIES the terminal
            // ("$ ls" + output rows + even the state anchor) instead of speaking.
            // Copied lines are dropped before parsing so they can't re-execute (an
            // anchor copy once got stored as the literal goal "(none yet) | awake:
            // 0h03m" and echoed in every later anchor). Track the lines actually
            // emitted (the dedup may have sent a bare poll).
            st.EchoSet = LastUserLineSet(userText);

            for (int round = 0; round < maxRounds && env.Running; round++)
            {
                string reply = env.QueryModel();
                if (reply == null) return; // endpoint problem — loop backs off and retries
                // Whether THIS reply was harvested from the reasoning channel —
                // captured now, before anything else can touch the flag.
                bool promotedReasoning = env.LastReplyPromotedReasoning;
                reply = TextUtil.Sanitize(reply).Trim();
                // Reasoning markers must never reach command-line parsing — strip
                // first so reasoning-only replies land in the empty-streak path.
                reply = ModelFamilies.StripThinkBlocks(reply);
                // Models that invent tool-call markup ("ls<|whoami?<k>sleep]") put
                // their whole command queue inside it — recover those commands as
                // lines instead of letting the markup ride inside speech.
                string salvaged = ConsoleSalvage.Clean(reply);
                if (salvaged != reply)
                    env.NoteSalvage(reply); // counter + throttled raw-reply sampling log
                reply = salvaged;

                // IDENTICAL-REPEAT guard: a roleplay-locked model re-sends the same
                // prose (same opening scene, greedy sampling) every round; re-adding
                // it turns the transcript into a wall of the same paragraph and
                // starves everything else. First repeat: note it while executing
                // (it might be a legitimate retry); from the second one onward stop
                // re-executing — inject one corrective and end the turn.
                if (reply.Length > 0 && string.Equals(reply, st.LastAssistantReply, StringComparison.Ordinal))
                    st.RepeatStreak++;
                else
                    st.RepeatStreak = 0;
                if (reply.Length > 0) st.LastAssistantReply = reply;
                if (reply.Length > 0 && st.RepeatStreak >= 2)
                {
                    env.LogWarning("[" + env.Name + "] console: identical reply " + st.RepeatStreak
                        + "x — turn ended (" + TextUtil.Shorten(reply, 80) + ")");
                    env.AddMsg("user", "You have replied with THE SAME text several times in a row. That is not a command. "
                        + "Break out NOW: send an actual command line (ls, cat needs, cd somewhere, echo <words>), add a plan step, or sleep.\n$\n");
                    env.Trim();
                    return;
                }
                env.AddMsg("assistant", reply);

                if (reply.Length == 0)
                {
                    st.EmptyStreak++;
                    st.ProseStreak++;
                    if (st.EmptyStreak < 3)
                    {
                        // An EMPTY reply = they pressed ENTER. That is not a failure;
                        // it's a deliberate "give me the next frame" — deliver one:
                        // any fresh events since the turn began + the live state
                        // anchor. Two presses are honored, then back off (a mind
                        // stuck mashing enter gets the corrective instead).
                        env.Trim(); // the empty path skips all other trims — without this the idle ENTER loop grows the transcript a frame per tick (an overnight 1398-message / 146k-token runaway)
                        string fresh = env.DrainEvents();
                        env.AddMsg("user", (fresh.Length > 0 ? fresh : "")
                            + "you pressed ENTER — nothing to run; here is the next frame instead."
                            + " Act on it or type a command (help lists them; an empty reply is always a 'next frame' ask).\n"
                            + env.StateAnchor() + "\n$\n");
                        continue;
                    }
                    env.LogWarning("[" + env.Name + "] console: repeated empty replies — backing off");
                    return;
                }

                string filtered = FilterTranscriptEcho(reply, st.EchoSet, out int echoCount);
                var lines = ConsoleShell.ParseReply(filtered);
                if (lines.Count == 0 && echoCount > 0)
                {
                    // The whole "reply" was a copy of the terminal — never execute.
                    st.ProseStreak++;
                    env.AddMsg("user", "That was a COPY of the terminal (" + echoCount
                        + " lines), not commands from you. Send NEW command lines only — no '$' prompt, no copied output (try: help).\n$\n");
                    env.Trim();
                    continue;
                }
                var known = new List<ConsoleLine>();
                foreach (var l in lines) if (!l.IsUnknown) known.Add(l);
                // Mixed reply (roleplay/response block + commands): the RP-marked
                // prose IS a chat line — send it (say), don't bury it as a thought.
                // Un-marked reasoning in a mixed reply still lands as the thought.
                if (known.Count > 0 && lines.Count > known.Count)
                {
                    var proseParts = new List<string>();
                    foreach (var l in lines)
                        if (l.IsUnknown && l.Raw.Trim().Length > 0) proseParts.Add(l.Raw.Trim());
                    string proseBlock = string.Join(" ", proseParts.ToArray());
                    bool recorded;
                    if (env.SpeakBlock(proseBlock, hadTrigger, false, promotedReasoning, out recorded))
                        st.ProseStreak = 0;
                    else if (!recorded && proseBlock.Length > 4)
                        // Only record here when the block wasn't a response block (maybe-
                        // speak left it unrecorded); a spoken/private block already pushed
                        // its planning remainder, so pushing again would double-record it.
                        env.PushHistory("think", proseBlock.Length > 60 ? proseBlock.Substring(0, 60) : proseBlock);
                }

                if (known.Count == 0)
                {
                    // "$"/">"-only "commands" are the same ENTER gesture written
                    // with markers — serve the next frame, not a prose nudge.
                    bool onlyPromptMarks = reply.Trim().Length > 0;
                    if (onlyPromptMarks)
                        foreach (char c in reply)
                            if ("$># \t\n".IndexOf(c) < 0) { onlyPromptMarks = false; break; }
                    if (onlyPromptMarks)
                    {
                        st.EmptyStreak++;
                        if (st.EmptyStreak < 3)
                        {
                            env.Trim(); // same empty-path trim
                            string fresh2 = env.DrainEvents();
                            env.AddMsg("user", (fresh2.Length > 0 ? fresh2 : "")
                                + "you pressed ENTER — nothing to run; here is the next frame instead."
                                + " Act on it or type a command (an empty reply is always a 'next frame' ask).\n"
                                + env.StateAnchor() + "\n$\n");
                            continue;
                        }
                        env.LogWarning("[" + env.Name + "] console: repeated empty/prompt-marker replies — backing off");
                        return;
                    }
                    // A RESPONSE BLOCK: the model answered in roleplay (dialogue /
                    // asterisk actions / {{user}}). That IS its voice — send the
                    // whole block to chat instead of demanding `echo`. Speaking is
                    // the turn; no nudge.
                    bool recorded;
                    if (env.SpeakBlock(reply, hadTrigger, true, promotedReasoning, out recorded))
                    {
                        st.ProseStreak = 0;
                        st.EmptyStreak = 0;
                        // A block that NARRATES physical acts ("*walks to the
                        // kitchen*") is fiction until a command runs — remind,
                        // throttled, so the intent lands as a cd next turn.
                        if (TextUtil.StarsWantRealActions(reply)
                            && env.Now() - st.StarNudgeT > 45f)
                        {
                            st.StarNudgeT = env.Now();
                            env.AddMsg("user", "(your *starred action* was narration only — the game did not move. What you *do* in stars is words to the reader; the world only changes through commands: cd <place> / use <thing> / get — send one now.)\n$\n");
                            st.RepeatStreak = 0; // the corrective row breaks the pattern, not the guard
                        }
                        env.Trim();
                        return;
                    }
                    // No commands at all: prose/thought. Give nudges, then
                    // accept it as a thought (don't punish thinking out loud forever).
                    st.ProseStreak++;

                    string nudgeMsg;
                    if (st.ProseStreak == 1)
                    {
                        nudgeMsg = "That's not a command line. Reply with commands; reasoning goes in `think <text>` (private), a roleplay block is spoken.\n$\n";
                    }
                    else if (st.ProseStreak == 2)
                    {
                        // More direct guidance for prose writers
                        nudgeMsg = "REPLY WITH COMMAND LINES ONLY — plain thoughts are refused; use `think <text>` for private reasoning; a ROLEPLAY block (quoted dialogue / *actions*) is accepted and spoken.\nTry: ls, cd, echo, think, sleep\n$\n";
                    }
                    else if (st.ProseStreak >= 3 && st.ProseStreak < 5)
                    {
                        // Offer help and state the problem
                        nudgeMsg = "COMMAND LINES ONLY (or a roleplay response block — quoted dialogue/*actions* — which is sent to chat).\nType 'help'.\nCurrent goal: " + (string.IsNullOrEmpty(env.Goal) ? "(none)" : env.Goal) + "\n$\n";
                    }
                    else
                    {
                        // Final warning before treating as thought
                        nudgeMsg = "Last chance: commands only. Or type sleep to end the turn.\n$\n";
                    }

                    if (!nudgedNoCmd && st.ProseStreak < 5)
                    {
                        nudgedNoCmd = true;
                        env.AddMsg("user", nudgeMsg);
                        continue;
                    }
                    env.LastThought = reply.Length > 80 ? reply.Substring(0, 80) : reply;
                    env.PushHistory("think", reply.Length > 60 ? reply.Substring(0, 60) : reply);
                    env.LogInfo("[" + env.Name + "] console thought: " + (reply.Length > 120 ? reply.Substring(0, 120) + "..." : reply));
                    env.Trim();
                    return;
                }

                // ---- execute the command lines, print terminal-style output ----
                st.ProseStreak = 0;
                st.EmptyStreak = 0;
                st.LastReplyWasBlock = false;
                var outSb = new StringBuilder();
                bool onlySpeech = true; // every executed line was echo/emote — talking to no one?
                int executed = 0;
                bool turnLimitHit = false;
                string pipeText = null; // previous stage's output (pipe input)
                bool pipeReady = false;
                foreach (var line in lines)
                {
                    bool feedPipe = pipeReady;
                    pipeReady = false;
                    // SCRATCH commands (think/buf/calc) have no game effect, so
                    // they ride FREE like the stream filters — a thinking-heavy
                    // model must never burn its acting budget (or learn to smuggle
                    // thoughts into speech again) and never counts as "did
                    // something" for the talking-to-nobody guard.
                    bool isScratch = !line.IsUnknown
                        && (line.Cmd == "think" || line.Cmd == "buf" || line.Cmd == "calc");
                    if (!line.IsFilter && !isScratch && st.CmdsThisCycle >= ConsoleMaxCmdsPerCycle)
                    {
                        // `sleep` is the turn terminator the prompts TELL the model to
                        // send — honor it even with the budget spent, or the model gets
                        // "sleep to continue later" followed by sleep being refused and
                        // stalls in a [limit] loop forever (playtest evidence: the agent
                        // never walked again).
                        if (!line.IsUnknown && line.Cmd == "sleep")
                        {
                            outSb.Append("$ ").AppendLine(line.Raw);
                            outSb.AppendLine(env.RunLine(line, null));
                            st.Slept = true;
                            break;
                        }
                        outSb.AppendLine("[limit] command limit for this turn reached — end the turn now; fresh budget next turn");
                        turnLimitHit = true;
                        break;
                    }
                    outSb.Append("$ ").AppendLine(line.Raw);
                    string output;
                    if (line.IsFilter)
                    {
                        // Stream filter over the piped input; FREE — no game
                        // effect, no command slot, no history bookkeeping. The
                        // sink lets `tee` copy the stream into the scratch buffer
                        // while passing it on.
                        output = ConsolePipe.RunFilter(line.Cmd, line.Args, feedPipe ? pipeText : null, env.BufSink);
                        pipeText = output;
                        pipeReady = line.PipeDown;
                        outSb.AppendLine(output);
                        continue;
                    }
                    if (line.IsUnknown)
                    {
                        // Escalating unknown-command hint (was: silent for 45s, then one
                        // generic line — small models just looped on the bad word). Keep
                        // the first nag quiet so a stray token doesn't clutter the turn,
                        // then escalate: once the model has produced ≥2 unknown lines this
                        // cycle, hand it a concrete "did you mean / try help" nudge so it
                        // stops firing blind commands. The 45s throttle still caps the
                        // repeated identical nags so it can't spam.
                        if (st.UnknownLines >= 2 && env.Now() - st.UnknownNagT >= 45f)
                        {
                            st.UnknownNagT = env.Now();
                            output = "unknown command — type `help` to see what you can do, or `man <topic>` for the rules; don't keep firing words that aren't commands.";
                        }
                        else output = ""; // first unknown: stay quiet (it's likely a parse artifact)
                        st.UnknownLines++;
                    }
                    else
                    {
                        if (!isScratch) { st.CmdsThisCycle++; executed++; }
                        output = env.RunLine(line, feedPipe ? pipeText : null);
                        if (!isScratch && line.Cmd != "echo" && line.Cmd != "emote") onlySpeech = false;
                        // A suppressed echo/emote (repeat guard) consumed no real
                        // action — give the budget slot back: the playtest stalled in
                        // a permanent [limit] loop because the model's repeated
                        // echoes ate the budget until `sleep` itself was refused.
                        if ((line.Cmd == "echo" || line.Cmd == "emote")
                            && output != null
                            && output.IndexOf("suppressed", StringComparison.OrdinalIgnoreCase) == 0)
                            st.CmdsThisCycle--;
                        // Same tool-log the report tool attaches — console commands
                        // deserve it too ("report sonar garbage" needs the receipts).
                        env.RecordToolCall(line.Cmd + "(" + string.Join(",", line.Args ?? new string[0]) + ") -> " + TextUtil.Shorten(output, 40));
                    }
                    outSb.AppendLine(output);
                    if (line.IsUnknown) continue;

                    // Pipe wiring: a PipeDown stage hands its output to the next
                    // line as filter input (stages without it reset the pipe).
                    if (line.PipeDown) { pipeText = output; pipeReady = true; }
                    else { pipeText = null; pipeReady = false; }

                    // history for `cat history` (think records its own content
                    // inside CmdThink; buf/calc are pure scratch — no history)
                    if (!isScratch)
                    {
                        string hist = TextUtil.Shorten(output, 60);
                        env.PushHistory(line.Cmd, hist);
                    }

                    // Record command for similarity tracking
                    env.RecordCommand(line.Raw);

                    // stuck detection: improved semantic similarity check + exact repeat
                    int simCount = 0;
                    bool isRepeat = env.IsCommandRepeat(line.Raw, out simCount);

                    string key = line.Cmd + ":" + string.Join(",", line.Args);
                    if (key == st.LastCmdKey || isRepeat)
                    {
                        st.StuckCount++;
                        // Progressive escalation (was: one static line forever — the model
                        // just looped on the same failing command). Each rung gives more
                        // concrete guidance, and the hard cap ENDS the turn so a stuck
                        // model can't burn the whole cycle repeating itself.
                        if (st.StuckCount == 2)
                            outSb.AppendLine("stuck — you're repeating similar commands. Try something different, or check `cat goal` / `cat places` / `ls` for what's actually here.");
                        else if (st.StuckCount == 3)
                            outSb.AppendLine("still stuck on the same thing (" + line.Cmd + "). A failing command prints WHY — read it and change approach, or type `help` to see your options.");
                        else if (st.StuckCount >= 4)
                        {
                            outSb.AppendLine("you've tried " + line.Cmd + " many times with no luck. Stop repeating it — `sleep` to reset, or `man` / `help` for guidance.");
                            st.StuckBreak = true; // end this turn; a fresh cycle starts next tick
                        }
                    }
                    else st.StuckCount = 0;
                    st.LastCmdKey = key;

                    if (line.Cmd == "sleep") st.Slept = true;
                    if (st.StuckBreak) st.Slept = true; // reuse the end-cycle path
                }
                // The budget is spent — stop asking the model THIS turn (asking on
                // just produced rounds of "[limit]" + a sleep refusal in the same
                // turn). A fresh budget comes with the next tick.
                if (turnLimitHit) break;
                // Talking-to-no-one guard: a turn of pure echo/emote with NO [chat]
                // or [event] behind it is the model conversing with its own past
                // lines ("responding to itself in a past message"). Nudge it toward
                // acting or sleeping instead, throttled so it can't loop nagging.
                if (!hadTrigger && onlySpeech && executed > 0
                    && env.Now() - st.IdleChatterT > 90f)
                {
                    st.IdleChatterT = env.Now();
                    outSb.AppendLine("(nobody spoke this turn — that message went to nobody. Don't re-greet or talk to yourself; DO something: ls / cd somewhere / goal, or sleep)");
                }
                if (echoCount > 0 && executed > 0)
                    outSb.AppendLine("(skipped " + echoCount + " echoed terminal line(s) — the terminal already showed those; send NEW commands only)");
                outSb.Append("$\n");

                // screenshot output carries the image part
                string shot = env.TakePendingShot();
                if (shot != null)
                {
                    if (env.ImageCooldownActive)
                    {
                        // The server rejected image parts recently — don't burn the
                        // screenshot into a guaranteed 400; tell the model instead.
                        outSb.AppendLine("(image skipped: the server refused image parts recently — the mod paused images; screenshot again in a couple of minutes)");
                        env.AddMsg("user", outSb.ToString());
                    }
                    else
                    {
                        var parts = new List<object>
                        {
                            new Dictionary<string, object> { ["type"] = "text", ["text"] = outSb.ToString() },
                            new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = shot } },
                        };
                        env.AddMsg("user", parts.ToArray());
                    }
                }
                else
                {
                    env.AddMsg("user", outSb.ToString());
                }
                st.EchoSet = LastUserLineSet(outSb.ToString());
                if (st.Slept) break;
            }
            env.Trim();
        }

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
        private static string FilterTranscriptEcho(string reply, HashSet<string> set, out int echoCount)
        {
            echoCount = 0;
            if (string.IsNullOrEmpty(reply) || set == null || set.Count == 0) return reply;
            var keep = new StringBuilder();
            foreach (var rawLine in reply.Split('\n'))
            {
                string t = rawLine.Trim();
                if (t.Length == 0) { keep.Append(rawLine).Append('\n'); continue; }
                t = TextUtil.ScrubTypeNames(t);
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
    }
}
