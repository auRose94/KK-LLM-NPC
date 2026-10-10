// Unit tests for TextUtil helpers (degenerate-say guard + AsciiSafe + ExpandTemplate).
// Run via run_tests.sh (suite "textutil").
using System;
using System.Text;
using KKLLMNPC;

internal static class TestTextUtil
{
    private static int _failed;

    private static void Assert(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("FAIL " + what); }
        else Console.WriteLine("  PASS: " + what);
    }

    public static void Main()
    {
        Console.WriteLine("TextUtil tests");

        // AsciiSafe (regression).
        Assert(TextUtil.AsciiSafe("ok") == "ok", "ascii passthrough");
        Assert(TextUtil.AsciiSafe("a" + (char)1 + "b") == "ab", "control chars dropped");
        Assert(TextUtil.AsciiSafe("café") == "caf", "non-ascii dropped");
        Assert(TextUtil.AsciiSafe(null) == "", "null -> empty");

        // Token-run glitch (the observed "I see you's see you's see you's ...").
        var glitch = new StringBuilder("I");
        for (int i = 0; i < 300; i++) glitch.Append(" see you's");
        var trimmed = TextUtil.TrimDegenerateRepeat(glitch.ToString(), 600);
        Assert(trimmed.StartsWith("I see you's see you's […]", StringComparison.Ordinal),
            "token-run trimmed to two units");
        Assert(trimmed.Length < 60, "token-run result is tiny, got " + trimmed.Length);

        // Bigram run ("see you's see you's ...").
        var bigram = new StringBuilder("hi ");
        for (int i = 0; i < 200; i++) bigram.Append("see you's ");
        var t2 = TextUtil.TrimDegenerateRepeat(bigram.ToString(), 600);
        Assert(t2.StartsWith("hi see you's see you's […]", StringComparison.Ordinal), "bigram run trimmed");

        // Char run ("!!!!!!...").
        var bang = "hey" + new string('!', 300) + " ok bye";
        var t3 = TextUtil.TrimDegenerateRepeat(bang, 600);
        Assert(t3.StartsWith("hey!! […]", StringComparison.Ordinal) && t3.EndsWith("ok bye"), "char run cut (2 left), tail kept");

        // Normal speech untouched.
        string normal = "Hello Rosemary! I'm here by the blender with you; want to cook something?";
        Assert(TextUtil.TrimDegenerateRepeat(normal, 600) == normal, "normal text untouched");

        // Content AFTER a degenerate run survives.
        var mixed = new StringBuilder("wow ");
        for (int i = 0; i < 50; i++) mixed.Append("no way ");
        mixed.Append("and this sentence matters!");
        var t4 = TextUtil.TrimDegenerateRepeat(mixed.ToString(), 600);
        Assert(t4.EndsWith("and this sentence matters!"), "tail after run preserved");
        Assert(t4.Contains("wow no way no way […]"), "run kept two units");

        // Length cap on non-repetitive long text.
        var words = new StringBuilder();
        int n = 0;
        while (words.Length < 700) { words.Append("word ").Append(n).Append(' '); n++; }
        var t5 = TextUtil.TrimDegenerateRepeat(words.ToString(), 600);
        Assert(t5.Length == 604 && t5.EndsWith(" […]", StringComparison.Ordinal), "length cap applies, got " + t5.Length);

        // Empty/null.
        Assert(TextUtil.TrimDegenerateRepeat(null, 600) == "", "null -> empty");
        Assert(TextUtil.TrimDegenerateRepeat("", 600) == "", "empty passthrough");

        // ExpandTemplate (per-mode chat-template files).
        var vars = new System.Collections.Generic.Dictionary<string, string>
        {
            ["{gender}"] = "female",
            ["{species}"] = "kobold",
            ["{taken}"] = "These names are TAKEN: Ember. ",
        };
        Assert(TextUtil.ExpandTemplate("Name: Gender: {gender}. Species: {species}. {taken}Go.", vars)
            == "Name: Gender: female. Species: kobold. These names are TAKEN: Ember. Go.",
            "all placeholders replaced");
        Assert(TextUtil.ExpandTemplate("keep {unknown} and {gender}", vars) == "keep {unknown} and female",
            "unknown token passes through untouched");
        var emptyTaken = new System.Collections.Generic.Dictionary<string, string> { ["{stereo}"] = "" };
        Assert(TextUtil.ExpandTemplate("before {stereo}after", emptyTaken) == "before after",
            "empty expansion removes the token");
        Assert(TextUtil.ExpandTemplate("no vars here", vars) == "no vars here", "placeholder-free template unchanged");
        Assert(TextUtil.ExpandTemplate(null, vars) == null && TextUtil.ExpandTemplate("", vars) == "",
            "null/empty template passthrough");
        Assert(TextUtil.ExpandTemplate("static", null) == "static", "null vars passthrough");

        // ---- viewer-relative fact hygiene (remember gate + store backstop) ----
        Assert(TextUtil.IsViewerRelativeFact(null) == false, "null fact not flagged");
        Assert(TextUtil.IsViewerRelativeFact("") == false, "empty fact not flagged");
        Assert(TextUtil.IsViewerRelativeFact("object to left"), "junk: object to left (user's example)");
        Assert(TextUtil.IsViewerRelativeFact("to my left"), "junk: to my left");
        Assert(TextUtil.IsViewerRelativeFact("the door on the right"), "junk: door on the right");
        Assert(TextUtil.IsViewerRelativeFact("wall ahead"), "junk: wall ahead (no anchor)");
        Assert(TextUtil.IsViewerRelativeFact("door behind"), "junk: door behind (no anchor)");
        Assert(TextUtil.IsViewerRelativeFact("the bed is here"), "junk: the bed is here");
        Assert(TextUtil.IsViewerRelativeFact("there's fruit next to me"), "junk: next to me");
        Assert(TextUtil.IsViewerRelativeFact("in front of me is a blender"), "junk: in front of me");
        Assert(TextUtil.IsViewerRelativeFact("bucket on my right side"), "junk: my right side");
        Assert(TextUtil.IsViewerRelativeFact("left of me there is a bed"), "junk: left of me");
        Assert(TextUtil.IsViewerRelativeFact("i must not fall behind"), "junk: bare behind (goal belongs in goal)");
        // GOOD facts must pass — name-anchored places, lessons, events.
        Assert(!TextUtil.IsViewerRelativeFact("bed is upstairs"), "good: durable place");
        Assert(!TextUtil.IsViewerRelativeFact("the blender is in the kitchen"), "good: named room anchor");
        Assert(!TextUtil.IsViewerRelativeFact("in front of the house grows a bush"), "good: anchored to a named thing");
        Assert(!TextUtil.IsViewerRelativeFact("the stairs to the left of the fireplace lead down"), "good: 'left of' a named anchor");
        Assert(!TextUtil.IsViewerRelativeFact("the cave is ahead of the barn door"), "good: ahead of a named anchor");
        Assert(!TextUtil.IsViewerRelativeFact("the mail machine sells kobolds — never approach it"), "good: lesson");
        Assert(!TextUtil.IsViewerRelativeFact("carried ~3m north from where I was grabbed"), "good: past-tense event, world compass");
        Assert(!TextUtil.IsViewerRelativeFact("was swallowed whole and digested into an egg"), "good: past-tense event");
        Assert(TextUtil.ViewerRelativeFactNote().Contains("STICKY"), "refusal note teaches sticky facts");

        // ---- junk-fact classifier round 2 (goals / self-state / object-state) ----
        Assert(TextUtil.IsViewerRelativeFact("bed is upstairs") == false, "good fact stays unflagged");
        Assert(TextUtil.JunkFactReason("goal: lay egg with reagent") == "goalish_fact", "junk: goal-as-fact");
        Assert(TextUtil.JunkFactReason("my goal is to explore the house") == "goalish_fact", "junk: my goal is");
        Assert(TextUtil.JunkFactReason("goal lay the egg") == "goalish_fact", "junk: leading goal word");
        Assert(TextUtil.JunkFactReason("set goal explore") == "goalish_fact", "junk: set goal");
        // ...but mid-sentence mentions without possessive/colon are fine.
        Assert(TextUtil.JunkFactReason("the goal of this machine is to sell") == null, "good: 'goal' mid-sentence, unpossessed");
        Assert(TextUtil.JunkFactReason("i am in the kitchen") == "self_state_fact", "junk: self position");
        Assert(TextUtil.JunkFactReason("i'm near the blender") == "self_state_fact", "junk: self near");
        Assert(TextUtil.JunkFactReason("i am hungry") == "self_state_fact", "junk: body need");
        Assert(TextUtil.JunkFactReason("i have 3 eggs") == "self_state_fact", "junk: carrying count");
        Assert(TextUtil.JunkFactReason("i am the owner of this farm") == null, "good: persona-ish self fact");
        Assert(TextUtil.JunkFactReason("the door is open") == "stale_state_fact", "junk: transient object state");
        Assert(TextUtil.JunkFactReason("the can was empty") == "stale_state_fact", "junk: was empty");
        Assert(TextUtil.JunkFactReason("the door upstairs is locked") == "stale_state_fact", "junk: is locked");
        Assert(TextUtil.JunkFactReason("the mail machine sells kobolds — never approach it") == null, "good: lesson");
        Assert(TextUtil.JunkFactReason("the blender is in the kitchen") == null, "good: place fact");
        Assert(TextUtil.JunkFactReason("object to left") == "stale_relative_fact", "round-1 class still routed");
        Assert(TextUtil.JunkFactNote("goalish_fact").Contains("goal slot"), "goalish note names the goal slot");
        Assert(TextUtil.JunkFactNote("self_state_fact").Contains("LIVE"), "self-state note points at live queries");
        Assert(TextUtil.JunkFactNote("stale_state_fact").Contains("LIVE"), "state note points at live queries");

        Console.WriteLine(_failed == 0 ? "textutil: all passed" : "textutil: " + _failed + " FAILURES");
        Environment.Exit(_failed);
    }
}