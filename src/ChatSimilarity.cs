// Pure text similarity helpers for say-loop suppression and chat ack matching.
// No Unity dependencies — compiles with plain mcs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KKLLMNPC
{
    internal static class ChatSimilarity
    {
        // Jaccard similarity over character 3-grams (bag model).
        // Returns 0.0 (no overlap) to 1.0 (identical).
        internal static double JaccardSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            var sa = NGrams(a.ToLowerInvariant(), 3);
            var sb = NGrams(b.ToLowerInvariant(), 3);
            if (sa.Count == 0 || sb.Count == 0) return 0.0;
            int intersection = 0;
            foreach (var s in sa)
                if (sb.Contains(s)) intersection++;
            int union = sa.Count + sb.Count - intersection;
            return union > 0 ? (double)intersection / union : 0.0;
        }

        // Levenshtein edit distance.
        internal static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b == null ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;
            int n = a.Length, m = b.Length;
            var d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        // Levenshtein ratio: 1.0 = identical, 0.0 = completely different.
        internal static double LevenshteinRatio(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            int dist = LevenshteinDistance(a.ToLowerInvariant(), b.ToLowerInvariant());
            int maxLen = Math.Max(a.Length, b.Length);
            return maxLen > 0 ? 1.0 - (double)dist / maxLen : 1.0;
        }

        // Check if toolText fuzzy-matches chatText: ≥6 significant words (length ≥ 4)
        // appear in both (case-insensitive).
        internal static bool FuzzyMatchesChat(string toolText, string chatText)
        {
            if (string.IsNullOrEmpty(toolText) || string.IsNullOrEmpty(chatText)) return false;
            var toolWords = SignificantWords(toolText);
            var chatWords = SignificantWords(chatText);
            if (toolWords.Count < 3 || chatWords.Count < 3) return false;
            int overlap = 0;
            foreach (var w in toolWords)
                if (chatWords.Contains(w)) overlap++;
            return overlap >= 3;
        }

        // Generate character n-grams from a string.
        private static HashSet<string> NGrams(string s, int n)
        {
            var result = new HashSet<string>();
            if (s.Length < n) { result.Add(s); return result; }
            for (int i = 0; i <= s.Length - n; i++)
                result.Add(s.Substring(i, n));
            return result;
        }

        // Extract significant words: lowercase, length >= 4, alphanumeric only.
        private static HashSet<string> SignificantWords(string text)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { FlushWord(sb, words); }
            }
            if (sb.Length > 0) FlushWord(sb, words);
            return words;
        }

        private static void FlushWord(StringBuilder sb, HashSet<string> set)
        {
            string w = sb.ToString();
            if (w.Length >= 4) set.Add(w);
            sb.Clear();
        }
    }
}
