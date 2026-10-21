// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Ollama — adopts an EXTERNALLY-running Ollama (the game can't exec the Ollama
// ELF under Proton) and keeps a live handle on it: installed-models discovery,
// the loaded model's real context window (which drives ContextCore compaction),
// an opt-in hand-off of LLM traffic ([Ollama] AutoEndpoint), and a model picker
// that writes [LLM].Model so the running LLM uses it.
//
// Design notes:
//  * NO spawn watchdog — Ollama can't be launched from inside the game, so this
//    only ever ADOPTS a running host. There is no KillProc/Spawn/Process life.
//  * Health truth beats process truth: "adopted" means an Ollama answered
//    /api/tags, not that a guess is configured. Off by default so an existing
//    LM Studio setup is never hijacked.
//  * Endpoint opt-in ([Ollama] AutoEndpoint): NPCInstance's POSTs consult
//    EndpointOverride(); off by default so a configured endpoint is always the
//    truth unless the user opts in.
//  * Tight mod<->Ollama link: the "select" action writes both [Ollama].SelectedModel
//    and [LLM].Model, so a WebUI pick takes effect on the very next chat POST.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Configuration;

namespace KKLLMNPC
{
    internal static class Ollama
    {
        private static LLMNPCPlugin _plugin;
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<string> _cfgEndpoint;
        private static ConfigEntry<bool> _cfgAutoEndpoint;
        private static ConfigEntry<int> _cfgPollInterval;
        private static ConfigEntry<string> _cfgSelectedModel;
        // The mod's own [LLM].Model entry, referenced (not re-bound) so a picker
        // pick is reflected into the running LLM immediately.
        private static ConfigEntry<string> _cfgLlmModel;

        private static volatile bool _running;
        private static Thread _watch;
        private static volatile bool _adopted;   // an Ollama answered /api/tags
        private static volatile bool _healthy;    // answered within the last poll
        private static string _status = "off";    // off|adopted|waiting-ollama|bad-endpoint
        private static string _note = "";
        private static string _lastError = "";
        private static string _base = "";         // derived Ollama base (host:port), stripped of /v1/…
        // Cached payloads — served to the WebUI verbatim.
        private static string _tagsJson = "";       // /api/tags
        private static string _showJson = "";       // /api/show for the current model
        private static string _currentModel = "";   // the model /api/show was fetched for

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        internal static void Ensure(LLMNPCPlugin plugin, ConfigEntry<string> cfgLlmModel)
        {
            if (_plugin != null) return;
            _plugin = plugin;
            _cfgLlmModel = cfgLlmModel;
            _cfgEnabled = plugin.Config.Bind("Ollama", "Enabled", false,
                "Adopt an externally-running Ollama (the game can't launch one under Proton). Off = don't touch Ollama; use [LLM].Endpoint like any other server.");
            _cfgEndpoint = plugin.Config.Bind("Ollama", "Endpoint", "http://127.0.0.1:11434",
                "Ollama base URL (host:port). A full /v1/chat/completions suffix is accepted and stripped. The WebUI 'AI server' tab installs models here with `ollama pull <model>` then `ollama run <model>`.");
            _cfgAutoEndpoint = plugin.Config.Bind("Ollama", "AutoEndpoint", false,
                "When an Ollama is adopted + healthy, route LLM + vision-fallback POSTs to it regardless of [LLM].Endpoint (hand-off: no config editing after a restart). Off = the configured endpoint stays the truth.");
            _cfgPollInterval = plugin.Config.Bind("Ollama", "PollIntervalSec", 10,
                "How often the watcher probes the Ollama /api/tags + /api/show when enabled.");
            _cfgSelectedModel = plugin.Config.Bind("Ollama", "SelectedModel", "",
                "The Ollama model the WebUI 'Use this model' picked. Writing this also writes [LLM].Model so it takes effect on the next chat POST. Empty = let /api/tags' current model be used.");
            _running = true;
            _watch = new Thread(WatchLoop) { IsBackground = true, Name = "KKLLMNPC-OllamaWatch" };
            _watch.Start();
        }

        internal static void Shutdown()
        {
            _running = false;
        }

