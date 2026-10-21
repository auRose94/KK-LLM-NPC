// kkllmnpc_turns.jsonl + kkllmnpc_turns_media/ — the training-data recorder.
//
// When [DataRecord] Enabled, one JSON Lines record per LLM round-trip:
//
//   {"v":1,"type":"sys","session":..,"hash":"<hex>","text":"<prompt text>"}            (vocab: once per session per distinct text)
//   {"v":1,"type":"tools","session":..,"hash":"<hex>","mode":"console","text":[...]}   (vocab: the console tools array)
//   {"v":1,"type":"turn","session":..,"ts":<epoch seconds>, "mode":"console|act|name|vision",
//    "npc":"..", "model":"..", "ms":<wall time>, "ok":1|0, "salvage":<n>, "attempt":<n>?,
//    "msgs":[{"role":"system","sys_ref":"<8hex>"}, {"role":"user","content":...}, ...],
//    "tools_ref":"<8hex>",   (only when a tools array was sent)
//    "frame_ref":"<hash>.<ext>",  (only when the RUNTIME model was text-only: the
//     recorder-captured first-person frame at decision time — never part of msgs)
//    "resp":{"content":"..", "tool":"..", "tool_args":"..", "calls":[{"name":..,"args":..}]}
//   }
//
// "msgs" is the exact conversation the model saw, with three rewrites that keep
// the file lean and private/rebuildable:
//   * system prompts become {"sys_ref"} (full text lives once in the matching
//     "sys" vocab record);
//   * image_url parts carrying a data-URI frame are materialized ONCE (content
//     hash, so repeated frames across the rolling history dedupe to one file)
//     into kkllmnpc_turns_media/ and become {"type":"image_url","img_ref":
//     "<hash>.<ext>"} — the training pipeline maps img_ref to a real file path;
//   * any other binary/url shape degrades to {"type":"image_url","omit":"binary"}.
//
// "salvage" counts recovery salvages (tool-markup/name-CoT recovery, junk
// carving) fired by this thread since the PREVIOUS record: recovery runs after
// the raw response arrives, so the count annotates how rough the previous
// response was, not this one.
//
// Pure System — no Unity/BepInEx calls here (Main.cs owns config wiring and the
// file paths), so the gameless test suite exercises every path directly.
namespace KKLLMNPC
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;

    internal static class DataRecord
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 10);
        private static readonly object Lock = new object();
        private static readonly List<string> Pending = new List<string>(64);
        private static readonly List<MediaItem> PendingMedia = new List<MediaItem>(16);
        private static readonly HashSet<string> VocabSeen = new HashSet<string>();
        private static readonly HashSet<string> MediaSeen = new HashSet<string>();

        private class MediaItem
        {
            public string Name;
            public byte[] Bytes;
        }

        internal static string FilePath;
        internal static string MediaDir;
        private static long _maxBytes = long.MaxValue;
        private static long _mediaBytes;
        private static bool _overCap;
        private static long _offset = -1;         // file size as of the last append we made
        private static bool _offsetDirty = true;  // recompute from disk after Configure
        private static AutoResetEvent _kick;
        private static bool _writerStarted;

        // Recovery paths bump this between records; the next Turn() on the same
        // thread carries the count and resets it.
        [ThreadStatic]
        private static int _salvagePending;

        // Called on notices; Main wires this to a LogWarning wrapper.
        internal static Action<string> Notice { set { _notice = value; } }
        private static Action<string> _notice;

        internal static bool Enabled { get; private set; }

        // True after the size cap fired — enqueueing is refused until reconfigure
        // (covers JSONL and media; the cap counts both).
        internal static bool OverCap { get { return _overCap; } }

        private static bool Active { get { return Enabled && !_overCap; } }

        // maxBytes <= 0 means uncapped (tests use tiny values; Main passes MB as bytes).
        // Reconfigure also un-sticks an over-cap stop so raising the cap (or deleting
        // the files) resumes recording.
        internal static void Configure(bool enabled, long maxBytes, string filePath)
        {
            FilePath = string.IsNullOrEmpty(filePath) ? null : filePath;
            MediaDir = null;
            if (FilePath != null)
                MediaDir = Path.Combine(Path.GetDirectoryName(FilePath), "kkllmnpc_turns_media");
            _maxBytes = maxBytes > 0 ? maxBytes : long.MaxValue;
            Enabled = enabled && FilePath != null;
            _overCap = false;
            if (Enabled && maxBytes > 0)
            {
                try { _overCap = new FileInfo(FilePath).Length > _maxBytes; } catch (Exception) { }
            }
            if (_overCap && Enabled)
                Notify("data record: size cap already exceeded — not recording (delete or move the files to start fresh)");
        }

        internal static void MarkSalvage()
        {
            _salvagePending++;
        }

        // Caller builds the record body (mode/npc/model/msgs/resp/...); this adds
        // the envelope. Never throws — recording must not disturb gameplay.
        internal static void Turn(Dictionary<string, object> rec)
        {
            if (!Enabled || rec == null) return;
            rec["type"] = "turn";
            var salvage = _salvagePending; _salvagePending = 0;
            if (salvage > 0) rec["salvage"] = salvage;
            rec["session"] = Session;
            rec["ts"] = Math.Round((DateTime.UtcNow - Epoch).TotalSeconds, 3);
            Enqueue(rec);
        }

        // One vocabulary record (system prompt text / tools array), only the first
        // time a given hash appears this session.
        internal static void VocabOnce(string kind, string hash, object text)
        {
            if (!Enabled) return;
            lock (Lock)
            {
                if (VocabSeen.Contains(kind + ":" + hash)) return;
                VocabSeen.Add(kind + ":" + hash);
            }
            var rec = new Dictionary<string, object>
            {
                ["type"] = kind,
                ["hash"] = hash,
                ["session"] = Session,
                ["text"] = text,
            };
            Enqueue(rec);
        }

        // ---- messages rewrite: system dedupe + frame materialization ----

        // Hash an object by its compact JSON form (stable within a session).
        internal static string HashOf(object o)
        {
            try { return HashOfString(Json.Write(o)); }
            catch (Exception) { return null; }
        }

        internal static string HashOfString(string s)
        {
            if (s == null) return null;
            return HashOfBytes(Encoding.UTF8.GetBytes(s));
        }

        // FNV-1a 64 over the bytes with a length suffix (defeats truncation
        // confusion): same value = same content, session-wide and across sessions.
        private static string HashOfBytes(byte[] b)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte x in b) { h ^= x; h *= 1099511628211UL; }
            h ^= (ulong)b.Length; h *= 1099511628211UL;
            return h.ToString("x16");
        }

        // Returns a copy of the conversation safe to log:
        //  - {"role":"system","content":<string>}  → {"role":"system","sys_ref":<hash>} + a vocab record
        //  - image_url part with a data-URI frame  → {"type":"image_url","img_ref":"<hash>.<ext>"} + one media file
        //  - any other image_url part              → {"type":"image_url","omit":"binary"}
        // Everything else passes through unchanged.
        internal static object[] RecordableMessages(object msgs)
        {
            var src = msgs as object[];
            if (src == null)
            {
                var list = msgs as List<object>;
                if (list != null) src = list.ToArray();
            }
            if (src == null) return null;
            var outp = new List<object>(src.Length);
            foreach (var o in src)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) { outp.Add(o); continue; }

                object roleBoxed;
                var role = d.TryGetValue("role", out roleBoxed) ? roleBoxed as string : null;
                object content;
                d.TryGetValue("content", out content);

                // System prompt: replace with a reference, keep the text once.
                if (role == "system")
                {
                    var sysText = content as string;
                    if (sysText != null)
                    {
                        string h = HashOfString(sysText);
                        VocabOnce("sys", h, sysText);
                        outp.Add(new Dictionary<string, object> { ["role"] = "system", ["sys_ref"] = h });
                        continue;
                    }
                }

                // Multimodal content: materialize/strip image binaries in a clone.
                var parts = content as List<object>;
                if (parts == null)
                {
                    var arr = content as object[];
                    if (arr != null) parts = new List<object>(arr);
                }
                if (parts != null && PartsHaveImage(parts))
                {
                    var clone = new Dictionary<string, object>(d);
                    var clean = new List<object>(parts.Count);
                    bool changed = false;
                    foreach (var p in parts)
                    {
                        var pd = p as Dictionary<string, object>;
                        object pt;
                        if (pd != null && pd.TryGetValue("type", out pt) && (pt as string) == "image_url")
                        {
                            // A data-URI frame MUST be replaced one way or the other —
                            // a failed materialize degrades to omit, never inline base64.
                            if (Active && FrameUrlIsData(pd))
                            {
                                clean.Add(MaterializeFrame(pd) ?? OmitPart());
                                changed = true;
                                continue;
                            }
                        }
                        clean.Add(p); // remote urls are small and meaningful — keep as-is
                    }
                    if (changed) clone["content"] = clean.ToArray();
                    outp.Add(clone);
                    continue;
                }

                outp.Add(d);
            }
            return outp.ToArray();
        }

        // Materialize ONE data-URI part under MediaDir, queued to the writer thread;
        // returns "<hash>.<ext>" or null (not decodable — the caller then omits).
        internal static Dictionary<string, object> MaterializeFrame(Dictionary<string, object> part)
        {
            string url = ExtractFrameUrl(part);
            string name = url != null ? MaterializeDataUri(url) : null;
            return name != null
                ? new Dictionary<string, object> { ["type"] = "image_url", ["img_ref"] = name }
                : null;
        }

        // Public path for record-only frames (a text-only runtime model receives no
        // image parts; the console cadence still captures the first-person view at
        // decision time and pairs it with the turn as frame_ref). Same contract:
        // content-hash → one file, deduped across turns and sessions.
        internal static string MaterializeDataUri(string url)
        {
            if (url == null || !Active) return null;
            try
            {
                const string scheme = "data:";
                if (!url.StartsWith(scheme, StringComparison.Ordinal)) return null;
                int marker = url.IndexOf(";base64,", StringComparison.Ordinal);
                if (marker < 0) return null;
                string mime = url.Substring(scheme.Length, marker - scheme.Length); // "image/jpeg"
                string b64 = url.Substring(marker + ";base64,".Length);
                byte[] bytes = Convert.FromBase64String(b64);
                if (bytes.Length < 64 || bytes.Length > 8 * 1024 * 1024) return null;
                string name = HashOfBytes(bytes) + MimeExt(mime);
                lock (Lock)
                {
                    if (!MediaSeen.Contains(name))
                    {
                        MediaSeen.Add(name);
                        PendingMedia.Add(new MediaItem { Name = name, Bytes = bytes });
                    }
                }
                return name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Does ANY message in this conversation carry an image_url part? (Used by
        // the record-only frame logic: frame_ref only applies to text-only turns.)
        internal static bool ConversationHasImage(object msgs)
        {
            object[] src = null;
            var lst = msgs as List<object>;
            if (lst != null) src = lst.ToArray();
            else src = msgs as object[];
            if (src == null) return false;
            foreach (var o in src)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                object content;
                if (!d.TryGetValue("content", out content)) continue;
                var parts = content as List<object>;
                if (parts == null)
                {
                    var arr = content as object[];
                    if (arr != null) parts = new List<object>(arr);
                }
                if (parts != null && PartsHaveImage(parts)) return true;
            }
            return false;
        }

        private static string ExtractFrameUrl(Dictionary<string, object> part)
        {
            object iu;
            if (!part.TryGetValue("image_url", out iu)) return null;
            var iud = iu as Dictionary<string, object>;
            object urlBoxed;
            string url = iud != null && iud.TryGetValue("url", out urlBoxed) ? urlBoxed as string : null;
            if (url == null) url = iu as string; // some servers inline the url string
            return url;
        }

        private static bool FrameUrlIsData(Dictionary<string, object> part)
        {
            string url = ExtractFrameUrl(part);
            return url != null && url.StartsWith("data:", StringComparison.Ordinal);
        }

        private static Dictionary<string, object> OmitPart()
        {
            return new Dictionary<string, object> { ["type"] = "image_url", ["omit"] = "binary" };
        }

        private static string MimeExt(string mime)
        {
            if (mime != null)
            {
                if (mime.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0) return ".png";
                if (mime.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0) return ".webp";
            }
            return ".jpg";
        }

        private static bool PartsHaveImage(List<object> parts)
        {
            foreach (var p in parts)
            {
                var pd = p as Dictionary<string, object>;
                object pt;
                if (pd != null && pd.TryGetValue("type", out pt) && (pt as string) == "image_url") return true;
            }
            return false;
        }

        // ---- file writer (JSONL lines + frame files) ----

        private static void Enqueue(Dictionary<string, object> rec)
        {
            if (!Active) return;
            string line;
            try { line = Json.Write(rec) + "\n"; } catch (Exception) { return; }
            lock (Lock)
            {
                Pending.Add(line);
            }
            StartWriter();
            if (_kick != null) try { _kick.Set(); } catch (Exception) { }
        }

        private static void StartWriter()
        {
            lock (Lock)
            {
                if (_writerStarted) return;
                _writerStarted = true;
                _kick = new AutoResetEvent(false);
            }
            var t = new Thread(WriterLoop);
            t.IsBackground = true;
            t.Name = "kkllmnpc-datarecord";
            t.Start();
        }

        private static void WriterLoop()
        {
            while (true)
            {
                try { _kick.WaitOne(); } catch (Exception) { }
                List<string> batch = null;
                List<MediaItem> media = null;
                lock (Lock)
                {
                    if (Pending.Count > 0)
                    {
                        batch = new List<string>(Pending);
                        Pending.Clear();
                    }
                    if (PendingMedia.Count > 0)
                    {
                        media = new List<MediaItem>(PendingMedia);
                        PendingMedia.Clear();
                    }
                    if (batch != null || media != null) _writingBatch = true; // Flush() must not report empty mid-write
                }
                if (batch == null && media == null) continue;
                try
                {
                    if (batch != null)
                    {
                        string path = FilePath;
                        if (!string.IsNullOrEmpty(path))
                        {
                            try
                            {
                                var sb = new StringBuilder();
                                foreach (var line in batch) sb.Append(line);
                                if (_offsetDirty)
                                {
                                    _offsetDirty = false;
                                    try { _offset = File.Exists(path) ? new FileInfo(path).Length : 0; } catch (Exception) { _offset = 0; }
                                }
                                File.AppendAllText(path, sb.ToString());
                                _offset += Encoding.UTF8.GetByteCount(sb.ToString());
                            }
                            catch (Exception) { /* drive blip: drop the batch, keep going */ }
                        }
                    }
                    if (media != null)
                    {
                        try
                        {
                            if (!Directory.Exists(MediaDir)) Directory.CreateDirectory(MediaDir);
                            foreach (var m in media)
                            {
                                string p = Path.Combine(MediaDir, m.Name);
                                if (File.Exists(p)) continue;
                                File.WriteAllBytes(p, m.Bytes);
                                _mediaBytes += m.Bytes.Length;
                            }
                        }
                        catch (Exception) { }
                    }
                    if (_maxBytes != long.MaxValue && (_offset + _mediaBytes) > _maxBytes)
                    {
                        _overCap = true;
                        Notify("data record: " + _maxBytes / (1024 * 1024) + " MB cap reached (turns + frames) — recording stopped (delete or move " + Path.GetFileName(FilePath) + " and kkllmnpc_turns_media/ to start fresh)");
                    }
                }
                finally
                {
                    lock (Lock) { _writingBatch = false; }
                }
            }
        }
        private static bool _writingBatch;

        // Test aid: wait until the JSONL queue AND the frame queue drain to disk.
        internal static bool Flush(int timeoutMs)
        {
            if (!Enabled) return true;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                bool settled;
                lock (Lock) settled = Pending.Count == 0 && PendingMedia.Count == 0 && !_writingBatch;
                if (settled) return true;
                if (_kick != null) try { _kick.Set(); } catch (Exception) { }
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(20);
            }
        }

        private static void Notify(string msg)
        {
            var n = _notice;
            if (n != null) try { n(msg); } catch (Exception) { }
        }
    }
}