// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// LMStudio — adopts an EXTERNALLY-running LM Studio (the game can't spawn the lms
// CLI under Proton, same rule as Ollama) and keeps a live handle on it: the
// loaded/not-loaded model list with REAL context windows via GET /api/v0/models
// (drives ContextCore compaction), an opt-in hand-off of LLM traffic
// ([LMStudio] AutoEndpoint), a TTL keep-warm heartbeat, and a model picker that
// both loads the model through LM Studio's own load API and writes [LLM].Model.
//
// Design notes (mirrors Ollama.cs):
//  * NO spawn watchdog — adopt-only; an LM Studio that isn't running just leaves
//    the watcher idle (healthy=false), so a fresh install is unaffected.
//  * Health truth beats config truth: "adopted" means /api/v0/models answered
//    with an LM Studio payload, not that a guess is configured.
//  * Endpoint precedence when BOTH an Ollama and an LM Studio are adopted:
//    Ollama wins ([Ollama] ships first, so nobody's working setup flips) —
//    see NPCInstance.Endpoint().
//  * Tight mod<->LM Studio link: the "select" action writes both
//    [LMStudio].SelectedModel and [LLM].Model, so a WebUI pick takes effect on
//    the very next chat POST; "load" goes further and loads the model in LM
//    Studio itself (something Ollama's API can't do without shelling out).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Configuration;

namespace KKLLMNPC
{
    internal static class LMStudio
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
        // [LLM].ApiKey — LM Studio auth is optional but a user who enabled it
        // expects the mod's probes to carry the same Bearer token as its chats.
        private static ConfigEntry<string> _cfgApiKey;

        private static volatile bool _running;
        private static Thread _watch;
        private static volatile bool _adopted;   // /api/v0/models answered with an LM Studio payload
        private static volatile bool _healthy;    // answered within the last poll
        private static string _status = "off";    // off|adopted|waiting-lmstudio|bad-endpoint
        private static string _note = "";
        private static string _lastError = "";
        private static string _base = "";         // derived LM Studio base (host:port), stripped of /v1/…
        // Cached payloads — served to the WebUI verbatim.
        private static string _modelsJson = "";   // /api/v0/models
        private static volatile string _currentModel = ""; // model the context was read for — watcher thread writes, others read
        private static volatile bool _currentLoaded;
        // Keep-alive heartbeat state (watcher thread only).
        private static DateTime _lastKeepAliveUtc = DateTime.MinValue; // last ping ATTEMPT — never hammer a slow server
        private static bool _keepAliveLogged;                          // one "heartbeat active" log per session
        private static string _lastCtxLogged = "";                     // ctx line logs once per model, not once per poll
        // Replicas (watcher thread maintains; agents read the volatile list).
        private static volatile List<string> _replicas;  // null/1 = shared base; [0]=base, [1..]=instances
        private static string _replicaBase = "";         // base model the set was built for
        private static readonly Dictionary<string, int> _instanceAttempts = new Dictionary<string, int>(); // pending instance → load attempts
        private static readonly HashSet<string> _modLoaded = new HashSet<string>(); // instances the MOD loaded (only these are auto-unloaded)
        private const int MaxInstanceAttempts = 6;

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        internal static void Ensure(LLMNPCPlugin plugin, ConfigEntry<string> cfgLlmModel, ConfigEntry<string> cfgApiKey)
        {
            if (LLMNPCPlugin.Alive(_plugin)) return;
            _plugin = plugin;
            _cfgLlmModel = cfgLlmModel;
            _cfgApiKey = cfgApiKey;
            _cfgEnabled = plugin.Config.Bind("LMStudio", "Enabled", true,
                "Adopt an externally-running LM Studio (the game can't launch one under Proton). ON by default: the mod watches your LM Studio for models, reads their real context windows, and can route the action model through it. With no LM Studio running it just idles (healthy=false), so a fresh install is unaffected. When BOTH an Ollama and an LM Studio are adopted, Ollama takes the traffic.");
            _cfgEndpoint = plugin.Config.Bind("LMStudio", "Endpoint", "http://127.0.0.1:1234",
                "LM Studio base URL (host:port). A full /v1/chat/completions suffix is accepted and stripped. Serve from LM Studio's Developer tab (or `lms server start`).");
            _cfgAutoEndpoint = plugin.Config.Bind("LMStudio", "AutoEndpoint", true,
                "When an LM Studio is adopted + healthy (and no Ollama is), route LLM + vision-fallback POSTs to it regardless of [LLM].Endpoint (hand-off: no config editing after a restart). Off = the configured endpoint stays the truth.");
            _cfgPollInterval = plugin.Config.Bind("LMStudio", "PollIntervalSec", 10,
                "How often the watcher probes the LM Studio /api/v0/models when enabled.");
            _cfgSelectedModel = plugin.Config.Bind("LMStudio", "SelectedModel", "",
                "The LM Studio model the WebUI 'Use this model' picked. Writing this also writes [LLM].Model so it takes effect on the next chat POST. Empty = use whatever LM Studio has loaded.");
            _cfgKeepAlive = plugin.Config.Bind("LMStudio", "KeepAlive", "30m",
                "Keep the adopted model resident so NPC turns never wait behind a model load. LM Studio evicts JIT-loaded models after their idle TTL (default 60m); the watcher sends a 1-token completion (which resets the idle timer) roughly every half of this window. Accepts durations ('45s', '30m', '2h'), a bare number (seconds), or '-1' to keep it resident for the whole session (15m cadence). 'off' (or empty) disables the heartbeat. GUI-loaded models have no TTL and stay resident on their own.");
            _cfgReplicas = plugin.Config.Bind("LMStudio", "Replicas", 1,
                "Parallel turns: every LOADED instance of the adopted model (its '<model>:N' keys — manual UI loads are adopted automatically) joins the agent pool, one instance per agent with a full context window each. This value only says 'ensure at least N': when fewer are loaded, the watcher loads the next missing index (one cold load per poll, TTL from KeepAlive) and adopts it once a poll confirms it loaded. Only mod-loaded instances are ever auto-unloaded (on model change or a return to 1) — yours stay. Each loaded instance takes the model's FULL VRAM. 1 = single shared instance (default). NOTE: server auth (0.4+) requires the token in [LLM].ApiKey or every probe 401s.");
            _running = true;
            _watch = new Thread(WatchLoop) { IsBackground = true, Name = "KKLLMNPC-LMStudioWatch" };
            _watch.Start();
        }

