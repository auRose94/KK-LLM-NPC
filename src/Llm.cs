// Decision loop: payload, JSON-schema act response, tool-call extraction (incl. salvage), plan chaining.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Photon.Pun;
using Photon.Realtime;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {

        // ------------------------------------------------------------------
        // background vision pass ("another process")
        // ------------------------------------------------------------------
        // Starts a caption job when due if one isn't already running. The render is
        // grabbed on the main thread (Unity must), then the HTTP+caption happens on
        // this background worker so the action loop never blocks on vision.
        private void MaybeCreativeCommentary(string perceptionJson)
        {
            if (_cfgCommentEvery == null || _cfgCommentEvery.Value <= 0) return;
            if (_tick - _lastCommentaryTick < Math.Max(1, _cfgCommentEvery.Value)) return;
            _lastCommentaryTick = _tick;
            string percep = perceptionJson; // captured for the worker
            var t = new Thread(() => CreativeCommentaryWorker(percep)) { IsBackground = true, Name = "KKLLMNPC-Comment" };
            t.Start();
        }

        // Ask the model for a short unprompted reaction to its current perception,
        // then say it out loud. No act schema — pure voice.
        private void CreativeCommentaryWorker(string percep)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] =
                            "You are an anthropomorphic person, you're an NPC with a personality within a game, KoboldKare. You have opinions about what you see and feel — comment freely, candidly, briefly. You were not gifted anything upon waking up, don't believe that." +
                            "Output ONE short line of spoken dialogue (max 12 words) you'd actually say out loud right now given the scene. No quotes, no narration." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "perception:" + percep + "\nscene:" + _sceneDesc },
                    },
                    ["max_tokens"] = 40,
                    ["temperature"] = _cfgCommentTemp != null ? (double)_cfgCommentTemp.Value : 0.9,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);
                var req = (HttpWebRequest)WebRequest.Create(Val(_cfgEndpoint));
                req.Method = "POST"; req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(Val(_cfgApiKey))) req.Headers["Authorization"] = "Bearer " + Val(_cfgApiKey);
                req.Timeout = 120000; req.ReadWriteTimeout = 120000;
                byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return;
                    using (var sr = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        var root = Json.Parse(json) as Dictionary<string, object>;
                        var choices = root?.GetValueOrDefault("choices") as List<object>;
                        if (choices != null && choices.Count > 0)
                        {
                            var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                            string content = msg?.GetValueOrDefault("content") as string;
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                string line = content.Trim().Split('\n')[0].Trim().Trim('"', '"');
                                if (line.Length > 0 && line.Length < 200)
                                {
                                    Logger.LogInfo("[" + MyName() + "] muses: " + Sanitize(line));
                                    try { ToolSay(new TextArgs(Sanitize(line))); } catch (Exception) { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("commentary: " + e.Message); }
        }

        // Ask the world-model a question ("what is that?", "is the bed taken?") with
        // the NPC's current perception as context. Async: answer lands in '_answered'
        // and gets read into perception next tick, and optionally said aloud by the model.
        // `ask` tool: send the current question to a worker thread with the perception as
        // context, without blocking the action loop. Answer appears next tick as 'answered'
        // and is auto-remembered as a fact.
        private object ToolAsk(JsonObj p)
        {
            string q = p.S("q", "");
            if (string.IsNullOrWhiteSpace(q)) return new { ok = false, reason = "empty_question" };
            _pendingQuestion = q.Trim();
            // Fire the question on a worker thread; the answer is picked up next tick.
            if (!_answerBusy)
            {
                _answerBusy = true;
                var t = new Thread(AnswerQuestionWorker) { IsBackground = true, Name = "KKLLMNPC-Answer" };
                t.Start();
            }
            return new { ok = true, asking = _pendingQuestion, note = "answer delivered next turn as 'answered'" };
        }

        private void AnswerQuestionWorker()
        {
            try
            {
                string q = _pendingQuestion;
                // Build context from the current perception.
                string percep = null;
                try { percep = (string)RunOnMainThread(() => Json.Write(BuildPerception(false)), 8000); } catch (Exception) { }
                string ctx = "Perception now: " + percep + " Scene: " + _sceneDesc + " Remembered: " + FactsJson();
                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "You are an anthropomorphic person, you're an NPC with a personality within a game, KoboldKare. You are the NPC's inner world-model and monologue. Answer its question in one short sentence using the provided perception and memory. Be concrete and literal (places, distances, who, what it is). KoboldKare is an adult world; answer candidly. You were not gifted anything upon waking up, don't believe that." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = ctx + " Question: " + q },
                    },
                    ["max_tokens"] = 80,
                    ["temperature"] = 0.4,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);
                var req = (HttpWebRequest)WebRequest.Create(Val(_cfgEndpoint));
                req.Method = "POST"; req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(Val(_cfgApiKey))) req.Headers["Authorization"] = "Bearer " + Val(_cfgApiKey);
                req.Timeout = 120000; req.ReadWriteTimeout = 120000;
                byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) { _answerBusy = false; return; }
                    var ms = new MemoryStream(); var buf = new byte[8192]; int total = 0, n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > 512 * 1024) break; ms.Write(buf, 0, n); }
                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    var choices = root?.GetValueOrDefault("choices") as List<object>;
                    if (choices != null && choices.Count > 0)
                    {
                        var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                        string content = msg?.GetValueOrDefault("content") as string;
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            _lastAnswer = Sanitize(content.Trim());
                            RememberFact("Q: " + q + " A: " + _lastAnswer);
                            Logger.LogInfo("asked: " + q + " => " + _lastAnswer);
                        }
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("ask: " + e.Message); }
            finally { _answerBusy = false; }
        }

        // ------------------------------------------------------------------
        // LLM loop: perception -> endpoint -> tool calls -> execute
        // ------------------------------------------------------------------
        // Perception -> LLM -> tool calls -> execute. The heart. Runs on its own
        // thread; main-thread Unity access is marshalled through RunOnMainThread. Catches
        // everything, self-heals, and only talks to the model when in a playable scene
        // with a body.
        private void LLMLoop()
        {
            // Give the game a moment to load before looking for a body.
            try { Thread.Sleep(4000); } catch (Exception) { return; }
            string lastState = "";
            while (_running)
            {
                try
                {
                    // Wait until we have a main-thread context before doing anything.
                    if (!MainReady) { Thread.Sleep(1000); continue; }

                    bool inGame = (bool)RunOnMainThread(() => IsPlayableScene(), 5000);
                    if (!inGame)
                    {
                        // Log state transitions so we can see *where* it's idle.
                        if (lastState != "no_scene") { lastState = "no_scene"; Logger.LogInfo("KKLLMNPC: waiting for a playable scene (player not spawned yet / wrong map)."); }
                        if (_kobold != null) try { RunOnMainThread(() => { TeardownBody(); return true; }, 5000); } catch (Exception) { }
                        Thread.Sleep(2000);
                        continue;
                    }

                    bool have = (bool)RunOnMainThread(() => EnsureBody(), 8000);
                    if (!have)
                    {
                        if (lastState != "no_body") { lastState = "no_body"; Logger.LogInfo("KKLLMNPC: in scene but no unoccupied AI kobold within AutoFindRange to possess."); }
                        Thread.Sleep(2000);
                        continue;
                    }

                    if (lastState != "running") { lastState = "running"; Logger.LogInfo("KKLLMNPC: loop active."); }

                    _tick++;
                    bool imageDue = _cfgSendImage.Value && (
                        !_cfgVision.Value
                        || _tick % Math.Max(1, _cfgImageEvery.Value) == 0
                        || (_cfgImageOnBump.Value && _needImageAfterBump)
                        || (_cfgImageOnTurn.Value && Time.unscaledTime - _lastBigTurnTime < 0.5f));

                    // Batch perception + image capture into a single main-thread marshal
                    // (typed wrapper — no 'dynamic'/'anonymous type': the game lacks
                    // Microsoft.CSharp.dll, so the dynamic binder kills the thread at JIT).
                    var frame = (PerceptionFrame)RunOnMainThread(() =>
                    {
                        var perc = BuildPerception(false);
                        string img = null;
                        if (imageDue) img = _lastVisionB64 ?? CaptureImageB64();
                        return new PerceptionFrame { Perception = perc, Image = img };
                    }, 15000);
                    object perception = frame.Perception;
                    string userJson = Json.Write(perception);
                    string imageB64 = frame.Image as string;
                    if (imageDue)
                    {
                        _needImageAfterBump = false;
                        // Reuse vision frame for action model to avoid double render
                        if (_cfgVision.Value && !string.IsNullOrEmpty(_lastVisionB64)) imageB64 = _lastVisionB64;
                        if (!string.IsNullOrEmpty(imageB64))
                        {
                            _pastImages.Add(imageB64);
                            int maxHist = _cfgImageHistory.Value;
                            while (_pastImages.Count > Math.Max(1, maxHist)) _pastImages.RemoveAt(0);
                        }
                    }

                    // Caption pass only runs if explicitly enabled.
                    if (_cfgVision.Value) MaybeStartVisionPass();
                    MaybeCreativeCommentary(userJson);

                    string reply = QueryLLM(userJson, imageB64);
                    if (reply == null) { Thread.Sleep((int)(GetDynamicThinkInterval() * 1000)); continue; }

                    ExecuteToolCalls(reply);
                    // Update activity timestamp if moving
                    UpdateActivity();
                    Thread.Sleep((int)(GetDynamicThinkInterval() * 1000));
                }
                catch (InvalidOperationException) { Thread.Sleep(1000); } // main not ready
                catch (ThreadInterruptedException) { return; }
                catch (ThreadAbortException) { return; }
                catch (Exception e)
                {
                    try { Logger.LogWarning("LLM loop: " + e); } catch (Exception) { }
                    try { Thread.Sleep(2000); } catch (Exception) { return; }
                }
            }
            Logger.LogWarning("KKLLMNPC: LLMLoop exited (running=false).");
        }

        private class PerceptionFrame
        {
            public object Perception;
            public string Image;
        }

        // Dynamic think interval based on activity
        private float GetDynamicThinkInterval()
        {
            float baseInterval = _cfgThinkInterval.Value;
            // If NPC has moved recently or is in station/penetration, keep fast
            bool active = Time.unscaledTime - _lastMoveTime < 2f
                || IsInAnimationStation()
                || IsPenetrated()
                || IsDickInside();
            float interval = active ? baseInterval : Mathf.Min(baseInterval * 2f, 0.8f);
            if (active)
                Logger.LogInfo($"think interval: {interval:F2}s (active)");
            else
                Logger.LogInfo($"think interval: {interval:F2}s (idle)");
            return interval;
        }

        private void UpdateActivity()
        {
            lock (_stateLock)
            {
                bool moving = Mathf.Abs(_moveLocalZ) > 0.01f || Mathf.Abs(_moveLocalX) > 0.01f;
                if (moving) _lastMoveTime = Time.unscaledTime;
                _wasMovingLastTick = moving;
            }
        }

        // The configured system prompt, plus the derived body persona (personality,
        // gender, pronouns) so every turn the model is reminded of WHO it is.
        private string SystemPromptWithPersona()
        {
            string base_ = Val(_cfgSystem);
            if (string.IsNullOrEmpty(_persona)) return base_;
            return base_ + "\n" + _persona;
        }

        // POST the perception+memory to the model. Sends response_format: json_schema
        // (not tools/tool_choice — many chat templates 400 on tool forcing). Model replies
        // with the act-args object as its content.
        // Protect everything stored/forwarded from Mono string conversion failures:
        // replace lone surrogate code units with '?', keep valid surrogate pairs
        // (emoji, CJK ext-B). LLM streaming can deliver a broken mid-multibyte
        // sequence that .NET keeps as an unpaired surrogate; the moment such a
        // string reaches a native call (HttpWebRequest, Logger, Unity) Mono dies
        // with "String conversion error: Illegal byte sequence ... in the input",
        // and because the failure text gets stored in history/state it keeps
        // re-triggering. Sanitize at the ingest points so nothing poisoned sticks.
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                    else sb.Append('?');
                }
                else if (char.IsLowSurrogate(c)) sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private string QueryLLM(string perceptionJson, string imageB64)
        {
            try
            {
                // Memory injected into the perception so the NPC remembers what it
                // was doing — otherwise each turn starts from zero.
                string mem = string.Format(",\"last_thought\":{0},\"memory\":{1},\"facts\":{2},\"last_action\":{3},\"scene\":{4},\"history\":{5},\"chat_log\":{6},\"model_error\":{7}",
                    Json.Write(_lastThought + (_blockedInfo != null ? " (" + _blockedInfo + ")" : "")),
                    ThoughtHistoryJson(), FactsJson(),
                    Json.Write(_lastAction), Json.Write(_sceneDesc), HistoryJson(), ChatLogJson(),
                    Json.Write(_modelError));
                string percep = perceptionJson;
                if (percep.EndsWith("}")) percep = percep.Substring(0, percep.Length - 1) + mem + "}";
                string text = "perception:" + percep;

                // With a vision-capable action model, attach frames as proper
                // image parts. Send past images (oldest first) plus current frame
                // so the model sees visual context over time.
                object userContent = text;
                if (imageB64 != null)
                {
                    var parts = new System.Collections.Generic.List<Dictionary<string, object>>();
                    parts.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = text });
                    // Past frames (oldest first) — skip the last one since it's the current frame.
                    for (int i = 0; i < _pastImages.Count - 1; i++)
                        parts.Add(new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = _pastImages[i] } });
                    // Current frame (always last).
                    parts.Add(new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = imageB64 } });
                    userContent = parts.ToArray();
                }

                // Instead of tools/tool_choice (Gemma's chat template rejects them
                // with HTTP 400 on LM Studio), constrain output with a JSON schema.
                // The model then replies *with the act-args object as content*,
                // which our parser already accepts via the content-JSON path.
                var schema = ActSchema();
                var responseFormat = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = "act",
                        ["strict"] = true,
                        ["schema"] = schema,
                    },
                };

                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = SystemPromptWithPersona() },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = userContent },
                    },
                    ["response_format"] = responseFormat,
                    ["temperature"] = Math.Round((double)_cfgTemperature.Value, 2),
                    ["max_tokens"] = _cfgMaxTokens.Value,
                    ["stream"] = true,
                };
                string body = Json.Write(payload);

                var req = (HttpWebRequest)WebRequest.Create(_cfgEndpoint.Value);
                req.Method = "POST";
                req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(_cfgApiKey.Value))
                    req.Headers["Authorization"] = "Bearer " + _cfgApiKey.Value;
                req.Timeout = 120000; req.ReadWriteTimeout = 120000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    // Read SSE stream: each line is "data: {...}" or "data: [DONE]".
                    // Accumulate delta.content AND delta.tool_calls into the response —
                    // backends (LM Studio + json_schema response_format) often stream the
                    // act output as a tool_call instead of content; dropping it is what
                    // produced the empty / "no tool call and no content" replies.
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    var contentBuilder = new StringBuilder();
                    var toolArgsBuilder = new StringBuilder();
                    string toolCallId = null;
                    string toolName = null;
                    string role = "assistant";
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        if (!line.StartsWith("data: ")) continue;
                        string data = line.Substring(6);
                        if (data == "[DONE]") break;
                        // Parse the SSE chunk
                        try
                        {
                            var chunk = Json.Parse(data) as Dictionary<string, object>;
                            if (chunk == null) continue;
                            var choices = chunk.GetValueOrDefault("choices") as List<object>;
                            if (choices == null || choices.Count == 0) continue;
                            var delta = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("delta") as Dictionary<string, object>;
                            if (delta == null) continue;
                            if (delta.ContainsKey("role")) role = delta["role"].ToString();
                            var c = delta.GetValueOrDefault("content") as string;
                            if (!string.IsNullOrEmpty(c)) contentBuilder.Append(Sanitize(c));
                            var tcs = delta.GetValueOrDefault("tool_calls") as List<object>;
                            if (tcs != null)
                            {
                                foreach (var tcObj in tcs)
                                {
                                    var tc = tcObj as Dictionary<string, object>; if (tc == null) continue;
                                    if (tc.GetValueOrDefault("id") is string idStr) toolCallId = idStr;
                                    var fn = tc.GetValueOrDefault("function") as Dictionary<string, object>; if (fn == null) continue;
                                    if (fn.GetValueOrDefault("name") is string n && !string.IsNullOrEmpty(n)) toolName = n;
                                    if (fn.GetValueOrDefault("arguments") is string a && !string.IsNullOrEmpty(a)) toolArgsBuilder.Append(Sanitize(a));
                                }
                            }
                        }
                        catch (Exception) { }
                    }
                    // Build a non-streaming response JSON so the rest of the pipeline works unchanged.
                    string fullContent = contentBuilder.ToString();
                    var message = new Dictionary<string, object> { ["role"] = role };
                    if (!string.IsNullOrEmpty(fullContent)) message["content"] = fullContent;
                    if (toolName != null && toolArgsBuilder.Length > 0)
                    {
                        message["tool_calls"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["id"] = toolCallId,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object>
                                {
                                    ["name"] = toolName,
                                    ["arguments"] = toolArgsBuilder.ToString(),
                                },
                            },
                        };
                    }
                    var fakeResp = new Dictionary<string, object>
                    {
                        ["choices"] = new object[]
                        {
                            new Dictionary<string, object> { ["message"] = message },
                        },
                    };
                    return Json.Write(fakeResp);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("LLM endpoint: " + e.Message);
                return null;
            }
        }

        private void ExecuteToolCalls(string responseJson)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(responseJson) as Dictionary<string, object>; }
            catch (Exception e) { Logger.LogWarning("parse llm json: " + e.Message); return; }
            if (root == null) { Logger.LogWarning("llm reply not an object"); return; }

            if (!root.TryGetValue("choices", out var choicesObj)) { Logger.LogWarning("llm reply: no choices"); return; }
            var choices = choicesObj as List<object>; if (choices == null || choices.Count == 0) { Logger.LogWarning("llm reply: empty choices"); return; }
            var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
            if (msg == null) { Logger.LogWarning("llm reply: no message"); return; }

            string content = (msg.GetValueOrDefault("content") as string)?.Trim();

            // Pull the `act` arguments wherever they live (tool_calls or content JSON).
            JsonObj args = ExtractActArgs(msg, content);

            if (args == null)
            {
                // The model failed to produce a valid act call. Show it the failure:
                // empty reply, prose instead of JSON, or JSON that doesn't parse.
                // The error is injected back into the NEXT perception as 'model_error'
                // so it corrects the format instead of repeating it.
                if (!string.IsNullOrEmpty(content))
                {
                    _lastThought = content.Length > 80 ? content.Substring(0, 80) : content;
                    _lastAction = "say";
                    // Still say the line so the NPC isn't inert, but flag the formatting.
                    SetModelError("You replied with PLAIN TEXT, not a structured act call. REPLY WITH ONLY ONE compact JSON object: {\"progress\":\"done|blocked|ongoing|changed\",\"why\":\"<1 short clause>\",\"thought\":\"<goal in <10 words>\",\"action\":\"<tool>\",...tool args...}. No prose, no markdown, no explanation around it. Your last text: " + (content.Length > 120 ? content.Substring(0, 120) + "..." : content));
                    Logger.LogInfo("LLM (plain text -> say): " + content);
                    try { ToolSay(new TextArgs(content)); } catch (Exception e) { Logger.LogWarning("say fallback: " + e.Message); }
                }
                else
                {
                    _emptyReplies++;
                    if (_emptyReplies == 1 || Time.unscaledTime - _lastEmptyReplyLog > 20f)
                    {
                        _lastEmptyReplyLog = Time.unscaledTime;
                        Logger.LogWarning("llm reply: no tool call and no content (streak=" + _emptyReplies + ")");
                    }
                    SetModelError("Your last reply was EMPTY (no content, no tool call). REPLY WITH ONLY ONE compact JSON object per the act schema: {\"progress\":\"...\",\"why\":\"...\",\"thought\":\"...\",\"action\":\"...\",...}. Never reply with nothing.");
                    // Feed it back to the model so next turn it corrects instead of idling.
                    PushHistory("eval", "empty reply");
                }
                return;
            }

            // The model produced a valid structured act — clear any outstanding error feedback.
            _modelError = null;
            // Even a structured act can use an invalid tool; that's flagged in ExecuteStep.

            _lastThought = args.S("thought", _lastThought);
            PushThought(_lastThought);
            // Track the self-assessment too, so progress notes become visible history.
            string progress = args.S("progress", "");
            string why = args.S("why", "");
            if (progress.Length > 0 || why.Length > 0)
                PushHistory("eval", progress + (why.Length > 0 ? " (" + why + ")" : ""));

            Logger.LogInfo($"act: thought=\"{_lastThought}\"");

            // Execute the root action, then the queued plan[] steps, with a delay
            // between each. Runs on the LLM thread — sleeping here is fine.
            ExecuteStep(args);
            var plan = args.A("plan");
            int max = Math.Max(1, _cfgMaxPlan.Value);
            int ran = 0;
            foreach (var step in plan)
            {
                if (ran >= max) { Logger.LogWarning($"plan capped at {max} steps"); break; }
                if (!_running) return;
                float wait = Mathf.Clamp(step.F("wait", _cfgStepDelay.Value), 0f, 3f);
                if (wait > 0f) Thread.Sleep((int)(wait * 1000f));
                else Thread.Sleep((int)(_cfgStepDelay.Value * 1000f));
                ExecuteStep(step);
                ran++;
            }
        }

        // One action step: optional say, then the action itself.
        // One action in a plan: say first (so speech lands on time), then the movement/
        // interaction itself. outcome is summarized into history so the model sees its effects.
        private void ExecuteStep(JsonObj args)
        {
            string action = args.S("action", "none");
            string say = args.S("say", "");
            _lastAction = action;
            Logger.LogInfo($"  step action={action}" + (say.Length > 0 ? $" say=\"{say}\"" : ""));
            if (!string.IsNullOrEmpty(say))
            {
                try { RunTool("say", new TextArgs(say)); } catch (Exception e) { Logger.LogWarning("say: " + e.Message); }
            }
            if (action != "none")
            {
                object result = RunTool(action, args);
                string summary = SummarizeResult(action, result);
                PushHistory(action, summary);
                // Notable outcomes become facts the model can recall later.
                if (action == "interact" && summary != null && summary.StartsWith("used ", StringComparison.Ordinal))
                {
                    string what = summary.Substring(5);
                    RememberFact(what + " used" + (_navTargetName != null ? " near " + _navTargetName : ""));
                }
                else if (action == "go_to" && _navTargetName != null)
                    RememberFact("went to " + _navTargetName);
                Logger.LogInfo($"  {action} -> {Json.Write(result)}");
            }
            else PushHistory(action, say.Length > 0 ? "said" : null);
        }

        // Short result digest for the history buffer ("used Bed", "blocked", "ok").
        // Compact the tool result into a few words for the history log
        // ('walk -> blocked', 'interact -> used BedStation').
        private string SummarizeResult(string action, object result)
        {
            try
            {
                var s = Json.Write(result);
                if (string.IsNullOrEmpty(s) || s == "null") return null;
                if (s.Contains("\"ok\":true"))
                {
                    if (action == "interact" && s.Contains("\"used\":\""))
                    {
                        int a = s.IndexOf("\"used\":\"", StringComparison.Ordinal) + 8;
                        int b = s.IndexOf('"', a);
                        if (b > a) return "used " + s.Substring(a, b - a);
                    }
                    if (action == "say") return "said";
                    return _blockedInfo != null ? _blockedInfo : "ok";
                }
                if (s.Contains("\"reason\":\""))
                {
                    int a = s.IndexOf("\"reason\":\"", StringComparison.Ordinal) + 10;
                    int b = s.IndexOf('"', a);
                    if (b > a) return "fail:" + s.Substring(a, b - a);
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // Finds the `act` function arguments regardless of how the model formatted them.
        // Find the act args across any reply shape: real tool_calls, JSON in content,
        // {"name":...,"arguments":{...}}, {"walk":{...}}, markdown-fenced, or truncated. Salvage
        // whatever fields we can when a reasoning model runs out of tokens mid-JSON.
        private JsonObj ExtractActArgs(Dictionary<string, object> msg, string content)
        {
            // 1) Proper tool_calls array.
            if (msg.TryGetValue("tool_calls", out var tcsObj) && tcsObj is List<object> tcs)
            {
                foreach (var tcObj in tcs)
                {
                    var tc = tcObj as Dictionary<string, object>; if (tc == null) continue;
                    var fn = tc.GetValueOrDefault("function") as Dictionary<string, object>; if (fn == null) continue;
                    string name = fn.GetValueOrDefault("name") as string;
                    string argStr = fn.GetValueOrDefault("arguments") as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    // Nested-JSON models double-encode arguments; unwrap once.
                    return UnwrapArgs(name, argStr);
                }
            }

            // 2) JSON in content: {"thought":...,"action":...} or {"name":"act","arguments":{...}}
            if (!string.IsNullOrEmpty(content))
            {
                var parsed = TryParseObj(content);
                if (parsed != null)
                {
                    if (parsed.ContainsKey("action")) return new JsonObj(parsed);
                    string n = (parsed.GetValueOrDefault("name") ?? parsed.GetValueOrDefault("tool")) as string;
                    if (n != null)
                    {
                        object a = parsed.GetValueOrDefault("arguments") ?? parsed.GetValueOrDefault("args");
                        return a is Dictionary<string, object> ad ? new JsonObj(ad)
                             : a is string astr ? new JsonObj(astr)
                             : null;
                    }
                    // Single action like {"walk": {"speed":1}} — treat first known key as action.
                    foreach (var k in new[] { "walk", "walk_ray", "go_to", "survey", "stop", "look", "jump", "exit_station", "crouch", "move_to", "interact", "grab", "drop", "say", "status" })
                        if (parsed.ContainsKey(k))
                        {
                            var inner = new Dictionary<string, object> { ["action"] = k, ["thought"] = "implicit" };
                            if (parsed[k] is Dictionary<string, object> innerArgs)
                                foreach (var kv in innerArgs) inner[kv.Key] = kv.Value;
                            return new JsonObj(inner);
                        }
                }
                // 2b) No JSON at all: some chat templates emit a flat OpenAI-style
                //     function-call like "action: go_to(name=\"TopDoor\")" as plain text.
                //     Honor it so the NPC acts instead of reading its own plan aloud.
                var flat = TryParseFlatArgs(content);
                if (flat != null) return flat;
            }
            return null;
        }

        // Parse a flat `key: value` reply that isn't JSON, e.g.
        //   progress: ongoing
        //   why: I see a TopDoor to the right...
        //   thought: Explore new room via door to right
        //   action: go_to(name="TopDoor")
        // and also the inline function-call form:
        //   action: go_to(name="TopDoor", at=1)
        //   action: interact(id=2)
        // Returns args if an action (and its call args) could be recovered, else null.
        private JsonObj TryParseFlatArgs(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            var fields = new Dictionary<string, string>();
            string action = null;
            string actionArgs = null;
            string implicitValue = null; // payload from `action: say "text"` space form
            // Some chat templates prepend control/role tokens before the actual reply,
            // e.g. "self<|message|>progress: ongoing\n..." Strip them so the first real
            // "key: value" line is recognized.
            content = StripTemplatePrefix(content);
            foreach (var rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                string key = null, val = null;
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    key = line.Substring(0, colon).Trim().ToLowerInvariant();
                    val = line.Substring(colon + 1).Trim();
                }
                else
                {
                    // No colon in the line. Three shapes:
                    //  - bare call:    "go_to(name=\"BedStation\")" — known tool + parens
                    //  - space form:   "say \"Hi, I'm Yorha!\"" — known tool + space + value
                    string lineLower = line.ToLowerInvariant();
                    int paren = line.IndexOf('(');
                    string beforeParen = paren > 0 ? line.Substring(0, paren).Trim().ToLowerInvariant() : "";
                    int firstSpace = line.IndexOf(' ');
                    string maybeKey = firstSpace > 0 ? line.Substring(0, firstSpace).Trim().ToLowerInvariant() : lineLower;
                    if (paren > 0 && IsKnownToolWord(beforeParen))
                    {
                        // Bare tool call on its own line → treat as the action.
                        key = "action";
                        val = line.Trim();
                    }
                    else if (firstSpace > 0 && IsKnownToolWord(maybeKey))
                    {
                        key = maybeKey;
                        val = line.Substring(firstSpace + 1).Trim();
                    }
                }
                if (key == null || key.Length == 0 || val == null || val.Length == 0) continue;
                if (key == "action")
                {
                    // Value forms: "go_to", "go_to(name=\"TopDoor\")", or the loose
                    // "say \"Hi! I'm Yorha\"" / "say Hi!" space form.
                    string v = val;
                    int parenPos = v.IndexOf('(');
                    if (parenPos > 0)   // "go_to(name=\"TopDoor\")" (paren anywhere)
                    {
                        action = v.Substring(0, parenPos).Trim();
                        int close = v.LastIndexOf(')');
                        actionArgs = close > parenPos ? v.Substring(parenPos + 1, close - parenPos - 1) : "";
                    }
                    else
                    {
                        int space = v.IndexOf(' ');
                        string tool = space > 0 ? v.Substring(0, space).Trim() : v;
                        string remainder = space > 0 ? v.Substring(space).Trim() : "";
                        if (remainder.Length > 0 && IsKnownToolWord(tool))
                        {
                            action = tool;   // "say \"Hi!\"" — tool with a payload value
                            implicitValue = remainder;
                        }
                        else action = tool;   // bare "say" / "go_to"
                    }
                }
                else fields[key] = val;
            }
            // Some chat templates drop the "action:" header and just emit the tool's
            // arg field directly: "say: Hi, I'm Yorha!" or "go_to: TopDoor". Infer the
            // action from a known tool key when no explicit action line was present.
            if (action == null)
            {
                string[] knownTools = new[] { "say", "go_to", "walk", "walk_ray", "survey", "remember", "ask", "look_around", "stop", "look", "jump", "exit_station", "crouch", "move_to", "interact", "grab", "drop", "status", "none" };
                foreach (var k in knownTools)
                {
                    string v;
                    if (fields.TryGetValue(k, out v))
                    {
                        action = k;
                        // "say: Hi, I'm Yorha!" -> say param = the text; quoted "TopDoor"
                        // under go_to -> name. Map the field's own value onto its primary
                        // arg so the tool gets real input, not just an empty call.
                        string content2 = v;
                        if (content2.Length > 0)
                        {
                            content2 = content2.Trim().Trim('"', '\'');
                            if (content2.Length > 0)
                            {
                                if (k == "say") fields[k] = content2;
                                else if (k == "go_to") { if (!fields.ContainsKey("name")) fields["name"] = content2; }
                                else if (k == "remember") { if (!fields.ContainsKey("mem")) fields["mem"] = content2; }
                                else if (k == "ask") { if (!fields.ContainsKey("q")) fields["q"] = content2; }
                            }
                        }
                        break;
                    }
                }
            }
            if (action == null) return null;
            // Validate the action is one we know; if not, don't fabricate.
            bool known = false;
            foreach (var k in new[] { "walk", "walk_ray", "go_to", "survey", "stop", "look", "jump", "exit_station", "crouch", "move_to", "interact", "grab", "drop", "say", "status", "remember", "ask", "look_around", "none" })
                if (k == action) { known = true; break; }
            if (!known) return null;

            var d = new Dictionary<string, object> { ["action"] = action };
            foreach (var f in fields) d[f.Key] = f.Value;

            // `action: say "text"` — hang the payload off the tool's primary arg.
            if (implicitValue != null && implicitValue.Length > 0)
            {
                string iv = implicitValue.Trim().Trim('"', '\'');
                if (iv.Length > 0)
                {
                    if (action == "say" && !d.ContainsKey("say")) d["say"] = iv;
                    else if (action == "go_to" && !d.ContainsKey("name")) d["name"] = iv;
                    else if (action == "remember" && !d.ContainsKey("mem")) d["mem"] = iv;
                    else if (action == "ask" && !d.ContainsKey("q")) d["q"] = iv;
                }
            }

            // Parse inline call args like name="TopDoor" or id=2 or at=1 into the dict.
            if (actionArgs != null && actionArgs.Length > 0)
                ApplyCallArgs(d, actionArgs);

            // Bolt a 'thought' onto anything that didn't carry one.
            if (!d.ContainsKey("thought")) d["thought"] = "implicit";
            Logger.LogInfo("flat-parse recovered action: " + action + (actionArgs != null && actionArgs.Length > 0 ? " (" + actionArgs + ")" : ""));
            return new JsonObj(d);
        }

        // True if the lowercased string is one of the tools the model can call.
        private bool IsKnownToolWord(string key)
        {
            switch (key)
            {
                case "walk": case "walk_ray": case "go_to": case "survey": case "stop":
                case "look": case "jump": case "exit_station": case "crouch": case "move_to":
                case "interact": case "grab": case "drop": case "say": case "status":
                case "remember": case "ask": case "look_around": case "none": return true;
                default: return false;
            }
        }

        // Strip leading chat-template control/role tokens from a reply, e.g.
        // "self<|message|>progress: ongoing..." or "<|user|>...". Repeatedly removes
        // a leading <|...|> token plus any word glued before it (like "self").
        // No-op for normal replies.
        private string StripTemplatePrefix(string content)
        {
            // Remove chat-template control/role tokens anywhere in the reply, e.g. the
            // leading "self<|message|>progress:…" or the embedded "say<|message|>Hi!…".
            //  - If the word glued before the token is a TOOL word ("say", "go_to", …),
            //    keep the word and replace the token with a space ("say Hi!").
            //  - Otherwise the word is a role token ("self", "user", …) and is dropped
            //    along with the token so "self<|message|>progress:" -> "progress:".
            if (content.IndexOf("<|", StringComparison.Ordinal) < 0) return content;
            var sb = new System.Text.StringBuilder(content.Length);
            int i = 0, n = content.Length;
            while (i < n)
            {
                if (i + 1 < n && content[i] == '<' && content[i + 1] == '|')
                {
                    int gt = content.IndexOf('>', i);
                    if (gt >= 0)
                    {
                        // Find the word immediately before this token (if any).
                        int wordStart = i;
                        while (wordStart > 0 && !char.IsWhiteSpace(content[wordStart - 1]) && content[wordStart - 1] != '<')
                            wordStart--;
                        string word = i > wordStart ? content.Substring(wordStart, i - wordStart) : "";
                        bool keepWord = word.Length > 0 && IsKnownToolWord(word);

                        // Back out the word we already appended.
                        int trim = i - wordStart;
                        if (sb.Length >= trim) sb.Length -= trim;

                        if (keepWord) sb.Append(word);

                        int end = gt + 1;
                        // Separate the kept tool word from what follows, if they'd touch.
                        if (keepWord && sb.Length > 0 && sb[sb.Length - 1] != ' ' && end < n && content[end] != ' ')
                            sb.Append(' ');
                        else if (!keepWord && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                            sb.Append(' ');
                        i = end;
                        continue;
                    }
                }
                sb.Append(content[i]);
                i++;
            }
            return sb.ToString().TrimStart('\n', '\r', ' ');
        }

        // Turn "name=\"TopDoor\", at=1, id=2, speed=0.5" into typed fields in dict.
        private void ApplyCallArgs(Dictionary<string, object> d, string callArgs)
        {
            // Split on commas not inside quotes.
            var parts = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQuote = false;
            foreach (char c in callArgs)
            {
                if (c == '"') inQuote = !inQuote;
                if (c == ',' && !inQuote) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());

            foreach (var partRaw in parts)
            {
                string part = partRaw.Trim();
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 1) continue;
                string k = part.Substring(0, eq).Trim();
                string v = part.Substring(eq + 1).Trim().Trim('"', '\'');
                if (k.Length == 0) continue;
                double num;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
                    d[k] = num;
                else
                    d[k] = v;
            }
        }

        private JsonObj UnwrapArgs(string name, string argStr)
        {
            if (string.IsNullOrEmpty(argStr)) return TrySalvageTruncated(argStr) ?? new JsonObj("{}");
            var parsed = TryParseObj(argStr);
            if (parsed == null)
            {
                // Truncated/unterminated JSON from a length-cut reasoning model.
                var salvaged = TrySalvageTruncated(argStr);
                if (salvaged != null) return salvaged;
                return new JsonObj("{}");
            }
            if (!parsed.ContainsKey("action") && parsed["arguments"] is string nested)
            {
                var inner = TryParseObj(nested);
                if (inner != null && inner.ContainsKey("action")) return new JsonObj(inner);
            }
            if (name != "act" && name != null) parsed["action"] = name;
            return new JsonObj(parsed);
        }

        // Given partial JSON like {"action":"wal...   or   {"action":"walk","say":"hi
        // extract whatever action/say we can with regex so the NPC still acts.
        private JsonObj TrySalvageTruncated(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var d = new Dictionary<string, object>();
            var ma = System.Text.RegularExpressions.Regex.Match(s, "\"action\"\\s*:\\s*\"([a-z_]+)");
            if (!ma.Success) return null;
            d["action"] = ma.Groups[1].Value;
            var ms = System.Text.RegularExpressions.Regex.Match(s, "\"say\"\\s*:\\s*\"([^\"]*)");
            if (ms.Success) d["say"] = ms.Groups[1].Value;
            foreach (var num in new[] { "speed", "strafe", "turn_deg", "yaw_deg", "pitch_deg", "x", "y", "z", "id", "at", "heading_deg", "range" })
            {
                var mn = System.Text.RegularExpressions.Regex.Match(s, "\"" + num + "\"\\s*:\\s*(-?[0-9.]+)");
                if (mn.Success) d[num] = double.Parse(mn.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            Logger.LogWarning("salvaged truncated act args: action=" + d["action"]);
            return new JsonObj(d);
        }

        private static Dictionary<string, object> TryParseObj(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Strip markdown fences that chat models love to wrap JSON in.
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
                s = s.Trim();
            }
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try { return Json.Parse(s.Substring(a, b - a + 1)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        // Adapter so say can be called with a raw string.
        private class TextArgs : JsonObj
        {
            public TextArgs(string text) : base(new Dictionary<string, object> { ["text"] = text }) { }
        }

        // Dispatch the LLM's chosen tool. Always returns a result object — never throws —
        // so a bad tool call is just a 'fail:' line in history, not a crash.
        private object RunTool(string name, JsonObj p)
        {
            try
            {
                switch (name)
                {
                    case "walk": return ToolWalk(p);
                    case "walk_ray": return ToolWalkRay(p);
                    case "go_to": return ToolGoTo(p);
                    case "survey": return ToolSurvey(p);
                    case "remember": return ToolRemember(p);
                    case "ask": return ToolAsk(p);
                    case "look_around": return ToolLookAround(p);
                    case "stop": return ToolStop();
                    case "look": return ToolLook(p);
                    case "jump": return ToolJump();
                    case "exit_station": return ToolExitStation();
                    case "crouch": return ToolCrouch(p);
                    case "move_to": return ToolMoveTo(p);
                    case "interact": return ToolInteract(p);
                    case "grab": return ToolGrab(p);
                    case "drop": return ToolDrop();
                    case "say": return ToolSay(p);
                    case "status": return ToolStatus();
                    case "none": return new { ok = true };
                    default:
                        // Model was asked for act= but produced a legacy/unknown name.
                        SetModelError("Invalid action '" + name + "'. Valid actions: walk, walk_ray, go_to, survey, look_around, look, jump, exit_station, crouch, move_to, interact, grab, drop, say, remember, ask, stop, status, none.");
                        Logger.LogWarning("unknown action: " + name);
                        return new { ok = false, reason = "unknown_tool", tool = name };
                }
            }
            catch (Exception e)
            {
                // Never let Mono's "Illegal byte sequence" exception message into the
                // tool result/state — it's toxic (contains a broken code unit) and
                // would be re-serialized every tick, turning one bad LLM byte into a
                // permanent loop. Replace with a stable ASCII reason.
                string msg = e.Message;
                if (msg != null && (msg.IndexOf("Illegal byte sequence", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("String conversion", StringComparison.OrdinalIgnoreCase) >= 0))
                    msg = "bad_encoding_from_llm";
                else msg = Sanitize(msg);
                return new { ok = false, reason = msg };
            }
        }

        // One mega-tool. Forcing tool_choice = act means the model can never
        // "just reply with text" — every tick yields a structured action.
        // Raw JSON schema for the act-args object — sent as response_format so any
        // model (not just tool-calling ones) is constrained to emit exactly this shape.
        private Dictionary<string, object> ActSchema()
        {
            var stepProps = ActionParamProps();
            stepProps["thought"] = Str("thought", "current goal summary, <15 words");
            stepProps["wait"] = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            var planItems = new Dictionary<string, object> { ["type"] = "object", ["properties"] = stepProps, ["required"] = new object[] { "action" }, ["additionalProperties"] = false };

            // Root response shape: reasoning fields FIRST so the model works out loud
            // before naming an action, then the action, then the optional plan.
            var props = new Dictionary<string, object>
            {
                ["progress"] = Str("progress", "one word: what happened with your previous goal (done|blocked|ongoing|changed)"),
                ["why"] = Str("why", "one short sentence justifying this action, referencing what you see"),
                ["thought"] = Str("thought", "restate your current goal in <15 words"),
                ["action"] = ActionParamProps()["action"],
            };
            foreach (var kv in ActionParamProps())
                if (kv.Key != "action") props[kv.Key] = kv.Value;
            props["wait"] = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            props["plan"] = new Dictionary<string, object> { ["type"] = new object[] { "array", "null" }, ["description"] = "optional follow-up actions, in order", ["items"] = planItems, ["maxItems"] = 8 };

            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new object[] { "progress", "why", "thought", "action" },
                ["additionalProperties"] = false,
            };
        }

        private Dictionary<string, object> ActionParamProps()
        {
            return new Dictionary<string, object>
            {
                // ACTION FIRST: reasoning models burn tokens on "thought" and can
                // truncate before emitting the action. Emitting action (and the
                // movement/say params) first means even a truncated call still acts.
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new object[] { "walk", "walk_ray", "go_to", "survey", "remember", "ask", "look_around", "stop", "look", "jump", "exit_station", "crouch", "move_to", "interact", "grab", "drop", "say", "status", "none" } },
                ["say"] = Str("say", "optional <10 words — posts to the real in-game chat window AND a speech bubble"),
                ["name"] = Str("name", "go_to: place to reach by name — 'bed' 'toilet' 'bath' 'sex' 'seat' 'door' 'bodyswap' 'player' or any usable's name"),
                ["id"] = Num("id", "go_to/interact: the numeric 'id' of a specific object from your 'nearby' or survey result — use this to target an exact object instead of matching by name (e.g. go_to id:3, interact id:2)"),
                ["at"] = Num("at", "go_to: stop this many meters SHORT of the target (default 1 — so you reach the object, not bump it; 0 = go right up to it)"),
                ["heading_deg"] = Num("heading_deg", "survey: absolute world yaw to look toward (omit = your current facing)"),
                ["range"] = Num("range", "survey: how far to probe, 2..40m (default = your ray range)"),
                ["mem"] = Str("mem", "optional: a fact to remember for future turns (e.g. 'bed is upstairs', 'player is friendly') — stored in your long-term memory"),
                ["q"] = Str("q", "ask: a question about the world ('what is this place?','who is that?') — answered from your current view + perception"),
                ["crouch"] = Num("crouch", "0..1 how much to crouch (0=stand, 1=full crouch) — also the state for the 'crouch' action"),
                ["speed"] = Num("speed", "walk forward -1..1"),
                ["strafe"] = Num("strafe", "walk sideways: +1=right, -1=left, -1..1 (for tight maneuvers, doorways, squeezing past furniture)"),
                ["duration"] = Num("duration", "walk: seconds to keep going, 0.1..8 (default 2, then auto-stop)"),
                ["turn_deg"] = Num("turn_deg", "walk/look deg turn (+right/-left); nearby.dir tells you which way"),
                ["yaw_deg"] = Num("yaw_deg", "look abs yaw"),
                ["pitch_deg"] = Num("pitch_deg", "look abs pitch"),
                ["ray"] = Num("ray", "walk_ray: ray index 0..N-1 across your view, or -1=center"),
                ["ray_deg"] = Num("ray_deg", "walk_ray: signed degrees left(-)/right(+) of camera center"),
                ["x"] = Num("x", "move_to x"),
                ["y"] = Num("y", "move_to y"),
                ["z"] = Num("z", "move_to z"),
                ["sweep"] = Num("sweep", "look_around: degrees to sweep around (default 120)"),
            };
        }

        private Dictionary<string, object> Num(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "number", "null" }, ["description"] = d }; }

        private Dictionary<string, object> Bool(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "boolean", "null" }, ["description"] = d }; }

        private Dictionary<string, object> Str(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "string", "null" }, ["description"] = d }; }
    }
}