        // Derive the base (host:port) from the configured endpoint, tolerating a
        // full /v1/chat/completions suffix. Returns "" when it doesn't look like
        // an Ollama base, which the watcher treats as "not adopted / ignore me".
        private static string DeriveBase()
        {
            string ep = _cfgEndpoint != null ? _cfgEndpoint.Value : "";
            string base_ = OllamaArgs.BaseUrl(ep);
            if (_base != base_)
            {
                _base = base_;
                if (base_.Length > 0)
                    try { LLMNPCPlugin.Log?.LogInfo("Ollama: adopting base " + base_); } catch (Exception) { }
                else
                    try { LLMNPCPlugin.Log?.LogInfo("Ollama: endpoint isn't an Ollama base — adoption off"); } catch (Exception) { }
            }
            return base_;
        }

        // ------------------------------------------------------------------
        // watcher — adopts, then polls /api/tags + /api/show
        // ------------------------------------------------------------------
        private static void WatchLoop()
        {
            while (_running)
            {
                try
                {
                    bool enabled = _cfgEnabled != null && _cfgEnabled.Value;
                    if (!enabled)
                    {
                        if (_adopted)
                        {
                            _adopted = false;
                            _note = "adoption disabled — waiting for [Ollama].Enabled";
                        }
                        _healthy = false;
                        _status = "off";
                        PollSleep();
                        continue;
                    }
                    string base_ = DeriveBase();
                    if (string.IsNullOrEmpty(base_))
                    {
                        if (_status != "bad-endpoint")
                        {
                            _status = "bad-endpoint";
                            _note = "[Ollama].Endpoint isn't an Ollama base (e.g. http://127.0.0.1:11434) — adoption off";
                        }
                        _healthy = false;
                        PollSleep();
                        continue;
                    }
                    // /api/tags — does an Ollama answer here?
                    string tagsJson = HttpGet(base_ + "/api/tags");
                    if (tagsJson == null)
                    {
                        if (_adopted || _status != "off")
                        {
                            _adopted = false;
                            _status = "waiting-ollama";
                            _note = "no Ollama answering at " + base_ + " — start `ollama serve` (or run it in a terminal)";
                        }
                        _healthy = false;
                        PollSleep();
                        continue;
                    }
                    // Adopted — an Ollama is on the line.
                    if (!_adopted)
                    {
                        _adopted = true;
                        _healthy = true;
                        _status = "adopted";
                        _lastError = "";
                        _note = "adopted Ollama at " + base_;
                        try { LLMNPCPlugin.Log?.LogInfo(_note); } catch (Exception) { }
                    }
                    _tagsJson = tagsJson;
                    // /api/show — the loaded model's real context window (drives compaction).
                    string cur = (_cfgSelectedModel != null && _cfgSelectedModel.Value.Length > 0)
                        ? _cfgSelectedModel.Value
                        : "";
                    if (string.IsNullOrEmpty(cur))
                    {
                        // First adopt: pick the most-recently-used model if tags carries one.
                        var installed = OllamaArgs.ParseTags(tagsJson);
                        if (installed != null && installed.Count > 0)
                            cur = installed[0].Name;
                    }
                    if (cur.Length > 0)
                    {
                        var payload = new Dictionary<string, object> { ["model"] = cur };
                        string showJson = HttpPost(base_ + "/api/show", Json.Write(payload));
                        if (showJson != null)
                        {
                            _showJson = showJson;
                            var show = OllamaArgs.ParseShow(showJson);
                            _currentModel = cur;
                            if (show != null && show.ContextLength > 0)
                                try { LLMNPCPlugin.Log?.LogInfo("Ollama /api/show: " + cur + " ctx=" + show.ContextLength); } catch (Exception) { }
                        }
                    }
                    PollSleep();
                }
                catch (Exception e)
                {
                    try { _note = "(watcher) " + e.Message; } catch (Exception) { }
                    try { LLMNPCPlugin.Log?.LogWarning("Ollama watcher: " + e.Message); } catch (Exception) { }
                    PollSleep();
                }
            }
        }

