// TextUtil — small shared string helpers that several perception/movement files
// used to re-implement inline. Keep this file Unity-free where possible so it
// can be reused from test suites.
using System.Text;

namespace KKLLMNPC
{
    internal static class TextUtil
    {
        // Keep printable ASCII only. Game strings (entity names, owner nicknames,
        // model replies) can carry control characters or non-ASCII junk that
        // corrupts JSON payloads and Mono's string handling. Dropped (not '?') —
        // matches the historical inline pattern at the three original call sites.
        public static string AsciiSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            bool anyBad = false;
            foreach (char c in s)
            {
                if (c < 32 || c >= 127) { anyBad = true; break; }
            }
            if (!anyBad) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (c >= 32 && c < 127) sb.Append(c);
            return sb.ToString();
        }
    }
}