// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// LMStudioArgs — pure logic for adopting an LM Studio host: endpoint parsing and
// /api/v0/models JSON parsing. No game/BepInEx/Unity references so the headless
// test suite can compile it directly (the watcher side lives in LMStudio.cs).
//
// LM Studio is a *running* host the mod adopts (same rule as Ollama: the game
// can't spawn the lms CLI under Proton), so this is just parsing — one endpoint
// carries everything the watcher needs, which is why LM Studio needs no /api/show
// round-trip: GET /api/v0/models lists every model WITH its load state, real
// max_context_length (drives ContextCore compaction), arch, and quantization.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class LMStudioArgs
    {
        // Same suffix-stripping as the Ollama base: the endpoint config may carry
        // a full OpenAI path; the native API hangs off the bare host:port.
        public static string BaseUrl(string endpoint)
        {
            return OllamaArgs.BaseUrl(endpoint);
        }

        // Parse an LM Studio GET /api/v0/models payload. Real shape (verified
        // against the docs):
        //   { "object": "list", "data": [ { "id": "...", "object": "model",
        //       "type": "llm", "publisher": "...", "arch": "qwen2_vl",
        //       "compatibility_type": "gguf", "quantization": "Q4_K_M",
        //       "state": "loaded" | "not-loaded", "max_context_length": 32768 } ] }
        // Returns null when the payload isn't an LM Studio model list — in
        // particular a plain OpenAI /v1/models body (id/object/created/owned_by
        // only) must NOT adopt: only entries carrying an LM-Studio-only field
        // (state / quantization / arch / max_context_length) do. That's the
        // false-positive guard for servers that alias /api/v0/models.
        public static List<LmStudioModel> ParseModels(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;
            object dataObj;
            if (!root.TryGetValue("data", out dataObj) || !(dataObj is List<object> list)) return null;

            var outp = new List<LmStudioModel>();
            bool sawLmField = false;
            foreach (var item in list)
            {
                var o = item as Dictionary<string, object>;
                if (o == null) continue;
                var m = new LmStudioModel(
                    Str(o, "id"),
                    Str(o, "type"),
                    Str(o, "publisher"),
                    Str(o, "arch"),
                    Str(o, "compatibility_type"),
                    Str(o, "quantization"),
                    Str(o, "state"),
                    Int(o, "max_context_length"));
                if (m.State.Length > 0 || m.Quantization.Length > 0 || m.Arch.Length > 0 || m.MaxContextLength > 0)
                    sawLmField = true;
                outp.Add(m);
            }
            return sawLmField ? outp : null;
        }

        // The model LM Studio is actually serving. With JIT loading enabled the
        // list includes not-loaded models, so "first entry" isn't good enough —
        // take the first state=="loaded"; null when nothing is loaded.
        public static LmStudioModel LoadedModel(List<LmStudioModel> models)
        {
            if (models == null) return null;
            foreach (var m in models)
                if (string.Equals(m.State, "loaded", StringComparison.OrdinalIgnoreCase))
                    return m;
            return null;
        }

        // The real context window to advertise, preferring the model's own
        // max_context_length. 0 = unknown (caller falls back).
        public static int EffectiveContext(LmStudioModel m, int fallback)
        {
            if (m != null && m.MaxContextLength > 0) return m.MaxContextLength;
            return fallback > 0 ? fallback : 0;
        }

        // How often the keep-alive heartbeat should ping: LM Studio's idle TTL
        // resets on every inference request, so a ping landing inside the TTL
        // window keeps the model resident forever. Half the configured window,
        // clamped to 5..1800 s — the cap is 30 MINUTES, not Ollama's 60 s,
        // because LM Studio TTLs are long (JIT default 60 m) and a 1-token
        // completion ping is nearly free; pinging every minute would just keep
        // the GPU warm for nothing. 0 means "don't ping".
        public static int PingCadenceSecs(long keepAliveSecs)
        {
            if (keepAliveSecs == -1) return 900;  // session pin — quiet 15 m cadence
            if (keepAliveSecs <= 0) return 0;
            return (int)Math.Max(5, Math.Min(1800, keepAliveSecs / 2));
        }

        // Keep-alive window parsing is Ollama's duration grammar verbatim
        // ('45s' / '30m' / '2h' / bare seconds / '-1' pin / 'off') — reuse it.
        public static long KeepAliveSeconds(string v)
        {
            return OllamaArgs.KeepAliveSeconds(v);
        }

        // ------------------------------------------------------------------
        // replicas — multiple loaded instances of the same model
        // ------------------------------------------------------------------
        // LM Studio names the Nth loaded instance of a model by appending an
        // index to the key (Rose-verified live, 2026-11-17: loading the same
        // model twice yields "<model>" and "<model>:2"). Requests addressed to
        // the suffixed key hit THAT instance, so per-agent routing is just
        // per-agent model names.
        public static string InstanceName(string baseModel, int copyIndex)
        {
            if (string.IsNullOrEmpty(baseModel) || copyIndex < 2) return baseModel ?? "";
            return baseModel + ":" + copyIndex;
        }

        // The shared base form of any instance key ("<m>:2" → "<m>"; keys with a
        // colon but no trailing digits are left alone — publisher/model ids never
        // end in ":N").
        public static string InstanceBase(string instanceKey)
        {
            if (string.IsNullOrEmpty(instanceKey)) return instanceKey ?? "";
            int colon = instanceKey.LastIndexOf(':');
            if (colon < 1 || colon + 1 >= instanceKey.Length) return instanceKey;
            for (int i = colon + 1; i < instanceKey.Length; i++)
                if (instanceKey[i] < '0' || instanceKey[i] > '9') return instanceKey;
            return instanceKey.Substring(0, colon);
        }

        // The full replica set for `replicas` TOTAL instances: [base, base:2..:N].
        public static List<string> ReplicaSet(string baseModel, int replicas)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(baseModel)) return l;
            l.Add(baseModel);
            for (int i = 2; i <= replicas; i++) l.Add(InstanceName(baseModel, i));
            return l;
        }

        // Round-robin an agent onto a replica (mirrors OllamaArgs.ReplicaFor).
        public static string ReplicaFor(List<string> replicas, int agentOrdinal)
        {
            if (replicas == null || replicas.Count == 0) return null;
            if (agentOrdinal < 0) agentOrdinal = 0;
            return replicas[agentOrdinal % replicas.Count];
        }

        // On-demand scaling (v1.6.0): instances to keep LIVE right now — capped by
        // the configured max AND the live agent count, floor 1 (the shared base).
        // Identical math to OllamaArgs.ReplicasForPopulation by design.
        public static int ReplicasForPopulation(int want, int liveAgents)
        {
            return OllamaArgs.ReplicasForPopulation(want, liveAgents);
        }

        // Every LOADED instance of `baseModel` (the base plus any ":N"), ordered
        // base-first then by index, capped at 8. This is the ADOPTION scan:
        // instances the user loaded by hand in the LM Studio UI are picked up
        // exactly like the mod's own (verified against a live dual-instance
        // server: base + base:2 both state=loaded).
        public static List<string> LoadedInstancesOf(List<LmStudioModel> models, string baseModel)
        {
            var outp = new List<string>();
            if (models == null || string.IsNullOrEmpty(baseModel)) return outp;
            var byIndex = new SortedDictionary<int, string>();
            bool baseLoaded = false;
            foreach (var m in models)
            {
                if (!string.Equals(m.State, "loaded", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(m.Id, baseModel, StringComparison.OrdinalIgnoreCase)) { baseLoaded = true; continue; }
                if (!string.Equals(InstanceBase(m.Id), baseModel, StringComparison.OrdinalIgnoreCase)) continue;
                int idx = 2; // a base-family key with no parseable :N≥2 still counts as an instance
                int colon = m.Id.LastIndexOf(':');
                int parsed;
                if (colon >= 0 && int.TryParse(m.Id.Substring(colon + 1), out parsed) && parsed >= 2) idx = parsed;
                byIndex[idx] = m.Id;
            }
            if (baseLoaded) outp.Add(baseModel);
            foreach (var kv in byIndex)
            {
                if (outp.Count >= 8) break;
                outp.Add(kv.Value);
            }
            return outp;
        }

        // Navigate a dotted key through nested dictionaries; return "" when missing.
        private static string Str(Dictionary<string, object> o, string key)
        {
            var segs = key.Split('.');
            var cur = o;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                object v;
                if (cur != null && cur.TryGetValue(segs[i], out v))
                    cur = v as Dictionary<string, object>;
                else
                    return "";
            }
            object final;
            return cur != null && cur.TryGetValue(segs[segs.Length - 1], out final) && final != null
                ? final.ToString() : "";
        }

        // Navigate a dotted key; return the value as an int, or 0.
        private static int Int(Dictionary<string, object> o, string key)
        {
            var segs = key.Split('.');
            var cur = o;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                object v;
                if (cur != null && cur.TryGetValue(segs[i], out v))
                    cur = v as Dictionary<string, object>;
                else
                    return 0;
            }
            object final;
            if (cur != null && cur.TryGetValue(segs[segs.Length - 1], out final) && final != null)
            {
                if (final is long l) return (int)l;
                if (final is int i) return i;
                double d;
                if (double.TryParse(final.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                    return (int)d;
            }
            return 0;
        }
    }

    // One model as reported by LM Studio GET /api/v0/models.
    internal class LmStudioModel
    {
        public string Id;
        public string Type;              // llm | vlm | embeddings
        public string Publisher;
        public string Arch;              // e.g. qwen2_vl, llama
        public string Compatibility;     // gguf | mlx
        public string Quantization;      // e.g. Q4_K_M, 4bit
        public string State;             // "loaded" | "not-loaded"
        public int MaxContextLength;     // real window — drives ContextCore compaction

        public LmStudioModel(string id, string type, string publisher, string arch,
            string compatibility, string quantization, string state, int maxContextLength)
        {
            Id = id;
            Type = type;
            Publisher = publisher;
            Arch = arch;
            Compatibility = compatibility;
            Quantization = quantization;
            State = state;
            MaxContextLength = maxContextLength;
        }
    }
}
