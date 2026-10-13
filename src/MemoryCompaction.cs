// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// MemoryCompaction — deterministic merge of two lives into one compact record.
// Pure C# (no Unity/BepInEx deps) so tests/test_memory_compaction.cs can compile it
// alone; swallowed (absorb) and hatched (egg rebirth) memories both funnel through it.
//
// Why compaction instead of a verbatim dump: the old Grow absorb appended the
// victim's whole fact list after the eater's, flooding both the LIVE fact ring
// (category dedupe collapses/evicts unpredictably) and the disk record (the
// restore path then evicts the eater's own oldest facts to make room). The
// merged life is capped at a single fact-ring budget and keeps BOTH halves:
// the eater's own knowledge wins, the digested person's knowledge is kept in a
// named, joined summary line ("absorbed:Kira: bed is upstairs | blender in the
// kitchen | ...") with the most durable details, rather than every raw line.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class MemoryCompaction
    {
        // Default merged-life budget — equals the old FactCap so a restored merged
        // record fits the ring without evicting either side.
        internal const int DefaultMergeCap = 24;
        // Max distinct facts from the OTHER life folded into one summary line.
        internal const int MaxOtherFacts = 8;
        // One other-life fact is shortened to this before joining (keeps the
        // summary line readable in the anchor/`cat facts`).
        internal const int OtherFactTrim = 64;
        // Cap for a whole summary line.
        internal const int SummaryTrim = 320;

        /// <summary>
        /// Merge a living record with a digested person's saved life.
        /// ownFacts win on category collisions; the other life is compacted into
        /// up to `MaxOtherFacts` details inside one "absorbed:NAME: ..." line
        /// (plus one "of:NAME" provenance line when there was anything to keep).
        /// Never returns more than mergeCap facts; never null.
        /// </summary>
        internal static List<string> MergeLife(List<string> ownFacts, string otherName, List<string> otherFacts,
            int mergeCap = DefaultMergeCap)
        {
            int cap = mergeCap > 0 ? mergeCap : DefaultMergeCap;
            var own = CleanFacts(ownFacts, cap);
            // The digested life is COMPACTED against the keeper's categories: a fact
            // whose category prefix the eater already knows adds nothing ("a compaction
            // of the two" — duplicates die, not the budget).
            var ownPrefixes = new HashSet<string>();
            foreach (var f in own)
            {
                try
                {
                    string p = f.Split(':')[0].Trim();
                    if (p.Length > 0 && p.Length <= 24) ownPrefixes.Add(p);
                }
                catch (Exception) { }
            }
            var other = CleanFacts(otherFacts, MaxOtherFacts * 2, ownPrefixes);

            var merged = new List<string>(own.Count + 2);
            // Keep the eater's own knowledge first; if there is no room left for the
            // other life, shed our OLDEST facts (the list head) to fit the summary.
            int reserve = 2; // absorbed summary + provenance
            while (own.Count + reserve > cap && own.Count > 0)
                own.RemoveAt(0);

            string line = null;
            if (other.Count > 0)
            {
                var parts = new List<string>();
                foreach (var f in other)
                {
                    string s = TrimMid(f, OtherFactTrim);
                    if (parts.Count >= MaxOtherFacts) break;
                    parts.Add(s);
                }
                line = "absorbed:" + ProvenanceName(otherName) + ": " + string.Join(" | ", parts.ToArray());
                line = TrimMid(line, SummaryTrim);
            }

            bool hasOwnAbsorbMarker = false;
            foreach (var f in own)
            {
                if (line != null && f.StartsWith("absorbed:", StringComparison.Ordinal)) { hasOwnAbsorbMarker = true; continue; }
                merged.Add(f);
            }
            if (line != null)
                merged.Add(line + (hasOwnAbsorbMarker ? "" : " (their memories are yours now)"));
            foreach (var f in own)
                if (f.StartsWith("absorbed:", StringComparison.Ordinal)) { merged.Add(f); break; }
            if (other.Count > 0 || line != null)
                merged.Add("of:" + ProvenanceName(otherName) + " was digested whole by this body — their memories live in the absorbed: line above");

            // Final cap (the provenance line may have pushed us over) — drop the
            // oldest kept facts first, not the absorbed summary at the tail.
            while (merged.Count > cap && merged.Count > 0) merged.RemoveAt(0);
            return merged;
        }

        /// <summary>
        /// The LIVE (ring) variant: one RememberFact-safe line — the merged summary
        /// of what the digested person knew. Never more than SummaryTrim chars, so
        /// a single absorbed: category slot carries the whole compaction.
        /// </summary>
        internal static string LiveAbsorbLine(string otherName, List<string> otherFacts)
        {
            var other = CleanFacts(otherFacts, MaxOtherFacts);
            if (other.Count == 0)
                return "absorbed:" + ProvenanceName(otherName) + " was digested whole by this body — no memories survived the conversion to carry over";
            var parts = new List<string>();
            foreach (var f in other)
            {
                if (parts.Count >= MaxOtherFacts) break;
                parts.Add(TrimMid(f, OtherFactTrim));
            }
            return TrimMid("absorbed:" + ProvenanceName(otherName) + ": " + string.Join(" | ", parts.ToArray()), SummaryTrim);
        }

        /// <summary>
        /// Egg-rebirth child blend: the child's OWN previous life (the swallowed
        /// person) plus a SHORT taste of the eater's life (the body/person that
        /// carried them). "A compaction of the two" — the child remembers being
        /// themselves and knows who carried them.
        /// </summary>
        internal static List<string> MergeRebirth(List<string> childFacts, string eaterName, List<string> eaterFacts,
            string hatchLine, int mergeCap = DefaultMergeCap)
        {
            int cap = mergeCap > 0 ? mergeCap : DefaultMergeCap;
            var core = CleanFacts(childFacts, cap);
            var eater = CleanFacts(eaterFacts, MaxOtherFacts * 2);
            var merged = new List<string>(core.Count + 3);
            if (!string.IsNullOrWhiteSpace(hatchLine)) merged.Add(hatchLine.Trim());
            foreach (var f in core) merged.Add(f);
            if (eater.Count > 0)
            {
                var parts = new List<string>();
                foreach (var f in eater)
                {
                    if (parts.Count >= 4) break; // the carrier's life is flavor, not the headline
                    parts.Add(TrimMid(f, OtherFactTrim));
                }
                string line = TrimMid("of:" + ProvenanceName(eaterName) + " carried me through their body — a taste of what they knew: "
                    + string.Join(" | ", parts.ToArray()), SummaryTrim);
                merged.Add(line);
            }
            while (merged.Count > cap && merged.Count > 0) merged.RemoveAt(0);
            return merged;
        }

        // Trim + junk-filter + exact-dedupe, preferring the NEWEST facts: lives are
        // stored oldest-newest (the fact ring appends at the tail), so the useful
        // surviving details are the recent ones. Input order preserved in the result.
        private static List<string> CleanFacts(List<string> facts, int cap, HashSet<string> skipPrefixes = null)
        {
            var outp = new List<string>(cap > 0 ? cap : 8);
            if (facts == null) return outp;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = facts.Count - 1; i >= 0 && outp.Count < cap; i--)
            {
                string f = facts[i];
                if (string.IsNullOrWhiteSpace(f)) continue;
                string t = f.Replace("\r", " ").Replace("\n", " ").Trim();
                if (t.Length == 0) continue;
                try
                {
                    if (KKLLMNPC.TextUtil.JunkFactReason(t) != null) continue;
                }
                catch (Exception) { }
                if (!seen.Add(t)) continue;
                if (skipPrefixes != null)
                {
                    string prefix = t.Split(':')[0].Trim();
                    if (prefix.Length > 0 && prefix.Length <= 24 && skipPrefixes.Contains(prefix)) continue;
                }
                outp.Add(t);
            }
            outp.Reverse(); // newest selected, listed in their original (old-new) order
            return outp;
        }

        private static string ProvenanceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "someone";
            string n = name.Trim();
            if (n.Length > 40) n = n.Substring(0, 40);
            return n;
        }

        // Cut to n chars without breaking on exactly n (leaves an ellipsis).
        private static string TrimMid(string s, int n)
        {
            if (s == null) return "";
            if (s.Length <= n) return s;
            if (n <= 1) return n == 1 ? "…" : "";
            return s.Substring(0, n - 1) + "…";
        }
    }
}