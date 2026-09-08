// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace KKLLMNPC
{
    internal static class ModelProbe
    {
        // Probe results — set once at startup, read by NPCInstance.
        internal static string DetectedModelName;
        internal static string DetectedTier;   // "small" | "medium" | "large" | null (unknown)
        internal static int DetectedContextLength;
        internal static long DetectedParameters;  // total parameter count
        internal static string DetectedQuantization;
        internal static string DetectedError;

        // Server capabilities — discovered at probe time.
        internal static string BaseUrl;            // derived from endpoint (e.g. "http://127.0.0.1:5001")
        internal static bool AdminAvailable;     // /api/admin endpoints respond (200)
        internal static int TrueContextLength;  // from /api/extra/true_max_context_length
        internal static readonly List<ModelInfo> AvailableModels = new List<ModelInfo>();

        internal class ModelInfo
        {
            public string Id;
            public int ContextLength;
            public long Parameters;
            public string Tier;  // classified: small/medium/large
        }

        /// <summary>
        /// Probe the KoboldCpp server for model capabilities.  Called once from Main.Awake
        /// after config is bound.  Reads LLM.Endpoint, derives the base URL, then tries:
        ///   1. /model          (KoboldCpp-specific — has parameter count + context)
        ///   2. /v1/models      (OpenAI-standard — has model name)
        /// Results are stored in Detected* fields for NPCInstance to use.
        /// Retries each HTTP call up to 3 times with 1s delay to handle startup race
        /// conditions (server not ready yet is common with LM Studio).
        /// </summary>
        internal static void Probe(string endpoint, string apiKey)
        {
            try
            {
                // Derive base URL: strip /v1/chat/completions or /chat/completions or trailing /
                string baseUrl = (endpoint ?? "").TrimEnd('/');
                string[] suffixes = { "/v1/chat/completions", "/chat/completions", "/v1/completions", "/completions" };
                foreach (var suf in suffixes)
                {
                    if (baseUrl.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                    {
                        baseUrl = baseUrl.Substring(0, baseUrl.Length - suf.Length);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(baseUrl)) { DetectedError = "empty endpoint"; return; }
                BaseUrl = baseUrl;

                // Retry each probe up to 3 times to handle startup race conditions
                const int probeRetries = 3;
                const int probeDelayMs = 1000;

                // 1. Try KoboldCpp /model endpoint (richest info)
                bool koboldOk = false;
                for (int attempt = 0; attempt < probeRetries; attempt++)
                {
                    TryKoboldModel(baseUrl, apiKey);
                    if (DetectedModelName != null || DetectedContextLength > 0) { koboldOk = true; break; }
                    if (attempt < probeRetries - 1)
                        LLMNPCPlugin.Log?.LogInfo("ModelProbe: /model probe attempt " + (attempt + 1) + "/" + probeRetries + ", retrying...");
                    Thread.Sleep(probeDelayMs);
                }

                // 2. Try OpenAI /v1/models (at least gives us the model name)
                bool openAiOk = false;
                for (int attempt = 0; attempt < probeRetries; attempt++)
                {
                    TryOpenAIModels(baseUrl, apiKey);
                    if (DetectedModelName != null || AvailableModels.Count > 0) { openAiOk = true; break; }
                    if (attempt < probeRetries - 1)
                        LLMNPCPlugin.Log?.LogInfo("ModelProbe: /v1/models probe attempt " + (attempt + 1) + "/" + probeRetries + ", retrying...");
                    Thread.Sleep(probeDelayMs);
                }

                // 3. If no models found at all, warn clearly
                if (DetectedModelName == null && AvailableModels.Count == 0)
                {
                    DetectedError = "NO_MODELS_LOADED";
                    LLMNPCPlugin.Log?.LogError("ModelProbe: NO MODELS LOADED on the server! Load a model in the server UI first.");
                    LLMNPCPlugin.Log?.LogError("ModelProbe: LM Studio → Developer tab → Load Model.  KoboldCpp → load via GUI or --model flag.");
                }

                // 4. Classify from whatever we gathered
                ClassifyFromDetected();

                // 5. Discover server capabilities (also retry)
                for (int attempt = 0; attempt < probeRetries; attempt++)
                {
                    DiscoverServerCapabilities(baseUrl, apiKey);
                    if (AdminAvailable || !string.IsNullOrEmpty(BaseUrl)) break;
                    if (attempt < probeRetries - 1) Thread.Sleep(probeDelayMs);
                }
            }
            catch (Exception e)
            {
                DetectedError = e.Message;
                LLMNPCPlugin.Log?.LogWarning("ModelProbe: failed — " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // KoboldCpp /model — returns { "result":"ok", "data":{ "model_name", "context_length", "total_parameters", "quantization" } }
        // ------------------------------------------------------------------
        private static void TryKoboldModel(string baseUrl, string apiKey)
        {
            try
            {
                string json = HttpGet(baseUrl + "/model", apiKey);
                if (string.IsNullOrEmpty(json)) return;

                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) return;

                // Check for "result": "ok"
                object resultObj;
                if (!root.TryGetValue("result", out resultObj)) return;
                if ((resultObj as string) != "ok") return;

                object dataObj;
                if (!root.TryGetValue("data", out dataObj)) return;
                var data = dataObj as Dictionary<string, object>;
                if (data == null) return;

                object modelNameObj;
                if (data.TryGetValue("model_name", out modelNameObj))
                {
                    string modelName = modelNameObj as string;
                    if (!string.IsNullOrEmpty(modelName)) DetectedModelName = modelName;
                }

                object ctxObj;
                if (data.TryGetValue("context_length", out ctxObj))
                    int.TryParse(ctxObj.ToString(), out DetectedContextLength);

                object paramObj;
                if (data.TryGetValue("total_parameters", out paramObj))
                    long.TryParse(paramObj.ToString(), out DetectedParameters);

                object quantObj;
                if (data.TryGetValue("quantization", out quantObj))
                {
                    string quant = quantObj as string;
                    if (!string.IsNullOrEmpty(quant)) DetectedQuantization = quant;
                }

                LLMNPCPlugin.Log?.LogInfo("ModelProbe /model: name=" + (DetectedModelName ?? "?")
                    + " ctx=" + DetectedContextLength
                    + " params=" + DetectedParameters
                    + " quant=" + (DetectedQuantization ?? "?"));
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log?.LogInfo("ModelProbe /model: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // OpenAI /v1/models — returns { "data": [ { "id": "...", ... } ] }
        // ------------------------------------------------------------------
        private static void TryOpenAIModels(string baseUrl, string apiKey)
        {
            try
            {
                string url = baseUrl;
                if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    url += "/v1";
                url += "/models";

                string json = HttpGet(url, apiKey);
                if (string.IsNullOrEmpty(json)) return;

                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) return;

                object dataObj;
                if (!root.TryGetValue("data", out dataObj)) return;
                var data = dataObj as List<object>;
                if (data == null || data.Count == 0)
                {
                    LLMNPCPlugin.Log?.LogWarning("ModelProbe /v1/models: server returned empty model list — no model is loaded!");
                    return;
                }

                // Take the first model's "id"
                var first = data[0] as Dictionary<string, object>;
                object idObj;
                string id = first != null && first.TryGetValue("id", out idObj) ? idObj as string : null;
                if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(DetectedModelName))
                    DetectedModelName = id;

                LLMNPCPlugin.Log?.LogInfo("ModelProbe /v1/models: id=" + (id ?? "?"));
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log?.LogInfo("ModelProbe /v1/models: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // Classify tier from detected parameters (and name heuristics)
        // ------------------------------------------------------------------
        private static void ClassifyFromDetected()
        {
            if (DetectedParameters > 0)
            {
                if (DetectedParameters < 3_000_000_000L)
                    DetectedTier = "small";
                else if (DetectedParameters < 15_000_000_000L)
                    DetectedTier = "medium";
                else
                    DetectedTier = "large";
                LLMNPCPlugin.Log?.LogInfo("ModelProbe: classified as '" + DetectedTier + "' from " + DetectedParameters + " parameters");
                return;
            }

            // Fallback: guess from model name
            string name = (DetectedModelName ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) { DetectedTier = null; return; }

            // Small model heuristics
            if (name.Contains("0.5b") || name.Contains("1b") || name.Contains("1.5b") || name.Contains("3b")
                || name.Contains("7b") || name.Contains("8b") || name.Contains("qwen2-7")
                || name.Contains("llama-7") || name.Contains("llama-8"))
            {
                DetectedTier = "small";
            }
            // Large model heuristics
            else if (name.Contains("70b") || name.Contains("72b") || name.Contains("405b")
                || name.Contains("gpt-4") || name.Contains("claude") || name.Contains("mixtral-8x22")
                || name.Contains("deepseek-r1") || name.Contains("qwen-max"))
            {
                DetectedTier = "large";
            }
            // Medium is everything else with a size hint
            else if (name.Contains("13b") || name.Contains("14b") || name.Contains("20b")
                || name.Contains("30b") || name.Contains("34b") || name.Contains("32b"))
            {
                DetectedTier = "medium";
            }

            if (DetectedTier != null)
                LLMNPCPlugin.Log?.LogInfo("ModelProbe: classified as '" + DetectedTier + "' from model name '" + DetectedModelName + "'");
        }

        // ------------------------------------------------------------------
        // Discover server capabilities: admin mode, true context length, etc.
        // ------------------------------------------------------------------
        private static void DiscoverServerCapabilities(string baseUrl, string apiKey)
        {
            // Try KoboldCpp admin check
            try
            {
                string json = HttpGet(baseUrl + "/api/admin/list_options", apiKey);
                if (json != null)
                {
                    AdminAvailable = true;
                    LLMNPCPlugin.Log?.LogInfo("ModelProbe: admin mode available");
                }
            }
            catch (Exception) { AdminAvailable = false; }

            // Try true context length (KoboldCpp-specific)
            try
            {
                string json = HttpGet(baseUrl + "/api/extra/true_max_context_length", apiKey);
                if (json != null)
                {
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    object val;
                    if (root != null && root.TryGetValue("value", out val))
                        int.TryParse(val.ToString(), out TrueContextLength);
                    if (TrueContextLength > 0)
                        LLMNPCPlugin.Log?.LogInfo("ModelProbe: true context length = " + TrueContextLength);
                }
            }
            catch (Exception e) { LLMNPCPlugin.Log?.LogInfo("ModelProbe /api/extra/true_max_context_length: " + e.Message); }

            // Try /v1/models to list all available models
            try
            {
                string url = baseUrl;
                if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url += "/v1";
                url += "/models";
                string json = HttpGet(url, apiKey);
                if (json != null)
                {
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    object dataObj;
                    if (root != null && root.TryGetValue("data", out dataObj))
                    {
                        var data = dataObj as List<object>;
                        if (data != null)
                        {
                            foreach (var item in data)
                            {
                                var dict = item as Dictionary<string, object>;
                                if (dict == null) continue;
                                object idObj;
                                string id = dict.TryGetValue("id", out idObj) ? idObj as string : null;
                                if (string.IsNullOrEmpty(id)) continue;
                                var info = new ModelInfo { Id = id };
                                // Try to extract context length and params from owned_by or metadata
                                object ctxObj;
                                if (dict.TryGetValue("context_length", out ctxObj))
                                    int.TryParse(ctxObj.ToString(), out info.ContextLength);
                                object paramObj;
                                if (dict.TryGetValue("total_parameters", out paramObj))
                                    long.TryParse(paramObj.ToString(), out info.Parameters);
                                // Classify from name
                                string lower = id.ToLowerInvariant();
                                if (info.Parameters > 0)
                                {
                                    if (info.Parameters < 3_000_000_000L) info.Tier = "small";
                                    else if (info.Parameters < 15_000_000_000L) info.Tier = "medium";
                                    else info.Tier = "large";
                                }
                                else
                                {
                                    if (lower.Contains("7b") || lower.Contains("8b") || lower.Contains("3b") || lower.Contains("1b"))
                                        info.Tier = "small";
                                    else if (lower.Contains("70b") || lower.Contains("72b") || lower.Contains("405b"))
                                        info.Tier = "large";
                                    else if (lower.Contains("13b") || lower.Contains("14b") || lower.Contains("30b") || lower.Contains("34b"))
                                        info.Tier = "medium";
                                }
                                AvailableModels.Add(info);
                            }
                            LLMNPCPlugin.Log?.LogInfo("ModelProbe: found " + AvailableModels.Count + " model(s) on server");
                        }
                    }
                }
            }
            catch (Exception e) { LLMNPCPlugin.Log?.LogInfo("ModelProbe /v1/models: " + e.Message); }
        }

        // ------------------------------------------------------------------
        // Try to switch the server to a different model (KoboldCpp admin mode).
        // Returns true if the switch was initiated. The server will restart
        // with the new model — expect a brief pause.
        // ------------------------------------------------------------------
        internal static bool TrySwitchModel(string modelConfigName, string apiKey)
        {
            if (!AdminAvailable || string.IsNullOrEmpty(BaseUrl))
            {
                LLMNPCPlugin.Log?.LogInfo("ModelProbe: cannot switch model (admin not available)");
                return false;
            }
            try
            {
                // KoboldCpp admin reload_config — switches to a .kcpps config file.
                // The config file name is the model identifier.
                var payload = new Dictionary<string, object> { ["config"] = modelConfigName };
                string body = Json.Write(payload);
                var req = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/admin/reload_config");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 30000;
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers["Authorization"] = "Bearer " + apiKey;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                {
                    LLMNPCPlugin.Log?.LogInfo("ModelProbe: model switch initiated to '" + modelConfigName + "'");
                    return true;
                }
            }
            catch (Exception e)
            {
                LLMNPCPlugin.Log?.LogWarning("ModelProbe: model switch failed — " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Query the current context length from the server.
        // ------------------------------------------------------------------
        internal static int QueryContextLength(string apiKey)
        {
            if (string.IsNullOrEmpty(BaseUrl)) return 0;
            try
            {
                string json = HttpGet(BaseUrl + "/api/v1/config/max_context_length", apiKey);
                if (json != null)
                {
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    object val;
                    if (root != null && root.TryGetValue("value", out val))
                    {
                        int ctx;
                        if (int.TryParse(val.ToString(), out ctx) && ctx > 0) return ctx;
                    }
                }
            }
            catch (Exception e) { LLMNPCPlugin.Log?.LogInfo("ModelProbe /config/max_context_length: " + e.Message); }
            return TrueContextLength > 0 ? TrueContextLength : DetectedContextLength;
        }

        // ------------------------------------------------------------------
        // Find the best available model for a given tier and minimum context.
        // Returns the model ID, or null if none found.
        // ------------------------------------------------------------------
        internal static string FindBestModel(string desiredTier, int minContext)
        {
            string best = null;
            int bestScore = -1;
            foreach (var m in AvailableModels)
            {
                // Must meet context requirement
                if (m.ContextLength > 0 && m.ContextLength < minContext) continue;
                // Score: prefer matching tier, then larger context
                int score = 0;
                if (m.Tier == desiredTier) score += 100;
                else if (desiredTier == "small" && m.Tier == "medium") score += 50;
                else if (desiredTier == "medium" && m.Tier == "large") score += 50;
                score += m.ContextLength / 1000;  // bonus for larger context
                if (score > bestScore) { bestScore = score; best = m.Id; }
            }
            return best;
        }

        // ------------------------------------------------------------------
        // HTTP GET / POST helpers
        // ------------------------------------------------------------------
        private static string HttpGet(string url, string apiKey)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 5000;
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["Authorization"] = "Bearer " + apiKey;
            using (var resp = req.GetResponse())
            using (var stream = resp.GetResponseStream())
            {
                if (stream == null) return null;
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }
}
