// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// OllamaArgs — pure logic for adopting an Ollama host: endpoint parsing, /api/tags and
// /api/show JSON parsing, and context-window fit math. No game/BepInEx/Unity references so the
// headless test suite can compile it directly (the process/watcher side lives in Ollama.cs).
//
// Ollama is a *running* host the mod adopts (it can't spawn one under Proton), so there is no
// command-line to build here — just parsing, endpoint derivation, and the "how big can my
// context window be" math that drives compaction.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KKLLMNPC
{
    internal static class OllamaArgs
    {
        // Strip the OpenAI-style suffix the endpoint config carries (/v1/chat/completions, etc.) so
        // we can probe the API root. Returns "" when nothing recognizable was found, which the caller
        // treats as "endpoint is opaque / not Ollama".
        public static string BaseUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "";
            string ep = endpoint.Trim();
            string[] suffixes =
            {
                "/v1/chat/completions", "/chat/completions",
                "/v1/completions", "/completions", "/v1/systemone",
            };
            foreach (var suf in suffixes)
            {
                if (ep.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                    ep = ep.Substring(0, ep.Length - suf.Length);
            }
            return ep.TrimEnd('/');
        }

        // Detect an Ollama /api/tags payload, returning the installed models (name, architecture
        // family, format, byte size, digest). Returns null when the payload isn't a tags list, so the
        // caller can fall back to /v1/models or mark the host absent.
        public static List<InstalledModel> ParseTags(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;
            object modelsObj;
            if (!root.TryGetValue("models", out modelsObj) || !(modelsObj is List<object> list)) return null;

            var outp = new List<InstalledModel>();
            foreach (var item in list)
            {
                var o = item as Dictionary<string, object>;
                if (o == null) continue;
                outp.Add(new InstalledModel(
                    Str(o, "name"),
                    Str(o, "family"),
                    Str(o, "format"),
                    Long(o, "size"),
                    Str(o, "digest")));
            }
            return outp;
        }

        // Parse an Ollama /api/show payload. Real shape (verified against a running server):
        //   details: { family:"qwen35", format:"gguf", quantization_level:"Q8_0", ... }
        //   model_info: { "general.architecture":"qwen35", "qwen35.context_length":262144, ... }
        // The context-key suffix is <family>.context_length, so read details.family and look that
        // up in model_info. Returns null when the payload isn't a show payload.
        public static ShowInfo ParseShow(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;

            object detailObj;
            var details = root.TryGetValue("details", out detailObj) ? detailObj as Dictionary<string, object> : null;
            object infoObj;
            var modelInfo = root.TryGetValue("model_info", out infoObj) ? infoObj as Dictionary<string, object> : null;

            if (details == null && modelInfo == null) return null;

            string family = Str(details, "family");
            // Fallback to model_info["general.architecture"] if details.family is absent.
            if (string.IsNullOrEmpty(family) && modelInfo != null) family = Str(modelInfo, "general.architecture");

            // Context window: model_info["<family>.context_length"]. This is a literal key with a
            // dot IN it (not nested dicts), so do a direct lookup.
            int contextLength = 0;
            if (modelInfo != null && !string.IsNullOrEmpty(family))
            {
                object key;
                if (modelInfo.TryGetValue(family + ".context_length", out key) && key != null)
                {
                    if (key is long l) contextLength = (int)l;
                    else if (key is int i) contextLength = i;
                    else if (double.TryParse(key.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        contextLength = (int)d;
                }
            }
            // Fallback: model_info["context_length"] (flat) in case a server flattens it.
            if (contextLength == 0 && modelInfo != null)
            {
                object key;
                if (modelInfo.TryGetValue("context_length", out key) && key != null)
                {
                    if (key is long l) contextLength = (int)l;
                    else if (key is int i) contextLength = i;
                    else if (double.TryParse(key.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        contextLength = (int)d;
                }
            }

            return new ShowInfo(
                family ?? Str(modelInfo, "general.architecture"),
                contextLength,
                Str(details, "quantization_level"));
        }

        // The real context window Ollama served, preferring the /api/show value, falling back to a
        // detected value (from /v1/models or a model-name heuristic) — whichever is larger is safer.
        public static int EffectiveContext(ShowInfo show, int fallback)
        {
            if (show != null && show.ContextLength > 0) return show.ContextLength;
            if (fallback > 0) return fallback;
            return 0;
        }

        // Compute a num_ctx that fits a target fill fraction of the model's real window, floored at
        // the model's own window. The mod never raises num_ctx (that's a modelfile setting); this
        // just tells the user the window their context must fit in for compaction.
        public static int FitContext(int realWindow, double targetFill)
        {
            if (realWindow <= 0) return 0;
            int fit = (int)Math.Round(Math.Min(realWindow, realWindow * targetFill));
            return Math.Max(1024, fit);
        }

        // Convert bytes to a human size (GiB-ish, 2^30).
        public static string HumanSize(long bytes)
        {
            if (bytes <= 0) return "";
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
        }

        // Parse a [Ollama].KeepAlive value into seconds. Accepts Ollama's duration
        // forms — "<n>s" / "<n>m" / "<n>h" or a bare number (seconds) — and "-1"
        // (unit optional) to pin the model in memory forever. Returns:
        //   -1  pin forever
        //    0  disabled / unparseable — the caller must NOT ping
        //   >0  keep-alive window in seconds
        // Compound ("1h30m"), fractional and negative-other-than--1 forms are
        // rejected: on a knob this quiet, silence beats a wrong guess.
        public static long KeepAliveSeconds(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 0;
            string s = v.Trim().ToLowerInvariant();
            if (s == "off" || s == "none" || s == "disable" || s == "disabled" || s == "false") return 0;
            long mul = 1;
            char unit = s[s.Length - 1];
            if (unit == 's' || unit == 'm' || unit == 'h')
            {
                s = s.Substring(0, s.Length - 1);
                if (unit == 'm') mul = 60;
                else if (unit == 'h') mul = 3600;
                if (s.Length == 0) return 0;
            }
            long n;
            try { n = long.Parse(s, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
            if (n == -1) return -1;
            if (n <= 0) return 0;
            long r;
            try { checked { r = n * mul; } } catch (Exception) { return 0; }
            return r <= 0 ? 0 : r;
        }

        // How often to re-ping for a keep-alive window: half the window, so a ping
        // can never race the eviction, clamped to 5..60 s. -1 (pin) and any window
        // >= 2 min both land on the quiet 60 s cadence; 0 means "don't ping".
        public static int KeepAlivePingSecs(long keepAliveSecs)
        {
            if (keepAliveSecs == -1) return 60;
            if (keepAliveSecs <= 0) return 0;
            return (int)Math.Max(5, Math.Min(60, keepAliveSecs / 2));
        }

        // ------------------------------------------------------------------
        // replicas — N loaded instances of the same model, one per agent
        // ------------------------------------------------------------------
        // Ollama loads by TAG: /api/copy of the base model to a second tag shares
        // the blobs on disk (zero copy cost) but loads as a SEPARATE runner
        // instance with its OWN full context window — unlike OLLAMA_NUM_PARALLEL
        // slots, which split one window. Shard tags carry a kk-suffix that keeps
        // them valid regardless of whether the base is "name" (implicit :latest)
        // or "name:tag" (suffix joins the tag part; '.', ':' are tag-legal chars).
        public static string ShardName(string baseModel, int copyIndex)
        {
            if (string.IsNullOrEmpty(baseModel) || copyIndex < 2) return baseModel ?? "";
            return baseModel.IndexOf(':') >= 0
                ? baseModel + ".kk" + copyIndex
                : baseModel + ":kk" + copyIndex;
        }

        // The full replica set for `replicas` TOTAL instances: [base, copy2..copyN].
        public static List<string> ReplicaSet(string baseModel, int replicas)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(baseModel)) return l;
            l.Add(baseModel);
            for (int i = 2; i <= replicas; i++) l.Add(ShardName(baseModel, i));
            return l;
        }

        // Round-robin an agent onto a replica. Null/empty set → null (caller uses
        // the shared base model); negative ordinals count as the first agent.
        public static string ReplicaFor(List<string> replicas, int agentOrdinal)
        {
            if (replicas == null || replicas.Count == 0) return null;
            if (agentOrdinal < 0) agentOrdinal = 0;
            return replicas[agentOrdinal % replicas.Count];
        }

        // On-demand scaling (v1.6.0): how many instances should be LIVE right now —
        // never more than the configured cap, never more than there are agents, and
        // always at least the shared base. want≤1 → 1. Negative/zero agents → 1
        // (the base is the fallback every agent shares until its instance exists).
        public static int ReplicasForPopulation(int want, int liveAgents)
        {
            if (want < 1) want = 1;
            if (want > 8) want = 8;
            if (liveAgents < 1) liveAgents = 1;
            return Math.Min(want, liveAgents);
        }

        // Navigate a dotted key (e.g. "general.architecture") through nested dictionaries and
        // return the final value as a string. Returns "" if any segment is missing or not a dict.
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

        // Navigate a dotted key through nested dictionaries; return the value as a long, or 0.
        private static long Long(Dictionary<string, object> o, string key)
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
                if (final is long l) return l;
                if (final is int i) return i;
                if (double.TryParse(final.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return (long)d;
            }
            return 0;
        }

    }

    // An installed Ollama model as reported by /api/tags.
    internal class InstalledModel
    {
        public string Name;
        public string Family;
        public string Format;
        public long Size;
        public string Digest;

        public InstalledModel(string name, string family, string format, long size, string digest)
        {
            Name = name;
            Family = family;
            Format = format;
            Size = size;
            Digest = digest;
        }
    }

    // What /api/show reported for the currently loaded model.
    internal class ShowInfo
    {
        public string Architecture;
        public int ContextLength;
        public string Quantization;

        public ShowInfo(string architecture, int contextLength, string quantization)
        {
            Architecture = architecture;
            ContextLength = contextLength;
            Quantization = quantization;
        }
    }
}
