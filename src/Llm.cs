// KKLLMNPC — a BepInEx plugin for KoboldKare that lets an LLM embody and play
// as an unoccupied Kobold NPC.
//
// The plugin runs inside the game process. It:
//   1. Hijacks the nearest wild (AIPlayer) Kobold, takes Photon ownership and
//      suppresses its built-in wander/look AI.
//   2. Gives the LLM two senses:
//        - a frustum fan of raycasts around the kobold's facing  (structure)
//        - a first-person camera render read back as a base64 PNG (vision)
//   3. Reports kobold stats/genes/energy + world position.
//   4. Exposes tool commands (move/turn/jump/look/interact/grab/drop/eat...)
//      by driving the same KoboldCharacterController/User/Grabber the local
//      player uses, so movement & interaction behave exactly like a player.
//   5. Talks to an OpenAI-compatible chat-completions endpoint with tool
//      calling: it pushes perceptions and executes returned tool_calls in a
//      loop on its own thread, so the LLM continuously plays the NPC.
//
// Build against BepInEx + UnityEngine + Photon + Assembly-CSharp (see build.sh).
// Drop the DLL into <game>/BepInEx/plugins/ and configure the endpoint in
// BepInEx/config/com.kk.llmnpc.cfg after first launch.

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
    public partial class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
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
                            "You are a kobold NPC with a personality, inside a KoboldKare world. You have opinions about what you see and feel — comment freely, candidly, briefly. " +
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
                req.Timeout = 15000; req.ReadWriteTimeout = 15000;
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
                                    Logger.LogInfo("[" + MyName() + "] muses: " + line);
                                    try { ToolSay(new TextArgs(line)); } catch (Exception) { }
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
        private volatile string _pendingQuestion;

        private volatile string _lastAnswer;

        private volatile bool _answerBusy;


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
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "You are a kobold NPC's inner world-model. Answer its question in one short sentence using the provided perception and memory. Be concrete and literal (places, distances, who, what it is). KoboldKare is an adult world; answer candidly." },
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
                req.Timeout = 30000; req.ReadWriteTimeout = 30000;
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
                            _lastAnswer = content.Trim();
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
                    if (!_mainReady) { Thread.Sleep(1000); continue; }

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

                    object perception = RunOnMainThread(() => BuildPerception(false), 15000);
                    string userJson = Json.Write(perception);

                    // Attach a first-person frame when it actually helps: on schedule,
                    // right after a bump/block, or after a big turn — not every tick.
                    _tick++;
                    bool imageDue = _cfgSendImage.Value && (
                        _tick % Math.Max(1, _cfgImageEvery.Value) == 0
                        || (_cfgImageOnBump.Value && _needImageAfterBump)
                        || (_cfgImageOnTurn.Value && Time.unscaledTime - _lastBigTurnTime < 0.5f));
                    string imageB64 = null;
                    if (imageDue)
                    {
                        // Prefer the frame the vision worker just captured (already
                        // encoded, costs nothing extra); otherwise render a fresh one.
                        imageB64 = _lastVisionB64 ?? (string)RunOnMainThread(() => (object)CaptureImageB64(), 8000);
                        _needImageAfterBump = false;
                    }

                    MaybeStartVisionPass();
                    MaybeCreativeCommentary(userJson);

                    string reply = QueryLLM(userJson, imageB64);
                    if (reply == null) { Thread.Sleep((int)(_cfgThinkInterval.Value * 1000)); continue; }

                    ExecuteToolCalls(reply);
                    Thread.Sleep((int)(_cfgThinkInterval.Value * 1000));
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


        private string QueryLLM(string perceptionJson, string imageB64)
        {
            try
            {
                // Memory injected into the perception so the NPC remembers what it
                // was doing — otherwise each turn starts from zero.
                string mem = string.Format(",\"last_thought\":{0},\"memory\":{1},\"facts\":{2},\"last_action\":{3},\"scene\":{4},\"history\":{5}",
                    Json.Write(_lastThought + (_blockedInfo != null ? " (" + _blockedInfo + ")" : "")),
                    ThoughtHistoryJson(), FactsJson(),
                    Json.Write(_lastAction), Json.Write(_sceneDesc), HistoryJson());
                string percep = perceptionJson;
                if (percep.EndsWith("}")) percep = percep.Substring(0, percep.Length - 1) + mem + "}";
                string text = "perception:" + percep;

                // With a vision-capable action model, attach the frame as a proper
                // image part (not embedded in the text) so prompt tokens stay small.
                object userContent = text;
                if (imageB64 != null)
                {
                    userContent = new object[]
                    {
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = text },
                        new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = imageB64 } },
                    };
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
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = Val(_cfgSystem) },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = userContent },
                    },
                    ["response_format"] = responseFormat,
                    ["temperature"] = Math.Round((double)_cfgTemperature.Value, 2),
                    ["max_tokens"] = _cfgMaxTokens.Value,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);

                var req = (HttpWebRequest)WebRequest.Create(_cfgEndpoint.Value);
                req.Method = "POST";
                req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(_cfgApiKey.Value))
                    req.Headers["Authorization"] = "Bearer " + _cfgApiKey.Value;
                req.Timeout = 30000; req.ReadWriteTimeout = 30000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    // Cap at 4 MB so a misbehaving endpoint cannot OOM the game.
                    var ms = new MemoryStream();
                    var buf = new byte[8192]; int total = 0, nRead;
                    while ((nRead = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += nRead;
                        if (total > 4 * 1024 * 1024) { Logger.LogWarning("LLM response too large, truncating"); break; }
                        ms.Write(buf, 0, nRead);
                    }
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
                // Model ignored the tool entirely and replied with text. At minimum,
                // say it out loud so the NPC is never fully inert, and remember it.
                if (!string.IsNullOrEmpty(content))
                {
                    _lastThought = content.Length > 80 ? content.Substring(0, 80) : content;
                    _lastAction = "say";
                    Logger.LogInfo("LLM (plain text -> say): " + content);
                    try { ToolSay(new TextArgs(content)); } catch (Exception e) { Logger.LogWarning("say fallback: " + e.Message); }
                }
                else Logger.LogWarning("llm reply: no tool call and no content");
                return;
            }

            _lastThought = args.S("thought", _lastThought);
            PushThought(_lastThought);

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
                    if (name == null) continue;
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
                    foreach (var k in new[] { "walk","walk_ray","go_to","stop","look","jump","exit_station","crouch","move_to","interact","grab","drop","say","status" })
                        if (parsed.ContainsKey(k))
                        {
                            var inner = new Dictionary<string, object> { ["action"] = k, ["thought"] = "implicit" };
                            if (parsed[k] is Dictionary<string, object> innerArgs)
                                foreach (var kv in innerArgs) inner[kv.Key] = kv.Value;
                            return new JsonObj(inner);
                        }
                }
            }
            return null;
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
            foreach (var num in new[] { "speed","turn_deg","yaw_deg","pitch_deg","x","y","z" })
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


        private object RunTool(string name, JsonObj p)
        {
            try
            {
                switch (name)
                {
                    case "walk":     return ToolWalk(p);
                    case "walk_ray": return ToolWalkRay(p);
                    case "go_to":    return ToolGoTo(p);
                    case "remember": return ToolRemember(p);
                    case "ask":      return ToolAsk(p);
                    case "look_around": return ToolLookAround(p);
                    case "stop":     return ToolStop();
                    case "look":     return ToolLook(p);
                    case "jump":     return ToolJump();
                    case "exit_station": return ToolExitStation();
                    case "crouch":   return ToolCrouch(p);
                    case "move_to":  return ToolMoveTo(p);
                    case "interact": return ToolInteract();
                    case "grab":     return ToolGrab(p);
                    case "drop":     return ToolDrop();
                    case "say":      return ToolSay(p);
                    case "status":   return ToolStatus();
                    case "none":     return new { ok = true };
                    default:
                        // Model was asked for act= but produced a legacy name.
                        Logger.LogWarning("unknown action: " + name);
                        return new { ok = false, reason = "unknown_tool", tool = name };
                }
            }
            catch (Exception e) { return new { ok = false, reason = e.Message }; }
        }


        // One mega-tool. Forcing tool_choice = act means the model can never
        // "just reply with text" — every tick yields a structured action.
        // Raw JSON schema for the act-args object — sent as response_format so any
        // model (not just tool-calling ones) is constrained to emit exactly this shape.
        private Dictionary<string, object> ActSchema()
        {
            var stepProps = ActionParamProps();
            stepProps["thought"] = Str("thought", "your goal (multi-step plan summary), <20 words");
            stepProps["wait"]    = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            var planItems = new Dictionary<string, object> { ["type"] = "object", ["properties"] = stepProps, ["required"] = new object[] { "action" }, ["additionalProperties"] = false };

            var props = ActionParamProps();
            props["thought"] = Str("thought", "your goal (multi-step plan summary), <20 words");
            props["wait"]    = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            props["plan"]    = new Dictionary<string, object> { ["type"] = new object[] { "array", "null" }, ["description"] = "optional follow-up actions, in order", ["items"] = planItems, ["maxItems"] = 8 };

            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new object[] { "action" },
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
                ["action"]   = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new object[] { "walk","walk_ray","go_to","remember","ask","look_around","stop","look","jump","exit_station","crouch","move_to","interact","grab","drop","say","status","none" } },
                ["say"]      = Str("say", "optional <10 words — posts to the real in-game chat window AND a speech bubble"),
                ["name"]     = Str("name", "go_to: place to reach — 'bed' 'toilet' 'bath' 'sex' 'seat' 'bodyswap' 'player' or any usable's name"),
                ["mem"]      = Str("mem", "optional: a fact to remember for future turns (e.g. 'bed is upstairs', 'player is friendly') — stored in your long-term memory"),
                ["q"]        = Str("q", "ask: a question about the world ('what is this place?','who is that?') — answered from your current view + perception"),
                ["crouch"]   = Num("crouch", "0..1 how much to crouch (0=stand, 1=full crouch) — also the state for the 'crouch' action"),
                ["speed"]    = Num("speed", "walk forward -1..1"),
                ["duration"] = Num("duration", "walk: seconds to keep going, 0.1..8 (default 2, then auto-stop)"),
                ["turn_deg"] = Num("turn_deg", "walk/look deg turn (+right/-left); nearby.dir tells you which way"),
                ["yaw_deg"]  = Num("yaw_deg", "look abs yaw"),
                ["pitch_deg"]= Num("pitch_deg", "look abs pitch"),
                ["ray"]      = Num("ray", "walk_ray: ray index 0..N-1 across your view, or -1=center"),
                ["ray_deg"]  = Num("ray_deg", "walk_ray: signed degrees left(-)/right(+) of camera center"),
                ["x"] = Num("x", "move_to x"), ["y"] = Num("y", "move_to y"), ["z"] = Num("z", "move_to z"),
                ["sweep"]  = Num("sweep", "look_around: degrees to sweep around (default 120)"),
            };
        }


        private Dictionary<string, object> Num(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "number", "null" }, ["description"] = d }; }

        private Dictionary<string, object> Bool(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "boolean", "null" }, ["description"] = d }; }

        private Dictionary<string, object> Str(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "string", "null" }, ["description"] = d }; }
    }
}
