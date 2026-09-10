// Tests for ChatSimilarity (Jaccard, Levenshtein, FuzzyMatchesChat).
// Pure C# — no Unity deps.
// Build: mcs -target:exe -out:/tmp/t_chat.exe tests/test_chat_similarity.cs src/ChatSimilarity.cs && mono /tmp/t_chat.exe
using System;
using KKLLMNPC;

static class ChatSimilarityTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    static void AssertNear(double actual, double expected, double tol, string name)
    {
        Assert(Math.Abs(actual - expected) <= tol, name + " (" + actual + " ≈ " + expected + ")");
    }

    // ---- Jaccard ----

    static void JaccardIdentical()
    {
        AssertNear(ChatSimilarity.JaccardSimilarity("hello world", "hello world"), 1.0, 0.01, "Jaccard identical");
    }

    static void JaccardDifferent()
    {
        double sim = ChatSimilarity.JaccardSimilarity("aaa", "zzz");
        Assert(sim < 0.1, "Jaccard completely different (" + sim + ")");
    }

    static void JaccardPartial()
    {
        // 3-gram Jaccard for "hello world" vs "world hello": 6/12 = 0.5
        double sim = ChatSimilarity.JaccardSimilarity("hello world", "world hello");
        Assert(sim > 0.4, "Jaccard partial overlap (" + sim + ")");
    }

    static void JaccardEmpty()
    {
        AssertNear(ChatSimilarity.JaccardSimilarity("", "abc"), 0.0, 0.01, "Jaccard empty a");
        AssertNear(ChatSimilarity.JaccardSimilarity("abc", ""), 0.0, 0.01, "Jaccard empty b");
        AssertNear(ChatSimilarity.JaccardSimilarity(null, null), 0.0, 0.01, "Jaccard both null");
    }

    // ---- Levenshtein ----

    static void LevenshteinIdentical()
    {
        Assert(ChatSimilarity.LevenshteinDistance("hello", "hello") == 0, "Levenshtein identical dist");
        AssertNear(ChatSimilarity.LevenshteinRatio("hello", "hello"), 1.0, 0.01, "Levenshtein identical ratio");
    }

    static void LevenshteinOneEdit()
    {
        Assert(ChatSimilarity.LevenshteinDistance("cat", "bat") == 1, "Levenshtein one edit dist");
        double ratio = ChatSimilarity.LevenshteinRatio("cat", "bat");
        Assert(ratio > 0.6, "Levenshtein one edit ratio (" + ratio + ")");
    }

    static void LevenshteinDifferent()
    {
        double ratio = ChatSimilarity.LevenshteinRatio("abc", "xyz");
        Assert(ratio < 0.5, "Levenshtein different ratio (" + ratio + ")");
    }

    static void LevenshteinEmpty()
    {
        Assert(ChatSimilarity.LevenshteinDistance("", "abc") == 3, "Levenshtein empty vs abc");
        AssertNear(ChatSimilarity.LevenshteinRatio("", ""), 1.0, 0.01, "Levenshtein both empty");
        AssertNear(ChatSimilarity.LevenshteinRatio("abc", ""), 0.0, 0.01, "Levenshtein abc vs empty");
    }

    // ---- FuzzyMatchesChat ----

    static void FuzzyExactMatch()
    {
        string text = "the quick brown fox jumps over the lazy dog near the river";
        Assert(ChatSimilarity.FuzzyMatchesChat(text, text), "FuzzyMatchesChat exact");
    }

    static void FuzzyPartial()
    {
        string a = "I think the brown fox jumps over the lazy dog every day";
        string b = "the quick brown fox jumps over the lazy dog happily";
        Assert(ChatSimilarity.FuzzyMatchesChat(a, b), "FuzzyMatchesChat partial overlap");
    }

    static void FuzzyTooShort()
    {
        Assert(!ChatSimilarity.FuzzyMatchesChat("short text", "another short text here"), "FuzzyMatchesChat too few words");
    }

    static void FuzzyDifferent()
    {
        string a = "completely different sentences with no common words at all here";
        string b = "nothing matches between these two entirely unrelated strings here";
        Assert(!ChatSimilarity.FuzzyMatchesChat(a, b), "FuzzyMatchesChat no overlap");
    }

    static void FuzzyCaseInsensitive()
    {
        string text = "The Quick Brown Fox Jumps Over The Lazy Dog Every Morning";
        Assert(ChatSimilarity.FuzzyMatchesChat(text, text.ToLowerInvariant()), "FuzzyMatchesChat case-insensitive");
    }

    static int RunAll()
    {
        Console.WriteLine("== ChatSimilarity Tests ==");
        JaccardIdentical();
        JaccardDifferent();
        JaccardPartial();
        JaccardEmpty();
        LevenshteinIdentical();
        LevenshteinOneEdit();
        LevenshteinDifferent();
        LevenshteinEmpty();
        FuzzyExactMatch();
        FuzzyPartial();
        FuzzyTooShort();
        FuzzyDifferent();
        FuzzyCaseInsensitive();
        Console.WriteLine();
        Console.WriteLine("Results: " + _passed + " passed, " + _failed + " failed");
        return _failed;
    }

    static int Main()
    {
        return RunAll();
    }
}
