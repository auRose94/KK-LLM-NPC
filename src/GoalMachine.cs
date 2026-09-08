// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Persistent goal holder — the NPC's "bigger thinking" lives here, not re-derived each tick.
//
// The per-tick LLM call is deliberately slim: it takes the NEXT STEP toward the current
// goal instead of re-deliberating what the goal is. The LLM steers the machine via three
// tools (set_goal / complete_goal / drop_goal). This is also the main fix for two observed
// failure modes:
//   * repetition — the model used to re-assert the same goal every tick; now the goal is
//     stored once and a repetition guard nudges it to act / finish / abandon instead.
//   * slow forgetting — completing or dropping a goal clears it and sheds its scratch facts,
//     so finished business doesn't linger in context.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // A goal that was dropped recently, so the model doesn't immediately re-set it
        // (flapping between "drop" and "set").
        private class DroppedGoal { public string Text; public int Tick; }

        // ---- goal machine state (written on the LLM thread, read on the main thread) ----
        private readonly object _goalLock = new Object();
        private string _goal;              // the current goal (the "bigger thinking")
        private int _goalTick = -1;        // _tick when the goal was (re)set
        private int _goalRepeats = 0;      // times the model re-asserted the same goal
        private string _goalProgress = ""; // last progress note on the current goal
        private readonly List<DroppedGoal> _recentlyDropped = new List<DroppedGoal>();

        // Thought repetition guard (for models that don't use the goal tools).
        private string _lastThoughtText;
        private int _thoughtRepeat = 0;

        // Repetition guard threshold: after this many re-assertions of the same goal with no
        // completion, start nudging the model to act / finish / abandon.
        private const int GoalRepeatNudgeAt = 2;

        // How long (in ticks) a dropped/completed goal stays "recently finished" — both for
        // the 'goal.recently_dropped' perception list and the ToolSetGoal flap guard.
        private const int RecentlyFinishedWindow = 40;

        // ------------------------------------------------------------------
        // goal tools
        // ------------------------------------------------------------------
        // Set or replace the current goal. This is where the "bigger thinking" goes.
        // If the model re-asserts a goal it already has, that counts as a repeat (the
        // repetition guard), not a fresh goal — so re-asserting isn't free.
        private object ToolSetGoal(JsonObj p)
        {
            string goal = Sanitize(p.S("goal", "").Trim());
            string why = p.S("why", "").Trim();
            if (goal.Length == 0) return new { ok = false, reason = "empty_goal" };

            lock (_goalLock)
            {
                bool same = !string.IsNullOrEmpty(_goal) &&
                    string.Equals(NormalizeGoal(_goal), NormalizeGoal(goal), StringComparison.Ordinal);
                if (same)
                {
                    // Re-asserting the goal it already has: bump the repeat counter and
                    // gently push it to ACT on the goal rather than re-declare it.
                    _goalRepeats++;
                    _goalProgress = why.Length > 0 ? why : _goalProgress;
                    PushHistory("goal", "reassert#" + _goalRepeats + (why.Length > 0 ? " " + why : ""));
                    Logger.LogInfo("goal re-asserted (" + _goalRepeats + "x): " + _goal);
                    string nudge = _goalRepeats >= GoalRepeatNudgeAt
                        ? "You already have this goal. Don't re-declare it — take the NEXT CONCRETE STEP toward it, or complete_goal if it's done."
                        : "Goal unchanged. Take a concrete step toward it.";
                    return new { ok = true, goal = _goal, repeats = _goalRepeats, note = nudge };
                }

                // Flap guard: refuse to re-set a goal dropped/completed within the recent
                // window. Perception shows 'goal.recently_dropped'; this ENFORCES it, so
                // weaker models can't loop set->drop->set. (Call while holding _goalLock.)
                if (MatchesRecentlyFinished(goal))
                {
                    PushHistory("goal", "refused(recent):" + goal);
                    Logger.LogInfo("goal refused (recently dropped/completed): " + goal);
                    return new { ok = false, reason = "recently_dropped",
                        note = "You just dropped or completed that goal — don't re-set it. If it's the same ongoing task, just keep acting on it (go_to/interact). Otherwise pick a DIFFERENT goal or act freely." };
                }

                // A genuinely new goal. Record what it replaces so we can shed its facts.
                string previous = _goal;
                _goal = goal;
                _goalTick = _tick;
                _goalRepeats = 0;
                _goalProgress = why;
                // A fresh goal supersedes the old one — shed the old goal's scratch facts
                // so they stop competing for the model's attention (forgetting).
                if (!string.IsNullOrEmpty(previous))
                    ShedFactsForGoal(previous);
                PushHistory("goal", "set:" + goal + (why.Length > 0 ? " (" + why + ")" : ""));
                Logger.LogInfo("goal set: " + goal + (why.Length > 0 ? " — " + why : ""));
                return new { ok = true, goal = _goal, note = "goal stored. Each turn now: take the next step toward it. complete_goal when done." };
            }
        }

        // Mark the current goal done: clears it, sheds its scratch facts, and records it as
        // recently completed so the model doesn't immediately re-set it.
        private object ToolCompleteGoal(JsonObj p)
        {
            string note = Sanitize(p.S("note", "").Trim());
            string completed;
            lock (_goalLock)
            {
                completed = _goal;
                if (string.IsNullOrEmpty(completed))
                    return new { ok = false, reason = "no_goal", note = "no active goal to complete — set_goal first" };
                _recentlyFinished(completed);
                _goal = null;
                _goalTick = -1;
                _goalRepeats = 0;
                _goalProgress = "";
                ShedFactsForGoal(completed);
            }
            PushHistory("goal", "done:" + completed + (note.Length > 0 ? " (" + note + ")" : ""));
            Logger.LogInfo("goal complete: " + completed);
            return new { ok = true, completed = completed, note = "goal cleared. Pick a new one with set_goal or act freely." };
        }

        // Abandon the current goal (blocked, or a better idea). Records it as dropped so the
        // model doesn't flapping back to it, and sheds its scratch facts.
        private object ToolDropGoal(JsonObj p)
        {
            string reason = Sanitize(p.S("reason", "").Trim());
            string dropped;
            lock (_goalLock)
            {
                dropped = _goal;
                if (string.IsNullOrEmpty(dropped))
                    return new { ok = false, reason = "no_goal", note = "no active goal to drop" };
                _recentlyDropped.Add(new DroppedGoal { Text = dropped, Tick = _tick });
                while (_recentlyDropped.Count > 8) _recentlyDropped.RemoveAt(0);
                _goal = null;
                _goalTick = -1;
                _goalRepeats = 0;
                _goalProgress = "";
                ShedFactsForGoal(dropped);
            }
            PushHistory("goal", "drop:" + dropped + (reason.Length > 0 ? " (" + reason + ")" : ""));
            Logger.LogInfo("goal dropped: " + dropped + (reason.Length > 0 ? " — " + reason : ""));
            return new { ok = true, dropped = dropped, note = "goal abandoned. Don't re-set it for a bit — pick something else." };
        }

        private void _recentlyFinished(string goal)
        {
            // Completed goals also go into the recently-dropped ring so the model doesn't
            // instantly re-set the goal it just finished.
            _recentlyDropped.Add(new DroppedGoal { Text = goal, Tick = _tick });
            while (_recentlyDropped.Count > 8) _recentlyDropped.RemoveAt(0);
        }

        // Called on each tick (LLM thread) right after we learn the model's 'thought'.
        // Drives the thought-repetition guard for models that don't use the goal tools.
        internal void NoteThought(string thought)
        {
            if (string.IsNullOrWhiteSpace(thought)) return;
            string norm = NormalizeGoal(thought);
            lock (_goalLock)
            {
                if (string.Equals(norm, NormalizeGoal(_lastThoughtText), StringComparison.Ordinal))
                    _thoughtRepeat++;
                else
                {
                    _thoughtRepeat = 0;
                    _lastThoughtText = thought;
                }
            }
        }

        // A short nudge to inject into perception when the model is stuck repeating.
        // Returns null when there's nothing to say.
        internal string RepetitionNudge()
        {
            lock (_goalLock)
            {
                if (_goalRepeats >= GoalRepeatNudgeAt && !string.IsNullOrEmpty(_goal))
                    return "STUCK: you've re-asserted '" + _goal + "' " + _goalRepeats + "x without finishing. Do ONE of: (a) a concrete different action, (b) complete_goal if it's actually done, (c) drop_goal and choose something else.";
                if (_thoughtRepeat >= 3)
                    return "STUCK: same thought 3+ turns in a row. Change what you DO, not just what you say.";
            }
            return null;
        }

        // Perception snapshot of the goal machine (main thread).
        internal object GoalPerception()
        {
            lock (_goalLock)
            {
                if (string.IsNullOrEmpty(_goal))
                    return (object)new
                    {
                        goal = (object)null,
                        goal_for = 0,
                        repeats = 0,
                        progress = (object)null,
                        recently_dropped = DroppedList(),
                        hint = "No active goal. If you have something to do, set_goal(goal='...') once, then work it. Don't re-derive it every turn.",
                    };
                int age = _tick - _goalTick;
                return new
                {
                    goal = _goal,
                    goal_for = age,
                    repeats = _goalRepeats,
                    progress = _goalProgress.Length > 0 ? _goalProgress : null,
                    recently_dropped = DroppedList(),
                    hint = "Work this goal. Next turn: the next concrete step. complete_goal when it's done; drop_goal to abandon.",
                };
            }
        }

        private string[] DroppedList()
        {
            // Drop entries older than the recent window so the ring doesn't haunt forever.
            var out_ = new List<string>();
            foreach (var d in _recentlyDropped)
            {
                if (_tick - d.Tick > RecentlyFinishedWindow) continue;
                out_.Add(d.Text);
            }
            return out_.Count > 0 ? out_.ToArray() : new string[0];
        }

        // True when this goal text matches a goal dropped/completed within the recent
        // window. Call while holding _goalLock.
        private bool MatchesRecentlyFinished(string goal)
        {
            string norm = NormalizeGoal(goal);
            for (int i = _recentlyDropped.Count - 1; i >= 0; i--)
            {
                if (_tick - _recentlyDropped[i].Tick > RecentlyFinishedWindow) continue;
                if (string.Equals(NormalizeGoal(_recentlyDropped[i].Text), norm, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Normalize a goal/thought for comparison (case/punct/whitespace-insensitive).
        private static string NormalizeGoal(string s)
        {
            if (s == null) return "";
            s = s.Trim().ToLowerInvariant();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // Shed "scratch" facts that belong to a finished/abandoned goal so they stop
        // lingering in context (the forgetting half of the goal machine).
        //
        // Safety: only a fact whose category prefix matches the goal's AND that shares
        // >=2 significant words with the goal text is removed. Without the word overlap,
        // a goal phrased "nest: lay an egg" would wipe the map fact "nest: upstairs" —
        // world-map facts must survive, only goal scratch notes go.
        private void ShedFactsForGoal(string goal)
        {
            if (string.IsNullOrEmpty(goal) || goal.IndexOf(':') < 0) return;
            string prefix = goal.Split(':')[0];
            var goalWords = SignificantWords(goal);
            lock (_facts)
            {
                for (int i = _facts.Count - 1; i >= 0; i--)
                {
                    string f = _facts[i].Text;
                    if (f.Split(':')[0] != prefix) continue;
                    if (SharedWordCount(f, goalWords) < 2) continue;
                    _facts.RemoveAt(i);
                }
            }
        }

        private static readonly char[] WordSeparators = new[] { ' ', '\t', '\n', '\r', ',', ';', ':', '.', '(', ')', '[', ']', '{', '}', '/', '-', '\\', '"', '\'', '`', '!' };
        private static readonly string[] StopWords = { "the", "and", "for", "with", "your", "from", "into", "onto", "over", "under", "just", "very", "some", "this", "that", "then", "than", "have", "will", "would", "could", "should", "their", "there", "where", "when", "what", "which", "who", "how", "why", "been", "being", "also", "only", "even", "much", "many", "other", "again", "about", "after", "before", "between", "during", "while", "because", "until", "within", "without", "don't", "dont", "can't", "cant", "it's", "its" };

        private static bool IsSignificantWord(string w)
        {
            return w.Length >= 4 && Array.IndexOf(StopWords, w) < 0;
        }

        // Significant words of s (lowercased tokens, len>=4, not stopwords).
        private static List<string> SignificantWords(string s)
        {
            var list = new List<string>();
            foreach (var w in s.ToLowerInvariant().Split(WordSeparators))
                if (IsSignificantWord(w)) list.Add(w);
            return list;
        }

        // How many significant words of 'fact' also appear in 'goalWords'.
        private static int SharedWordCount(string fact, List<string> goalWords)
        {
            int n = 0;
            foreach (var w in fact.ToLowerInvariant().Split(WordSeparators))
                if (IsSignificantWord(w) && goalWords.IndexOf(w) >= 0) n++;
            return n;
        }
    }
}
