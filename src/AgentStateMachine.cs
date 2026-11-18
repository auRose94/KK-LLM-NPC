// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// AgentStateMachine — the RUNTIME for the LLM-programmed state machine
// (v1.12.0, Rose's design): the model authors behavior once ("when a player
// says hi -> say hi back"; "timer 300 -> say something idle") and this runtime
// executes it every turn BEFORE the LLM is consulted. The machine lives as a
// skill named "fsm" (so `skill add fsm | state idle ; enter -> say ...` edits
// it, `cat skills` shows it, and saved lives carry it to bodies that inherit
// the agent's memory — behavioral culture that outlives the body). Fresh
// agents are seeded with the standard machine and add to it over time.
//
// Modes ([AgentFSM] Mode): off (no machine), assist (machine acts first, the
// LLM still polls — safe rollout), full (a fired machine that consumed every
// event SKIPS the LLM turn entirely — the token economy for multi-avatar
// play; a forced-poll ladder still summons the model: direct address,
// unmatched events, and a periodic reflection check-in).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // FSM runtime state (per agent, session-lived; the machine TEXT lives in
        // the skill store and is re-read each turn, so edits apply live).
        private string _fsmState = "idle";
        private float _fsmStateSince = -1f;
        private int _fsmSkipStreak;          // consecutive full-mode skips (forced-poll ladder)
        private float _fsmLastReflection = -1f;
        internal const int FsmForcePollSkips = 8;       // every N skips, let the model look around
        internal const float FsmReflectionMinutes = 10f; // ... and at least this often

        internal bool AgentFsmTurn(string drainedEvents, out string remainder)
        {
            remainder = drainedEvents;
            try
            {
                var plugin = Plugin;
                if (!LLMNPCPlugin.Alive(plugin)) return false;
                var modeCfg = plugin._cfgAgentFsmMode;
                string mode = modeCfg != null ? (modeCfg.Value ?? "assist").Trim().ToLowerInvariant() : "assist";
                if (mode == "off") return false;

                var steps = SkillStepsFor("fsm");
                if (steps == null)
                {
                    // Seed the standard machine once — every agent starts with a
                    // base behavior program and adds to it over time.
                    SkillAddImpl("fsm | " + string.Join(" ; ", AgentStateMachineCore.StandardSteps()));
                    steps = SkillStepsFor("fsm");
                    if (steps == null) { remainder = drainedEvents; return false; }
                }

                var machine = AgentStateMachineCore.Parse(steps);
                float now = Time.unscaledTime;
                if (_fsmStateSince < 0f) _fsmStateSince = now;
                double dwell = now - _fsmStateSince;

                var lines = new List<string>();
                if (!string.IsNullOrEmpty(drainedEvents))
                    foreach (var l in drainedEvents.Split('\n'))
                        if (l.Trim().Length > 0) lines.Add(l);

                var input = new AgentStateMachineCore.TickInput { State = _fsmState, DwellSeconds = dwell, Lines = lines.ToArray() };
                var res = AgentStateMachineCore.Tick(machine, input);
                if (!res.Fired)
                {
                    remainder = drainedEvents; // nothing matched — the model should see it all
                    return false;
                }

                // Execute the actions in order.
                var executed = new List<string>();
                foreach (var a in res.Actions)
                {
                    string text = (a.Text ?? "").Trim();
                    if (text.Length == 0) continue;
                    try
                    {
                        if (a.Kind == "cmd")
                        {
                            foreach (var line in ConsoleShell.ParseReply(text))
                            {
                                if (line.IsUnknown) continue;
                                string outp = RunConsoleLine(line, null);
                                executed.Add("$ " + line.Raw + " → " + (outp != null && outp.Length > 60 ? outp.Substring(0, 60) + "…" : outp));
                            }
                        }
                        else if (a.Kind == "emit")
                        {
                            PushWorldEvent("[fsm] " + text);
                            executed.Add("emit: " + text);
                        }
                        else // say
                        {
                            foreach (var line in ConsoleShell.ParseReply("echo " + text))
                            {
                                if (line.IsUnknown) continue;
                                RunConsoleLine(line, null);
                                executed.Add("said: " + text);
                                break;
                            }
                        }
                    }
                    catch (Exception) { }
                }

                // Transition.
                if (res.NewState != null && machine.Find(res.NewState) != null)
                {
                    _fsmState = res.NewState;
                    _fsmStateSince = now;
                }

                // Skip decision. assist: the LLM still polls (the machine acted,
                // the model sees nothing unusual). full: a machine that consumed
                // EVERY event skips the LLM turn — unless the forced-poll ladder
                // says the model should look around (direct address never skips:
                // a [chat] line is always unmatched here because machines only
                // consume what their patterns match).
                bool allConsumed = res.ConsumedLines.Count >= lines.Count;
                if (mode == "full" && allConsumed && lines.Count > 0)
                {
                    _fsmSkipStreak++;
                    float sinceReflect = _fsmLastReflection < 0f ? FsmReflectionMinutes * 60f : now - _fsmLastReflection;
                    if (_fsmSkipStreak < FsmForcePollSkips && sinceReflect < FsmReflectionMinutes * 60f)
                    {
                        _fsmLastReflection = _fsmLastReflection < 0f ? now : _fsmLastReflection;
                        return true; // SKIP the LLM turn — the machine owned this beat
                    }
                    _fsmLastReflection = now;
                    _fsmSkipStreak = 0;
                    // forced reflection poll: hand the model a digest instead of
                    // the raw events (they were consumed) — it reviews, adjusts.
                    remainder = "[fsm] your behavior machine handled the last events (state: " + _fsmState
                        + "; it said/did: " + string.Join(" | ", executed.ToArray()) + ")"
                        + " — periodic check-in: adjust the machine (skill add fsm | …) or act as you see fit.\n";
                    return false;
                }

                // assist / unmatched remainder: the normal turn proceeds with the
                // lines the machine did NOT match (matched ones are consumed —
                // the model would double-respond otherwise).
                var left = new List<string>();
                for (int i = 0; i < lines.Count; i++)
                    if (!res.ConsumedLines.Contains(i)) left.Add(lines[i]);
                remainder = left.Count > 0 ? string.Join("\n", left.ToArray()) + "\n" : "";
                return false;
            }
            catch (Exception)
            {
                remainder = drainedEvents;
                return false; // never let the machine break the turn
            }
        }
    }
}
