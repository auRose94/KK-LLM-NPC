// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// DecisionModel — a small "decision model" (a recognizer such as tev1) run as an optional
// PRE-TICK pass that RECOGNISES the NPC's current task and blockers, then steers the fine-tuned
// roleplay model's next step.
//
// DESIGN NOTE — the domain knowledge lives in the QUESTION SCHEMA + the mechanical RULES, NOT in
// the model. The recognizer (e.g. tev1, a ~4B classifier) does NOT know KoboldKare's mechanics
// (nest/egg rules, stations, penetration/belty state, buy contracts, ...). It answers a fixed set
// of typed noul questions against the live state and returns a 0..1 score per question. Two things
// we deliberately DO NOT ask it:
//
//   1. Threshold arithmetic. It is bad at it — "is energy > 1?" or "does the path have > 5 tiles"
//      produces unstable, timing-dependent scores. So we only ask CATEGORICAL recognition
//      ("is a player visible?", "is the nearest item busy?"), which it does reliably.
//   2. Choosing the next tool. The task → next-tool mapping is a mechanical rule applied in code
//      after recognition, so the same rule holds for every model. The model only has to recognise
//      what's going on; the steering is deterministic.
//
// This is Wave 1 (advise-only). It never forces an action — it appends `guidance` to the next
// turn's perception so the RP model sees task/blocker/tool suggestion before it answers. Wave 2
// (act-drive) would add an override field read in ExecuteToolCalls.
//
// Endpoint: the recognizer is queried via Ollama's /v1/systemone (state + questions/criteria),
// not /v1/chat/completions. If the configured endpoint is NOT /v1/systemone, the chat-completions
// JSON-object path is used instead, so other decision-model servers stay working (compat).
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // The recognition question set the model answers. Baked mechanical rules live in the
        // wording so a ~4B classifier never has to know KoboldKare — the model just scores the
        // state we feed it. Extract to a prompt file if useful.
        private static string DecisionSystemPrompt() =>
            "You are the routing/recognition model for a game NPC agent. You do NOT write prose or take actions — you answer a fixed set of yes/no questions about the current game state and return a 0..1 score for each via the noul tool. The state carries the game's real mechanics as text; read it and score. Only CATEGORICAL recognition: decide whether a condition HOLDS or NOT. Do not try to guess numbers, distances, or thresholds — if the state doesn't say a condition is true, score it below 0.5.";

        // Recognition-only questions. No threshold comparisons — only "does this hold?".
        // TASK questions (argmax wins → the recognised task; its score = confidence).
        // BLOCKER questions (highest-scoring "fired" one wins → the blocker).
        private Dictionary<string, object> SystemoneQuestions()
        {
            return new Dictionary<string, object>
            {
                ["task_follow"] = noulQuestion("Is the NPC's current task to follow the player/another character?"),
                ["task_talk"]   = noulQuestion("Is the NPC's current task to talk to / greet the player?"),
                ["task_station"]= noulQuestion("Is the NPC's current task to use a station or activity (eating, sleeping, crafting, etc.)?"),
                ["task_nest"]   = noulQuestion("Does the NPC need to lay eggs now (a nest is available and it has energy)?"),
                ["task_gather"] = noulQuestion("Is the NPC's current task gathering or foraging for food or other resources?"),
                ["task_idle"]   = noulQuestion("Is the NPC idle — no active task, just standing/waiting?"),
                ["block_moved"]   = noulQuestion("Is the NPC's movement currently obstructed right now (a blocked/no-path condition is set)?"),
                ["block_busy"]    = noulQuestion("Is the nearest useful item currently busy / in use by someone?"),
                ["block_locked"]  = noulQuestion("Is the nearest useful item locked, blocked, or otherwise unreachable?"),
                ["block_needs_buy"] = noulQuestion("Is the nearest useful item unbuilt or not-yet-owned and must be bought/built first?"),
            };
        }

        // A single noul question in the /v1/systemone payload shape. The criteria keys are the
        // instructions the model uses to decide true vs false; the labels are fixed so the model
        // reasons consistently.
        private static Dictionary<string, object> noulQuestion(string instruction)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "noul",
                ["instructions"] = instruction,
                ["criteria"] = new Dictionary<string, object>
                {
                    ["true"] = "Yes — the condition clearly holds in the state.",
                    ["false"] = "No — the condition does not hold.",
                },
            };
        }

        // Task question name → task id (matches the guidance task vocabulary the RP model uses).
        private static readonly (string, string)[] TASK_QUESTIONS =
        {
            ("task_follow", "follow-player"),
            ("task_talk",   "talk-to-player"),
            ("task_station","visit-station"),
            ("task_nest",   "nest-eggs"),
            ("task_gather", "gather"),
            ("task_idle",   "idle"),
        };

        // Blocker question name → blocker label (matches the guidance blocker vocabulary).
        private static readonly (string, string)[] BLOCKER_QUESTIONS =
        {
            ("block_moved",   "locked_or_unreachable"),
            ("block_busy",    "busy"),
            ("block_locked",  "locked_or_unreachable"),
            ("block_needs_buy","needs_buy"),
        };

        // Below this argmax score the model did not confidently recognise any task — treat as
        // "no recognition this cycle" and keep the prior guidance. Keeps a half-answered reply
        // from masquerading as a decision.
        private const double MIN_TASK_CONFIDENCE = 0.35;
        // A blocker question scores at least this much to count as "blocked".
        private const double MIN_BLOCKER_CONFIDENCE = 0.4;

        // Only the fields a recognition model needs — a small, focused slice of the full
        // perception so the call stays tiny and cheap.
        private object DecisionStateSlice()
        {
            try
            {
                object perc = _lastDecisionSource; // built by the caller, already JSON-safe
                var root = perc as Dictionary<string, object>;
                if (root == null) return new { ok = false, reason = "no_perception" };
                return new Dictionary<string, object>
                {
                    ["me"] = root.TryGetValue("me", out var m) ? m : null,
                    ["time"] = root.TryGetValue("time", out var t) ? t : null,
                    ["pos"] = root.TryGetValue("pos", out var p) ? p : null,
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

        // Whether the configured endpoint is Ollama's /v1/systemone (state + questions payload).
        // Anything else is treated as a chat/completions server and gets the JSON-object path.
        private bool UseSystemoneEndpoint()
        {
            string ep = Val(_cfgDecisionEndpoint);
            return ep != null && ep.IndexOf("/systemone", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Run the decision pass. Returns null on any failure/timeout/degradation (never throws) so
        // the loop simply falls back to the current act path. The recognition lives in the pure
        // SystemoneAnswers so a ~4B classifier's boolean-score output is handled in one place.
        private Guidance RunDecisionPass()
        {
            if (_cfgDecisionEnabled == null || !_cfgDecisionEnabled.Value) return null;
            string endpoint = Val(_cfgDecisionEndpoint);
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            string model = Val(_cfgDecisionModel);
            if (string.IsNullOrWhiteSpace(model)) return null;

            if (UseSystemoneEndpoint())
                return RunSystemonePass(model, endpoint);

            // Compatibility path: an OpenAI-style chat/completions endpoint returns a JSON object
            // we recover with the existing pure parser.
            return RunChatPass(model, endpoint);
        }

        // /v1/systemone path: build the state + questions payload, query the recognizer, and map
        // the boolean-noul answers into a Guidance via mechanical rules.
        private Guidance RunSystemonePass(string model, string endpoint)
        {
            object slice = DecisionStateSlice();
            if (slice == null) return null;

            string payload = Json.Write(new Dictionary<string, object>
            {
                ["model"] = model,
                ["state"] = Json.Write(slice),
                ["questions"] = SystemoneQuestions(),
                ["temperature"] = 0.1,
            });

            string body = SafeHttp.Post(endpoint, payload, Val(_cfgDecisionApiKey),
                retries: 2, timeout: TimeSpan.FromSeconds(90));
            if (body == null) return null;

            var root = Json.Parse(body) as Dictionary<string, object>;
            var answers = root?.GetValueOrDefault("answers") as Dictionary<string, object>;
            if (answers == null || answers.Count == 0) return null;

            var sliceDict = slice as Dictionary<string, object> ?? new Dictionary<string, object>();
            // Pure recognition (game-independent) finds the task + blocker + confidence;
            // NPCInstance adds the game-dependent task→next-tool rule below.
            var g = DecisionParse.SystemoneAnswers(answers, TASK_QUESTIONS, BLOCKER_QUESTIONS);
            if (g == null) return null;
            g.NextTool = DecideNextTool(g.Task, sliceDict);
            return g;
        }

        // Compatible chat/completions path (a model that emits a JSON object rather than noul).
        private Guidance RunChatPass(string model, string endpoint)
        {
            object slice = DecisionStateSlice();
            if (slice == null) return null;

            string payload = Json.Write(new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = DecisionSystemPrompt() },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = "state:\n" + Json.Write(slice) },
                },
                ["temperature"] = 0.1,
                ["max_tokens"] = 128,
                ["stream"] = false,
            });

            string body = SafeHttp.Post(endpoint, payload, Val(_cfgDecisionApiKey),
                retries: 2, timeout: TimeSpan.FromSeconds(60), maxResponseBytes: 8 * 1024);
            if (body == null) return null;

            var root = Json.Parse(body) as Dictionary<string, object>;
            var choices = root?.GetValueOrDefault("choices") as List<object>;
            var choice = choices?.Count > 0 ? choices[0] as Dictionary<string, object> : null;
            var msg = choice?.GetValueOrDefault("message") as Dictionary<string, object>;
            string content = msg?.GetValueOrDefault("content") as string;
            if (string.IsNullOrWhiteSpace(content)) return null;

            return DecisionParse.Parse(content);
        }

        // Mechanical task → next-tool rule. This is where the domain knowledge lives: a recognition
        // model can't be trusted to pick the right tool, so the mapping is fixed and the same for
        // every model.
        //
        //   in_station  → none   (already animating; never send another station/interact tool)
        //   blocked     → go_to  (movement stuck; try a different target, not another straight trip)
        //   follow      → follow
        //   talk        → say
        //   station/nest → interact
        //   gather      → grab
        //   explore     → look_around
        //   otherwise   → none
        private static string DecideNextTool(string task, Dictionary<string, object> slice)
        {
            bool inStation = HasTrueFlag(slice, "in_station");
            if (inStation) return "none";

            if (task == "follow-player") return "follow";
            if (task == "talk-to-player") return "say";
            if (task == "visit-station" || task == "nest-eggs") return "interact";
            if (task == "gather") return "grab";
            if (task == "explore") return "look_around";

            bool moved = HasTrueFlag(slice, "blocked");
            return moved ? "go_to" : "none";
        }

        // --- helpers --------------------------------------------------------------------------

        // Was this flag set to true in the state slice (real bool, "true" string, or >0 number)?
        private static bool HasTrueFlag(Dictionary<string, object> slice, string key)
        {
            if (slice == null || !slice.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            if (v is string s) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            if (v is double d) return d > 0.5;
            if (v is long l) return l != 0;
            if (v is int i) return i != 0;
            if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                return p > 0.5;
            return false;
        }

        // Called once per loop cycle before the act query. Rate-limits the extra call to at most
        // once per [Decision].MinIntervalSec, runs the pass, stores the guidance for the next turn,
        // records telemetry, and logs one line for live observation. Never throws — on any failure
        // it just leaves _lastDecision unchanged and the loop falls back.
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
                    Logger.LogInfo("[" + MyName() + "] DECISION task=" + g.Task + " blocked=" + g.Blocked +
                        " tool=" + g.NextTool + " (" + confPct + "%) — " + g.Rationale);
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

        // Serialize the current guidance slice into a JSON-safe object for the `guidance` key
        // injected into the next turn's perception. Null guidance returns null so the format string
        // stays clean (empty perception) — the RP model simply sees no hint.
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
                    ["endpoint"] = UseSystemoneEndpoint() ? "systemone" : "chat",
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
