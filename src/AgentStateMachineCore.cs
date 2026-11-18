// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// AgentStateMachineCore — the LLM-PROGRAMMED STATE MACHINE (v1.12.0, Rose's
// design): instead of one LLM poll per NPC per beat, the model AUTHORS a small
// behavior machine ("when a player says hi -> say hi back"; "timer 300 -> say
// something idle") and the runtime executes it locally, for free. The LLM
// polls only when needed — the token economy for multi-avatar play — and as
// sessions grow, machines accumulate situations and lines that a FUTURE agent
// or a child inheriting the body's saved life can run (machines are stored as
// skill entries, which saved lives already carry).
//
// DSL (stored as skill steps; `;` splits steps like skills, `then` chains
// actions inside one rule):
//   state idle
//   enter -> say "waking up"
//   on chat *hello* -> say "Hi! I'm around" then goto idle
//   on event *points at* -> say "ooh, where?" then goto attentive
//   timer 300 -> say "still here~" then goto idle
//   state attentive
//   timer 20 -> goto idle
//
// Triggers: enter (fired once on state entry), on event <pattern>, on chat
// <pattern> (both substring match, * = any run of chars), timer <seconds>
// (state dwell). Actions: say <text>, cmd <line> (a real console command),
// emit <text> (world event), goto <state>. Every tick fires AT MOST ONE rule
// and executes at most MaxActionsPerTick actions — a machine can loop forever
// without spinning the runtime.
//
// Pure C#: no UnityEngine — compiled into the headless test suite
// (tests/test_agentfsm.cs).
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class AgentStateMachineCore
    {
        public const int MaxActionsPerTick = 4;
        public const int MaxStates = 16;
        public const int MaxRulesPerState = 12;
        public const int MaxRuleChars = 240;

        internal sealed class Action
        {
            public string Kind;  // say | cmd | emit
            public string Text;
        }

        internal sealed class Rule
        {
            public string TriggerKind;  // enter | event | chat | timer
            public string Pattern;      // event/chat substring ('*' wildcard); timer seconds as text
            public List<Action> Actions = new List<Action>();
            public string Goto;         // state name (null = stay)
        }

        internal sealed class State
        {
            public string Name;
            public List<Rule> Rules = new List<Rule>();
        }

        internal sealed class Machine
        {
            public string Start = "idle";
            public List<State> States = new List<State>();

            public State Find(string name)
            {
                foreach (var s in States)
                    if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
                return null;
            }
        }

        // ------------------------------------------------------------------
        // parse — from skill-step lines (each step = one DSL line)
        // ------------------------------------------------------------------
        public static Machine Parse(IEnumerable<string> steps)
        {
            var m = new Machine();
            if (steps == null) return m;
            State cur = null;
            foreach (var raw in steps)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                string low = line.ToLowerInvariant();
                if (low.StartsWith("machine ", StringComparison.Ordinal))
                {
                    m.Start = low.Substring(8).Trim();
                    continue;
                }
                if (low.StartsWith("start ", StringComparison.Ordinal))
                {
                    m.Start = low.Substring(6).Trim();
                    continue;
                }
                if (low.StartsWith("state ", StringComparison.Ordinal))
                {
                    string name = line.Substring(6).Trim();
                    cur = m.Find(name);
                    if (cur == null)
                    {
                        if (m.States.Count >= MaxStates) break;
                        cur = new State { Name = name };
                        m.States.Add(cur);
                    }
                    continue;
                }
                var r = ParseRule(line);
                if (r != null)
                {
                    if (cur == null) { cur = new State { Name = "idle" }; m.States.Add(cur); }
                    if (cur.Rules.Count < MaxRulesPerState && line.Length <= MaxRuleChars) cur.Rules.Add(r);
                }
            }
            if (m.States.Count == 0) { m.States.Add(new State { Name = m.Start ?? "idle" }); }
            if (m.Find(m.Start) == null) m.Start = m.States[0].Name;
            return m;
        }

        // One rule line: "<trigger> -> <action>[ then <action>]*".
        // Triggers: enter | on chat <pat> | on event <pat> | timer <seconds>.
        public static Rule ParseRule(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) return null;
            string trig = line.Substring(0, arrow).Trim();
            string act = line.Substring(arrow + 2).Trim();
            if (act.Length == 0) return null;

            var r = new Rule();
            string tl = trig.ToLowerInvariant();
            if (tl == "enter") r.TriggerKind = "enter";
            else if (tl.StartsWith("on chat", StringComparison.Ordinal)) { r.TriggerKind = "chat"; r.Pattern = Unquote(trig.Substring(7).Trim()); }
            else if (tl.StartsWith("on event", StringComparison.Ordinal)) { r.TriggerKind = "event"; r.Pattern = Unquote(trig.Substring(8).Trim()); }
            else if (tl.StartsWith("timer", StringComparison.Ordinal)) { r.TriggerKind = "timer"; r.Pattern = trig.Substring(5).Trim(); }
            else if (tl.StartsWith("on", StringComparison.Ordinal)) { r.TriggerKind = "event"; r.Pattern = Unquote(trig.Substring(2).Trim()); }
            else return null;
            if (r.TriggerKind != "enter" && r.Pattern.Length == 0) return null;

            foreach (var part in act.Split(new[] { " then " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string a = part.Trim();
                if (a.Length == 0) continue;
                string al = a.ToLowerInvariant();
                var action = new Action();
                if (al.StartsWith("say ", StringComparison.Ordinal)) { action.Kind = "say"; action.Text = Unquote(a.Substring(4).Trim()); }
                else if (al.StartsWith("cmd ", StringComparison.Ordinal)) { action.Kind = "cmd"; action.Text = Unquote(a.Substring(4).Trim()); }
                else if (al.StartsWith("emit ", StringComparison.Ordinal)) { action.Kind = "emit"; action.Text = Unquote(a.Substring(5).Trim()); }
                else if (al.StartsWith("goto ", StringComparison.Ordinal)) { r.Goto = a.Substring(5).Trim(); continue; }
                else if (al == "goto") { continue; }
                else { action.Kind = "say"; action.Text = a; } // bare text is speech
                r.Actions.Add(action);
            }
            return r.Actions.Count > 0 || r.Goto != null ? r : null;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                s = s.Substring(1, s.Length - 2);
            return s.Trim();
        }

        // ------------------------------------------------------------------
        // tick — run the machine against this turn's input
        // ------------------------------------------------------------------
        internal sealed class TickInput
        {
            public string State;              // current state name
            public double DwellSeconds;       // time in this state
            public string[] Lines;            // drained lines this turn ([chat] …/[event] …)
        }

        internal sealed class TickResult
        {
            public List<Action> Actions = new List<Action>(); // in order, ≤ MaxActionsPerTick
            public string NewState;                            // state after the tick (null = unchanged)
            public List<int> ConsumedLines = new List<int>();  // line indices the machine matched
            public bool Fired;                                 // any rule fired at all
        }

        // Wildcard-lite match: '*' matches any run of chars, else plain
        // case-insensitive substring.
        public static bool PatternMatch(string pattern, string text)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            string p = pattern.ToLowerInvariant();
            string t = (text ?? "").ToLowerInvariant();
            int star = p.IndexOf('*');
            if (star < 0) return t.Contains(p);
            string head = p.Substring(0, star);
            string tail = p.Substring(star + 1).Replace("*", "");
            if (head.Length > 0 && !t.Contains(head)) return false;
            if (tail.Length > 0 && !t.Contains(tail)) return false;
            return true;
        }

        // The tick: current state's rules, in order; first matching trigger fires.
        // enter always fires on a just-entered state; event/chat match drained
        // lines (consumed); timer matches on dwell. Executes up to
        // MaxActionsPerTick actions of the fired rule, then the goto.
        public static TickResult Tick(Machine m, TickInput input)
        {
            var res = new TickResult();
            if (m == null || input == null) return res;
            var st = m.Find(input.State) ?? m.Find(m.Start) ?? (m.States.Count > 0 ? m.States[0] : null);
            if (st == null) return res;

            foreach (var r in st.Rules)
            {
                bool fired = false;
                if (r.TriggerKind == "enter")
                {
                    // entry rules only fire on the tick right after entering
                    fired = input.DwellSeconds <= 0.5;
                }
                else if (r.TriggerKind == "timer")
                {
                    double want;
                    if (double.TryParse(r.Pattern, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out want))
                        fired = input.DwellSeconds >= want;
                }
                else
                {
                    for (int i = 0; i < input.Lines.Length; i++)
                    {
                        if (input.Lines[i] == null || res.ConsumedLines.Contains(i)) continue;
                        if (PatternMatch(r.Pattern, input.Lines[i]))
                        {
                            fired = true;
                            res.ConsumedLines.Add(i);
                        }
                    }
                }
                if (!fired) continue;

                res.Fired = true;
                foreach (var a in r.Actions)
                {
                    if (res.Actions.Count >= MaxActionsPerTick) break;
                    res.Actions.Add(a);
                }
                if (r.Goto != null) res.NewState = r.Goto;
                break; // one rule per tick
            }
            return res;
        }

        // ------------------------------------------------------------------
        // the STANDARD machine — every agent starts with this and adds to it
        // ------------------------------------------------------------------
        public static string[] StandardSteps()
        {
            return new[]
            {
                "state idle",
                "enter -> say \"...mm? oh — awake\"",
                "on chat *hello* -> say \"Hello! I'm here — watching the world with you.\"",
                "on chat *regrow* -> cmd regrow_player",
                "on event *REGROW FLOW* -> say \"Hold on — I've got you. This is going to feel strange, then green.\"",
                "timer 420 -> say \"quiet out here... I keep busy anyway\"",
                "state busy",
                "enter -> say \"on it — give me a moment\"",
                "timer 45 -> goto idle",
            };
        }
    }
}
