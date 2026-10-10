// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// WebUI — the mod's debug console, delivered over the loopback instead of squeezed
// into the stripped IMGUI overlay. The user wanted a browser-facing surface they can
// keep open while playing (and one the DEvelopment assistant can fetch too: any agent
// with shell access can `curl http://127.0.0.1:<port>/api/state` mid-session).
//
//   GET /            single-file HTML (no assets, no build tools; vanilla-JS polling)
//   GET /api/state   JSON: plugin status + every instance (name/goal/facts/history/
//                    terminal tail/counts) + spectate status + the mod's BepInEx log ring
//   GET /api/reports the AI bug-report log (text/plain tail, newest last)
//
// Design constraints honored:
// * LOOPBACK ONLY (127.0.0.1), read-only — it never mutates game state and is never
//   reachable from the network.
// * THREAD-SAFE by construction: the handler thread reads ONLY lock-guarded strings
//   and volatiles — never Unity objects, so it still answers while the main thread is
//   mid-freeze (which is exactly when you want it). Positions/transforms are
//   deliberately absent.
// * All work in one background thread; every request is try/caught; nothing here can
//   take the game down.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
        internal const int Cap = 400;

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
                try { LLMNPCPlugin.Log.LogInfo("webui: debug console at http://127.0.0.1:" + port + "/ (loopback only, read-only)"); } catch (Exception) { }
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
                if (path == "/" || path == "/index.html")
                {
                    body = Encoding.UTF8.GetBytes(PageHtml);
                    mime = "text/html; charset=utf-8";
                }
                else if (path == "/api/state")
                {
                    body = Encoding.UTF8.GetBytes(JsonSnapshot());
                    mime = "application/json; charset=utf-8";
                }
                else if (path == "/api/reports")
                {
                    body = Encoding.UTF8.GetBytes(ReportsText());
                    mime = "text/plain; charset=utf-8";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("not found — / | /api/state | /api/reports");
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
            var root = new Dictionary<string, object>
            {
                ["running"] = mainReady,
                ["phase"] = phase ?? "-",
                ["main_idle_wall_s"] = (double)idle,
                ["dll_poll"] = poll ?? "unavailable",
                ["spectate"] = (object)spectate ?? "none",
                ["log"] = _ring.Tail(150),
                ["instances"] = instances.ToArray(),
            };
            return Json.Write(root);
        }

        // ------------------------------------------------------------------
        // Single-file page: vanilla JS, fetches /api/state every 2s, renders one
        // tab per instance + the shared log/spectate/reports boxes.
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
table{border-collapse:collapse}td{padding:1px 8px;color:#9fb4d0}
#meta b{color:#7d9ec7}
</style></head><body>
<h1>KK-LLM-NPC debug</h1>
<div id='meta' class='dim'>connecting…</div>
<div id='tabs'></div>
<div id='inst'></div>
<div id='spectate'></div>
<h2>mod log (tail)</h2><div id='log' class='box dim' style='max-height:220px;overflow-y:auto'></div>
<h2>AI reports</h2><div id='reports' class='box dim' style='max-height:160px;overflow-y:auto'></div>
<script>
var sel=0;
function esc(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')}
function box(title,body){return '<h2>'+esc(title)+'</h2><div class=box>'+esc(body)+'</div>'}
function render(d){
  document.getElementById('meta').innerHTML='<b>main:</b> '+(d.running?'running':'idle')+' &nbsp; <b>phase:</b> '+esc(d.phase)+' &nbsp; <b>idle:</b> '+d.main_idle_wall_s+'s &nbsp; <span class=ok>http://127.0.0.1:'+location.port+'/api/state</span>';
  var ins=d.instances||[];if(sel>=ins.length)sel=0;
document.getElementById('tabs').innerHTML='';
var tabsEl=document.getElementById('tabs');
for(var i=0;i<ins.length;i++){var tb=document.createElement('div');tb.className='tab'+(i==sel?' sel':'');tb.textContent=ins[i].name||('#'+i);tb.onclick=(function(idx){return function(){sel=idx;poll();};})(i);tabsEl.appendChild(tb);}
  var n=ins[sel]||{};
  var h='<div class=box>'+esc(n.body||'')+'</div>';
  h+=box('goal',n.goal==null?'(none)':(n.goal+(n.goal_for!=null?(' — for '+n.goal_for+' ticks'):'')));
  var fs=(n.facts||[]).join('\n');h+=box('facts ('+(n.facts||[]).length+')',fs);
  h+=box('history',((n.history||[]).join('\n')));
  h+=box('terminal (last turns)',((n.terminal||[]).join('\n')));
  var st='';
  if(n.thread_ok)st+='✓ llm thread alive';else st+='✗ THREAD DEAD';
  if(n.blocked)st+=' — blocked: '+n.blocked;
  h+='<div class=box>'+st+'</div>';
  document.getElementById('inst').innerHTML=h;
  document.getElementById('spectate').innerHTML=d.spectate&&d.spectate!=='none'?box('spectate',d.spectate):'';
  var lg=(d.log||[]).map(function(l){var c='dim';if(l.indexOf('[warn]')===0)c='warn';if(l.indexOf('[error]')===0)c='err';return String('<div class='+c+'>')+esc(l)+'</div>'}).join('');
  document.getElementById('log').innerHTML=lg;
}
function reports(){var x=new XMLHttpRequest();x.open('GET','/api/reports');x.onload=function(){document.getElementById('reports').textContent=x.responseText};x.send()}
function poll(){
  var x=new XMLHttpRequest();x.open('GET','/api/state');x.onload=function(){try{render(JSON.parse(x.responseText))}catch(e){document.getElementById('meta').innerHTML='<span class=err>bad state json</span>'}};x.onerror=function(){document.getElementById('meta').textContent='server unreachable'};x.send()}
function draw(){poll()}
setInterval(poll,2000);poll();reports();setInterval(reports,10000);
</script></body></html>";
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