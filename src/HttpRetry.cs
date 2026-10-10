// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// HttpRetryLadder — pure classification + payload-shrink helpers for HTTP 4xx recovery.
// Pure C# (no Unity/BepInEx deps) so it is unit-testable:
//   mcs -target:exe -out:/tmp/t_hr.exe tests/test_httpretry.cs src/HttpRetry.cs src/Json.cs && mono /tmp/t_hr.exe
//
// Shapes it handles (observed on local OpenAI-compatible servers):
//   * {"error":"Engine protocol predict stream returned an error: {\"code\":500,
//      \"message\":\"The model produced output that does not match the expected
//      peg-native format\",\"type\":\"server_error\"}"} — the server's streaming
//     grammar hiccuped on the MODEL'S OWN output; retrying the same payload works.
//   * {"error":"context length exceeded"} / HTTP 413 — payload too big; shrink.
//   * image/multipart rejections on text-only models; response_format/json_schema
//     rejections on servers without structured output; max_tokens complaints.
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class HttpRetryLadder
    {
        /// <summary>
        /// Classify a SafeHttp error string ("HTTP 400 .. body"). Returns:
        /// "transient_engine" (retry unchanged), "image", "response_format",
        /// "max_tokens", "context" (shrink the payload), or null when the
        /// failure is not a payload problem (auth, unknown model, network).
        /// </summary>
        internal static string Classify(string err)
        {
            if (err == null) return null;
            bool payloadStatus = err.Contains("HTTP 400") || err.Contains("HTTP 413") || err.Contains("HTTP 422");
            if (!payloadStatus) return null;
            string e = err.ToLowerInvariant();
            // Engine-side grammar/decode hiccups (the server's own complaint is a
            // nested 500) — the payload was ACCEPTED, the output stream wasn't.
            // Retrying the identical payload is the fix. "predict request failed:
            // fetch failed" (2026-10-09) is the same class with NO nested 500: LM
            // Studio's engine fetch hiccuped — a payload property it is not; don't
            // shrink a request that was never read.
            if (e.Contains("peg-native") || e.Contains("predict stream") || e.Contains("predict request")
                || e.Contains("fetch failed")
                || e.Contains("\"type\":\"server_error\"") || e.Contains("code\":500"))
                return "transient_engine";
            if (e.Contains("image") || e.Contains("vision") || e.Contains("multimodal"))
                return "image";
            if (e.Contains("response_format") || e.Contains("json_schema"))
                return "response_format";
            if (e.Contains("max_tokens") || e.Contains("n_predict"))
                return "max_tokens";
            // Every other 400/413/422 ("context length exceeded", "too many tokens",
            // unrecognized shapes) is best treated as a payload-size problem.
            return "context";
        }

        // ------------------------------------------------------------------
        // 500s stream INSIDE 200-OK responses — the payload was accepted; the
        // engine choked mid-stream ("event: error … does not match the expected
        // peg-gemma4 format"). Detected AFTER parse (empty content + error event)
        // → the caller retries the same payload (engine case).
        // ------------------------------------------------------------------
        internal static string StreamEngineError(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string e = raw.ToLowerInvariant();
            if (e.IndexOf("event: error", StringComparison.Ordinal) < 0
                && e.IndexOf("event :error", StringComparison.Ordinal) < 0) return null;
            if (!(e.Contains("peg-") || e.Contains("server_error") || e.Contains("predict stream") || e.Contains("code\":500")))
                return null;
            // Pull the human "message" out for the log.
            int mi = raw.IndexOf("message\":\"", StringComparison.OrdinalIgnoreCase);
            string msg = mi >= 0 ? raw.Substring(mi + 10) : raw;
            int cut = msg.IndexOf("\",", StringComparison.Ordinal);
            if (cut < 0) cut = 140;
            msg = msg.Substring(0, cut).Replace('\\', ' ').Trim().Trim('"');
            if (msg.Length > 140) msg = msg.Substring(0, 140);
            return msg.Length > 0 ? msg : "engine stream error";
        }

        /// <summary>
        /// Shrink a chat payload in place (context-family fix). Stage 1: drop image
        /// parts and the big optional perception blobs; halve chat_log; cap nearby/
        /// stations. Stage 2 (long console conversations): ALSO drop the oldest half
        /// of the non-system messages and the memory/history/facts bulk.
        /// </summary>
        internal static void ShrinkMessages(Dictionary<string, object> payload, int stage)
        {
            object msgsObj;
            if (payload == null || !payload.TryGetValue("messages", out msgsObj)) return;
            var msgArr = msgsObj as object[];
            if (msgArr == null) return;

            foreach (var m in msgArr)
            {
                var mdict = m as Dictionary<string, object>;
                if (mdict == null) continue;
                object c;
                if (!mdict.TryGetValue("content", out c) || c == null) continue;
                var parts = c as object[];
                if (parts != null)
                {
                    string merged = TextPartsOnly(parts);
                    if (merged != null && merged.StartsWith("perception:", StringComparison.Ordinal))
                        merged = ShrinkPerceptionText(merged, stage);
                    mdict["content"] = merged;
                }
                else if ((c as string) != null && c.ToString().StartsWith("perception:", StringComparison.Ordinal))
                    mdict["content"] = ShrinkPerceptionText(c.ToString(), stage);
            }

            // Long console conversations: after losing images + big text, drop the
            // OLDEST conversation half (system stays index 0; the tail is the live
            // loop). Never touch a 2-message act payload.
            if (stage >= 2 && msgArr.Length > 4)
            {
                int keepTail = (msgArr.Length - 1) / 2;
                var kept = new object[1 + keepTail];
                kept[0] = msgArr[0];
                Array.Copy(msgArr, msgArr.Length - keepTail, kept, 1, keepTail);
                payload["messages"] = kept;
            }
        }

        /// <summary>Multipart content → plain text (images dropped, texts joined).</summary>
        internal static string TextPartsOnly(object[] parts)
        {
            if (parts == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
            {
                var pd = p as Dictionary<string, object>;
                if (pd == null) continue;
                object t;
                if (!pd.TryGetValue("type", out t) || t == null || t.ToString() != "text") continue;
                object txt;
                if (pd.TryGetValue("text", out txt) && txt != null)
                    sb.Append(txt.ToString()).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// Surgical shrink of the "perception:{...}" text blob the legacy act loop
        /// sends. Removes big optional keys and caps the big arrays; preserves
        /// whatever prefix/suffix surrounds the JSON ("perception:" head, the rare
        /// [INSTRUCTION] tail).
        /// </summary>
        internal static string ShrinkPerceptionText(string s, int stage)
        {
            try
            {
                int br = s.IndexOf('{');
                if (br < 0) return s;
                int lastBrace = s.LastIndexOf('}');
                if (lastBrace <= br) return s;
                string head = s.Substring(0, br);
                string tail = s.Substring(lastBrace + 1);
                var d = Json.Parse(s.Substring(br, lastBrace - br + 1)) as Dictionary<string, object>;
                if (d == null) return s;

                d.Remove("radar");
                d.Remove("radar_filter");
                d.Remove("rays");           // the ray fan is the single biggest array
                HalveKey(d, "chat_log");
                CapKey(d, "nearby", 5);
                CapKey(d, "stations", 8);
                CapKey(d, "people", 3);
                if (stage >= 2)
                {
                    d.Remove("history");
                    d.Remove("memory");
                    d.Remove("facts");
                }
                return head + Json.Write(d) + tail;
            }
            catch (Exception)
            {
                return s;
            }
        }

        private static void HalveKey(Dictionary<string, object> d, string key)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v)) return;
                var list = v as List<object>;
                if (list == null || list.Count <= 2) return;
                list.RemoveRange(0, list.Count / 2);
            }
            catch (Exception) { }
        }

        private static void CapKey(Dictionary<string, object> d, string key, int cap)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v)) return;
                var list = v as List<object>;
                if (list == null) return;
                while (list.Count > cap) list.RemoveAt(list.Count - 1);
            }
            catch (Exception) { }
        }
    }
}