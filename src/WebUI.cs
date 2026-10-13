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
                else if (path == "/api/reports")
                {
                    body = Encoding.UTF8.GetBytes(ReportsText());
                    mime = "text/plain; charset=utf-8";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("not found — / | /api/state | /api/instances | /api/config | /api/memories | /api/reports");
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
            var items = new List<object>();
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
                    items.Add(new Dictionary<string, object>
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
            // Vore arm state at a glance (F7 cycles it; the settings tab edits it).
            string voreDest = "?"; bool voreOn = false;
            try
            {
                if (plugin != null && plugin._cfgSwallowDestination != null)
                    voreDest = SwallowLogic.DestinationLabel(plugin._cfgSwallowDestination.Value);
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
                ["vore"] = new { enabled = voreOn, destination = voreDest },
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
<html><head><meta charset='utf-8'><title>KK-LLM-NPC debug</title><style>
body{background:#15171c;color:#cdd3de;font:13px/1.45 monospace;margin:18px}
h1{font-size:16px;color:#e8c17a;margin:0 0 6px}h2{font-size:13px;color:#9fb4d0;margin:14px 0 4px}
.tab{display:inline-block;padding:4px 12px;margin:2px;background:#22262e;border:1px solid #333a45;cursor:pointer;border-radius:4px}
.tab.sel{background:#33405a;color:#fff}
.box{background:#1b1e25;border:1px solid #333a45;border-radius:6px;padding:8px 12px;margin:8px 0;white-space:pre-wrap;word-break:break-word}
.err{color:#e88} .warn{color:#da6} .dim{color:#7a8290} .ok{color:#7d7}
.fact{display:inline-block;background:#232a3a;color:#9fc3e8;padding:1px 6px;margin:2px;border-radius:8px}
.chip{font-weight:bold;color:#e8c17a}
table{border-collapse:collapse;width:100%}td{padding:2px 8px;color:#9fb4d0;vertical-align:top}
input,select{background:#10131a;color:#cdd3de;border:1px solid #333a45;border-radius:4px;padding:2px 6px;font:12px monospace;width:180px}
button{background:#33405a;color:#fff;border:1px solid #4a5a75;border-radius:4px;padding:3px 10px;cursor:pointer;font:12px monospace}
button:hover{background:#415578}
button.danger{background:#5a3340;border-color:#754a5a}
button.danger:hover{background:#704052}
#meta b{color:#7d9ec7}
.kv b{color:#9fb4d0}
.holdbar{position:sticky;top:0;background:#3a2f1b;color:#e8c17a;border:1px solid #75583a;border-radius:4px;padding:2px 8px;margin-bottom:4px;font-size:12px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:8px}
</style></head><body>
<h1>KK-LLM-NPC debug</h1>
<div id='meta' class='dim'>connecting…</div>
<div id='tabs'></div><div id='inst'></div><div id='rich'></div><div id='spectate'></div>
<h2>mod log (tail)</h2><div id='log' class='box dim' style='max-height:220px;overflow-y:auto'></div>
<h2>AI reports</h2><div id='reports' class='box dim' style='max-height:160px;overflow-y:auto'></div>
<div id='settings'></div>
<script>
// RENDER DISCIPLINE (the user-facing fix): the page used to innerHTML-rewrite
// every box on every 2s poll — scroll reset constantly and moving text was
// unreadable. Now: (1) a box whose html did not change is NEVER touched, (2)
// rewrites preserve scrollTop, (3) the log/report boxes FREEZE while you are
// scrolled up (the newest state is held; scrolling back to the bottom flushes
// it), (4) details/summary open-state survives rebuilds, (5) a pause button
// stops all polls (and the cadence is slower: 3s state, 15s reports).
var sel=0, mode='kobolds', cfgList=[], pollSeq=0, PAUSED=false;
function esc(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt')}
function box(title,body){return '<h2>'+esc(title)+'</h2><div class=box>'+esc(body==null?'':body)+'</div>'}
function setMeta(html){var m=document.getElementById('meta');if(m._kkHtml!==html){m.innerHTML=html;m._kkHtml=html;}}
// Render one scrollable/replaceable box. stickBottom boxes (log) hold when the
// user scrolled up, and flush the held html once they return to the bottom.
function renderBox(id, html, stickBottom){
  var el=document.getElementById(id);
  // #rich lives inside #inst and dies with every instance-panel rebuild —
  // recreate on demand so the stats keep flowing after a panel rewrite.
  if(!el && id==='rich'){ el=document.createElement('div'); el.id='rich'; document.getElementById('inst').appendChild(el); }
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
  var saved=el._kkOpen||[]; var ds=el.querySelectorAll('details');
  for(var i=0;i<ds.length;i++){ if(saved[i]) ds[i].open=true; }
  el._kkOpen=[];for(var i=0;i<ds.length;i++){ (function(d,idx){ d.addEventListener('toggle',function(){el._kkOpen[idx]=d.open;}); })(ds[i],i); }
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
  var pa=PAUSED?'<button id=pausebtn onclick=togglePause()>▶ paused</button>':'<button id=pausebtn onclick=togglePause()>⏸ pause</button> ';
  setMeta(pa+'<b>main:</b> '+(d.running?'running':'idle')+' &nbsp; <b>phase:</b> '+esc(d.phase)
    +' &nbsp; <b>idle:</b> '+d.main_idle_wall_s+'s'
    +' &nbsp; <b class='+(v.enabled?'warn':'dim')+'>vore:</b> '+(v.enabled?esc(v.destination):'OFF')
    +' &nbsp; <span class=ok>/api/state</span>');
}
function togglePause(){ PAUSED=!PAUSED; document.getElementById('pausebtn').textContent=PAUSED?'▶ paused':'⏸ pause'; poll(); }
function renderKobolds(d){
  var tabsEl=document.getElementById('tabs');
  var th='<div class=&quot;tab sel&quot;>kobolds</div><div class=tab onclick=mode=&quot;settings&quot;;poll()>settings</div>';
  for(var i=0;i<(d.instances||[]).length;i++){var ins=(d.instances||[])[i];
    th+='<div class=tab'+(i==sel?' sel':'')+' onclick=sel='+i+';poll()>'+esc(ins.name||('#'+i))+'</div>';}
  renderBox('tabs',th,false);
  insts=d.instances||[];if(sel>=insts.length)sel=0;
  var n=insts[sel]||{};
  var h='<div class=box>'+esc(n.body||'')+'</div>';
  h+=box('goal',n.goal==null?'(none)':(n.goal+(n.goal_for!=null?(' — for '+n.goal_for+' ticks'):'')));
  h+=box('facts ('+(n.facts||[]).length+')',(n.facts||[]).join('\n'));
  h+=box('history',((n.history||[]).join('\n')));
  h+=box('terminal (last turns)',((n.terminal||[]).join('\n')));
  var st='';
  if(n.thread_ok)st+='✓ llm thread alive';else st+='✗ THREAD DEAD';
  if(n.blocked)st+=' — blocked: '+n.blocked;
  h+='<div class=box>'+st+'</div><div class=box><details><summary>controls for '+esc(n.name||'')+'</summary>'
    +'<button onclick=post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;say&quot;,text:prompt(&quot;say as player:&quot;)})>inject player say</button> '
    +'<button onclick=post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;start&quot;})>start</button> '
    +'<button class=danger onclick=if(confirm(&quot;stop '+esc(n.name)+ '?&quot;))post(&quot;/api/instance&quot;,{name:&quot;'+esc(n.name)+'&quot;,action:&quot;stop&quot;})>stop</button></details></div>';
  renderBox('inst',h,false);
  rich(n.name||'');
}
var richSeq=0;
function rich(name){
  var seq=++richSeq;
  var x=new XMLHttpRequest();x.open('GET','/api/instances');
  x.onload=function(){ if(seq!==richSeq)return;
    var list=[];try{list=JSON.parse(x.responseText)}catch(e){}
    var n=null;for(var i=0;i<list.length;i++)if(list[i].name===name)n=list[i];
    if(!n||mode!=='kobolds')return;
    var g='<h2>full stats</h2><div class=grid>';
    for(var k in n){var v=n[k];
      if(v==null||typeof v==='object')v=JSON.stringify(v);
      g+='<div class=box><span class=chip>'+esc(k)+'</span><br>'+esc(String(v).slice(0,400))+'</div>';
    }
    g+='</div>';
    renderBox('rich',g,false);
  };x.send();
}
function renderSettings(){
  var tabsEl=document.getElementById('tabs');
  var th='<div class=tab onclick=mode=&quot;kobolds&quot;;poll()>kobolds</div><div class=&quot;tab sel&quot;>settings</div>';
  renderBox('tabs',th,false);
  // Self-heal: an empty list re-fetches and re-renders when the fetch lands —
  // the boot-time fetch once raced the first render and the page showed
  // a loading placeholder forever.
  if(!cfgList.length){
    renderBox('settings','<h2>settings</h2><div class=box>loading config…</div>',false);
    cfg(function(){ setTimeout(function(){ if(mode==='settings') { renderSettings(); renderOps(); } }, 500); });
    return;
  }
  var h='<h2>settings (live — saved to the BepInEx config file)</h2>';
  var cur='';
  h+='<table>';
  cfgList.forEach(function(c){
    if(c.section!==cur){cur=c.section;h+='<tr><td colspan=3><h2 style=margin:10px 0 2px>'+esc(c.section)+'</h2></td></tr>';}
    h+='<tr><td style=color:#e8c17a;width:220px>'+esc(c.key)+'</td>'
      +'<td><input id=cfg_'+esc(c.id)+' value=\''+esc(c.value)+'\' style=width:260px></td>'
      +'<td><button onclick=post(&quot;/api/config&quot;,{id:&quot;'+esc(c.id)+'&quot;,value:document.getElementById(&quot;cfg_'+esc(c.id)+'&quot;).value})>set</button></td></tr>';
  });
  h+='</table><div class=dim>full descriptions: GET /api/config</div>';
  renderBox('settings',h,false);
}
function opsHtml(){
  return '<h2>operations</h2><div class=box>'
    +'<button onclick=post(&quot;/api/ops&quot;,{action:&quot;delete_maps&quot;})>delete map + trail data</button> '
    +'<small class=dim>(world map rebuilds from scratch)</small><br><br>'
    +'<button class=danger onclick=if(confirm(&quot;Delete ALL saved kobold memory files?&quot;))post(&quot;/api/ops&quot;,{action:&quot;delete_memories_all&quot;})>delete ALL memory files</button> '
    +'<button class=danger onclick=if(confirm(&quot;Reset every setting to its default?&quot;))post(&quot;/api/ops&quot;,{action:&quot;reset_defaults&quot;})>reset settings to defaults</button>'
    +'<div class=dim style=margin-top:8px>memory files refresh with the ops tab</div></div>';
}
function renderOps(){
  renderBox('ops',opsHtml(),false);
  var x=new XMLHttpRequest();x.open('GET','/api/memories');
  x.onload=function(){try{var l=JSON.parse(x.responseText);
    var h='<div class=dim style=margin-top:8px>'+(l.length?l.map(function(f){return '<span class=fact>'+esc(f)
      +'</span> <button onclick=if(confirm(&quot;delete '+esc(f)+'?&quot;))post(&quot;/api/ops&quot;,{action:&quot;delete_memory&quot;,file:&quot;'+esc(f)+'&quot;})>×</button> '
    }).join(''):'(no memory files on disk)')+'</div>';
    var el=document.getElementById('ops').querySelector('.box');
    if(el){var old=el.querySelector('.memlist');if(old)old.remove();
      var d=document.createElement('div');d.className='memlist';d.innerHTML=h;el.appendChild(d);}
  }catch(e){}};x.send();
}
function render(d){
  renderMeta(d);
  renderBox('spectate',d.spectate&&d.spectate!=='none'?box('spectate',d.spectate):'',false);
  if(mode==='settings'){renderSettings();renderOps();}
  else{document.getElementById('settings').innerHTML='';document.getElementById('ops').innerHTML='';renderKobolds(d);}
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
}
setInterval(poll,3000);poll();reports();setInterval(reports,15000);cfg();setInterval(cfg,30000);
</script>
<div id='ops'></div>
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