// Unit tests for TextUtil helpers (degenerate-say guard + AsciiSafe).
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

        Console.WriteLine(_failed == 0 ? "textutil: all passed" : "textutil: " + _failed + " FAILURES");
        Environment.Exit(_failed);
    }
}