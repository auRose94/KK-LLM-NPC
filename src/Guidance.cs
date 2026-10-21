// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Guidance — the scored recognition result produced by the decision-model (clef-flash) POC:
// which task the NPC is on, whether it's blocked, which tool would help next, how confident
// the classifier is, and a one-line why. Top-level (not nested) so the pure, game-independent
// DecisionParse can produce it and the headless tests can assert on it.
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal class Guidance
    {
        public string Task = "";                 // best-fit task id (explore | talk-to-player | visit-station | gather | nest-eggs | follow-player | idle | none)
        public string Blocked = "none";          // none | busy | locked_or_unreachable | no_target | needs_buy
        public string NextTool = "";             // most useful tool right now (go_to | walk | grab | interact | say | ... | none)
        public float Confidence = 0f;            // 0..1
        public string Rationale = "";            // short why, referencing what's seen

        // JSON-safe view of the fields for the `guidance` key injected into the next turn's
        // perception. GuidanceJson() owns the "when should this be null" decision; this just
        // shapes the object.
        internal object ToJson()
        {
            return new Dictionary<string, object>
            {
                ["task"] = Task,
                ["blocked"] = Blocked,
                ["blocker"] = Blocked,
                ["next_tool"] = NextTool,
                ["confidence"] = Confidence,
                ["rationale"] = Rationale,
            };
        }
    }
}
