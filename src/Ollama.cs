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
        private static ConfigEntry<string> _cfgKeepAlive;
        private static ConfigEntry<int> _cfgReplicas;
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
        private static volatile string _currentModel = ""; // the model /api/show was fetched for — written by the watcher thread, read off-thread
        // Keep-alive heartbeat state (watcher thread only).
        private static DateTime _lastKeepAliveUtc = DateTime.MinValue; // last ping ATTEMPT — success or failure, never hammer a slow server
        private static bool _keepAliveLogged;                          // one "heartbeat active" log per session
        // Replicas (watcher thread maintains; agents read the volatile array).
        private static volatile List<string> _replicas;  // null/1 = shared base; [0]=base, [1..]=copies
        private static string _replicaBase = "";         // base model the set was built for
        private static int _agentSeq;                    // round-robin source for per-agent ordinals
        private static int _shrinkHold;                  // consecutive low-population polls before scaling down
        private const int ShrinkHoldPolls = 3;           // ~30s at the default 10s poll — don't churn VRAM on flicker

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        internal static void Ensure(LLMNPCPlugin plugin, ConfigEntry<string> cfgLlmModel)
        {
            if (LLMNPCPlugin.Alive(_plugin)) return;
            _plugin = plugin;
            _cfgLlmModel = cfgLlmModel;
            _cfgEnabled = plugin.Config.Bind("Ollama", "Enabled", true,
                "Adopt an externally-running Ollama (the game can't launch one under Proton). ON by default: the mod watches your Ollama for models and can route the action model through it. Off = don't touch Ollama; use [LLM].Endpoint like any other server. With no Ollama running it just idles (healthy=false), so a fresh install is unaffected.");
            _cfgEndpoint = plugin.Config.Bind("Ollama", "Endpoint", "http://127.0.0.1:11434",
                "Ollama base URL (host:port). A full /v1/chat/completions suffix is accepted and stripped. The WebUI 'AI server' tab installs models here with `ollama pull <model>` then `ollama run <model>`.");
            _cfgAutoEndpoint = plugin.Config.Bind("Ollama", "AutoEndpoint", true,
                "When an Ollama is adopted + healthy, route LLM + vision-fallback POSTs to it regardless of [LLM].Endpoint (hand-off: no config editing after a restart). ON by default so the adopted Ollama is the live endpoint; Off = the configured endpoint stays the truth.");
            _cfgPollInterval = plugin.Config.Bind("Ollama", "PollIntervalSec", 10,
                "How often the watcher probes the Ollama /api/tags + /api/show when enabled.");
            _cfgSelectedModel = plugin.Config.Bind("Ollama", "SelectedModel", "",
                "The Ollama model the WebUI 'Use this model' picked. Writing this also writes [LLM].Model so it takes effect on the next chat POST. Empty = let /api/tags' current model be used.");
            _cfgKeepAlive = plugin.Config.Bind("Ollama", "KeepAlive", "30m",
                "Keep the adopted model resident so NPC turns never wait behind a model reload. The watcher pings /api/generate with an empty prompt (Ollama's documented keep-warm call) roughly every half of this window. Accepts Ollama durations ('45s', '30m', '2h'), a bare number (seconds), or '-1' to pin the model in RAM for the whole session. 'off' (or empty) disables the heartbeat — Ollama then unloads after its own ~5m idle and the next turn pays the reload. Pings stop with the game, so '30m' also unloads shortly after you quit.");
            _cfgReplicas = plugin.Config.Bind("Ollama", "Replicas", 1,
                "Load UP TO this many instances of the adopted model — scaled ON DEMAND to the live agent count (instance N loads only when a Nth agent exists; extras release VRAM when agents leave), so agents run parallel turns each with a FULL context window (the OLLAMA_NUM_PARALLEL alternative SPLITS one window across its slots). Extra instances are zero-disk copies via /api/copy (same files, separate loaded runner). Each loaded instance takes the model's FULL VRAM: 2 instances + server-side per-model concurrency is the sweet spot for one shared GPU — 4× a multi-GB model can black-screen/crash a machine that drives its display from the same card (seen live); use a second machine or a GPU dedicated to compute if you want more. Agents are assigned round-robin while the Ollama hand-off is live. 1 = the single shared instance (default). [LMStudio] has the same knob.");
            if (_cfgReplicas.Value >= 3)
                try { LLMNPCPlugin.Log?.LogWarning("Ollama Replicas=" + _cfgReplicas.Value + ": every loaded instance takes the model's FULL VRAM on the SAME GPU that may be driving your display — 4× a multi-GB model has black-screened a machine here. 2 + per-model server concurrency is the safer shape; consider a second machine or a compute-only GPU."); } catch (Exception) { }
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
                    // Replicas: maintain N loaded copies (copy/prewarm/unload),
                    // then keep every one of them warm.
                    MaybeMaintainReplicas(base_, cur);
                    MaybeKeepAlive(base_);
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

        // ------------------------------------------------------------------
        // keep-alive heartbeat — /api/generate with an empty prompt
        // ------------------------------------------------------------------
        // The chat POSTs ride Ollama's OpenAI-compatible /v1/chat/completions,
        // where a body-level keep_alive is not part of the schema. The NATIVE
        // /api/generate takes it, and an empty prompt makes the call a no-op
        // generation that just (re)asserts residency — Ollama's documented
        // keep-warm trick. Without it Ollama's default ~5m idle unload evicts
        // the model during quiet play, and the next NPC turn stalls behind a
        // multi-gigabyte reload: the exact stall the v1.2.2 model_unloaded
        // retry only patched around. Runs on the existing watcher thread; the
        // 20s timeout covers a cold (re)load without hanging the poll loop.
        private static void MaybeKeepAlive(string base_)
        {
            long secs = OllamaArgs.KeepAliveSeconds(_cfgKeepAlive != null ? _cfgKeepAlive.Value : null);
            int every = OllamaArgs.KeepAlivePingSecs(secs);
            if (every <= 0) return;
            if (string.IsNullOrEmpty(base_) || string.IsNullOrEmpty(_currentModel)) return;
            if ((DateTime.UtcNow - _lastKeepAliveUtc).TotalSeconds < every) return;
            // Stamp the ATTEMPT, not the success: a timed-out ping usually means
            // the server is mid-(re)load — the one situation where retrying fast hurts.
            _lastKeepAliveUtc = DateTime.UtcNow;
            // Every loaded replica has its OWN idle timer — warm them all, or the
            // shard nobody's talking to this minute gets evicted mid-session.
            var names = _replicas;
            if (names == null || names.Count == 0) names = new List<string> { _currentModel };
            foreach (var name in names)
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = name,
                    ["prompt"] = "",
                    ["keep_alive"] = secs, // -1 (pin) or the window in seconds — both native forms
                };
                HttpPost(base_ + "/api/generate", Json.Write(payload), 20000);
            }
            if (!_keepAliveLogged)
            {
                _keepAliveLogged = true;
                try { LLMNPCPlugin.Log?.LogInfo("Ollama keep-alive: " + names.Count + " model(s) stay resident (keep_alive="
                    + (secs == -1 ? "-1 (pinned)" : secs + "s") + ", ping every " + every + "s)"); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // replicas — N loaded instances of the adopted model, one per agent
        // ------------------------------------------------------------------
        // Ollama loads by TAG: /api/copy makes a second tag over the same blobs
        // (zero disk cost) that loads as a SEPARATE runner with its own FULL
        // context window. The watcher keeps [base, copy2..copyN] existing,
        // prewarmed (cold loads can take a minute each — doing them here, on the
        // watcher thread, is the same trade the keep-alive already makes) and,
        // on a model change or shutdown-to-1, unloaded. A copy that can't be
        // created (old Ollama, read-only registry) is skipped: those agents fall
        // back to the shared base rather than failing their turns.
        private static void MaybeMaintainReplicas(string base_, string cur)
        {
            if (string.IsNullOrEmpty(base_) || string.IsNullOrEmpty(cur)) return;
            int want = _cfgReplicas != null ? _cfgReplicas.Value : 1;
            if (want < 1) want = 1;
            if (want > 8) want = 8;

            List<string> have = _replicas;
            bool sameBase = string.Equals(_replicaBase, cur, StringComparison.Ordinal);
            if (want == 1)
            {
                if (have != null && have.Count > 1) UnloadShards(base_, have);
                _replicas = null;
                _replicaBase = "";
                return;
            }
            if (!sameBase)
            {
                // Model changed (or first run): retire the old copies before
                // building the new set — otherwise they squat VRAM next to the
                // new model's.
                if (have != null && have.Count > 1) UnloadShards(base_, have);
                _replicas = null;
                _replicaBase = cur;
                have = null;
            }

            // ON-DEMAND (v1.6.0): scale to the LIVE agent population, not the
            // configured cap — instance N loads only when a Nth agent exists
            // (a fresh cold load lands while that agent shares the base; the
            // next query migrates it). Load-balancing: scale down (with a hold,
            // so a body death/respawn flicker doesn't churn VRAM) too.
            int target = OllamaArgs.ReplicasForPopulation(want, LLMNPCPlugin.LiveAgentCount);
            if (have != null && have.Count == target) { _shrinkHold = 0; return; } // maintained
            if (have != null && have.Count > target)
            {
                if (++_shrinkHold < ShrinkHoldPolls) return;
                _shrinkHold = 0;
                var shrunk = new List<string>(have);
                while (shrunk.Count > target)
                {
                    var payload = new Dictionary<string, object>
                    {
                        ["model"] = shrunk[shrunk.Count - 1],
                        ["prompt"] = "",
                        ["keep_alive"] = 0,
                    };
                    HttpPost(base_ + "/api/generate", Json.Write(payload), 20000);
                    shrunk.RemoveAt(shrunk.Count - 1);
                }
                _replicas = shrunk.Count > 1 ? shrunk : null;
                try { LLMNPCPlugin.Log?.LogInfo("Ollama replicas: scaled down to " + target
                    + " (agents left) — VRAM released"); } catch (Exception) { }
                return;
            }
            _shrinkHold = 0;

            // Scale up: add the next missing shard — ONE cold load per poll
            // (a second attempt would just queue behind a load in progress).
            var set = OllamaArgs.ReplicaSet(cur, target);
            var live = new List<string>(have ?? new List<string> { cur });
            if (live[0] != cur) live[0] = cur;
            if (live.Count < set.Count)
            {
                string shard = set[live.Count];
                if (EnsureShardExists(base_, cur, shard)) // failure logged inside — retry next poll
                {
                    PrewarmShard(base_, shard);
                    live.Add(shard);
                    try { LLMNPCPlugin.Log?.LogInfo("Ollama replicas: instance '" + shard + "' live for agent "
                        + live.Count + " (on-demand)"); } catch (Exception) { }
                }
            }
            _replicas = live.Count > 1 ? live : null;
            if (live.Count > 1 && (have == null || have.Count < live.Count) && live.Count == target)
                try { LLMNPCPlugin.Log?.LogInfo("Ollama replicas: " + live.Count + " instances of " + cur
                    + " live — each agent gets its own (full context window each)"); } catch (Exception) { }
        }

        // Copy the base to the shard tag; a copy that fails because the tag
        // already exists (previous session) still counts — /api/show confirms it.
        private static bool EnsureShardExists(string base_, string baseModel, string shard)
        {
            var payload = new Dictionary<string, object> { ["source"] = baseModel, ["destination"] = shard };
            if (HttpPost(base_ + "/api/copy", Json.Write(payload), 20000) != null) return true;
            var probe = new Dictionary<string, object> { ["model"] = shard };
            if (HttpPost(base_ + "/api/show", Json.Write(probe), 10000) != null) return true;
            try { LLMNPCPlugin.Log?.LogWarning("Ollama replicas: shard '" + shard + "' unavailable (copy + show both failed) — agents fall back to the shared instance"); } catch (Exception) { }
            return false;
        }

        private static void PrewarmShard(string base_, string shard)
        {
            long secs = OllamaArgs.KeepAliveSeconds(_cfgKeepAlive != null ? _cfgKeepAlive.Value : null);
            var payload = new Dictionary<string, object>
            {
                ["model"] = shard,
                ["prompt"] = "",
                ["keep_alive"] = secs <= 0 ? 1800L : secs,
            };
            // 120s: a cold load of a big model takes tens of seconds — a shorter
            // timeout would report failure while the load is still succeeding.
            HttpPost(base_ + "/api/generate", Json.Write(payload), 120000);
        }

        private static void UnloadShards(string base_, List<string> shards)
        {
            for (int i = 1; i < shards.Count; i++) // [0] is the live base — stays
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = shards[i],
                    ["prompt"] = "",
                    ["keep_alive"] = 0,
                };
                HttpPost(base_ + "/api/generate", Json.Write(payload), 20000);
            }
        }

        // The model an AGENT should send: its own replica while the hand-off is
        // live (a shard name sent anywhere else is a 404), the shared base
        // otherwise. Round-robin over the live set.
        internal static string AgentModel(int agentOrdinal)
        {
            if (EndpointOverride() == null) return CurrentModel();
            var r = _replicas;
            if (r == null || r.Count < 2) return CurrentModel();
            string pick = OllamaArgs.ReplicaFor(r, agentOrdinal);
            return string.IsNullOrEmpty(pick) ? CurrentModel() : pick;
        }

        internal static int NextAgentOrdinal()
        {
            return System.Threading.Interlocked.Increment(ref _agentSeq);
        }

        private static void PollSleep()
        {
            int ms = _cfgPollInterval != null ? _cfgPollInterval.Value * 1000 : 10000;
            if (ms < 1000) ms = 1000;
            try { Thread.Sleep(ms); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // endpoint hand-off (opt-in)
        // ------------------------------------------------------------------
        internal static string EndpointOverride()
        {
            bool auto = _cfgAutoEndpoint != null && _cfgAutoEndpoint.Value;
            if (!auto || !_adopted || !_healthy || string.IsNullOrEmpty(_base)) return null;
            return _base + "/v1/chat/completions";
        }

        // The model the watcher last saw as current on /api/tags ("" = nothing
        // adopted yet). Lets the payload's model field resolve the "local-model"
        // placeholder against the real server instead of 404ing (see
        // NPCInstance.EffectiveModel).
        internal static string CurrentModel()
        {
            return _currentModel ?? "";
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
            return HttpPost(url, body, 5000);
        }

        private static string HttpPost(string url, string body, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = timeoutMs;
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
                ["keepAlive"] = _cfgKeepAlive != null ? _cfgKeepAlive.Value : "30m",
                ["replicas"] = _replicas != null ? _replicas.Count : 1,
                ["replicaModels"] = _replicas,
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
