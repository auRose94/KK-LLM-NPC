// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// DecisionParse — the PURE game-independent core of the decision-model (clef-flash) POC:
// turn the raw text a small classification model returns into a scored Guidance.
//
// The decision model does NOT get to invent a free-form answer. It scores a fixed set of
// typed questions and replies with a fixed JSON shape (see DecisionModel.DecisionSystemPrompt):
//   { "task": ..., "blocked": bool, "blocker": ..., "next_tool": ..., "confidence": 0..1, "rationale": ... }
// The whole value of this piece is that it recovers THAT shape out of whatever a 9B classifier
// actually emits — fences, markdown prose, half-JSON, missing fields, a confidence that came
// back as a string — without ever throwing, so the loop can always fall back.
//
// This file is deliberately self-contained: only Json.cs (no Unity, no NPCInstance, no
// BepInEx), so it can run in the headless test suite.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class DecisionParse
    {
        // Recovers a Guidance from the model's raw reply text, or returns null when the
        // reply carries nothing usable. Never throws. The model is REQUIRED to have a task and a
        // confidence of >0 to count as a real decision — that is the single guard that keeps a
        // half-answered or empty reply from being mistaken for guidance.
        internal static Guidance Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string content = raw.Trim();
            // Recover the JSON object regardless of surrounding fences/markdown/prose by slicing
            // between the first '{' and the last '}'. This tolerates ```` ```json ... ``` ``,
            // leading prose ("Let me think:\n{...}"), and trailing text without needing to strip
            // each wrapper shape individually. No '{' or no closing '}' means there's no object —
            // that's "not usable", same as garbage.
            int start = content.IndexOf('{');
            if (start < 0) return null;
            int end = content.LastIndexOf('}');
            if (end <= start) return null;
            string inner = content.Substring(start + 1, end - start - 1);
            string json = "{" + inner + "}";

            var g = Json.Parse(json) as Dictionary<string, object>;
            if (g == null || g.Count == 0) return null;

            // Both are mandatory to count as a real decision; a model that only answers half the
            // questions isn't guidance, it's noise — the loop falls back to the act path. Every
            // field is read via TryGetValue so a missing key degrades to null, never throws.
            string task = Field(g, "task");
            if (string.IsNullOrEmpty(task)) return null;
            double conf = ExtractConfidence(Field(g, "confidence"));
            if (conf <= 0) return null;

            return new Guidance
            {
                Task = task,
                Blocked = Field(g, "blocker") ?? "none",
                NextTool = Field(g, "next_tool") ?? "",
                Confidence = Clamp((float)conf),
                Rationale = Field(g, "rationale") ?? "",
            };
        }

        // Turn a recognizer's /v1/systemone answers into a Guidance via pure mechanical rules — no
        // game knowledge here, just "which score is highest". The task is the argmax over the task
        // questions (that score is the confidence); the blocker is the highest-scoring blocker
        // question that crossed its bar; below the task bar, no task was recognised → null so the
        // loop keeps prior guidance. The game-dependent task→next-tool mapping lives in
        // NPCInstance.DecideNextTool, so this stays unit-testable without the mod.
        //
        //   answers  — { "questionKey": { "noul": 0.0..1.0 } } from /v1/systemone
        //   taskPairs  — {(questionKey, taskId)} for each task question
        //   blockPairs — {(questionKey, blockerLabel)} for each blocker question
        //   taskConf / blockConf — argmax thresholds (pass 0 for a per-call tweak)
        internal static Guidance SystemoneAnswers(
            Dictionary<string, object> answers,
            (string, string)[] taskPairs,
            (string, string)[] blockPairs,
            double taskConf = 0.35,
            double blockConf = 0.4)
        {
            if (answers == null) return null;

            double maxTask = 0; string task = "";
            foreach (var pair in taskPairs)
            {
                double s = Noul(answers, pair.Item1);
                if (s > maxTask) { maxTask = s; task = pair.Item2; }
            }
            if (task.Length == 0 || maxTask < taskConf) return null; // no clear task

            double maxBlock = 0; string blocker = "none";
            foreach (var pair in blockPairs)
            {
                double s = Noul(answers, pair.Item1);
                if (s > maxBlock) { maxBlock = s; blocker = pair.Item2; }
            }
            bool isBlocked = maxBlock >= blockConf;
            // Guidance.Blocked holds the blocker label or "none" — a downstream caller checks
            // `blocker != "none"` to know it's blocked.
            string blocked = isBlocked ? blocker : "none";

            return new Guidance
            {
                Task = task,
                Blocked = blocked,
                Confidence = Clamp((float)maxTask),
                Rationale = "recognised " + task + " at " + (int)Math.Round(maxTask * 100f) + "%" +
                    (isBlocked ? " — " + blocker + " at " + (int)Math.Round(maxBlock * 100f) + "%" : ""),
            };
        }

        // Read one noul score (answers[key].noul) as a 0..1 double; 0 if absent/unparseable.
        private static double Noul(Dictionary<string, object> answers, string key)
        {
            if (!answers.TryGetValue(key, out var v)) return 0;
            if (v is Dictionary<string, object> q && q.TryGetValue("noul", out var nv) && nv != null)
            {
                if (nv is double d) return Clamp01(d);
                if (nv is long l) return Clamp01((double)l);
                if (nv is int i) return Clamp01((double)i);
                if (double.TryParse(nv.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var p))
                    return Clamp01(p);
            }
            return 0;
        }

        private static double Clamp01(double d) { return d < 0 ? 0 : d > 1 ? 1 : d; }

        // Get one field as a cleaned string, or null if absent/blank. Centralised so a missing
        // key is "no value" everywhere, not a throw — the guard above depends on that contract.
        private static string Field(Dictionary<string, object> g, string key)
        {
            if (g.TryGetValue(key, out var v) && v != null)
                return CleanValue(v);
            return null;
        }

        // A confidence can arrive as a float, a string, or a stringified number. Normalize to
        // 0..1; anything unparseable counts as "no confidence" so it fails the >0 guard.
        private static double ExtractConfidence(object o)
        {
            if (o == null) return 0;
            if (o is double d) return d;
            if (o is long l) return l;
            if (o is int i) return i;
            if (double.TryParse(o.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0;
        }

        // Clamp 0..1. The model may clamp low itself, but we never let it emit a confidence that
        // would mislead the RP model into over-trusting a half-answered question.
        private static float Clamp(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        // Coerce a JSON value to a clean ASCII string, trimming whitespace. A blank or null value
        // becomes null so the caller can treat "field absent" the same as "empty" — the guard
        // above then rejects a decision that's missing its mandatory task.
        private static string CleanValue(object o)
        {
            if (o == null) return null;
            string s = o.ToString();
            return string.IsNullOrEmpty(s) ? null : s.Trim();
        }
    }
}
