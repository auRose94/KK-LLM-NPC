// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// TruncatedArgs — recover action/say + numeric args from a length-cut JSON tool argument.
//
// Small/low-capability models frequently answer an `act` call with a JSON object that the request
// got truncated mid-way (the request's max token limit cut the response off). We do NOT want the
// NPC to fall through to a "no action" — every salvaged tool call still drives the game. This
// extracts whatever action/say/args we can with a small set of regexes so the model still acts.
//
// Deliberately dependency-free (no Unity, no BepInEx) so it stays compilable in the headless test
// suite and can cache its regexes once instead of recompiling them on every call.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KKLLMNPC
{
    internal static class TruncatedArgs
    {
        // Action/say keys are stable; the scalar-list below is the only thing to edit if the tool
        // surface grows numeric args. The regexes are `static readonly` so the runtime compiles them
        // once per process rather than per truncated argument.
        private const string ActionPattern = "\"action\"\\s*:\\s*\"([a-z_]+)\"";
        private const string SayPattern = "\"say\"\\s*:\\s*\"([^\"]*)\"";
        private static readonly string[] ScalarKeys =
        {
            "speed", "strafe", "turn_deg", "yaw_deg", "pitch_deg", "x", "y", "z",
            "id", "at", "heading_deg", "range",
        };

        // Extract whatever action/say/scalar args survive in a truncated JSON fragment. Returns null
        // when there is nothing recoverable (no action key at all), so the caller can fall through to
        // other handling.
        internal static JsonObj Salvage(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            var ma = Regex.Match(s, ActionPattern);
            if (!ma.Success) return null;

            var d = new Dictionary<string, object>();
            d["action"] = ma.Groups[1].Value;

            var ms = Regex.Match(s, SayPattern);
            if (ms.Success) d["say"] = ms.Groups[1].Value;

            foreach (var key in ScalarKeys)
            {
                // Rebuild this per-key pattern (the key is interpolated). The compile cost of a single
                // short pattern is tiny and the regex cache makes it effectively free after the first
                // use; iterating the keys avoids allocating 13 compiled-match objects up front.
                var mn = Regex.Match(s, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
                if (mn.Success)
                {
                    double parsed;
                    if (double.TryParse(mn.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                        d[key] = parsed;
                }
            }

            return new JsonObj(d);
        }
    }
}
