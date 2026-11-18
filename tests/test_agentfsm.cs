// Tests for AgentStateMachineCore.cs — the LLM-programmed state machine: DSL
// parsing (the skill-step format the model authors), wildcard trigger matching,
// tick semantics (one rule per tick, action budget, consumed lines, goto),
// the standard machine, and round-trip safety. Pure C#, no UnityEngine;
// compiled as part of run_tests.sh.
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestAgentFsm
{
    private static int _passed;
    private static int _failed;

    private static void Eq(object want, object got, string what)
    {
        bool ok = (want == null) ? got == null : want.Equals(got);
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what + " — want " + want + ", got " + got); }
        else _passed++;
    }

    private static void True(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what); }
        else _passed++;
    }

    private static AgentStateMachineCore.Machine ParseLines(params string[] lines)
    {
        return AgentStateMachineCore.Parse(lines);
    }

    private static void TestParse()
    {
        var m = ParseLines(
            "machine mine",
            "state idle",
            "enter -> say \"waking\"",
            "on chat *hello* -> say \"Hi!\" then goto greet",
            "state greet",
            "timer 20 -> goto idle");
        True(m.Find("idle") != null && m.Find("greet") != null, "parse: states");
        True(m.Start == "idle", "parse: machine header is a name; start falls back to the first state");
        var idle = m.Find("idle");
        True(idle.Rules.Count == 2, "parse: two rules in idle");
        True(idle.Rules[0].TriggerKind == "enter", "parse: enter trigger");
        True(idle.Rules[1].TriggerKind == "chat" && idle.Rules[1].Pattern == "*hello*", "parse: chat trigger pattern");
        True(idle.Rules[1].Actions.Count == 1 && idle.Rules[1].Actions[0].Kind == "say" && idle.Rules[1].Actions[0].Text == "Hi!", "parse: say action unquoted");
        True(idle.Rules[1].Goto == "greet", "parse: goto chains");
        var greet = m.Find("greet");
        True(greet.Rules[0].TriggerKind == "timer" && greet.Rules[0].Pattern == "20", "parse: timer trigger");
        // no arrow → not a rule; garbage tolerated
        var m2 = ParseLines("hello world", "state idle");
        True(m2.States.Count == 1, "parse: junk lines ignored");
    }

    private static void TestTick()
    {
        var m = ParseLines(
            "state idle",
            "on chat *hello* -> say \"Hi\" then cmd echo wave then goto greet",
            "state greet",
            "timer 20 -> goto idle");

        // chat match: both actions in order, both lines consumed, state moves
        var input = new AgentStateMachineCore.TickInput
        {
            State = "idle", DwellSeconds = 5,
            Lines = new[] { "[chat] Player: hello there", "[event] something else" },
        };
        var r = AgentStateMachineCore.Tick(m, input);
        True(r.Fired, "tick: fired");
        True(r.Actions.Count == 2 && r.Actions[0].Kind == "say" && r.Actions[1].Kind == "cmd", "tick: both actions in order");
        True(r.ConsumedLines.Count == 1 && r.ConsumedLines[0] == 0, "tick: only the matched line consumed");
        True(r.NewState == "greet", "tick: goto moves state");
        Eq("echo wave", r.Actions[1].Text, "tick: cmd keeps its command line");

        // timer: dwell 20+ fires the goto back
        var input2 = new AgentStateMachineCore.TickInput { State = "greet", DwellSeconds = 21, Lines = new string[0] };
        var r2 = AgentStateMachineCore.Tick(m, input2);
        True(r2.Fired && r2.NewState == "idle", "tick: timer returns to idle");

        // nothing matches → not fired, nothing consumed
        var input3 = new AgentStateMachineCore.TickInput { State = "idle", DwellSeconds = 5, Lines = new[] { "[chat] Player: zzz" } };
        var r3 = AgentStateMachineCore.Tick(m, input3);
        True(!r3.Fired && r3.ConsumedLines.Count == 0, "tick: unmatched leaves lines for the LLM");

        // enter rule only on a fresh state
        var m3 = ParseLines("state idle", "enter -> say \"new\"");
        var r4 = AgentStateMachineCore.Tick(m3, new AgentStateMachineCore.TickInput { State = "idle", DwellSeconds = 0.1f, Lines = new string[0] });
        True(r4.Fired && r4.Actions.Count == 1, "tick: enter fires on fresh state");
        var r5 = AgentStateMachineCore.Tick(m3, new AgentStateMachineCore.TickInput { State = "idle", DwellSeconds = 10f, Lines = new string[0] });
        True(!r5.Fired, "tick: enter does not fire when settled");

        // wildcard patterns: * matches runs
        var m4 = ParseLines("state idle", "on event *REGROW* -> say \"on it\"");
        var r6 = AgentStateMachineCore.Tick(m4, new AgentStateMachineCore.TickInput
        {
            State = "idle", DwellSeconds = 1,
            Lines = new[] { "[event] REGROW FLOW begun: the player is converting" },
        });
        True(r6.Fired, "tick: wildcard matches mid-line");

        // action budget: 6 actions, only MaxActionsPerTick execute
        var m5 = ParseLines("state idle", "enter -> say a then say b then say c then say d then say e then say f");
        var r7 = AgentStateMachineCore.Tick(m5, new AgentStateMachineCore.TickInput { State = "idle", DwellSeconds = 0f, Lines = new string[0] });
        True(r7.Actions.Count == AgentStateMachineCore.MaxActionsPerTick, "tick: action budget caps");
    }

    private static void TestStandardAndGuards()
    {
        var m = AgentStateMachineCore.Parse(AgentStateMachineCore.StandardSteps());
        True(m.States.Count >= 2, "standard: at least two states");
        True(m.Find("idle") != null, "standard: idle state");
        var input = new AgentStateMachineCore.TickInput
        {
            State = "idle", DwellSeconds = 3,
            Lines = new[] { "[chat] Player: hello~" },
        };
        var r = AgentStateMachineCore.Tick(m, input);
        True(r.Fired && r.Actions.Count == 1 && r.Actions[0].Kind == "say", "standard: greets hello");

        // limits: state cap + rule cap respected
        var lines = new List<string>();
        for (int i = 0; i < 40; i++) lines.Add("state s" + i);
        var big = AgentStateMachineCore.Parse(lines);
        True(big.States.Count == AgentStateMachineCore.MaxStates, "limits: state cap");
        var ruleLines = new List<string> { "state idle" };
        for (int i = 0; i < 30; i++) ruleLines.Add("on chat *" + i + "* -> say " + i);
        var big2 = AgentStateMachineCore.Parse(ruleLines);
        True(big2.Find("idle").Rules.Count == AgentStateMachineCore.MaxRulesPerState, "limits: rule cap");
    }

    internal static int Run()
    {
        TestParse();
        TestTick();
        TestStandardAndGuards();

        if (_failed > 0)
        {
            Console.WriteLine("agentfsm: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("agentfsm: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
