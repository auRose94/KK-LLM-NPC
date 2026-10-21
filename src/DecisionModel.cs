// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// DecisionModel — a small "decision model" (clef-flash: turns a state + typed questions into
// decisions) run as an optional PRE-TICK pass that recognises the NPC's current task and blocks,
// then steers the fine-tuned roleplay model's next step.
//
// DESIGN NOTE — the domain knowledge lives in the QUESTION SCHEMA, not in the model.
//   clef-flash is a 9B classifier. It does NOT know KoboldKare's mechanics (nest/egg rules,
//   stations, penetration/belty state, buy contracts, ...). So its value is recognition/routing:
//   it scores a pre-baked set of typed questions against the live state. The mechanical rules are
//   WRITTEN INTO the question wording below, so the model only has to reason with the state we give
//   it. The fine-tuned RP model (kk-serve) still writes prose; this pass steers its next step.
//   If the POC proves useful, this system-prompt + question block is the natural thing to pull out
//   into a shipped prompt file (see templates/).
//
// This is Wave 1 (advise-only). It never forces an action — it only appends `guidance` to the next
// turn's perception so the RP model sees task/blocker/tool before it answers. Wave 2 (act-drive)
// adds an override field read in ExecuteToolCalls.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // The recognition question set the model answers. Baked mechanical rules live in the
        // wording so a 9B classifier never has to "know" KoboldKare — the model just reasons
        // with the state we feed it and scores these questions. Extract to a prompt file if useful.
        private static string DecisionSystemPrompt() =>
            "You are the routing/recognition model for a game NPC agent. You do NOT write prose or actions — you answer a fixed set of typed questions about the current game state and reply with ONLY a JSON object. The state carries the game's real mechanics as text; use it.\n" +
            "Answer these questions and reply with exactly this JSON (no code fences, no prose):\n" +
            "{ \"task\": <one of explore|talk-to-player|visit-station|gather|nest-eggs|follow-player|idle|none>,\n" +
            "  \"blocked\": true|false,\n" +
            "  \"blocker\": <one of none|busy|locked_or_unreachable|no_target|needs_buy>,\n" +
            "  \"next_tool\": <one of go_to|walk|grab|interact|say|emote|look_around|radar|survey|follow|set_goal|none>,\n" +
            "  \"confidence\": <float 0..1>,\n" +
            "  \"rationale\": <one short clause referencing what you see> }\n" +
            "Mechanical context (the model does not otherwise know these — read the matching state text):\n" +
            " - needs.eggs says 'READY_TO_LAY' only to nest-eggs, and only when energy>1 (nests need energy>1) — if energy is low, eat/rest first and pick gather/visit-station, do NOT path to a nest.\n" +
            " - in_station=true means the agent is ALREADY animating at a station; never send another station/interact-then-inject tool, keep doing the current activity.\n" +
            " - a set blocked/_blockedInfo means movement is obstructed right now; prefer go_to elsewhere or a different tool, not another straight travel to the same target.\n" +
            " - a nearby/station item tagged ':busy' or ':not_built' or ':needs_buy' cannot be used yet — do not send go_to/interact for it.\n" +
            " - penetrated/penetrating state means the agent is inside someone/being used; most tools are invalid — pick none or the release tool.";

        // Only the fields a routing model needs to recognise task/blocks — a small, focused slice
        // of the full perception so the call stays tiny and cheap (clef-flash is a small context model).
        private object DecisionStateSlice()
        {
            try
            {
                object perc = _lastDecisionSource; // the full perception dict (built by the caller, already JSON-safe)
                var root = perc as Dictionary<string, object>;
                if (root == null) return new { ok = false, reason = "no_perception" };
                return new Dictionary<string, object>
                {
                    ["me"] = root.TryGetValue("me", out var m) ? m : null,
                    ["time"] = root.TryGetValue("time", out var t) ? t : null,
                    ["pos"] = root.TryGetValue("pos", out var p) ? p : null,
                    ["facing"] = root.TryGetValue("facing", out var f) ? f : null,
                    ["goal"] = root.TryGetValue("goal", out var g) ? g : null,
                    ["needs"] = root.TryGetValue("needs", out var n) ? n : null,
                    ["in_station"] = root.TryGetValue("in_station", out var s) ? s : null,
                    ["station_use"] = root.TryGetValue("station_use", out var su) ? su : null,
                    ["blocked"] = root.TryGetValue("blocked", out var b) ? b : null,
                    ["penetrated"] = root.TryGetValue("penetrated", out var pn) ? pn : null,
                    ["penetrating"] = root.TryGetValue("penetrating", out var pt) ? pt : null,
                    ["holding"] = root.TryGetValue("holding", out var h) ? h : null,
                    ["player"] = root.TryGetValue("player", out var pl) ? pl : null,
                    ["quest"] = root.TryGetValue("quest", out var q) ? q : null,
                    ["nearby"] = root.TryGetValue("nearby", out var nr) && nr is object[] arr ? (object)arr.Length : 0,
                    ["people"] = root.TryGetValue("people", out var pe) && pe is object[] arr2 ? (object)arr2.Length : null,
                };
            }
            catch (Exception)
            {
                return new { ok = false, reason = "slice_failed" };
            }
        }

        // Run the decision pass. Returns null on any failure/timeout/degradation (never throws) so
        // the loop simply falls back to the current act path. The JSON recovery lives in the pure,
        // unit-tested DecisionParse so a 9B model's messy output is handled in one place.
        private Guidance RunDecisionPass()
        {
            bool enabled = _cfgDecisionEnabled != null && _cfgDecisionEnabled.Value
                && !string.IsNullOrEmpty(Val(_cfgDecisionEndpoint));
            if (!enabled) return null;

            object slice = DecisionStateSlice();
            if (slice == null) return null;
            string payload = Json.Write(new Dictionary<string, object>
            {
                ["model"] = Val(_cfgDecisionModel),
                ["messages"] = new object[]
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = DecisionSystemPrompt() },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = "state:\n" + Json.Write(slice) },
                },
                ["temperature"] = 0.1,
                ["max_tokens"] = 128,
                ["stream"] = false,
            });

            string body = SafeHttp.Post(Val(_cfgDecisionEndpoint), payload, Val(_cfgDecisionApiKey),
                retries: 2, timeout: TimeSpan.FromSeconds(60), maxResponseBytes: 8 * 1024);
            if (body == null) return null;

            var root = Json.Parse(body) as Dictionary<string, object>;
            var choices = root?.GetValueOrDefault("choices") as List<object>;
            var choice = choices?.Count > 0 ? choices[0] as Dictionary<string, object> : null;
            var msg = choice?.GetValueOrDefault("message") as Dictionary<string, object>;
            string content = msg?.GetValueOrDefault("content") as string;
            if (string.IsNullOrWhiteSpace(content)) return null;

            // Returns null if the reply is unusable (no task / no positive confidence / garbage).
            return DecisionParse.Parse(content);
        }

        // Called once per loop cycle before the act query. Rate-limits the extra call to at
        // most once per [Decision].MinIntervalSec, runs the pass, stores the guidance for the
        // next turn, records telemetry, and logs one line for live observation. Never throws —
        // on any failure it just leaves _lastDecision unchanged and the loop falls back.
        private Guidance MaybeRunDecisionPass()
        {
            if (_cfgDecisionEnabled == null || !_cfgDecisionEnabled.Value) return _lastDecision;
            float min = _cfgDecisionMinIntervalSec != null ? Mathf.Max(0, _cfgDecisionMinIntervalSec.Value) : 5f;
            if (min > 0 && (Time.unscaledTime - _lastDecisionTime) < min) return _lastDecision;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var g = RunDecisionPass();
                sw.Stop();
                _lastDecisionTime = Time.unscaledTime;
                if (g != null && g.Task.Length > 0)
                {
                    _lastDecision = g;
                    int confPct = (int)Math.Round(g.Confidence * 100f);
                    Logger.LogInfo("[" + MyName() + "] DECISION task=" + g.Task + " blocked=" +
                        g.Blocked + " tool=" + g.NextTool + " (" + confPct + "%) — " + g.Rationale);
                    RecordDecisionTurn(g, sw.ElapsedMilliseconds);
                    return g;
                }
                // No usable guidance this cycle — keep the prior guidance so guidance persists
                // across turns (the RP model sees a stable hint), but don't log a false hit.
                if (_lastDecision != null) RecordDecisionTurn(_lastDecision, sw.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                // Never let a decision-model failure disturb the loop.
                try { Logger.LogWarning("[" + MyName() + "] decision pass failed: " + e.Message); } catch (Exception) { }
            }
            return _lastDecision;
        }

        // Serialize the current guidance slice into a JSON-safe object for the `guidance`
        // key injected into the next turn's perception. Null guidance returns null so the
        // format string stays clean (empty perception) — the RP model simply sees no hint.
        private object GuidanceJson()
        {
            var g = _lastDecision;
            if (g == null || g.Confidence <= 0f) return null;
            return g.ToJson();
        }
        private void RecordDecisionTurn(Guidance guidance, long ms)
        {
            if (!DataRecord.Enabled) return;
            try
            {
                var rec = new Dictionary<string, object>
                {
                    ["mode"] = "decision",
                    ["npc"] = MyName(),
                    ["model"] = Val(_cfgDecisionModel),
                    ["task"] = guidance?.Task ?? "",
                    ["blocked"] = guidance?.Blocked ?? "none",
                    ["blocker"] = guidance?.Blocked ?? "none",
                    ["next_tool"] = guidance?.NextTool ?? "",
                    ["confidence"] = guidance != null ? guidance.Confidence : 0f,
                    ["ok"] = guidance != null && guidance.Confidence > 0 ? 1 : 0,
                    ["ms"] = ms,
                };
                DataRecord.Turn(rec);
            }
            catch (Exception) { }
        }
    }
}