        internal static void Shutdown()
        {
            _running = false;
        }

        // Derive the base (host:port) from the configured endpoint, tolerating a
        // full /v1/chat/completions suffix. Returns "" when it doesn't look like
        // a base, which the watcher treats as "not adopted / ignore me".
        private static string DeriveBase()
        {
            string ep = _cfgEndpoint != null ? _cfgEndpoint.Value : "";
            string base_ = LMStudioArgs.BaseUrl(ep);
            if (_base != base_)
            {
                _base = base_;
                if (base_.Length > 0)
                    try { LLMNPCPlugin.Log?.LogInfo("LMStudio: adopting base " + base_); } catch (Exception) { }
                else
                    try { LLMNPCPlugin.Log?.LogInfo("LMStudio: endpoint isn't a usable base — adoption off"); } catch (Exception) { }
            }
            return base_;
        }

        // ------------------------------------------------------------------
        // watcher — adopts, then polls /api/v0/models
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
                            _note = "adoption disabled — waiting for [LMStudio].Enabled";
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
                            _note = "[LMStudio].Endpoint isn't a usable base (e.g. http://127.0.0.1:1234) — adoption off";
                        }
                        _healthy = false;
                        PollSleep();
                        continue;
                    }
                    // /api/v0/models — does an LM Studio answer here?
                    string modelsJson = HttpGet(base_ + "/api/v0/models");
                    var models = LMStudioArgs.ParseModels(modelsJson);
                    if (models == null)
                    {
                        if (_adopted || _status != "off")
                        {
                            _adopted = false;
                            _status = "waiting-lmstudio";
                            _note = "no LM Studio answering at " + base_ + " — start the server in the Developer tab (or `lms server start`)";
                        }
                        _healthy = false;
                        PollSleep();
                        continue;
                    }
                    // Adopted — an LM Studio is on the line.
                    if (!_adopted)
                    {
                        _adopted = true;
                        _healthy = true;
                        _status = "adopted";
                        _lastError = "";
                        _note = "adopted LM Studio at " + base_;
                        try { LLMNPCPlugin.Log?.LogInfo(_note); } catch (Exception) { }
                    }
                    _modelsJson = modelsJson;
                    // Current model = the user's pick, else whatever LM Studio
                    // has loaded. With JIT loading the list also carries
                    // not-loaded models, so state matters (LMStudioArgs.LoadedModel).
                    string cur = (_cfgSelectedModel != null && _cfgSelectedModel.Value.Length > 0)
                        ? _cfgSelectedModel.Value
                        : "";
                    var loaded = LMStudioArgs.LoadedModel(models);
                    if (string.IsNullOrEmpty(cur) && loaded != null)
                        cur = loaded.Id;
                    // The first LOADED entry can be an instance key ("model:2")
                    // when replicas are up — the shared base is the routing root.
                    if (!string.IsNullOrEmpty(cur)) cur = LMStudioArgs.InstanceBase(cur);
                    if (!string.IsNullOrEmpty(cur))
                    {
                        _currentModel = cur;
                        _currentLoaded = loaded != null && string.Equals(loaded.Id, cur, StringComparison.OrdinalIgnoreCase);
                        var m = FindModel(models, cur);
                        int ctx = LMStudioArgs.EffectiveContext(m, 0);
                        if (ctx > 0 && _lastCtxLogged != cur)
                        {
                            _lastCtxLogged = cur;
                            try { LLMNPCPlugin.Log?.LogInfo("LMStudio /api/v0/models: " + cur + " ctx=" + ctx); } catch (Exception) { }
                        }
                    }
                    else
                    {
                        _currentModel = "";
                        _currentLoaded = false;
                    }
                    // Replicas: load/confirm N instances, then keep them all warm.
                    MaybeMaintainReplicas(cur, models);
                    MaybeKeepAlive(base_);
                    PollSleep();
                }
                catch (Exception e)
                {
                    try { _note = "(watcher) " + e.Message; } catch (Exception) { }
                    try { LLMNPCPlugin.Log?.LogWarning("LMStudio watcher: " + e.Message); } catch (Exception) { }
                    PollSleep();
                }
            }
        }

        private static LmStudioModel FindModel(List<LmStudioModel> models, string id)
        {
            if (models == null || string.IsNullOrEmpty(id)) return null;
            foreach (var m in models)
                if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                    return m;
            return null;
        }

        // ------------------------------------------------------------------
        // keep-alive heartbeat — /v1/completions with a 1-token prompt
        // ------------------------------------------------------------------
        // LM Studio's TTL ("how long a model can stay loaded without receiving
        // any requests") resets on every inference request, and only JIT-loaded
        // models carry one (GUI-loaded models are resident until unloaded). A
        // 1-token completion is the cheapest request that definitely counts —
        // listing models may not — so the watcher pings one at half the
        // configured window. Runs on the existing watcher thread; the 20s
        // timeout covers a JIT cold load the ping itself triggers without
        // hanging the poll loop.
        private static void MaybeKeepAlive(string base_)
        {
            long secs = LMStudioArgs.KeepAliveSeconds(_cfgKeepAlive != null ? _cfgKeepAlive.Value : null);
            int every = LMStudioArgs.PingCadenceSecs(secs);
            if (every <= 0) return;
            if (string.IsNullOrEmpty(base_) || string.IsNullOrEmpty(_currentModel)) return;
            if ((DateTime.UtcNow - _lastKeepAliveUtc).TotalSeconds < every) return;
            // Stamp the ATTEMPT, not the success: a timed-out ping usually means
            // the server is mid-(re)load — the one situation where retrying fast hurts.
            _lastKeepAliveUtc = DateTime.UtcNow;
            // Every loaded instance has its OWN idle TTL — warm them all, or the
            // replica nobody's talking to this minute gets evicted mid-session.
            var names = _replicas;
            if (names == null || names.Count == 0) names = new List<string> { _currentModel };
            foreach (var name in names)
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = name,
                    ["prompt"] = ".",
                    ["max_tokens"] = 1,
                };
                string resp = HttpPost(base_ + "/v1/completions", Json.Write(payload), 20000);
                if (!_keepAliveLogged && resp != null)
                {
                    _keepAliveLogged = true;
                    try { LLMNPCPlugin.Log?.LogInfo("LMStudio keep-alive: " + names.Count + " model(s) stay resident (idle-ttl reset every " + every + "s)"); } catch (Exception) { }
                }
            }
        }

        // ------------------------------------------------------------------
        // replicas — loaded instances of the adopted model, one per agent
        // ------------------------------------------------------------------
        // LM Studio names the Nth loaded instance "<model>:N" (verified live,
        // 2026-11-17 — loading the same model twice yields model + model:2), and
        // a request to that key hits THAT instance. ADOPTION FIRST: every loaded
        // instance of the base joins the pool, manual UI loads included — the
        // pool is what the server says is live, not what the mod created.
        // `Replicas` only says "ensure at least N": when fewer are loaded, the
        // watcher loads the next missing index (ONE attempt per poll — cold
        // loads are slow), capped per instance. Instances the MOD loaded are
        // unloaded on teardown; the user's manual ones are never touched.
        private static void MaybeMaintainReplicas(string cur, List<LmStudioModel> models)
        {
            if (string.IsNullOrEmpty(cur)) return;
            int want = _cfgReplicas != null ? _cfgReplicas.Value : 1;
            if (want < 1) want = 1;
            if (want > 8) want = 8;

            var have = _replicas;
            if (want == 1)
            {
                if (have != null && have.Count > 1) UnloadModLoaded(have);
                _replicas = null;
                _replicaBase = "";
                _instanceAttempts.Clear();
                _modLoaded.Clear();
                return;
            }
            if (!string.Equals(_replicaBase, cur, StringComparison.Ordinal))
            {
                if (have != null && have.Count > 1) UnloadModLoaded(have);
                _replicas = null;
                _replicaBase = cur;
                _instanceAttempts.Clear();
                _modLoaded.Clear();
            }

            // Adopt everything the server says is loaded for this base family.
            var confirmed = LMStudioArgs.LoadedInstancesOf(models, cur);
            if (confirmed.Count == 0) confirmed.Add(cur); // nothing loaded yet — the pool is the base

            // Ensure the configured count: attempt the next missing index, one
            // cold load per poll (it shows up as loaded on a later poll and is
            // adopted then — verification-driven, never trusted blind).
            if (confirmed.Count < want && confirmed.Count < 8)
            {
                for (int i = 2; i <= 8; i++)
                {
                    string name = LMStudioArgs.InstanceName(cur, i);
                    if (confirmed.Contains(name)) continue;
                    int tries;
                    _instanceAttempts.TryGetValue(name, out tries);
                    if (tries < MaxInstanceAttempts)
                    {
                        _instanceAttempts[name] = tries + 1;
                        if (LoadInstance(name)) _modLoaded.Add(name);
                        else if (tries + 1 >= MaxInstanceAttempts)
                            try { LLMNPCPlugin.Log?.LogWarning("LMStudio replicas: instance '" + name + "' never came up after "
                                + MaxInstanceAttempts + " loads — leaving it; the adopted pool stands"); } catch (Exception) { }
                    }
                    break; // one new cold load per poll, however this attempt went
                }
            }

            if (confirmed.Count > 1)
            {
                if (have == null || have.Count != confirmed.Count)
                    try { LLMNPCPlugin.Log?.LogInfo("LMStudio replicas: " + confirmed.Count + " loaded instance(s) of " + cur
                        + " adopted — each agent gets its own" + (_modLoaded.Count > 0 ? " (" + _modLoaded.Count + " loaded by the mod)" : "")); } catch (Exception) { }
                _replicas = confirmed;
            }
            else if (have != null)
                _replicas = null;
        }

        // Load one instance by its ":N" key. TTL from KeepAlive; a successful
        // POST still needs a later poll to confirm (that's the state machine).
        private static bool LoadInstance(string name)
        {
            long ttl = LMStudioArgs.KeepAliveSeconds(_cfgKeepAlive != null ? _cfgKeepAlive.Value : null);
            var body = new Dictionary<string, object> { ["model"] = name };
            if (ttl > 0) body["ttl"] = ttl;
            // 120s: an instance load of a big model takes tens of seconds — a
            // shorter timeout reports failure while the load is still succeeding.
            return HttpPost(_base + "/api/v0/models/load", Json.Write(body), 120000) != null;
        }

        // Best-effort teardown of the instances the MOD loaded (the unload body
        // shape is docs-summarized, not byte-verified — a failure just leaves
        // the instance loaded, which the user can drop in the UI). Instances the
        // USER loaded by hand are never auto-unloaded: they're theirs.
        private static void UnloadModLoaded(List<string> names)
        {
            for (int i = 1; i < names.Count; i++) // [0] = the live base — stays
            {
                if (!_modLoaded.Contains(names[i])) continue;
                var body = new Dictionary<string, object> { ["model"] = names[i] };
                HttpPost(_base + "/api/v0/models/unload", Json.Write(body), 20000);
            }
            _modLoaded.Clear();
        }

        // The model an AGENT should send: its own instance while THIS host is the
        // live endpoint (an instance key sent anywhere else is a 404), the shared
        // base otherwise. Round-robin over the confirmed set. The ordinal itself
        // is claimed once per agent from Ollama's shared counter (see
        // NPCInstance.EffectiveModel — only the live endpoint's claim matters).
        internal static string AgentModel(int agentOrdinal)
        {
            if (EndpointOverride() == null) return CurrentModel();
            var r = _replicas;
            if (r == null || r.Count < 2) return CurrentModel();
            string pick = LMStudioArgs.ReplicaFor(r, agentOrdinal);
            return string.IsNullOrEmpty(pick) ? CurrentModel() : pick;
        }

        private static void PollSleep()
        {
            int ms = _cfgPollInterval != null ? _cfgPollInterval.Value * 1000 : 10000;
            if (ms < 1000) ms = 1000;
            try { Thread.Sleep(ms); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // endpoint hand-off (opt-in; Ollama outranks LM Studio — see NPCInstance)
        // ------------------------------------------------------------------
        internal static string EndpointOverride()
        {
            bool auto = _cfgAutoEndpoint != null && _cfgAutoEndpoint.Value;
            if (!auto || !_adopted || !_healthy || string.IsNullOrEmpty(_base)) return null;
            return _base + "/v1/chat/completions";
        }

        // The model the watcher last saw as current ("" = nothing adopted yet).
        // Lets the payload's model field resolve the "local-model" placeholder
        // against the real server (see NPCInstance.EffectiveModel) — and with
        // JIT loading enabled LM Studio will even load that model on demand.
        internal static string CurrentModel()
        {
            return _currentModel ?? "";
        }

        // ------------------------------------------------------------------
        // HTTP — short-timeout GET/POST with optional Bearer (LM Studio auth is
        // opt-in server-side), no /health (probe /api/v0/models)
        // ------------------------------------------------------------------
        private static string HttpGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 5000;
                string key = _cfgApiKey != null ? _cfgApiKey.Value : "";
                if (!string.IsNullOrEmpty(key))
                    try { req.Headers["Authorization"] = "Bearer " + key; } catch (Exception) { }
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

        private static string HttpPost(string url, string body, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = timeoutMs;
                string key = _cfgApiKey != null ? _cfgApiKey.Value : "";
                if (!string.IsNullOrEmpty(key))
                    try { req.Headers["Authorization"] = "Bearer " + key; } catch (Exception) { }
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
            object models = null;
            if (_modelsJson.Length > 0)
            {
                try { models = Json.Parse(_modelsJson); }
                catch (Exception) { models = _modelsJson; }
            }
            int ctx = 0;
            string arch = "", quant = "";
            if (!string.IsNullOrEmpty(_currentModel))
            {
                var parsed = LMStudioArgs.ParseModels(_modelsJson);
                var m = FindModel(parsed, _currentModel);
                if (m != null)
                {
                    ctx = LMStudioArgs.EffectiveContext(m, 0);
                    arch = m.Arch;
                    quant = m.Quantization;
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
                ["currentLoaded"] = _currentLoaded,
                ["contextLength"] = ctx,
                ["family"] = arch,
                ["quantization"] = quant,
                ["models"] = models,
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
                    Save("LM Studio model set to " + model);
                    return "{\"ok\":true,\"note\":\"using " + model + "\" — [LLM].Model set\"}";
                }
                if (action == "load")
                {
                    // Load the model in LM Studio itself (verified shape:
                    // POST /api/v0/models/load {"model": id, "ttl": seconds}).
                    // ttl comes from the KeepAlive window; "off"/-1 loads
                    // WITHOUT a TTL — resident until unloaded, like `lms load`.
                    string model = Convert.ToString(d.ContainsKey("model") ? d["model"] : "").Trim();
                    if (model.Length == 0)
                        return "{\"ok\":false,\"error\":\"model name required\"}";
                    long ttl = LMStudioArgs.KeepAliveSeconds(_cfgKeepAlive != null ? _cfgKeepAlive.Value : null);
                    var body = new Dictionary<string, object> { ["model"] = model };
                    if (ttl > 0) body["ttl"] = ttl;
                    string resp = HttpPost(_base + "/api/v0/models/load", Json.Write(body), 60000);
                    if (resp == null)
                        return "{\"ok\":false,\"error\":\"load failed — is the LM Studio base " + Json.Write(_base) + " adopted and serving?\"}";
                    Save("LM Studio load requested for " + model);
                    return "{\"ok\":true,\"note\":\"loading " + model + " — watch the next poll\"}";
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