        private static void PollSleep()
        {
            int ms = _cfgPollInterval != null ? _cfgPollInterval.Value * 1000 : 10000;
            if (ms < 1000) ms = 1000;
            try { Thread.Sleep(ms); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // endpoint hand-off (opt-in) — mirrors KKServe.EndpointOverride
        // ------------------------------------------------------------------
        internal static string EndpointOverride()
        {
            bool auto = _cfgAutoEndpoint != null && _cfgAutoEndpoint.Value;
            if (!auto || !_adopted || !_healthy || string.IsNullOrEmpty(_base)) return null;
            return _base + "/v1/chat/completions";
        }

        // ------------------------------------------------------------------
        // HTTP — short-timeout GET/POST, no /health on Ollama (probe /api/tags)
        // ------------------------------------------------------------------
        private static string HttpGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 5000;
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string HttpPost(string url, string body)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 5000;
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException) { return null; }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------
        // SnapshotJson / HandlePost — transport over the WebUI HTTP server
        // ------------------------------------------------------------------
        internal static string SnapshotJson()
        {
            object tags = null;
            if (_tagsJson.Length > 0)
            {
                try { tags = Json.Parse(_tagsJson); }
                catch (Exception) { tags = _tagsJson; }
            }
            object show = null;
            if (_showJson.Length > 0)
            {
                try { show = Json.Parse(_showJson); }
                catch (Exception) { show = _showJson; }
            }
            int ctx = 0;
            string fam = "", quant = "";
            if (!string.IsNullOrEmpty(_currentModel))
            {
                var showInfo = OllamaArgs.ParseShow(_showJson);
                if (showInfo != null)
                {
                    ctx = showInfo.ContextLength;
                    fam = showInfo.Architecture;
                    quant = showInfo.Quantization;
                }
            }
            string selected = _cfgSelectedModel != null ? _cfgSelectedModel.Value : "";
            var d = new Dictionary<string, object>
            {
                ["status"] = _status,
                ["adopted"] = _adopted,
                ["healthy"] = _healthy,
                ["enabled"] = _cfgEnabled != null && _cfgEnabled.Value,
                ["autoEndpoint"] = _cfgAutoEndpoint != null && _cfgAutoEndpoint.Value,
                ["endpoint"] = _cfgEndpoint != null ? _cfgEndpoint.Value : "",
                ["pollIntervalSec"] = _cfgPollInterval != null ? _cfgPollInterval.Value : 10,
                ["selected"] = selected,
                ["currentModel"] = _currentModel,
                ["contextLength"] = ctx,
                ["family"] = fam,
                ["quantization"] = quant,
                ["tags"] = tags,
                ["show"] = show,
                ["note"] = _note,
                ["lastError"] = _lastError,
            };
            return Json.Write(d);
        }

        private static void Save(string note)
        {
            _note = note;
            _lastError = "";
            try { _plugin.Config.Save(); } catch (Exception) { }
        }

        internal static string HandlePost(string payload)
        {
            try
            {
                var d = Json.Parse(payload) as Dictionary<string, object>;
                if (d == null) return "{\"ok\":false,\"error\":\"bad json\"}";
                string action = Convert.ToString(d.ContainsKey("action") ? d["action"] : "");
                if (action == "select")
                {
                    string model = Convert.ToString(d.ContainsKey("model") ? d["model"] : "").Trim();
                    if (model.Length == 0)
                        return "{\"ok\":false,\"error\":\"model name required\"}";
                    _cfgSelectedModel.Value = model;
                    // Tight link: write the running LLM's model too, so the very
                    // next chat POST uses it (no restart needed).
                    if (_cfgLlmModel != null) _cfgLlmModel.Value = model;
                    Save("Ollama model set to " + model);
                    return "{\"ok\":true,\"note\":\"using " + model + "\" — [LLM].Model set\"}";
                }
                if (action == "refresh")
                {
                    // Force the next watcher poll to refetch; the loop runs on its
                    // own interval so this is a nudge.
                    Save("models refresh requested");
                    return "{\"ok\":true,\"note\":\"refetching models on the next poll\"}";
                }
                return "{\"ok\":false,\"error\":\"unknown action '" + Json.Write(action).Trim() + "'\"}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + Json.Write(e.Message ?? "failed") + "}";
            }
        }
    }
}
