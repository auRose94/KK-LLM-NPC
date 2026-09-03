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

        // Background vision pipeline.
        private string _sceneDesc = "unknown";

        private string _lastVisionCaption = "";   // previous caption — sent as context to vision model

        private volatile string _lastVisionB64;

        // Steering hint from the vision model (goal-driven "where should I go next").
        private class VisionSteer { public float deg; public string reason; }

        private volatile VisionSteer _visionSteer;

        private volatile bool _visionBusy;

        private float _visionStartTime;

        private int _lastVisionTick = -999;

        private bool _visionModelLoggedOnce;

        private int _visionShot; // last written debug frame index

        private volatile bool _needImageAfterBump;

        private float _lastBigTurnTime = -99f;


        private void MaybeStartVisionPass()
        {
            if (!_cfgVision.Value || !_mainReady) return;
            if (_visionBusy)
            {
                // Reset a wedged vision flag (hung HTTP, etc.) after 2 minutes.
                if (Time.unscaledTime - _visionStartTime > 120f) _visionBusy = false; else return;
            }
            if (_tick - _lastVisionTick < Math.Max(1, _cfgVisionEvery.Value)) return;
            if (!IsAlive(_kobold) || !IsAlive(_cam)) return;
            _visionBusy = true;
            _visionStartTime = Time.unscaledTime;
            _lastVisionTick = _tick;
            var t = new Thread(VisionWorker) { IsBackground = true, Name = "KKLLMNPC-Vision" };
            t.Start();
        }


        private void VisionWorker()
        {
            try
            {
                // Render the current first-person view on the main thread.
                byte[] jpg = null;
                try { jpg = (byte[])RunOnMainThread(() => (object)CaptureImageBytes(), 8000); }
                catch (Exception) { return; }
                if (jpg == null || jpg.Length == 0) { Logger.LogWarning("vision: no frame captured"); return; }
                _lastVisionB64 = "data:image/jpeg;base64," + Convert.ToBase64String(jpg);

                string img = _lastVisionB64;

                if (_cfgVisionDebug.Value) DumpVisionFrame(jpg);

                Logger.LogInfo($"vision: sending {jpg.Length / 1024}KB frame (yaw={F(_yawDeg)})");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string cap = CaptionImage(img);
                sw.Stop();

                if (!string.IsNullOrEmpty(cap))
                {
                    if (!_visionModelLoggedOnce)
                    {
                        _visionModelLoggedOnce = true;
                        string m = !string.IsNullOrWhiteSpace(_cfgVisModel.Value) ? _cfgVisModel.Value : _cfgModel.Value;
                        Logger.LogInfo("KKLLMNPC: vision captions via model=" + m);
                    }
                    _lastVisionCaption = cap; // remembered for next pass's context

                    // Auto-remember landmarks it mentions ("bed is left", "toilet at
                    // 2m ahead") so the action model accumulates a spatial map.
                    try
                    {
                        foreach (var line in cap.Split(new[] { '.', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string l = line.Trim();
                            if (l.Length < 4 || l.Length > 60) continue;
                            string low = l.ToLowerInvariant();
                            // "X is <dir>" / "X at <dist>m <dir>" / "X left|right|ahead"
                            if (low.Contains(" is ") || low.Contains(" at ") || low.Contains(" ahead") || low.Contains(" left") || low.Contains(" right") || low.Contains(" behind"))
                                RememberFact(l);
                        }
                    }
                    catch (Exception) { }

                    // Parse steering hint "go:<deg>:<reason>" out of the caption.
                    _visionSteer = null;
                    int gi = cap.IndexOf("go:", StringComparison.OrdinalIgnoreCase);
                    if (gi >= 0)
                    {
                        string rest = cap.Substring(gi + 3);
                        int end = rest.IndexOfAny(new[] { ':', '|', ' ', '\n' });
                        if (end < 0) end = rest.Length;
                        float deg;
                        if (float.TryParse(rest.Substring(0, end), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deg))
                        {
                            deg = Mathf.Clamp(deg, -120f, 120f);
                            string why = rest.Length > end + 1 ? rest.Substring(end + 1).Trim(':',' ','|','\n') : "vision";
                            _visionSteer = new VisionSteer { deg = deg, reason = why.Length > 0 ? why : "vision" };
                        }
                    }
                    // Also keep the text part (without the go:" token) as the caption.
                    int cut = cap.IndexOf("| go:", StringComparison.OrdinalIgnoreCase);
                    string desc = gi >= 0 ? (cut >= 0 ? cap.Substring(0, cut).Trim() : cap.Substring(0, gi).Trim()) : cap;
                    _sceneDesc = string.IsNullOrEmpty(desc) ? cap : desc;
                    Logger.LogInfo($"vision ({sw.ElapsedMilliseconds}ms): " + cap);
                }
                else Logger.LogWarning($"vision: empty caption after {sw.ElapsedMilliseconds}ms (model may not be vision-capable)");
            }
            catch (Exception e) { Logger.LogWarning("vision: " + e.Message); }
            finally { _visionBusy = false; }
        }


        // Write the exact JPEG the VLM sees so you can inspect the NPC's view.
        private void DumpVisionFrame(byte[] jpg)
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath), "..", "plugins", "KKLLMNPC_frames");
                dir = Path.GetFullPath(dir);
                Directory.CreateDirectory(dir);
                int idx = ++_visionShot;
                File.WriteAllBytes(Path.Combine(dir, $"v{idx:D4}_{(int)_yawDeg}.jpg"), jpg);
                // Keep only the last 40 frames.
                var files = new DirectoryInfo(dir).GetFiles("v*.jpg").OrderBy(f => f.Name).ToList();
                while (files.Count > 40) { try { files[0].Delete(); } catch (Exception) { } files.RemoveAt(0); }
            }
            catch (Exception e) { Logger.LogWarning("vision dump: " + e.Message); }
        }


        // Send the JPEG to the vision model for a short scene caption. Uses the
        // [VisionModel] config when set; otherwise falls back to the main LLM
        // endpoint (which must then be a vision-capable model).
        private string CaptionImage(string imageB64)
        {
            try
            {
                string model    = !string.IsNullOrWhiteSpace(_cfgVisModel.Value)    ? _cfgVisModel.Value.Trim()    : Val(_cfgModel);
                string endpoint = !string.IsNullOrWhiteSpace(_cfgVisEndpoint.Value) ? _cfgVisEndpoint.Value.Trim() : Val(_cfgEndpoint);
                string apiKey   = !string.IsNullOrWhiteSpace(_cfgVisApiKey.Value)   ? _cfgVisApiKey.Value          : Val(_cfgApiKey);

                // Give the vision model its own prior caption as context so it can
                // tell *what changed* instead of describing from zero every time.
                // Also give it the NPC's current goal so its steering suggestion is
                // goal-driven, not just "most open space".
                string visionText = Val(_cfgVisionPrompt) +
                    " Current goal: \"" + _lastThought + "\"" +
                    (_blockedInfo != null ? " Note: " + _blockedInfo + "." : "");
                if (!string.IsNullOrEmpty(_lastVisionCaption))
                    visionText += " Previously: \"" + _lastVisionCaption + "\".";

                var payload = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = new object[]
                            {
                                new Dictionary<string, object> { ["type"] = "text", ["text"] = visionText },
                                new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = imageB64 } },
                            },
                        },
                    },
                    ["max_tokens"] = _cfgVisMaxTokens.Value,
                    ["temperature"] = 0.2,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);

                var req = (HttpWebRequest)WebRequest.Create(endpoint);
                req.Method = "POST";
                req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers["Authorization"] = "Bearer " + apiKey;
                req.Timeout = 60000; req.ReadWriteTimeout = 60000; // vision encode is slow
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    var ms = new MemoryStream();
                    var buf = new byte[8192]; int total = 0, nRead;
                    while ((nRead = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += nRead; if (total > 1024 * 1024) break;
                        ms.Write(buf, 0, nRead);
                    }
                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    var choices = root?.GetValueOrDefault("choices") as List<object>;
                    if (choices == null || choices.Count == 0) return null;
                    var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                    string content = msg?.GetValueOrDefault("content") as string;
                    return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
                }
            }
            catch (Exception e) { Logger.LogWarning("vision endpoint: " + e.Message); return null; }
        }
    }
}
