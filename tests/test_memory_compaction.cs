// Unit tests for MemoryCompaction — the deterministic merge of two lives used by
// swallow-absorb (Grow mode) and egg-rebirth. Guards the user-facing behaviors:
//   * the digested person's memories COMPACT into a bounded summary instead of
//     flooding the eater's fact ring/record with the raw list;
//   * the EATER's own facts win on collisions and are evicted last;
//   * a hatched child gets its own life + a taste of the carrier's; and
//   * nothing exceeds the merge cap.
// Run via run_tests.sh (suite "memory_compaction").
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestMemoryCompaction
{
    private static int _failed;

    private static void Assert(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static List<string> L(params string[] xs)
    {
        return new List<string>(xs);
    }

    private static bool HasLine(List<string> xs, string contains)
    {
        foreach (var s in xs)
            if (s != null && s.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static void Main()
    {
        // MergeLife: eater facts first, then one compacted absorbed: line.
        var eater = L("place: bed is upstairs", "danger: never near the mail machine", "social2: the player owns the farm",
            "farm: blender is in the kitchen", "goal idea: grow food for winter", "player_wants: find the mailbox",
            "world: sunny valley", "energy: low", "farm2: water once per stage", "me: I am big",
            "absorbed:OldVictim: some older absorption stays", "e: keep me too");
        var victim = L("place: the shop is by the spawn", "farm: blender is ALSO in the kitchen", "danger2: mailbox is safe",
            "social: call me if lost", "danger: the mailbox is behind me", "goal: be helpful", "egg: nests need 5ml",
            "me: I was small", "carried:", "farm2: water twice at stage 2");
        var merged = MemoryCompaction.MergeLife(eater, "Nibble", victim, 24);

        // Cap respected.
        Assert(merged.Count <= 24, "merged respects the cap (got " + merged.Count + ")");
        // Eater's own knowledge kept FIRST (nothing evicted at this size).
        Assert(HasLine(merged, "bed is upstairs"), "eater facts survive the merge");
        // One compacted absorbed: line carries the other life — NOT the raw flood.
        int absorbed = 0;
        foreach (var f in merged) if (f.StartsWith("absorbed:", StringComparison.Ordinal)) absorbed++;
        Assert(absorbed >= 1, "an absorbed: summary line exists");
        Assert(HasLine(merged, "nests need 5ml"), "the victim's non-duplicate knowledge rides along");
        Assert(!HasLine(merged, "the shop is by the spawn"), "duplicate-category victim facts compact away (eater already knows place:)");
        // Provenance line exists (who was digested).
        Assert(HasLine(merged, "Nibble"), "provenance names the digested one");
        // Junk (viewer-relative "wandering: to my left", empty lines) is filtered.
        Assert(!HasLine(merged, "behind me"), "junk victim facts dropped");
        // Only ONE provenance/absorbed cluster — bounded, not a verbatim dump:
        bool anyEqualsRaw = false;
        foreach (var f in merged) if (f == "me: I was small") anyEqualsRaw = true;
        Assert(!anyEqualsRaw, "no raw verbatim victim fact line remains (only the compacted summary)");
        Assert(!HasLine(merged, "water twice at stage 2") || HasLine(merged, "absorbed:"), "victim knowledge only via the summary line");

        // Squeezed cap eats eater's OLDEST first, still keeps the absorbed line.
        var squeezed = MemoryCompaction.MergeLife(L("a1: oldest", "a2: middle", "a3: newest"), "V",
            L("v1: knowledge one", "v2: knowledge two"), 4);
        Assert(squeezed.Count <= 4, "squeezed respects the cap, got " + squeezed.Count);
        Assert(HasLine(squeezed, "knowledge one"), "absorbed summary survives the squeeze");
        Assert(!HasLine(squeezed, "oldest"), "oldest own fact (list head) evicted first under squeeze");
        Assert(HasLine(squeezed, "newest"), "newest own fact kept under squeeze");

        // Empty / null inputs never explode.
        var empty = MemoryCompaction.MergeLife(null, null, null, 24);
        Assert(empty != null && empty.Count == 0, " merging nothing is an empty list, not null");
        var onlyOther = MemoryCompaction.MergeLife(new List<string>(), "Solo",
            L("x: something"), 24);
        Assert(HasLine(onlyOther, "something"), "absorbing into a blank eater keeps the summary");

        // LiveAbsorbLine: ONE fact line, capped, junk-free.
        var live = MemoryCompaction.LiveAbsorbLine("Kira", L("place: the pantry has apples",
            "danger: the pond edge is slippery", "danger2: the well is behind me"));
        Assert(live.StartsWith("absorbed:Kira:", StringComparison.Ordinal), "live line is the absorbed: category");
        Assert(live.Length <= MemoryCompaction.SummaryTrim, "live line bounded (" + live.Length + ")");
        Assert(live.IndexOf("pantry", StringComparison.OrdinalIgnoreCase) >= 0, "live line carries real knowledge");
        Assert(live.IndexOf("behind me", StringComparison.OrdinalIgnoreCase) < 0, "live line junk-filtered");
        Assert(live.IndexOf("well is", StringComparison.OrdinalIgnoreCase) < 0, "viewer-relative detail dropped from the live summary");
        var liveEmpty = MemoryCompaction.LiveAbsorbLine("Ghost", new List<string>());
        Assert(liveEmpty.IndexOf("no memories", StringComparison.OrdinalIgnoreCase) >= 0, "empty life says so honestly");

        // MergeRebirth: child life + hatch line + a taste of the carrier.
        var child = MemoryCompaction.MergeRebirth(L("place: I knew the barn", "me: I was eaten once"),
            "Kira", L("social: Kira likes jokes", "place: Kira's bed is by the window"),
            "was swallowed whole and digested into an egg by Kira; hatched back in a NEW body. You are the same person.", 24);
        Assert(HasLine(child, "You are the same person"), "hatch line present");
        Assert(HasLine(child, "I knew the barn"), "child's own life kept");
        Assert(HasLine(child, "Kira carried me"), "carrier provenance present");
        Assert(HasLine(child, "likes jokes"), "a taste of the carrier's life rides along");
        Assert(child.Count <= 24, "rebirth blend respects the cap");
        var childNoCarrier = MemoryCompaction.MergeRebirth(L("me: still me"), null, null, "hatched.", 24);
        Assert(HasLine(childNoCarrier, "still me"), "rebirth without a carrier record still keeps the core");

        Console.WriteLine(_failed == 0 ? "test_memory_compaction: all OK" : "test_memory_compaction: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}