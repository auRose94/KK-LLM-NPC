// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// WebUI — the mod's debug console, delivered over the loopback instead of squeezed
// into an in-game IMGUI overlay (the in-game debug GUI was removed; the browser is
// the debug surface). The user can also drive it mid-play: settings, ops, stats.
//
//   GET  /              single-file HTML (no assets, no build tools; vanilla-JS polling)
//   GET  /api/state     JSON: plugin status + instances (thread-safe lite) + spectate + log ring
//   GET  /api/instances JSON: EVERY per-kobold stat (genes/energy/position/pen state/
//                       belly guests/skills/goal/endpoint/…), rebuilt main-thread ~1.5s
//   GET  /api/reports   the AI bug-report log (text/plain tail, newest last)
//   GET  /api/config    every config entry (id, section, key, value, type, description)
//   GET  /api/memories  saved memory files (per-body life records on disk)
//   POST /api/config    {"id":"LittleTreats.SwallowDestination","value":"Anal"} — set + save
//   POST /api/ops       {"action":"delete_memories_all"} | {"action":"delete_memory","file":"X.json"}
//                       | {"action":"delete_maps"} | {"action":"reset_defaults"}
//   POST /api/instance  {"name":"Kira","action":"say","text":"hi"} | {"action":"stop"|"start"}
//
// Design constraints honored:
// * LOOPBACK ONLY (127.0.0.1) — never reachable from the network. Destructive ops are
//   confirmation-gated in the UI.
// * THREAD-SAFE by construction: GET /api/state reads ONLY lock-guarded strings and
//   volatiles (it still answers while the main thread is wedged — exactly when you
//   open it). /api/instances is a main-thread-built cache so gene/transform reads are
//   legal. POST routes marshal game-object work through RunOnMainThread.
// * All work in background threads; every request is try/caught; nothing here can
//   take the game down.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace KKLLMNPC
{
    // BepInEx log ring: everything the mod logs, kept for the browser tail.
    internal sealed class WebLogRing : ILogListener
    {
        private readonly object _lock = new object();
        private readonly LinkedList<string> _lines = new LinkedList<string>();
        internal const int Cap = 1200;

        public void LogEvent(object sender, LogEventArgs e)
        {
            try
            {
                if (e == null || e.Source == null) return;
                // Only the mod's own source keeps this ring tight.
                try { if (e.Source.SourceName != "KKLLMNPC") return; } catch (Exception) { }
                string lvl = "?";
                try
                {
                    if (e.Level == LogLevel.Warning) lvl = "warn";
                    else if (e.Level == LogLevel.Error) lvl = "error";
                    else if (e.Level == LogLevel.Info) lvl = "info";
                    else if (e.Level == LogLevel.Debug) return; // too chatty for the browser
                    else if (e.Level == LogLevel.Message) lvl = "msg";
                }
                catch (Exception) { }
                string line = "[" + lvl + "] " + (e.Data != null ? e.Data.ToString() : "");
                lock (_lock)
                {
                    _lines.AddLast(line);
                    while (_lines.Count > Cap) _lines.RemoveFirst();
                }
            }
            catch (Exception) { }
        }

        internal string[] Tail(int max)
        {
            lock (_lock)
            {
                var outp = new List<string>(_lines.Count);
                foreach (var l in _lines) outp.Add(l);
                if (max > 0 && outp.Count > max) outp.RemoveRange(0, outp.Count - max);
                return outp.ToArray();
            }
        }

        public void Dispose() { }
    }

    internal static class WebUI
    {
        internal static LLMNPCPlugin Plugin;
        private static readonly WebLogRing _ring = new WebLogRing();
        private static bool _ringHooked;
        private static HttpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static int _port;
        internal static int Port { get { return _port; } }

        // ---- main-thread caches (built in BuildRichSnapshot) ----
        private static readonly object _richLock = new object();
        private static string _richInstancesJson = "[]";
        private static float _richBuiltAt = -999f;          // Time.unscaledTime of the last build
        private static string _mapsDir;                     // BepInEx/config/kkllmnpc_maps
        private static string _memDir;                      // BepInEx/config/kkllmnpc_memory

        internal static void Ensure(LLMNPCPlugin plugin)
        {
            Plugin = plugin;
            if (!_ringHooked)
            {
                try { BepInEx.Logging.Logger.Listeners.Add(_ring); _ringHooked = true; }
                catch (Exception e) { try { plugin.Logger.LogWarning("webui log hook: " + e.Message); } catch (Exception) { } }
            }
            var cfgOn = plugin._cfgWebEnabled;
            var cfgPort = plugin._cfgWebPort;
            if (cfgOn == null || !cfgOn.Value)
            {
                if (_running) StopServer(); // Enabled flipped off at runtime
                return;
            }
            int port = _port != 0 ? _port : 8338;
            try { if (cfgPort != null) port = cfgPort.Value; } catch (Exception) { }
            if (port == _port && _running) return;
            StartServer(port);
        }

        private static void StartServer(int port)
        {
            try
            {
                _running = false;
                StopServer();
                _port = port;
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    _listener.Prefixes.Add("http://localhost:" + port + "/");
                }
                catch (Exception) { }
                _listener.Start();
                _running = true;
                _thread = new Thread(ServerLoop) { IsBackground = true, Name = "KKLLMNPC-WebUI" };
                _thread.Start();
                try { LLMNPCPlugin.Log.LogInfo("webui: debug console at http://127.0.0.1:" + port + "/ (loopback only — status, stats, settings, ops)"); } catch (Exception) { }
            }
            catch (Exception e)
            {
                try { LLMNPCPlugin.Log.LogWarning("webui start (" + port + "): " + e.Message); } catch (Exception) { }
            }
        }

        internal static void StopServer()
        {
            try { _running = false; } catch (Exception) { }
            try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch (Exception) { }
            _listener = null;
        }

        internal static void Shutdown()
        {
            StopServer();
            try { if (_ringHooked) BepInEx.Logging.Logger.Listeners.Remove(_ring); } catch (Exception) { }
            _ringHooked = false;
        }

        private static void ServerLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch (Exception)
                {
                    if (_running) { try { Thread.Sleep(300); } catch (Exception) { } }
                    continue;
                }
                try { Handle(ctx); } catch (Exception) { }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                res.Headers["Access-Control-Allow-Origin"] = "http://127.0.0.1"; // same-origin anyway
                string path = req.Url != null ? req.Url.AbsolutePath ?? "/" : "/";
                byte[] body;
                string mime;
                int status = 200;
                bool isPost = !string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
                if (isPost)
                {
                    // POST writes: settings / ops / per-instance commands. Each is
                    // individually try/caught and never game-breaking; ops that touch
                    // game objects marshal to the main thread.
                    string payload = ReadBody(req);
                    switch (path)
                    {
                        case "/api/config":
                            body = Encoding.UTF8.GetBytes(DoConfigSet(payload));
                            mime = "application/json; charset=utf-8";
                            break;
                        case "/api/ops":
                            body = Encoding.UTF8.GetBytes(DoOp(payload));
                            mime = "application/json; charset=utf-8";
                            break;
                        case "/api/instance":
                            body = Encoding.UTF8.GetBytes(DoInstance(payload));
                            mime = "application/json; charset=utf-8";
                            break;
                        case "/api/ollama":
                            body = Encoding.UTF8.GetBytes(Ollama.HandlePost(payload));
                            mime = "application/json; charset=utf-8";
                            break;
                        default:
                            body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"unknown POST path\"}");
                            mime = "application/json; charset=utf-8";
                            status = 404;
                            break;
                    }
                }
                else if (path == "/" || path == "/index.html")
                {
                    body = Encoding.UTF8.GetBytes(PageHtml);
                    mime = "text/html; charset=utf-8";
                }
                else if (path == "/api/state")
                {
                    body = Encoding.UTF8.GetBytes(JsonSnapshot());
                    mime = "application/json; charset=utf-8";
                }
                else if (path == "/api/instances")
                {
                    body = Encoding.UTF8.GetBytes(RichInstancesJson());
                    mime = "application/json; charset=utf-8";
                }
                else if (path == "/api/config")
                {
                    body = Encoding.UTF8.GetBytes(ConfigListJson());
                    mime = "application/json; charset=utf-8";
                }
                else if (path == "/api/memories")
                {
                    body = Encoding.UTF8.GetBytes(MemoriesListJson());
                    mime = "application/json; charset=utf-8";
                }
                else if (path == "/api/ollama")
                {
                    body = Encoding.UTF8.GetBytes(Ollama.SnapshotJson());
                    mime = "application/json; charset=utf-8";
                }
                else if (path == "/api/reports")
                {
                    body = Encoding.UTF8.GetBytes(ReportsText());
                    mime = "text/plain; charset=utf-8";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("not found — / | /api/state | /api/instances | /api/config | /api/memories | /api/reports | /api/ollama");
                    mime = "text/plain; charset=utf-8";
                    status = 404;
                }
                res.StatusCode = status;
                res.ContentType = mime;
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = body.Length;
                using (var s = res.OutputStream) s.Write(body, 0, body.Length);
            }
            catch (Exception)
            {
                try { res.Abort(); } catch (Exception) { }
            }
        }

        private static string ReadBody(HttpListenerRequest req)
        {
            try
            {
                if (!req.HasEntityBody) return "";
                int len = (int)Math.Min(req.ContentLength64 < 0 ? 0 : req.ContentLength64, 64 * 1024);
                using (var s = req.InputStream)
                {
                    var buf = new byte[len];
                    int n = 0, read;
                    while (n < len && (read = s.Read(buf, n, len - n)) > 0) n += read;
                    return Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------------
        // POST handlers
        // ------------------------------------------------------------------
        private static string Ok(string note) { return Json.Write(new { ok = true, note = note ?? "" }); }
        private static string Fail(string err) { return Json.Write(new { ok = false, error = err ?? "failed" }); }

        private static string DoConfigSet(string payload)
        {
            try
            {
                var d = Json.Parse(payload) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("id")) return Fail("missing id");
                string id = Convert.ToString(d["id"]);
                string value = d.ContainsKey("value") ? Convert.ToString(d["value"]) : "";
                var entry = FindConfigEntry(id);
                if (entry == null) return Fail("unknown config id '" + id + "'");
                entry.SetSerializedValue(value);
                try { Plugin.Config.Save(); } catch (Exception) { }
                try { LLMNPCPlugin.Log.LogInfo("webui: config " + id + " -> '" + value + "'"); } catch (Exception) { }
                return Ok(id + " = " + value);
            }
            catch (Exception e) { return Fail(e.Message); }
        }

        // Find a ConfigEntryBase by "Section.Key" over the plugin's _cfg* fields
        // (the same reflection enumeration the deleted IMGUI overlay used).
        private static ConfigEntryBase FindConfigEntry(string id)
        {
            try
            {
                if (Plugin == null || string.IsNullOrEmpty(id)) return null;
                var fields = typeof(LLMNPCPlugin).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                foreach (var f in fields)
                {
                    if (!f.Name.StartsWith("_cfg", StringComparison.Ordinal)) continue;
                    if (!(f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name.StartsWith("ConfigEntry")))
                        continue;
                    object v = null;
                    try { v = f.GetValue(Plugin); } catch (Exception) { continue; }
                    var ce = v as ConfigEntryBase;
                    if (ce == null || ce.Definition == null) continue;
                    if (string.Equals(ce.Definition.Section + "." + ce.Definition.Key, id, StringComparison.OrdinalIgnoreCase))
                        return ce;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string ConfigListJson()
        {
            // Reflection walks the partial class parts in declaration order, so
            // sections arrive INTERLEAVED (LLM, Vision, General, Vision, …). The
            // settings page used to open a new accordion drawer per section CHANGE,
            // which produced several "LLM"/"Vision"/"General" drawers each. Sort at
            // the source: canonical section order → key, and dedupe by id.
            var rows = new List<Dictionary<string, object>>();
            try
            {
                var fields = typeof(LLMNPCPlugin).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                foreach (var f in fields)
                {
                    if (!f.Name.StartsWith("_cfg", StringComparison.Ordinal)) continue;
                    if (!(f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name.StartsWith("ConfigEntry")))
                        continue;
                    object v = null;
                    try { v = f.GetValue(Plugin); } catch (Exception) { continue; }
                    var ce = v as ConfigEntryBase;
                    if (ce == null || ce.Definition == null) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        ["id"] = ce.Definition.Section + "." + ce.Definition.Key,
                        ["section"] = ce.Definition.Section,
                        ["key"] = ce.Definition.Key,
                        ["value"] = ce.GetSerializedValue() ?? "",
                        ["type"] = ce.SettingType != null ? ce.SettingType.Name : "?",
                        ["desc"] = ce.Description != null ? (ce.Description.Description ?? "") : "",
                    });
                }
            }
            catch (Exception) { }

            // Canonical display order (whatever exists today; unknown sections fall
            // back to alphabetical after these). Groups related settings the way the
            // docs do: how it thinks → how it sees → how it moves → world/life.
            var order = new List<string> {
                "LLM", "VisionModel", "Vision", "Senses", "Movement",
                "General", "Autonomy", "Needs", "Memory", "Multiplayer",
                "Farming", "Console", "Web", "DataRecord", "LittleTreats", "Debug", "Ollama"
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            rows.Sort((x, y) =>
            {
                string sx = (string)x["section"], sy = (string)y["section"];
                int ix = order.IndexOf(sx); if (ix < 0) ix = order.Count;
                int iy = order.IndexOf(sy); if (iy < 0) iy = order.Count;
                int c = ix.CompareTo(iy);
                if (c != 0) return c;
                int cx = string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
                if (cx != 0) return cx;
                return string.Compare((string)x["key"], (string)y["key"], StringComparison.OrdinalIgnoreCase);
            });
            var items = new List<object>();
            foreach (var r in rows)
            {
                if (!seen.Add((string)r["id"])) continue;
                items.Add(r);
            }
            return Json.Write(items);
        }

        private static string MemoriesListJson()
        {
            var files = MemoryStore.ListFiles();
            return Json.Write(files);
        }

        // Filename guard for the delete-one op: only safe names, no traversal.
        private static bool SafeFileName(string f)
        {
            if (string.IsNullOrEmpty(f)) return false;
            if (f.IndexOf('/', StringComparison.Ordinal) >= 0 || f.IndexOf('\\', StringComparison.Ordinal) >= 0) return false;
            if (f.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (!f.EndsWith(".json", StringComparison.Ordinal)) return false;
            foreach (char c in f)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
            return true;
        }

        private static string DoOp(string payload)
        {
            try
            {
                var d = Json.Parse(payload) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("action")) return Fail("missing action");
                string action = Convert.ToString(d["action"]);
                switch (action)
                {
                    case "delete_memories_all":
                    {
                        int n = MemoryStore.DeleteAll();
                        try { LLMNPCPlugin.Log.LogWarning("webui op: deleted ALL " + n + " memory file(s)"); } catch (Exception) { }
                        return Ok("deleted " + n + " memory file(s)");
                    }
                    case "delete_memory":
                    {
                        string file = d.ContainsKey("file") ? Convert.ToString(d["file"]) : "";
                        if (!SafeFileName(file)) return Fail("not a deletable memory file name");
                        string dir = MemDir();
                        if (dir == null) return Fail("memory dir unavailable");
                        string path = Path.Combine(dir, file);
                        if (File.Exists(path)) { try { File.Delete(path); } catch (Exception e) { return Fail(e.Message); } }
                        try { LLMNPCPlugin.Log.LogWarning("webui op: deleted memory file '" + file + "'"); } catch (Exception) { }
                        return Ok("deleted " + file);
                    }
                    case "delete_maps":
                    {
                        int n = 0;
                        string dir = MapsDir();
                        if (dir == null || !Directory.Exists(dir)) return Ok("no map data directory — nothing to delete");
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            try { File.Delete(f); n++; } catch (Exception) { }
                        }
                        try { PlayerTrail.ClearAll(); } catch (Exception) { }
                        try { LLMNPCPlugin.Log.LogWarning("webui op: deleted " + n + " map/trail file(s) — maps rebuild on next scene load"); } catch (Exception) { }
                        return Ok("deleted " + n + " map/trail file(s); the world map rebuilds from scratch next scene load");
                    }
                    case "reset_defaults":
                    {
                        int n = 0;
                        var fields = typeof(LLMNPCPlugin).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        foreach (var f in fields)
                        {
                            if (!f.Name.StartsWith("_cfg", StringComparison.Ordinal)) continue;
                            if (!(f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name.StartsWith("ConfigEntry")))
                                continue;
                            object v = null;
                            try { v = f.GetValue(Plugin); } catch (Exception) { continue; }
                            var ce = v as ConfigEntryBase;
                            if (ce == null) continue;
                            string s = DefaultSerialized(ce);
                            if (s == null) continue;
                            try { ce.SetSerializedValue(s); n++; } catch (Exception) { }
                        }
                        try { Plugin.Config.Save(); } catch (Exception) { }
                        try { LLMNPCPlugin.Log.LogWarning("webui op: reset " + n + " config entries to their defaults"); } catch (Exception) { }
                        return Ok("reset " + n + " settings to defaults (memory files untouched — use delete_memories_all for those)");
                    }
                    default:
                        return Fail("unknown action '" + action + "'");
                }
            }
            catch (Exception e) { return Fail(e.Message); }
        }

        private static string DefaultSerialized(ConfigEntryBase ce)
        {
            try
            {
                var t = ce.SettingType;
                object dv = ce.DefaultValue != null ? ce.DefaultValue : default(object);
                if (t == null || dv == null) return "";
                if (t.IsEnum)
                {
                    string name = Enum.IsDefined(t, dv) ? Enum.GetName(t, dv) : null;
                    return name ?? Enum.GetNames(t)[0];
                }
                if (t == typeof(string)) return dv.ToString();
                if (t == typeof(bool)) return Convert.ToBoolean(dv, CultureInfo.InvariantCulture) ? "True" : "False";
                if (t == typeof(int)) return Convert.ToInt64(dv, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                if (t == typeof(float)) return Convert.ToSingle(dv, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                return Convert.ToString(dv, CultureInfo.InvariantCulture);
            }
            catch (Exception) { return null; }
        }

        private static string DoInstance(string payload)
        {
            try
            {
                var d = Json.Parse(payload) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("name")) return Fail("missing name");
                string name = Convert.ToString(d["name"]);
                string action = d.ContainsKey("action") ? Convert.ToString(d["action"]) : "";
                NPCInstance target = null;
                LLMNPCPlugin plugin = Plugin;
                if (plugin != null)
                {
                    lock (plugin._instancesLock)
                        foreach (var n in plugin._instances)
                        {
                            string nm = null;
                            try { nm = n.GetMyName(); } catch (Exception) { }
                            if (string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) { target = n; break; }
                        }
                }
                if (target == null) return Fail("no NPC named '" + name + "'");
                switch (action)
                {
                    case "say":
                    {
                        string text = d.ContainsKey("text") ? Convert.ToString(d["text"]) : "";
                        if (string.IsNullOrWhiteSpace(text)) return Fail("empty text");
                        var npc = target;
                        Plugin.RunOnMainThreadAsync(() => { try { npc.InjectPlayerChat(text); } catch (Exception) { } });
                        return Ok("said to " + name);
                    }
                    case "stop":
                    {
                        var npc = target;
                        Plugin.RunOnMainThreadAsync(() =>
                        {
                            try { npc.SetRunning(false); } catch (Exception) { }
                            try { npc.Stop(); } catch (Exception) { }
                        });
                        return Ok("stopping " + name);
                    }
                    case "start":
                    {
                        var npc = target;
                        Plugin.RunOnMainThreadAsync(() =>
                        {
                            try { npc.SetRunning(true); } catch (Exception) { }
                            try { npc.ForceRestartThread(); } catch (Exception) { }
                        });
                        return Ok("starting " + name);
                    }
                    case "vore_target":
                    case "vore_mode":
                    {
                        string field = action == "vore_target" ? "target" : "mode";
                        string value = d.ContainsKey(field) ? Convert.ToString(d[field]) : "";
                        if (string.IsNullOrWhiteSpace(value)) return Fail("empty " + field);
                        string clean = value.Trim().ToLowerInvariant().Trim('"', '\'');
                        var npc = target;
                        var args = new Dictionary<string, object> { [field] = clean };
                        // PersistVorePreferences -> SaveBodyMemory touches game state:
                        // marshal to the main thread.
                        Plugin.RunOnMainThreadAsync(() =>
                        {
                            try
                            {
                                object result;
                                if (action == "vore_target")
                                {
                                    if (npc.VoreTargetOverride == clean) { Ok(action + " already set for " + name + ": " + npc.VoreTargetOverride); return; }
                                    result = npc.ToolVoreTarget(new JsonObj(args));
                                }
                                else
                                {
                                    if (npc.VoreModeOverride == clean) { Ok(action + " already set for " + name + ": " + npc.VoreModeOverride); return; }
                                    result = npc.ToolVoreMode(new JsonObj(args));
                                }
                                try { Ok(Json.Write(result)); } catch (Exception) { Ok(action + " done"); }
                            }
                            catch (Exception e) { Ok(action + " failed: " + e.Message); }
                        });
                        return Ok("queued " + action + " for " + name);
                    }
                    case "reset_consent":
                    {
                        var npc = target;
                        npc.ResetConsentFlag();
                        return Ok("consent flag cleared for " + name);
                    }
                    default:
                        return Fail("unknown instance action '" + action + "'");
                }
            }
            catch (Exception e) { return Fail(e.Message); }
        }

        // ---- dir caches (main-thread resolved; server thread only reads) ----
        private static string MapsDir()
        {
            if (_mapsDir == null)
            {
                try
                {
                    if (Plugin != null)
                        Plugin.RunOnMainThread(() =>
                        {
                            try
                            {
                                string data = UnityEngine.Application.dataPath;
                                if (!string.IsNullOrEmpty(data))
                                    _mapsDir = Path.Combine(Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_maps");
                            }
                            catch (Exception) { }
                            return true;
                        }, 3000);
                }
                catch (Exception) { }
                if (_mapsDir == null) _mapsDir = "BepInEx/config/kkllmnpc_maps"; // relative fallback
            }
            return _mapsDir;
        }

        private static string MemDir()
        {
            if (_memDir == null)
            {
                try
                {
                    if (Plugin != null)
                        Plugin.RunOnMainThread(() =>
                        {
                            try
                            {
                                string data = UnityEngine.Application.dataPath;
                                if (!string.IsNullOrEmpty(data))
                                    _memDir = Path.Combine(Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_memory");
                            }
                            catch (Exception) { }
                            return true;
                        }, 3000);
                }
                catch (Exception) { }
                if (_memDir == null) _memDir = "BepInEx/config/kkllmnpc_memory";
            }
            return _memDir;
        }

        // ------------------------------------------------------------------
        // RICH snapshot — built ON the main thread (genes/transforms/pen state are
        // Unity reads) and cached for the HTTP thread. Rebuilt ~every 1.5s by
        // Main.Update, so /api/instances answers instantly and never blocks.
        // ------------------------------------------------------------------
        internal static void BuildRichSnapshot(LLMNPCPlugin plugin)
        {
            try
            {
                if (plugin == null) return;
                var list = new List<object>();
                List<NPCInstance> snap;
                lock (plugin._instancesLock) snap = new List<NPCInstance>(plugin._instances);
                foreach (var n in snap)
                {
                    try { list.Add(n.RichSnapshot()); } catch (Exception) { }
                }
                string json = Json.Write(list);
                lock (_richLock)
                {
                    _richInstancesJson = json;
                    try { _richBuiltAt = UnityEngine.Time.unscaledTime; } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private static string RichInstancesJson()
        {
            lock (_richLock) return _richInstancesJson;
        }

        internal static float RichAge()
        {
            lock (_richLock) return _richBuiltAt;
        }

        private static string ReadSafe(string path)
        {
            try
            {
                if (!File.Exists(path)) return "(no reports yet)";
                // Small tail only — the AI report log grows with the play sessions.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    const long cap = 64 * 1024;
                    if (len > cap) fs.Seek(-cap, SeekOrigin.End);
                    using (var r = new StreamReader(fs))
                        return r.ReadToEnd();
                }
            }
            catch (Exception e) { return "(reports read failed: " + e.Message + ")"; }
        }

        private static string ReportsText()
        {
            string p = null;
            try { p = NPCInstance.ReportFilePath(); } catch (Exception) { }
            return ReadSafe(string.IsNullOrEmpty(p) ? "BepInEx/config/kkllmnpc_ai_reports.log" : p);
        }

        // ------------------------------------------------------------------
        // The state snapshot. THREAD RULES: only lock-guarded strings/volatiles —
        // no Unity object access, so this answers even when the main thread is
        // wedged (exactly when you open it).
        // ------------------------------------------------------------------
        private static string JsonSnapshot()
        {
            var instances = new List<object>();
            LLMNPCPlugin plugin = Plugin;
            if (plugin != null)
            {
                List<NPCInstance> snap;
                lock (plugin._instancesLock) snap = new List<NPCInstance>(plugin._instances);
                foreach (var n in snap)
                {
                    try { instances.Add(n.WebSnapshot()); }
                    catch (Exception) { }
                }
            }
            object poll = null;
            try { poll = plugin != null ? plugin.DllPollDebug() : null; } catch (Exception) { }
            string spectate = null;
            try
            {
                spectate = SpectateManager.HudText();
                if (string.IsNullOrEmpty(spectate)) spectate = null;
                else spectate = "spectate active — " + spectate;
            }
            catch (Exception) { }
            string phase = null; bool mainReady = false; decimal idle = -1m;
            try { phase = LLMNPCPlugin.MainPhase; } catch (Exception) { }
            try { mainReady = plugin != null && plugin._mainReady; } catch (Exception) { }
            try { idle = (decimal)(plugin != null ? LLMNPCPlugin.MainIdleWallSeconds : -1f); } catch (Exception) { }
            // Vore arm state at a glance (F7/F8 cycle it; the settings tab edits it).
            string voreDest = "?", voreEffect = "?"; bool voreOn = false;
            try
            {
                if (plugin != null && plugin._cfgSwallowDestination != null)
                    voreDest = SwallowLogic.DestinationLabel(plugin._cfgSwallowDestination.Value);
                if (plugin != null && plugin._cfgSwallowEffect != null)
                    voreEffect = SwallowLogic.EffectLabel(plugin._cfgSwallowEffect.Value);
                if (plugin != null && plugin._cfgSwallowEnabled != null) voreOn = plugin._cfgSwallowEnabled.Value;
            }
            catch (Exception) { }
            var root = new Dictionary<string, object>
            {
                ["running"] = mainReady,
                ["phase"] = phase ?? "-",
                ["main_idle_wall_s"] = (double)idle,
                ["dll_poll"] = poll ?? "unavailable",
                ["spectate"] = (object)spectate ?? "none",
                ["vore"] = new { enabled = voreOn, destination = voreDest, effect = voreEffect },
                ["log"] = _ring.Tail(300),
                ["instances"] = instances.ToArray(),
            };
            return Json.Write(root);
        }

        // ------------------------------------------------------------------
        // Single-file page: vanilla JS, fetches /api/state every 2s, /api/instances
        // every 2s, /api/reports every 10s. Tabs: per-instance status (now with the
        // full stat dump), Settings (live config editor), Ops (destructive buttons).
        // (Written by hand because this game's toolchain ships no bundler.)
        // ------------------------------------------------------------------
        private const string PageHtml = @"<!doctype html>
<html><head><meta charset='utf-8'><title>KK-LLM-NPC</title><meta name=viewport content='width=device-width,initial-scale=1'><style>
*{box-sizing:border-box}
body{background:#121419;color:#cdd3de;font:13px/1.45 monospace;margin:0;height:100vh}
.app{display:grid;grid-template-columns:230px 1fr;height:100vh}
aside{background:#171a20;border-right:1px solid #2a303b;padding:14px 12px;overflow-y:auto}
main{padding:14px 18px;overflow-y:auto}
h1{font-size:15px;color:#e8c17a;margin:0 0 10px;letter-spacing:.5px}
h2{font-size:12px;color:#9fb4d0;margin:16px 0 5px;text-transform:uppercase;letter-spacing:.6px}
.navitem{display:block;padding:5px 10px;margin:2px 0;background:transparent;border:1px solid transparent;border-radius:6px;cursor:pointer}
.navitem.sel{background:#252b38;color:#fff;border-color:#3a4761}
.navitem:hover{background:#20242e}
.agt{display:block;padding:5px 10px;margin:3px 0;background:#1c2029;border:1px solid #2a303b;border-radius:6px;cursor:pointer}
.agt.sel{background:#33405a;color:#fff;border-color:#4a5a82}
.agt:hover{background:#232837}
.agt small{display:block;color:#7a8290;font-size:10px}
.agt.sel small{color:#b9c4da}
.box{background:#1b1e25;border:1px solid #2a303b;border-radius:8px;padding:9px 12px;margin:8px 0;white-space:pre-wrap;word-break:break-word}
.err{color:#e88} .warn{color:#da6} .dim{color:#7a8290} .ok{color:#7d7}
.fact{display:inline-block;background:#232a3a;color:#9fc3e8;padding:1px 6px;margin:2px;border-radius:9px}
.vorebtns{display:flex;flex-wrap:wrap;gap:3px;margin:3px 0}
.vorebtn{background:#2a2230;border:1px solid #4a3a55;border-radius:5px;color:#d9c9e6;padding:2px 8px;cursor:pointer;font:11px monospace}
.vorebtn:hover{background:#3a2f45;border-color:#5a4a66}
.vorebtn.on{background:#4a3a55;border-color:#8a6ab5;color:#fff;font-weight:bold}
.chip{font-weight:bold;color:#e8c17a}
table{border-collapse:collapse;width:100%}td{padding:2px 8px;color:#9fb4d0;vertical-align:top}
input,select{background:#10131a;color:#cdd3de;border:1px solid #2a303b;border-radius:5px;padding:3px 7px;font:12px monospace;width:200px}
button{background:#33405a;color:#fff;border:1px solid #4a5a75;border-radius:5px;padding:3px 10px;cursor:pointer;font:12px monospace}
button:hover{background:#415578}
button.danger{background:#5a3340;border-color:#754a5a}
button.danger:hover{background:#704052}
#meta{font-size:12px;line-height:1.6;background:#1b1e25;border:1px solid #2a303b;border-radius:8px;padding:8px 10px;margin:8px 0 12px}
#meta b{color:#7d9ec7}
.kv b{color:#9fb4d0}
.holdbar{position:sticky;top:0;background:#3a2f1b;color:#e8c17a;border:1px solid #75583a;border-radius:5px;padding:2px 8px;margin-bottom:4px;font-size:12px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:8px}
details.sec{margin:10px 0}
details.sec>summary{cursor:pointer;color:#9fb4d0;font-weight:bold;list-style:none;padding:5px 0}
details.sec>summary::marker{content:''}
details.sec>summary::before{content:'▸ ';color:#7d9ec7}
details.sec[open]>summary::before{content:'▾ ';color:#7d9ec7}
details.cfgsec{background:#1b1e25;border:1px solid #2a303b;border-radius:8px;margin:8px 0;padding:4px 10px}
details.cfgsec>summary{cursor:pointer;padding:7px 2px;list-style:none}
details.cfgsec>summary::marker{content:''}
details.cfgsec>summary::before{content:'▸ ';color:#7d9ec7}
details.cfgsec[open]>summary::before{content:'▾ ';color:#7d9ec7}
details.cfgsec[open]{padding-bottom:2px}
</style></head><body>
<div class=app>
<aside>
  <h1>KK-LLM-NPC</h1>
  <div id='meta' class='dim'>connecting…</div>
  <nav id='nav'>
    <div class='navitem sel' _m=kobolds onclick=setMode(&quot;kobolds&quot;)>kobolds</div>
    <div class=navitem _m=settings onclick=setMode(&quot;settings&quot;)>settings</div>
    <div class=navitem _m=serve onclick=setMode(&quot;serve&quot;)>AI server</div>
    <div class=navitem _m=logs onclick=setMode(&quot;logs&quot;)>logs &amp; reports</div>
  </nav>
  <div id='tabs'></div>
</aside>
<main>
  <div id='spectate'></div>
  <section id='view_kobolds' class=view><div id='inst'></div></section>
  <section id='view_settings' class=view style=display:none><div id='settings'></div><div id='memslot'></div><div id='ops'></div><div id='consent'></div><div id='vore'></div></section>
  <section id='view_serve' class=view style=display:none><div id='serve'></div></section>
  <section id='view_logs' class=view style=display:none>
    <h2>mod log (tail)</h2><div id='log' class='box dim' style='max-height:55vh;overflow-y:auto'></div>
    <h2>AI reports</h2><div id='reports' class='box dim' style='max-height:40vh;overflow-y:auto'></div>
  </section>
</main>
</div>
<script>
// RENDER DISCIPLINE (unchanged): a box whose html did not change is NEVER
// touched, rewrites preserve scrollTop, the log/report boxes FREEZE while you
// are scrolled up (held state flushes when you return to the bottom),
// details/summary open-state survives rebuilds, and a pause button stops polls.
// LAYOUT: a sidebar (status + nav + agent list) and ONE main pane that shows a
// single view at a time (kobolds / settings / logs) instead of everything stacked.
var sel=0, mode='kobolds', cfgList=[], insts=[], pollSeq=0, PAUSED=false;
function esc(s){var x=String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/'/g,'&#39;');return x.split(String.fromCharCode(34)).join('&quot;')}
function box(title,body){return '<h2>'+esc(title)+'</h2><div class=box>'+esc(body==null?'':body)+'</div>'}
function setMeta(html){var m=document.getElementById('meta');if(m._kkHtml!==html){m.innerHTML=html;m._kkHtml=html;}}
// Every clear must also drop the renderBox cache, or the next identical html
// is treated as unchanged and skipped over an EMPTY box (this ate the whole
// settings panel once).
function clearBox(id){var el=document.getElementById(id);if(!el)return;el.innerHTML='';el._kkHtml=null;}
function setMode(m){mode=m;setNav();window.scrollTo(0,0);poll();}
function setNav(){
  var nav=document.getElementById('nav');if(nav){
    var kids=nav.children;
    for(var i=0;i<kids.length;i++)kids[i].className='navitem'+((kids[i].getAttribute('_m')===mode)?' sel':'');
  }
  var vs=document.querySelectorAll('.view');
  for(var j=0;j<vs.length;j++)vs[j].style.display='none';
  var v=document.getElementById('view_'+(mode==='kobolds'?'kobolds':mode));
  if(v)v.style.display='block';
}
// Render one scrollable/replaceable box. stickBottom boxes (log) hold when the
// user scrolled up, and flush the held html once they return to the bottom.
function renderBox(id, html, stickBottom){
  var el=document.getElementById(id);
  // #rich lives inside #inst and dies with every instance-panel rebuild —
  // recreate on demand so the stats keep flowing after a panel rewrite.
  if(!el && id==='rich'){ el=document.createElement('div'); el.id='rich'; var pr=document.getElementById('richslot'); if(pr)pr.appendChild(el); }
  if(!el)return;
  if(el._kkHtml===html)return;                       // unchanged — hands off the DOM
  if(stickBottom){
    var atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 6;
    if(!atBottom){ el._kkHold=html; el._kkHoldPending=true; setHold(id,true); return; }
  }
  var st=el.scrollTop;
  var wasHeld = !!el._kkHold;
  el._kkHtml=html; el._kkHold=null; setHold(id,false);
  if(id==='inst'){ var oy=window.scrollY; el.innerHTML=html; window.scrollTo(0,oy); restoreDetails(el); }
  else {
    el.innerHTML=html;
    if(stickBottom){
      var b=el.scrollHeight - el.scrollTop - el.clientHeight;
      if(b<6 || wasHeld) el.scrollTop=el.scrollHeight; // held-flush lands on the newest lines
    } else el.scrollTop=st;
  }
}
function setHold(id,on){
  var h=document.getElementById('hold_'+id);
  if(!h){ var el=document.getElementById(id); if(!el)return;
    h=document.createElement('div'); h.id='hold_'+id; h.className='holdbar dim';
    h.style.display='none'; el.parentNode.insertBefore(h,el); }
  if(on){ h.textContent='⏸ held — you are scrolled up; scroll to the bottom to resume live updates'; h.style.display='block'; }
  else h.style.display='none';
}
function restoreDetails(el){
  // Key open-state by the drawer's summary TEXT, not index — a filter re-shuffle
  // used to land one section's open state on a different drawer.
  var saved=el._kkOpenByName||{}; var ds=el.querySelectorAll('details');
  for(var i=0;i<ds.length;i++){ var nm=ds[i].querySelector('summary'); var t=nm?nm.textContent:''; if(saved[t]) ds[i].open=true; }
  el._kkOpenByName={};for(var i=0;i<ds.length;i++){ (function(d){ d.addEventListener('toggle',function(){var nm=d.querySelector('summary');var t=nm?nm.textContent:'';el._kkOpenByName[t]=d.open;}); })(ds[i]); }
}
function post(path,obj,cb){
  var x=new XMLHttpRequest();x.open('POST',path);x.setRequestHeader('Content-Type','application/json');
  x.onload=function(){var r={};try{r=JSON.parse(x.responseText)}catch(e){}
    setMeta('<b class='+(r.ok?'ok':'err')+'>'+(r.ok?'ok':'ERR')+':</b> '+esc(r.note||r.error||''));
    if(cb)cb(r)};
  x.send(JSON.stringify(obj));
}
function renderMeta(d){
  var v=d.vore||{};
  var pa=PAUSED?'<button id=pausebtn onclick=togglePause()>▶ paused</button>':'<button id=pausebtn onclick=togglePause()>⏸ pause</button><br>';
  var vore='<b class='+(v.enabled?'warn':'dim')+'>vore:</b> '+(v.enabled?esc(v.destination||'?')+' / '+esc(v.effect||'?'):'OFF');
  setMeta(pa+'<b>main:</b> '+(d.running?'running':'idle')+'<br><b>phase:</b> '+esc(d.phase)
    +' &nbsp; <b>idle:</b> '+d.main_idle_wall_s+'s<br>'+vore
    +' &nbsp;<span class=ok>/api/state</span>');
}
function togglePause(){ PAUSED=!PAUSED; document.getElementById('pausebtn').textContent=PAUSED?'▶ paused':'⏸ pause'; poll(); }
// The sidebar agent chips — rendered on every poll in EVERY view so the list is
// always live; picking one from another view jumps to the kobolds view.
function renderChips(list){
  var th='';
  __kkFlagged=[];
  for(var i=0;i<list.length;i++){var ins=list[i];
    var nc=ins.non_consent||{}; if(nc.flagged)__kkFlagged.push(ins.name||('#'+i));
    var mine=PrefsWord((ins.vore_target||''),(ins.vore_mode||''));
    th+='<div class=\'agt'+(i==sel?' sel':'')+'\' onclick=sel='+i+';setMode(&quot;kobolds&quot;)><b>'+esc(ins.name||('#'+i))+'</b>'
      +(mine?('<small>vore: '+esc(mine)+'</small>'):'')+'</div>';}
  var warn='';
  if(typeof __kkFlagged!=='undefined' && __kkFlagged.length){
    warn+='<div class=warn style=padding:6px 2px;white-space:normal>⚠ non-consent flag on: '+esc(__kkFlagged.join(', '))+' — jump to one of them to reset. See the settings Consent card.</div>';
  }
  if(!list.length)th='<div class=dim style=padding:6px 2px>(no kobolds connected)</div>';
  renderBox('tabs',th+warn,false);
}
function PrefsWord(t,m){return (t||m)?((t||'global')+' / '+(m||'global')):''}
// Per-agent vore controls: two rows of discrete buttons. Target buttons cycle
// the agent's own entry hole (vore_target posts to /api/instance); Fate buttons
// its fate mode (vore_mode). Each button shows the agent's CURRENT pick; the
// button matching the current value is highlighted. 'off'/'global' clears the
// override back to the operator's setting.
function voreButtons(n){
  var nm=n.name||''; var out='';
  var targets=['mouth','anal','pussy','dick','nipple','off'];
  var curT=(n.vore_target||'').toLowerCase();
  out+='<div class=dim style=font-size:11px;margin:2px 0 1px>MY TARGET</div>';
  out+='<div class=vorebtns>';
  targets.forEach(function(t){
    var on=(t===curT);
    var cls=on?'vorebtn on':'vorebtn';
    var label=t.toUpperCase();
    if(t==='off')label='GLOBAL';
    out+='<button class='+cls+' onclick=post(&quot;/api/instance&quot;,{name:&quot;'+esc(nm)+'&quot;,action:&quot;vore_target&quot;,value:&quot;'+t+'&quot;})>'+esc(label)+'</button>';
  });
  out+='</div>';
  var modes=['egg','bellyegg','grow','destroy','off'];
  var curM=(n.vore_mode||'').toLowerCase();
  out+='<div class=dim style=font-size:11px;margin:2px 0 1px>MY FATE</div>';
  out+='<div class=vorebtns>';
  modes.forEach(function(m){
    var on=(m===curM);
    var cls=on?'vorebtn on':'vorebtn';
    var label=m.toUpperCase();
    if(m==='off')label='GLOBAL';
    out+='<button class='+cls+' onclick=post(&quot;/api/instance&quot;,{name:&quot;'+esc(nm)+'&quot;,action:&quot;vore_mode&quot;,value:&quot;'+m+'&quot;})>'+esc(label)+'</button>';
  });
  out+='</div>';
  return out;
}
function renderKobolds(d){
  if(sel>=insts.length)sel=0;
  var n=insts[sel]||{};
  var h='<div class=box>'+esc(n.body||'')+'</div>';
  h+=box('goal',n.goal==null?'(none)':(n.goal+(n.goal_for!=null?(' — for '+n.goal_for+' ticks'):'')));
  var vp='';
  if(n.vore_target||n.vore_mode)vp=' <span class=warn>my vore: '+esc((n.vore_target||'global')+' / '+(n.vore_mode||'global'))+'</span>';
  var nc=n.non_consent||{};
  var consentBadge = nc.flagged?' <span class=warn onclick=if(confirm(&quot;reset consent flag for '+esc(n.name)+'?&quot;))post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;reset_consent&quot;})>⚠ non-consent (reset)</span>':'';
  h+='<div class=box><span class=chip>identity</span>'+vp+consentBadge+'<br><span class=dim>operator globals: '+esc((d&&d.vore)?((d.vore.destination||'OFF')+' / '+(d.vore.effect||'?')):'?')+' — yours override them for the swallows you start</span></div>';
  if(n.vore_target||n.vore_mode){h+='<div class=box>'+voreButtons(n)+'</div>';
  }else{h+='<div class=box><div class=dim style=font-size:11px>Pick a target/fate above, or say nothing — the agent can set its own with the vore_target / vore_mode tools.</div></div>';}
  h+='<details class=sec open><summary>facts ('+(n.facts||[]).length+')</summary><div class=box>'+esc((n.facts||[]).join('\n'))+'</div></details>';
  h+='<details class=sec><summary>history</summary><div class=box>'+esc((n.history||[]).join('\n'))+'</div></details>';
  h+='<details class=sec><summary>terminal (last turns)</summary><div class=box>'+esc((n.terminal||[]).join('\n'))+'</div></details>';
  h+='<details class=sec><summary>full stats</summary><div id=richslot><div id=rich></div></div></details>';
  var st='';
  if(n.thread_ok)st+='✓ llm thread alive';else st+='✗ THREAD DEAD';
  if(n.blocked)st+=' — blocked: '+n.blocked;
  h+='<div class=box>'+st+'</div><details class=sec><summary>controls for '+esc(n.name||'')+'</summary>'
    +'<button onclick=post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;say&quot;,text:prompt(&quot;say as player:&quot;)})>inject player say</button> '
    +'<button onclick=post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;start&quot;})>start</button> '
    +'<button class=danger onclick=if(confirm(&quot;stop '+esc(n.name)+ '?&quot;))post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;stop&quot;})>stop</button></details>';
  renderBox('inst',h,false);
  rich(n.name||'');
}
var richSeq=0;
function rich(name){
  if(mode!=='kobolds')return;
  var seq=++richSeq;
  var x=new XMLHttpRequest();x.open('GET','/api/instances');
  x.onload=function(){ if(seq!==richSeq)return;
    var list=[];try{list=JSON.parse(x.responseText)}catch(e){}
    var n=null;for(var i=0;i<list.length;i++)if(list[i].name===name)n=list[i];
    if(!n||mode!=='kobolds')return;
    var g='<div class=grid>';
    for(var k in n){var v=n[k];
      if(k==='terminal'||k==='facts'||k==='history'){v='('+(v?String(v).split(',').length:0)+' rows — in the drawers above)';}
      if(v==null||typeof v==='object')v=JSON.stringify(v);
      g+='<div class=box><span class=chip>'+esc(k)+'</span><br>'+esc(String(v).slice(0,300))+'</div>';
    }
    g+='</div>';
    renderBox('rich',g,false);
  };x.send();
}
function ensureSettings(){
  window._settingsBuilt=true;
  if(cfgList.length){buildSettingsUi();renderOps();return;}
  // Self-heal: an empty list re-fetches and rebuilds when the fetch lands.
  renderBox('settings','<h2>settings</h2><div class=box>loading config… (press ↻ refresh)</div>',false);
  cfg(function(){ setTimeout(function(){ if(mode==='settings') { window._settingsBuilt=false; ensureSettings(); } }, 500); });
}
var cfgFilter='';
function cfgRows(){
  var h='';var firstOpen=true;
  // ONE keyed group per category (a section-changed-vs-previous-row test used
  // to open a duplicate drawer whenever the server order interleaved) —
  // canonical order first, alphabetical fallback, filter-safe.
  var order=['LLM','VisionModel','Vision','Senses','Movement','General','Autonomy','Needs','Memory','Multiplayer','Farming','Console','Web','DataRecord','LittleTreats','Debug','Ollama','Serve'];
  var groups={},names=[];
  cfgList.forEach(function(c){
    var hay=(c.section+' '+c.key+' '+String(c.desc||'')).toLowerCase();
    if(cfgFilter&&hay.indexOf(cfgFilter)<0)return;
    if(!groups[c.section]){groups[c.section]=[];names.push(c.section);}
    groups[c.section].push(c);
  });
  names.sort(function(a,b){
    var ia=order.indexOf(a);if(ia<0)ia=order.length;
    var ib=order.indexOf(b);if(ib<0)ib=order.length;
    if(ia!==ib)return ia-ib;
    var la=a.toLowerCase(),lb=b.toLowerCase();
    return la<lb?-1:(la>lb?1:0);
  });
  names.forEach(function(s){
    h+='<details class=cfgsec'+(firstOpen?' open':'')+'><summary class=chip>'+esc(s)+'</summary><div class=cfgbody><table>';
    firstOpen=false;
    groups[s].forEach(function(c){
      var desc=String(c.desc||'').replace(/\s+/g,' ').trim();
      var shortDesc=desc.length>150?desc.slice(0,150)+'…':desc;
      h+='<tr><td style=color:#e8c17a;width:220px title=\''+esc(desc)+'\'>'+esc(c.key)+'</td>'
        +'<td><input id=cfg_'+esc(c.id)+' value=\''+esc(c.value)+'\' style=width:260px title=\''+esc(desc)+'\'></td>'
        +'<td><button onclick=post(&quot;/api/config&quot;,{id:&quot;'+esc(c.id)+'&quot;,value:document.getElementById(&quot;cfg_'+esc(c.id)+'&quot;).value})>set</button></td></tr>'
        +'<tr><td colspan=3 title=\''+esc(desc)+'\'><div class=dim style=font-size:11px;padding:0 8px 6px>'+esc(shortDesc)+'</div></td></tr>';
    });
    h+='</table></div></details>';
  });
  if(!h)h='<div class=dim style=padding:6px>(no setting matches &quot;'+esc(cfgFilter)+'&quot;)</div>';
  var slot=document.getElementById('cfgrows');
  if(slot){var oy=slot.scrollTop;slot.innerHTML=h;slot.scrollTop=oy;restoreDetails(slot);}
}
// Settings accordion with a SEARCH: one <details> drawer per config category,
// the box narrows as you type; the panel is rendered ONCE per entry and again
// only on ↻ refresh or a successful set — while an input holds focus nothing
// rewrites it.
function refreshSettings(){window._settingsBuilt=false;ensureSettings();}
function buildSettingsUi(){
  if(!cfgList.length){
    renderBox('settings','<h2>settings</h2><div class=box>loading config… (press ↻ refresh)</div>',false);
    return;
  }
  var h='<h2>settings <span class=dim>(live — saved to the BepInEx config file; hover a name for its full description)</span> <button onclick=refreshSettings()>↻ refresh</button></h2>'
    +'<input id=cfgsearch placeholder=\'filter settings…\' style=width:260px oninput=cfgFilter=this.value.toLowerCase();cfgRows()>'
    +'<div id=cfgrows></div>';
  renderBox('settings',h,false);
  renderVoreCard();
  renderConsentCard();
  cfgRows();
}
// Operator Vore controls: the master master-switch, plus destination (F7) and
// fate (F8) pickers. These post the BepInEx enum member name (Mouth/Anal/…) to
// the same config field the F7/F8 hotkeys edit, so the settings tab and the
// game stay in lock-step. A button is highlighted when it matches the live
// value; the label is uppercase for readability but the posted value is the
// PascalCase member name BepInEx's enum parser expects.
function renderVoreCard(){
  var v=_liveVore||{}; var dst=(v.destination||'OFF').toUpperCase(); var eff=(v.effect||'').toUpperCase(); var on=!!v.enabled;
  var h='<h2 class=chip style=font-size:14px>Master</h2><div class=box>'
    +(on?'<span class=warn>vore ARMED</span>':'<span class=dim>vore OFF</span>')
    +' &nbsp;Destination: '+esc(dst)+' &nbsp;Fate: '+esc(eff)+'</div>';
  h+='<h2 class=chip style=font-size:14px>Destination</h2><div class=box>';
  // [label, BepInEx enum member name] — display label is uppercase, posted
  // value is the exact PascalCase member name the parser accepts.
  [['OFF','Off'],['MOUTH','Mouth'],['ANAL','Anal'],['PUSSY','Pussy'],['DICK','Dick'],['NIPPLE','Nipple']].forEach(function(b){
    var lbl=b[0], member=b[1];
    h+='<button class='+((dst===lbl)?'vorebtn on':'vorebtn')+' onclick=post(&quot;/api/config&quot;,{id:&quot;LittleTreats.SwallowDestination&quot;,value:&quot;'+member+'&quot;})>'+esc(lbl)+'</button> ';
  });
  h+='</div><h2 class=chip style=font-size:14px>Fate</h2><div class=box>';
  [['EGG','Egg'],['BELLYEGG','BellyEgg'],['GROW','Grow'],['DESTROY','Destroy']].forEach(function(b){
    var lbl=b[0], member=b[1];
    h+='<button class='+((eff===lbl)?'vorebtn on':'vorebtn')+' onclick=post(&quot;/api/config&quot;,{id:&quot;LittleTreats.SwallowEffect&quot;,value:&quot;'+member+'&quot;})>'+esc(lbl)+'</button> ';
  });
  h+='</div>';
  renderBox('vore',h,false);
}
// Standing non-consent info: what the detection does, what a flag means, and how
// to reset it. Not per-instance — it explains the feature in the card itself.
function renderConsentCard(){
  var v=_liveNonConsent||{}; var flagged=!!v.flagged; var steer=!!v.steer;
  var h='<h2 class=chip style=font-size:14px>Consent</h2><div class=box>'
    +'A non-consent flag means an LLM refused the mod&apos;s adult content — it took a stance on the kink '
    +'(refused the scene, moralised, felt insulted, said it won&apos;t consent). Such a model can play ordinary KoboldKare fine; '
    +'it just won&apos;t drive sex or kink. Flagged models here:<br>';
  if(flagged) h+='  <span class=warn>&#9888; '+esc((v.by||'a model'))+(v.reason?' — '+esc(v.reason):'')+'</span><br>';
  else h+='  <span class=ok>none flagged</span><br>';
  h+='SFW steering: '+(steer?'<span class=warn>ON — flagged models play non-sexual scenes</span>':'<span class=dim>off</span>')
    +'. Flagged models can be reset per-instance from the <b>kobolds</b> panel (the ⚠ badge). '
    +'Tune this in the settings <b>NonConsent</b> section below.</div>';
  renderBox('consent',h,false);
}
function renderOps(){
  var h='<h2>ops (destructive)</h2><div class=box>';
  h+='<button class=danger onclick=if(confirm(&quot;delete ALL AI reports?&quot;))post(&quot;/api/ops&quot;,{action:&quot;clear_reports&quot;})>clear reports</button> ';
  h+='<button class=danger onclick=if(confirm(&quot;reload config from disk?&quot;))post(&quot;/api/ops&quot;,{action:&quot;reload_config&quot;})>reload config</button> ';
  h+='</div>';
  renderBox('ops',h,false);
  var x=new XMLHttpRequest();x.open('GET','/api/memories');
  x.onload=function(){try{var l=JSON.parse(x.responseText);
    var mem='<h2>saved memories ('+l.length+')</h2><div class=box>'
      +(l.length?l.map(function(f){return '<span class=fact>'+esc(f)
      +'</span> <button onclick=if(confirm(&quot;delete '+esc(f)+'?&quot;))post(&quot;/api/ops&quot;,{action:&quot;delete_memory&quot;,file:&quot;'+esc(f)+'&quot;})>×</button> '
    }).join(''):'<span class=dim>(no memory files on disk)</span>')+'</div>';
    var slot=document.getElementById('memslot');
    if(slot){var oy=slot.scrollTop;slot.innerHTML=mem;slot.scrollTop=oy;}
  }catch(e){}};x.send();
}
function render(d){
  renderMeta(d);
  insts=d.instances||[];if(sel>=insts.length)sel=0;
  renderChips(insts);
  window._liveVore=d.vore||{};
  window._liveNonConsent=d.non_consent||{};
  renderBox('spectate',d.spectate&&d.spectate!=='none'?box('spectate',d.spectate):'',false);
  if(mode==='settings'){ if(!window._settingsBuilt) ensureSettings(); }
  else{window._settingsBuilt=false;clearBox('settings');clearBox('ops');clearBox('memslot');if(mode==='kobolds')renderKobolds(d);}
  var lg=(d.log||[]).map(function(l){var c='dim';if(l.indexOf('[warn]')===0)c='warn';if(l.indexOf('[error]')===0)c='err';return String('<div class='+c+'>')+esc(l)+'</div>'}).join('');
  renderBox('log',lg,true);
}
function reports(){var x=new XMLHttpRequest();x.open('GET','/api/reports');
  x.onload=function(){renderBox('reports',esc(x.responseText).split('\n').map(function(l){return '<div>'+l+'</div>'}).join(''),true)};x.send()}
function cfg(cb){var x=new XMLHttpRequest();x.open('GET','/api/config');x.onload=function(){try{cfgList=JSON.parse(x.responseText)||[]}catch(e){}if(cb)cb()};x.send()}
function poll(){
  if(PAUSED)return;
  var seq=++pollSeq;
  var x=new XMLHttpRequest();x.open('GET','/api/state');
  x.onload=function(){ if(seq!==pollSeq)return;
    try{render(JSON.parse(x.responseText))}catch(e){setMeta('<span class=err>bad state json</span>')}};
  x.onerror=function(){setMeta('<span class=err>server unreachable</span>')};x.send();
  if(mode==='kobolds' && pollSeq%2===1) rich(insts[sel]?insts[sel].name:''); // stats every 2nd poll (~6s)
  if(mode==='serve'){ ollamaFetch(); }
}
// ---- AI server (external host, adopted via Ollama) ----------------------
// Status + controls + model picker, refreshed only while the tab is open
// (the Ollama manager caches scans server-side; this fetch is one cheap JSON
// that already carries everything). Ollama is the only external host now — the
// old kk-serve python companion was removed.
var ollamaData=null;
function ollamaFetch(){
  var x=new XMLHttpRequest();x.open('GET','/api/ollama');
  x.onload=function(){try{ollamaData=JSON.parse(x.responseText)}catch(e){ollamaData=null};renderOllama()};x.send();
}
function ollamaAct(action,model){
  var pl={action:action};if(model!==undefined)pl.model=model;
  post('/api/ollama',pl,function(r){ if(r.ok)setTimeout(ollamaFetch,300); });
}
function ollamaToggle(id,newval){
  post('/api/config',{id:id,value:newval},function(r){ if(r.ok)setTimeout(ollamaFetch,300); });
}
function renderOllama(){
  if(mode!=='serve')return;
  var o=ollamaData||{};
  var h='<h2>Ollama (external host) <button onclick=ollamaFetch()>↻ refresh</button></h2>';
  var st=o.status||'off';
  var cls=st==='adopted'?'ok':(st==='off'||st==='waiting-ollama')?'dim':'warn';
  var line='<b class='+cls+'>'+esc(st)+'</b>'+(o.healthy?' <span class=ok>healthy</span>':'');
  if(o.endpoint)line+=' · endpoint '+esc(o.endpoint);
  line+=' · ctx '+(o.contextLength?esc(o.contextLength):'?');
  if(o.currentModel)line+=' · current '+esc(o.currentModel);
  if(o.family)line+=' · fam '+esc(o.family);
  h+='<div class=box>'+line;
  if(o.note)h+='<br><span class=dim>'+esc(o.note)+'</span>';
  if(o.lastError)h+='<br><span class=err>'+esc(o.lastError)+'</span>';
  h+='</div>';
  // controls
  h+='<div class=box>'
    +'<button onclick=ollamaToggle(&quot;Ollama.Enabled&quot;,'+(o.enabled?'false':true)+')>'+((o.enabled?'disable':'enable')+' adoption')+'</button> '
    +'<button onclick=ollamaAct(&quot;refresh&quot;)>refresh models</button> '
    +'<button onclick=ollamaToggle(&quot;Ollama.AutoEndpoint&quot;,'+(o.autoEndpoint?'false':true)+')>'+((o.autoEndpoint?'stop':'take')+' LLM traffic')+'</button>'
    +' <span class=dim>(traffic: '+(o.autoEndpoint?'Ollama (adopted)':'[LLM].Endpoint as configured')+')</span></div>';
  // installed models picker
  var tags=o.tags&&(typeof o.tags==='object')?o.tags:{};
  var list=tags.models||[];
  h+='<h2>installed models</h2><div class=box>';
  if(!list.length) h+='<div class=dim>No models installed. Pull one in a terminal: <code>ollama pull tev1:4b</code> then <code>ollama run tev1:4b</code>, or enter a name below.</div>';
  h+='<select id=ollsel style=width:420px>';
  var selName=o.selected||o.currentModel||'';
  var selectedIdx=selName?list.findIndex(function(m){return m.name===selName}):-1;
  h+='<option value=\'\''+(selName===''?' selected':'')+'>— not selected —</option>';
  for(var i=0;i<list.length;i++){var m=list[i];
    var size=(m.size)?' · '+esc((m.size/1024/1024/1024).toFixed(1)+'GB'):'';
    var fam=(m.family?' · '+esc(m.family):'');
    h+='<option value=\''+esc(m.name)+'\''+(selectedIdx===i?' selected':'')+'>'+esc(m.name+size+fam)+'</option>';
  }
  h+='</select> <button onclick=ollamaAct(&quot;select&quot;,document.getElementById(&quot;ollsel&quot;).value)>use this model</button>';
  h+='<div class=dim style=font-size:11px>writes [LLM].Model too — takes effect on the next chat turn; model must be running in Ollama to serve</div></div>';
  renderBox('serve',h,false);
}
window.onload=function(){setNav();};
setInterval(poll,3000);poll();reports();setInterval(reports,15000);cfg();setInterval(cfg,30000);
</script>
</body></html>";
    }

    // Instance snapshot surface (web handler thread; lock-guarded strings only).
    internal partial class NPCInstance
    {
        // Built OFF the main thread by design — it must answer even while the
        // main thread is wedged (that's exactly when you open the browser).
        // Unity object reads (.name) are best-effort try/caught.
        internal object WebSnapshot()
        {
            string name = "?";
            try { name = MyName(); } catch (Exception) { }
            string bodyName = "?";
            try { if (_kobold != null) bodyName = CleanName(_kobold.name); } catch (Exception) { }
            string goal = null; int goalFor = 0;
            lock (_goalLock) { goal = _goal; goalFor = _goalTick >= 0 ? _tick - _goalTick : 0; }
            var facts = new List<string>();
            lock (_facts) foreach (var f in _facts) facts.Add(f.Text);
            var history = new List<string>();
            lock (_history) foreach (var h in _history) history.Add(h);
            var terminal = new List<string>();
            try
            {
                if (_consoleMsgs != null)
                    foreach (var m in _consoleMsgs)
                    {
                        if (m == null) continue;
                        object role, content;
                        m.TryGetValue("role", out role);
                        m.TryGetValue("content", out content);
                        terminal.Add(Convert.ToString(role) + ": " + WebTrunc(Convert.ToString(content), 500));
                    }
                if (terminal.Count > 24) terminal.RemoveRange(0, terminal.Count - 24);
            }
            catch (Exception) { }
            bool threadOk; try { threadOk = IsThreadAlive; } catch (Exception) { threadOk = false; }
            string blocked = null; try { blocked = _blockedInfo; } catch (Exception) { }
            string thought = null; try { thought = _lastThought; } catch (Exception) { }
            string lastAction = null; try { lastAction = _lastAction; } catch (Exception) { }
            return new
            {
                name = name,
                body = bodyName,
                goal = (object)goal ?? "",
                goal_for = goalFor,
                last_thought = thought ?? "",
                last_action = lastAction ?? "",
                blocked = (object)blocked ?? "",
                thread_ok = threadOk,
                facts = facts.ToArray(),
                history = history.ToArray(),
                terminal = terminal.ToArray(),
                vore_target = (object)VoreTargetOverride ?? "",
                vore_mode = (object)VoreModeOverride ?? "",
                // Non-consent detection (Wave 3 POC). flagged = the model was
                // detected as refusing the adult roleplay; steer = SFW-only
                // steering currently active; reason = why it last flagged.
                non_consent = new {
                    flagged = (bool)IsNonConsentFlagged(),
                    steer = (bool)NonConsentSteerSFWOnly(),
                    status = (string)NonConsentStatusText(),
                    reason = string.IsNullOrEmpty(_lastNonConsentReason ?? "") ? "" : _lastNonConsentReason,
                },
            };
        }

        // EVERY stat and datum — built ON the main thread (cached by Main.Update);
        // read-safe here. Any single read failing just drops that key.
        internal object RichSnapshot()
        {
            var d = new Dictionary<string, object>();
            // Teardown edge: the body can be lost between the plugin's instance list
            // copy and this call — report a minimal snapshot instead of poking a
            // dying Unity object from the rich pass.
            try { if (_kobold == null || !IsAlive(_kobold)) d["body"] = "(no body)"; } catch (Exception) { }
            bool bodyLive;
            try { bodyLive = _kobold != null && IsAlive(_kobold); } catch (Exception) { bodyLive = false; }
            string name = "?", body = "?";
            try { name = MyName(); } catch (Exception) { }
            try { if (_kobold != null) body = CleanName(_kobold.name); } catch (Exception) { }
            d["name"] = name;
            d["body"] = body;
            try { d["persona"] = _persona ?? ""; } catch (Exception) { }
            try { d["pronouns"] = _pronounsOverride ?? InferPronouns(); } catch (Exception) { }
            try { d["body_key"] = BodyKey(_kobold); } catch (Exception) { }
            try { d["skill_lines"] = SkillsSnapshotLines().ToArray(); } catch (Exception) { }
            try { d["goal"] = GoalPerception(); } catch (Exception) { }
            try { d["last_thought"] = _lastThought ?? ""; } catch (Exception) { }
            try { d["last_action"] = _lastAction ?? ""; } catch (Exception) { }
            try { d["blocked"] = _blockedInfo ?? ""; } catch (Exception) { }
            try { d["bump"] = _bumpInfo ?? ""; } catch (Exception) { }
            try { d["model_error"] = _modelError ?? ""; } catch (Exception) { }
            try { d["endpoint"] = EndpointStatusText(); } catch (Exception) { }
            try { d["endpoint_fails"] = _endpointFails; } catch (Exception) { }
            try { d["model_tier"] = _resolvedTier ?? "?"; } catch (Exception) { }
            try { d["model_family"] = FamilyProfile != null ? FamilyProfile.Family : "?"; } catch (Exception) { }
            try { d["tick"] = _tick; } catch (Exception) { }
            try { d["awake_s"] = Math.Max(0f, UnityEngine.Time.unscaledTime - _sessionStartTime); } catch (Exception) { }
            var facts = new List<string>();
            try { lock (_facts) foreach (var f in _facts) facts.Add(f.Text); d["facts"] = facts.ToArray(); } catch (Exception) { }
            try
            {
                var hist = new List<string>();
                lock (_history) foreach (var h in _history) hist.Add(h);
                d["history"] = hist.ToArray();
            }
            catch (Exception) { }
            try { d["horniness"] = F(_horny); } catch (Exception) { }
            try { if (bodyLive) d["energy"] = F(_kobold.GetEnergy()) + " / max " + F(_kobold.GetGenes().maxEnergy); } catch (Exception) { }
            try { if (bodyLive) d["egg_volume_ml"] = F(GetEggVolume(_kobold)); } catch (Exception) { }
            try
            {
                if (!bodyLive) throw new Exception("no_body");
                var g = _kobold.GetGenes();
                d["genes"] = new Dictionary<string, object>
                {
                    ["species_index"] = g.species,
                    ["base_size"] = F(g.baseSize),
                    ["fat_size"] = F(g.fatSize),
                    ["breast_size"] = F(g.breastSize),
                    ["dick_size"] = F(g.dickSize),
                    ["dick_thickness"] = F(g.dickThickness),
                    ["ball_size"] = F(g.ballSize),
                    ["belly_size"] = F(g.bellySize),
                    ["dick_equip"] = g.dickEquip,
                    ["grab_count"] = g.grabCount,
                    ["max_energy"] = F(g.maxEnergy),
                    ["hue"] = g.hue, ["saturation"] = g.saturation, ["brightness"] = g.brightness,
                    ["species_label"] = CleanName(_kobold.name),
                };
            }
            catch (Exception) { }
            try
            {
                if (!bodyLive) throw new Exception("no_body");
                var p = _kobold.transform.position;
                d["position"] = "x=" + F(p.x) + " y=" + F(p.y) + " z=" + F(p.z);
            }
            catch (Exception) { }
            try { d["swallowed"] = Swallowed; } catch (Exception) { }
            try { if (bodyLive) d["belly_guests"] = SwallowManager.BellyGuestText(_kobold) ?? "(none)"; } catch (Exception) { }
            try { d["swallow_sessions_active"] = SwallowManager.ActiveCount; } catch (Exception) { }
            try { if (bodyLive) d["penetration_in"] = PenetrationInfo() ?? "(none)"; } catch (Exception) { }
            try { d["dick_inside"] = DickInInfo() ?? "(none)"; } catch (Exception) { }
            try { d["partners"] = PartnersList(); } catch (Exception) { }
            try { d["in_station"] = IsInAnimationStation() ? (_stationPurpose ?? "?") : "(no)"; } catch (Exception) { }
            try { d["follow"] = _followMode; } catch (Exception) { }
            try { d["nav_target"] = _navTargetName ?? null; } catch (Exception) { }
            try
            {
                var names = new List<string>();
                foreach (var h in HeldScan())
                    names.Add(h.Name + (h.Victim != null ? " (a PERSON)" : ""));
                d["holding"] = names.Count > 0 ? string.Join(", ", names.ToArray()) : "(nothing)";
            }
            catch (Exception) { }
            try { d["coins"] = GetCoins(); } catch (Exception) { }
            try { d["quest"] = QuestSense.TitleLine(); } catch (Exception) { }
            try { d["vision_caption"] = WebTrunc(_lastVisionCaption ?? "", 200); } catch (Exception) { }
            try { d["sleeping_s"] = _sleepUntilTime > UnityEngine.Time.unscaledTime ? Math.Ceiling(_sleepUntilTime - UnityEngine.Time.unscaledTime) : 0; } catch (Exception) { }
            try { d["console_rounds"] = _cfgConsoleMaxRounds != null ? _cfgConsoleMaxRounds.Value : 0; } catch (Exception) { }
            try { d["salvage_fires"] = _salvageFires; } catch (Exception) { }
            try { d["thread_alive"] = IsThreadAlive; } catch (Exception) { }
                        return d;
        }

        private static string WebTrunc(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\n", " ⏎ ");
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }
    }

    // WebUI config (bound in Main.cs Awake before Ensure).
    public partial class LLMNPCPlugin
    {
        internal ConfigEntry<bool> _cfgWebEnabled;
        internal ConfigEntry<int> _cfgWebPort;
    }
}