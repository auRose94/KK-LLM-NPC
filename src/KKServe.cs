// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// KKServe — manages the kk-serve companion (serve/kk_serve.py, the numpy+torch
// OpenAI server this repo ships): launch when the game launches, health-watch,
// restart from the WebUI, proxy model discovery, capture its logs.
//
// Design notes:
//  * ALL Unity access is absent here — process + HTTP + config all happen on
//    the watcher thread, same discipline as the WebUI listener thread.
//  * Health truth beats process truth: a mod hot-reload kills our child while
//    a leftover instance may still bind the port — the watcher polls
//    /health so "running" means an answering server, not merely a live PID.
//  * Backoff on spawn failures: a missing python/kk_serve.py must not turn
//    into a fork bomb — tries grow 1s→16s and stall at "failed" until the
//    WebUI restarts or the config changes.
//  * Endpoint opt-in ([Serve] AutoEndpoint): NPCInstance's POSTs consult
//    EndpointOverride(); off by default so an existing LM Studio setup never
//    gets hijacked by a feature that silently failed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Configuration;

namespace KKLLMNPC
{
    internal static class KKServe
    {
        private static LLMNPCPlugin _plugin;
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<string> _cfgPython;
        private static ConfigEntry<string> _cfgServeDir;
        private static ConfigEntry<int> _cfgPort;
        private static ConfigEntry<bool> _cfgAutoEndpoint;
        private static ConfigEntry<string> _cfgSelected;
        private static ConfigEntry<string> _cfgLocations;
        private static ConfigEntry<float> _cfgVramLimit;
        private static ConfigEntry<string> _cfgExtra;
        private static ConfigEntry<string> _cfgDevice;
        private static ConfigEntry<string> _cfgPythonCpu;
        private static ConfigEntry<string> _cfgPythonCuda;
        private static ConfigEntry<string> _cfgPythonRocm;

        private static readonly object _lock = new object();
        private static Thread _watch;
        private static volatile bool _running;
        private static volatile bool _stopRequested;   // user pressed stop: kill, no respawn
        private static volatile bool _spawnNow;
        private static Process _proc;
        private static int _spawnTries;
        private static long _nextSpawnUtc;
        private static string _status = "off";         // off|starting|running|failed|stopped
        private static volatile bool _healthy;
        private static string _healthJson = "";
        private static string _modelsJson = "";
        private static string _devicesJson = "";
        private static string _catalogJson = "";
        private static string _pythonsJson = "";
        private static string _pyScanState = "idle";   // idle|detecting|done|error
        private static string _pyScanNote = "";
        private static Process _fetchProc;
        private static readonly LineRing _fetchLog = new LineRing(300);
        private static string _fetchState = "idle";   // idle|downloading:<repo>|scanning|done|error
        private static string _fetchNote = "";
        private static long _modelsFetchedUtc;
        private static string _lastError = "";
        private static string _note = "";
        private static string _scriptPath = "";
        private static readonly LineRing _log = new LineRing(600);
        private static string _hostKind;              // windows|wine|unix — cached
        private static string _unixResolved;
        private static bool _unixResolvedDone;
        private static string _binShPath = "";        // the shell spelling that exists here
        private static volatile bool _adoptedExternal;

        // Proton detection: mono reports Windows inside Proton, but /bin/sh is
        // real — File.Exists resolves /bin through wine's Z: drive mapping.
        private static string HostKind()
        {
            if (_hostKind != null) return _hostKind;
            bool windows;
            try { windows = Environment.OSVersion.Platform == PlatformID.Win32NT; }
            catch (Exception) { windows = false; }
            if (string.IsNullOrEmpty(_binShPath)) _binShPath = FindBinShPath();
            _hostKind = ServeArgs.HostKind(windows, _binShPath.Length > 0);
            if (_hostKind == "wine")
                try { LLMNPCPlugin.Log?.LogInfo("kk-serve: wine/proton host detected — python spawns use absolute unix paths"); } catch (Exception) { }
            return _hostKind;
        }

        // mono under wine rewrites leading-slash paths against the CURRENT
        // DRIVE (lpApplicationName "/bin/sh" -> S:\bin\sh there) — the working
        // spelling is the mapped root drive. Proton maps Z: -> / but probe
        // several spellings: whatever File.Exists confirms is the one CreateProcess
        // will resolve, since mono applies the same translation both ways.
        internal static string[] BinShSpellings()
        {
            return new[] { "/bin/sh", "Z:/bin/sh", "Z:\\bin\\sh", "C:/bin/sh" };
        }

        // ("/bin/sh" worked -> ""; "Z:/bin/sh" worked -> "Z:") — the mapped-root
        // prefix unix-absolute paths need to survive mono-under-wine's
        // drive-relative rewriting (File.Exists AND CreateProcess both apply it).
        private static string RootPrefix()
        {
            return _binShPath.Substring(0, Math.Max(0, _binShPath.Length - "/bin/sh".Length));
        }

        private static string FindBinShPath()
        {
            foreach (var cand in BinShSpellings())
            {
                try { if (File.Exists(cand)) return cand; }
                catch (Exception) { }
            }
            return "";
        }

        // Inside Proton, bare names don't resolve: ask the host shell once.
        private static string ResolvedUnixPython()
        {
            if (_unixResolvedDone) return _unixResolved;
            _unixResolvedDone = true;
            string sh = _binShPath.Length > 0 ? _binShPath : FindBinShPath();
            if (_hostKind == "unix" || sh.Length == 0) return null;
            if (_hostKind == "wine")
                _unixResolved = RunSimple(sh,
                    "command -v python3 2>/dev/null | head -n 1; command -v python 2>/dev/null | head -n 1", 4000);
            return _unixResolved;
        }

        // ------------------------------------------------------------------
        // model fetch (fetch_assets.py) — runs as its own process; the serve
        // health watcher is untouched while a multi-GB download streams through.
        // All python starts go through the candidate walker (Windows has no
        // 'python3': interpreters are python.exe / py.exe and PATH names often
        // fail under Unity's Process.Start).
        // ------------------------------------------------------------------
        private static bool StartFetch(Dictionary<string, object> opts, bool scanOnly, bool bepinexOnly = false)
        {
            lock (_lock)
            {
                if (_fetchProc != null && !_fetchProc.HasExited) return false;
                // locate fetch_assets.py next to kk_serve.py (or searched up)
                string serveScriptPath = _scriptPath;
                if (string.IsNullOrEmpty(serveScriptPath) || !File.Exists(serveScriptPath))
                    serveScriptPath = ServeArgs.FindScript(_cfgServeDir != null ? _cfgServeDir.Value : "",
                        PluginDir(), Directory.GetCurrentDirectory());
                string fetchScript = "";
                if (!string.IsNullOrEmpty(serveScriptPath))
                {
                    string d = Path.GetDirectoryName(serveScriptPath);
                    if (!string.IsNullOrEmpty(d)) fetchScript = Path.Combine(d, "fetch_assets.py");
                }
                if (string.IsNullOrEmpty(fetchScript) || !File.Exists(fetchScript))
                {
                    _fetchState = "error";
                    _fetchNote = string.IsNullOrEmpty(serveScriptPath)
                        ? "fetch_assets.py not found: kk_serve.py was not found either — run build.sh (ships serve/ next to the dll) or set [Serve].ServeDir"
                        : "fetch_assets.py missing next to " + _lastPart(serveScriptPath) + " — re-run build.sh";
                    return false;
                }
                string gameRoot = GameModelsDir();
                if (!string.IsNullOrEmpty(gameRoot)) gameRoot = Path.GetFullPath(Path.Combine(gameRoot, ".."));
                string pythonBin = _cfgPython != null ? _cfgPython.Value : "python3";
                string repo = scanOnly ? "" : Convert.ToString(opts.ContainsKey("model") ? opts["model"] : "");
                var args = ServeArgs.BuildFetchArgs(fetchScript, gameRoot, repo,
                    bepinexOnly && !scanOnly, _cfgBepinexVariant(), scanOnly);

                if (scanOnly)
                {
                    // synchronous on a worker: capture stdout into the catalog cache
                    _fetchState = "scanning";
                    _fetchNote = "";
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            string fail;
                            string outS = RunCaptureArgs(args, Path.GetDirectoryName(fetchScript), pythonBin, out fail);
                            if (outS == null)
                            {
                                _fetchState = "error";
                                _fetchNote = "catalog scan: " + fail;
                                return;
                            }
                            _catalogJson = outS;
                            _fetchState = _catalogJson.Length > 0 ? "done" : "error";
                            _fetchNote = _catalogJson.Length > 0 ? "" : "catalog scan produced nothing — check the python env has huggingface_hub";
                        }
                        catch (Exception e2)
                        {
                            _fetchState = "error";
                            _fetchNote = "catalog scan failed: " + e2.Message;
                        }
                    });
                    return true;
                }

                string failReport;
                var proc = StartPythonProcess(args, Path.GetDirectoryName(fetchScript), pythonBin, "model fetch", out failReport);
                if (proc == null)
                {
                    _fetchState = "error";
                    _fetchNote = failReport;
                    try { LLMNPCPlugin.Log?.LogWarning("kk-serve fetch: " + failReport); } catch (Exception) { }
                    return false;
                }
                _fetchLog.Clear();
                proc.OutputDataReceived += (s2, e) => { if (e.Data != null) _fetchLog.Add(e.Data); };
                proc.ErrorDataReceived += (s2, e) => { if (e.Data != null) _fetchLog.Add("(err) " + e.Data); };
                proc.EnableRaisingEvents = true;
                proc.Exited += (s2, e) =>
                {
                    bool ok;
                    try { ok = proc.ExitCode == 0; } catch (Exception) { ok = false; }
                    _fetchState = ok ? "done" : "error";
                    _fetchNote = ok ? "fetch finished — rescanning models…" : "fetch failed — see the fetch log";
                    if (ok) MaybeFetchModels(true);
                    try { LLMNPCPlugin.Log?.LogInfo("kk-serve fetch: exit " + proc.ExitCode); } catch (Exception) { }
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                _fetchProc = proc;
                _fetchState = bepinexOnly ? "downloading:BepInEx" : "downloading:" + repo;
                _fetchNote = "";
                return true;
            }
        }

        // ------------------------------------------------------------------
        // python discovery: PATH + the common install roots, each candidate
        // verified by a real interpreter run (kills Microsoft-Store stubs and
        // broken PATH entries), then a torch probe with a longer leash.
        // ------------------------------------------------------------------
        internal static void StartPyScan()
        {
            if (_pyScanState == "detecting") return;
            _pyScanState = "detecting";
            _pyScanNote = "";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string hostKind = HostKind();
                    var found = new List<string>();
                    if (hostKind == "windows")
                    {
                        foreach (var d in ServeArgs.ParsePathDirs(Environment.GetEnvironmentVariable("PATH"), true))
                            found.Add(Path.Combine(d, "python.exe"));
                        foreach (var root in CommonPythonRoots(true))
                        {
                            try
                            {
                                foreach (var sub in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                                    found.Add(Path.Combine(sub, "python.exe"));
                            }
                            catch (Exception) { }
                        }
                    }
                    else
                    {
                        // wine (proton) and native unix: enumerate through the
                        // host shell — wine's Process env is windows-ized and
                        // useless for unix discovery
                        string sh = _binShPath.Length > 0 ? _binShPath : FindBinShPath();
                        if (sh.Length == 0)
                        {
                            _pyScanNote = "no host shell found (probed /bin/sh, Z:/bin/sh) — set [Serve].PythonBin manually";
                        }
                        string list = sh.Length > 0 ? RunSimple(sh, UnixFindPySnippet(), 6000) : null;
                        if (list != null)
                            foreach (var lineRaw in list.Split('\n'))
                            {
                                string line = lineRaw.Trim();
                                if (line.Length == 0 || line.IndexOf("python", StringComparison.Ordinal) < 0) continue;
                                if (line.IndexOf(' ') >= 0) line = line.Split(' ')[0];
                                if (line[0] != '/') line = "/usr/bin/" + line;
                                found.Add(line);
                            }
                    }
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var verified = new List<object>();
                    string prefix = RootPrefix();
                    foreach (var exe in found)
                    {
                        string exePath = exe;
                        try
                        {
                            // unix-absolute candidates need the mapped root (Z:) to
                            // reach File.Exists/CreateProcess under wine
                            if (prefix.Length > 0 && exePath[0] == '/')
                                exePath = prefix + exePath;
                            if (exePath.Contains("bin/python3") && !File.Exists(exePath))
                                exePath = Path.Combine(Path.GetDirectoryName(exePath), "python3");
                        }
                        catch (Exception) { }
                        try
                        {
                            if (!File.Exists(exePath)) continue;
                            string norm = exePath.ToLowerInvariant();
                            if (!seen.Add(norm)) continue;
                            string versionLine = RunSimple(exePath, "import sys; print(sys.executable + '|' + sys.version.split()[0])", 4000);
                            if (string.IsNullOrEmpty(versionLine)) continue; // stub/broken
                            string real, version = "";
                            int bar = versionLine.IndexOf('|');
                            if (bar > 0) { real = versionLine.Substring(0, bar); version = versionLine.Substring(bar + 1).Trim(); }
                            else { real = versionLine.Trim(); }
                            if (string.IsNullOrEmpty(real) || !real.ToLowerInvariant().Contains("python")) continue;
                            string torchLine = RunSimple(exePath, "import torch; print((torch.__version__ or '?') + ('|hip' if torch.version.hip else '|cuda'))", 20000);
                            bool torchOk = torchLine != null && torchLine.Trim().Length > 0;
                            var item = new Dictionary<string, object>
                            {
                                ["path"] = real,
                                ["version"] = version,
                                ["torch"] = torchOk ? "yes " + (torchLine != null ? torchLine.Trim() : "") : "no",
                            };
                            verified.Add(item);
                        }
                        catch (Exception) { }
                    }
                    var d2 = new Dictionary<string, object>
                    {
                        ["pythons"] = verified,
                        ["note"] = verified.Count == 0 ? "no working python found in PATH or the common install roots — enter a venv path manually" : (verified.Count + " working interpreter(s)"),
                    };
                    _pythonsJson = Json.Write(d2);
                    _pyScanState = "done";
                    _pyScanNote = verified.Count == 0
                        ? (HostKind() == "wine"
                            ? "no windows python in this prefix and wine can't exec unix ELF — start kk-serve via serve/proton-launch.sh (Steam launch options; KKLLM_SERVE_PYTHON) and the mod adopts it"
                            : "none found — set [Serve].PythonBin manually")
                        : "";
                }
                catch (Exception e)
                {
                    _pyScanState = "error";
                    _pyScanNote = "python scan failed: " + e.Message;
                }
            });
        }

        // Host-shell enumeration for unix/wine hosts. Absolute paths only —
        // bare names don't survive wine's CreateProcess.
        private static string UnixFindPySnippet()
        {
            return "command -v python3 2>/dev/null; command -v python 2>/dev/null; "
                 + "ls -1d /usr/bin/python3* /usr/local/bin/python3* 2>/dev/null; "
                 + "ls -1 $HOME/.venvs/*/bin/python* 2>/dev/null | head -n 20; "
                 + "ls -1 /opt/*/bin/python3 2>/dev/null | head -n 10; true";
        }

        private static List<string> CommonPythonRoots(bool windows)
        {
            var out_ = new List<string>();
            try
            {
                if (windows)
                {
                    string lad = Environment.GetEnvironmentVariable("LocalAppData");
                    if (!string.IsNullOrEmpty(lad)) out_.Add(Path.Combine(lad, "Programs", "Python"));
                    out_.Add("C:\\");
                    string pf = Environment.GetEnvironmentVariable("ProgramFiles");
                    if (!string.IsNullOrEmpty(pf)) out_.Add(pf);
                }
                else
                {
                    out_.Add("/usr/local");
                    out_.Add("/opt");
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(home)) out_.Add(Path.Combine(home, ".venvs"));
                }
            }
            catch (Exception) { }
            for (int i = out_.Count - 1; i >= 0; i--)
                if (string.IsNullOrEmpty(out_[i])) out_.RemoveAt(i);
            return out_;
        }

        // small one-shot interpreter probe: run `python -c <code>`, capture stdout
        private static string RunSimple(string exe, string code, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-c " + ServeArgs.Quote(code),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                using (var so = p.StandardOutput)
                {
                    bool done = p.WaitForExit(timeoutMs);
                    if (!done) { try { p.Kill(); } catch (Exception) { } return null; }
                    if (p.ExitCode != 0) return null;
                    string outS = so.ReadToEnd();
                    return outS != null ? outS.Trim() : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Ask a server we did NOT spawn to exit (loopback /serve/shutdown),
        // so restart/stop can replace an external launch (proton route).
        private static void ShutdownExternal()
        {
            try
            {
                int port = _cfgPort != null ? _cfgPort.Value : 8339;
                string resp = HttpPostJson("http://127.0.0.1:" + port + "/serve/shutdown", "{}", 4000);
                _adoptedExternal = false;
                try { Thread.Sleep(900); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static string _cfgBepinexVariant()
        {
            return "win_x64";
        }

        // Start a python process walking the candidate interpreters until one
        // actually launches. Returns the live process (redirected streams;
        // attach handlers + Begin*ReadLine afterwards) — or null with a report.
        private static Process StartPythonProcess(List<string> argsBase, string workDir,
            string pythonBin, string role, out string failReport)
        {
            string lastErr = "";
            string hostKind = HostKind();
            var candidates = ServeArgs.PythonCandidates(pythonBin, hostKind, ResolvedUnixPython());
            foreach (var py in candidates)
            {
                // wine's lpCurrentDirectory is a windows path — spawning a unix
                // python with a unix workdir fails there; python reads its own
                // script dir for sys.path, so skip it
                bool skipWorkdir = hostKind == "wine";
                var psi = new ProcessStartInfo
                {
                    FileName = py,
                    Arguments = ServeArgs.QuoteJoin(argsBase),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = skipWorkdir ? null : (string.IsNullOrEmpty(workDir) ? null : workDir),
                };
                try
                {
                    var p = new Process { StartInfo = psi };
                    p.Start();
                    argsBase[0] = py; // the caller's log line shows the real interpreter
                    failReport = "";
                    _adoptedExternal = false;
                    return p;
                }
                catch (Exception e)
                {
                    lastErr = py + " → " + e.Message;
                    try { LLMNPCPlugin.Log?.LogInfo("kk-serve python try (" + role + "): " + lastErr); } catch (Exception) { }
                }
            }
            failReport = "no python worked (" + role + "; tried " +
                string.Join(", ", candidates.ToArray()) + ") — set [Serve].PythonBin to the absolute interpreter path (the venv's python.exe)";
            return null;
        }

        // Blocking variant for the catalog scan: first candidate that starts,
        // read to completion, return stdout (stderr when stdout is empty).
        private static string RunCaptureArgs(List<string> argsBase, string workDir,
            string pythonBin, out string failReport)
        {
            string hostKind = HostKind();
            var candidates = ServeArgs.PythonCandidates(pythonBin, hostKind, ResolvedUnixPython());
            string lastErr = "";
            foreach (var py in candidates)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = py,
                    Arguments = ServeArgs.QuoteJoin(argsBase),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = hostKind == "wine" ? "" : (string.IsNullOrEmpty(workDir) ? "" : workDir),
                };
                try
                {
                    using (var p = Process.Start(psi))
                    using (var so = p.StandardOutput)
                    using (var se = p.StandardError)
                    {
                        p.WaitForExit(90000);
                        string outp = so.ReadToEnd();
                        if (string.IsNullOrEmpty(outp)) outp = se.ReadToEnd();
                        failReport = "";
                        return outp;
                    }
                }
                catch (Exception e)
                {
                    lastErr = py + " → " + e.Message;
                }
            }
            failReport = "no python worked (tried " + string.Join(", ", candidates.ToArray()) + "): " + lastErr;
            return null;
        }

        private static List<string> MinusFirst(List<string> args)
        {
            var out_ = new List<string>();
            for (int i = 1; i < args.Count; i++) out_.Add(args[i]);
            return out_;
        }

        private static string _lastPart(string path)
        {
            try { return Path.GetFileName(path); } catch (Exception) { return path ?? ""; }
        }

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        internal static void Ensure(LLMNPCPlugin plugin)
        {
            if (_plugin != null) return;
            _plugin = plugin;
            _cfgEnabled = plugin.Config.Bind("Serve", "Enabled", false,
                "Launch the kk-serve companion (serve/kk_serve.py — the numpy+torch server for the fine-tuned artifacts) when the game launches, and manage it from the WebUI 'AI server' tab. Needs python3 with torch on this machine (or a venv path below).");
            _cfgPython = plugin.Config.Bind("Serve", "PythonBin", "python3",
                "Python executable used to run kk-serve. A venv interpreter path is best ('/srv/kk/venv/bin/python' or the Windows equivalent), so torch kernels are whatever the serve requirements installed.");
            _cfgServeDir = plugin.Config.Bind("Serve", "ServeDir", "",
                "Directory containing kk_serve.py (or the file path itself). Empty = auto-search: plugin dir, working dir, and their serve/ subfolders.");
            _cfgPort = plugin.Config.Bind("Serve", "Port", 8339,
                "Port for the companion server (pairs with the mod WebUI on 8338). The launched process binds 127.0.0.1 on this port.");
            _cfgAutoEndpoint = plugin.Config.Bind("Serve", "AutoEndpoint", false,
                "When the companion is healthy, LLM + vision fallback calls use it automatically, regardless of [LLM].Endpoint (hand-off: no config editing after a game restart). Off = your configured endpoint is always the truth.");
            _cfgSelected = plugin.Config.Bind("Serve", "SelectedModel", "auto",
                "Which model the companion serves: 'auto' = it scans ModelLocations and loads the best fit (a fine-tuned artifact from training/ wins; VRAM fit gates everything). A specific entry (discovered name or full path) pins it — change is hot-applied while the server runs.");
            _cfgLocations = plugin.Config.Bind("Serve", "ModelLocations", "",
                "Directories the companion scans for models (';' or newline separated). Also understands a HF hub cache root (models--org--name/snapshots).");
            _cfgVramLimit = plugin.Config.Bind("Serve", "VramLimitGb", 0f,
                "Cap the free-VRAM budget used for best-fit scoring (0 = trust the driver). Raise nothing by default — the card serves what it can.");
            _cfgExtra = plugin.Config.Bind("Serve", "ExtraArgs", "",
                "Extra kk_serve.py command line, appended verbatim (--kv-reuse off, --dtype float16, --chat-template …).");
            _cfgDevice = plugin.Config.Bind("Serve", "Device", "auto",
                "Which processor kk-serve loads the model on: auto (first torch device), cpu, or a specific id from its device list (cuda:0 …). On a mixed-vendor box a device picks its vendor's python too — see PythonCpu/PythonCuda/PythonRocm.");
            _cfgPythonCpu = plugin.Config.Bind("Serve", "PythonCpu", "",
                "Python binary to use for --device cpu (any interpreter; no GPU libs needed). Empty = PythonBin.");
            _cfgPythonCuda = plugin.Config.Bind("Serve", "PythonCuda", "",
                "Python binary with a CUDA-built torch — required for an NVIDIA card. Empty = PythonBin.");
            _cfgPythonRocm = plugin.Config.Bind("Serve", "PythonRocm", "",
                "Python binary with a ROCm-built torch — required for the AMD card(s) (and the iGPU, which only HIP builds see). Empty = PythonBin.");
            _running = true;
            _watch = new Thread(WatchLoop) { IsBackground = true, Name = "KKLLMNPC-ServeWatch" };
            _watch.Start();
        }

        internal static void Shutdown()
        {
            _running = false;
            KillProc();
        }

        // ------------------------------------------------------------------
        // watcher
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
                        if (_proc != null) KillProc();
                        _status = _stopRequested ? "stopped" : "off";
                        _healthy = false;
                        Thread.Sleep(2500);
                        continue;
                    }
                    if (_stopRequested)
                    {
                        KillProc();
                        _status = "stopped";
                        _healthy = false;
                        Thread.Sleep(2500);
                        continue;
                    }
                    // WINE/PROTON HOST: wine cannot exec unix ELF (verified on
                    // this box: ShellExecuteEx 'File not found' for /bin/sh) —
                    // spawning from inside the game is impossible by
                    // architecture. Wait for an EXTERNAL server (the
                    // proton-launch.sh launch-options route) and adopt it.
                    if (HostKind() == "wine" && _proc == null)
                    {
                        _spawnNow = false;
                        if (_probeHealthOnce())
                        {
                            if (!_adoptedExternal)
                            {
                                _adoptedExternal = true;
                                _status = "running";
                                _healthy = true;
                                _lastError = "";
                                _note = "adopted the externally-launched kk-serve (proton route)";
                                try { LLMNPCPlugin.Log?.LogInfo("kk-serve: adopted external server (wine host)"); } catch (Exception) { }
                            }
                            PollHealth();
                            MaybeFetchModels(false);
                            MaybeFetchDevices();
                            try { Thread.Sleep(2000); } catch (Exception) { return; }
                            continue;
                        }
                        if (_status != "waiting-external")
                        {
                            _status = "waiting-external";
                            _note = "proton/wine host: launch kk-serve from Steam launch options — "
                                  + "\"<game>/BepInEx/plugins/serve/proton-launch.sh\" %command%"
                                  + " (or run it manually; the mod adopts it here)";
                        }
                        if (_adoptedExternal) { _adoptedExternal = false; _status = "waiting-external"; }
                        _healthy = false;
                        try { Thread.Sleep(2000); } catch (Exception) { return; }
                        continue;
                    }
                    bool spawnDue = _spawnNow || !_spawnHeld();
                    if (_proc == null && spawnDue)
                    {
                        _spawnNow = false;
                        Spawn();
                    }
                    // ADOPTION: kk-serve may be running WITHOUT us — a Steam
                    // launch-option script (serve/proton-launch.sh), a systemd
                    // unit, or a terminal. Health truth wins: never spawn-war
                    // the port owner; the panel reports adopted-external.
                    if (_proc == null && _probeHealthOnce())
                    {
                        if (!_adoptedExternal)
                        {
                            _adoptedExternal = true;
                            _status = "running";
                            _healthy = true;
                            _lastError = "";
                            _note = "an answering kk-serve is on the port that WE did not spawn (external launch) — adopted; the restart button will replace it";
                            try { LLMNPCPlugin.Log?.LogInfo("kk-serve: adopted an externally-launched server on the port"); } catch (Exception) { }
                        }
                        MaybeFetchModels(false);
                        MaybeFetchDevices();
                        try { Thread.Sleep(2000); } catch (Exception) { return; }
                        continue;
                    }
                    if (_adoptedExternal && _proc == null)
                    {
                        // external server went away → fall back to managed spawning
                        if (spawnDue) { _adoptedExternal = false; _note = ""; }
                    }
                    PollHealth();
                    MaybeFetchModels(false);
                    MaybeFetchDevices();
                }
                catch (Exception e)
                {
                    try { _log.Add("(watcher) " + e.Message); } catch (Exception) { }
                    try { LLMNPCPlugin.Log?.LogWarning("kk-serve watcher: " + e.Message); } catch (Exception) { }
                }
                try { Thread.Sleep(2000); } catch (Exception) { return; }
            }
        }

        // Cheap availability check for adoption (no state change; 1.5s timeout).
        private static bool _probeHealthOnce()
        {
            int port = _cfgPort != null ? _cfgPort.Value : 8339;
            return HttpGetString("http://127.0.0.1:" + port + "/health", 1500) != null;
        }

        private static bool _spawnHeld()
        {
            if (_spawnTries == 0) return false;
            return DateTime.UtcNow.Ticks < _nextSpawnUtc;
        }

        private static void Spawn()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return;
                _proc = null;
                int port = _cfgPort != null ? _cfgPort.Value : 8339;
                _scriptPath = ServeArgs.FindScript(_cfgServeDir != null ? _cfgServeDir.Value : "",
                    PluginDir(), Directory.GetCurrentDirectory());
                if (string.IsNullOrEmpty(_scriptPath))
                {
                    _spawnTries++;
                    _nextSpawnUtc = DateTime.UtcNow.Ticks + 80000000L * (1 << Math.Min(4, _spawnTries)); // 8s..~2min
                    _status = "failed";
                    _lastError = "kk_serve.py not found — run build.sh (it ships serve/ next to the dll) or set [Serve].ServeDir to the folder holding kk_serve.py";
                    return;
                }
                var locations = ServeArgs.MergeLocations(
                    ServeArgs.ParseLocations(_cfgLocations != null ? _cfgLocations.Value : ""),
                    ImplicitDirs());
                string device = _cfgDevice != null ? _cfgDevice.Value ?? "auto" : "auto";
                var args = ServeArgs.BuildArgs(_scriptPath, port,
                    _cfgSelected != null ? _cfgSelected.Value : "auto",
                    locations,
                    _cfgVramLimit != null ? _cfgVramLimit.Value : 0f,
                    _cfgExtra != null ? _cfgExtra.Value : "", "127.0.0.1", device);
                // walk the python candidates: Windows has no 'python3', and a
                // PATH name that resolves for a shell may not for Process.Start —
                // the walk + the failure report make the remedy obvious.
                string pythonBin = ResolvePython(device);
                string failReport;
                var p = StartPythonProcess(args, Path.GetDirectoryName(_scriptPath), pythonBin, "kk-serve", out failReport);
                if (p == null)
                {
                    _spawnTries++;
                    _nextSpawnUtc = DateTime.UtcNow.Ticks + 80000000L * (1 << Math.Min(4, _spawnTries));
                    _status = _spawnTries >= 8 ? "failed" : "starting";
                    _lastError = failReport;
                    try { LLMNPCPlugin.Log?.LogWarning("kk-serve: " + failReport); } catch (Exception) { }
                    return;
                }
                p.OutputDataReceived += (s2, e) => { if (e.Data != null) { _log.Add(e.Data); try { LLMNPCPlugin.Log?.LogInfo("kk-serve: " + e.Data); } catch (Exception) { } } };
                p.ErrorDataReceived += (s2, e) => { if (e.Data != null) { _log.Add("(err) " + e.Data); try { LLMNPCPlugin.Log?.LogWarning("kk-serve: " + e.Data); } catch (Exception) { } } };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                _spawnTries++;
                _nextSpawnUtc = DateTime.UtcNow.Ticks + 80000000L * (1 << Math.Min(4, _spawnTries));
                _status = "starting";
                _note = "";
                try { LLMNPCPlugin.Log?.LogInfo("kk-serve: launching with " + args[0] + " " + ServeArgs.QuoteJoin(MinusFirst(args))); } catch (Exception) { }
            }
        }

        private static string PluginDir()
        {
            try
            {
                string loc = typeof(KKServe).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch (Exception) { }
            return "";
        }

        // Where fetch_assets.py lands models: <game root>/kk-llm-models — next
        // to the game so deleting the game takes them too. Always scanned (the
        // scan skips a missing dir), so a build.sh-fetched model needs no config.
        internal static string GameModelsDir()
        {
            try
            {
                string plugins = PluginDir();
                if (string.IsNullOrEmpty(plugins)) return "";
                string game = Path.GetFullPath(Path.Combine(plugins, "..", ".."));
                if (string.IsNullOrEmpty(game)) return "";
                return Path.Combine(game, "kk-llm-models");
            }
            catch (Exception) { return ""; }
        }

        private static List<string> ImplicitDirs()
        {
            var l = new List<string>();
            string d = GameModelsDir();
            if (d.Length > 0) l.Add(d);
            return l;
        }

        private static void KillProc()
        {
            lock (_lock)
            {
                var p = _proc;
                _proc = null;
                if (p == null) return;
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        try { p.WaitForExit(4000); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
                _healthy = false;
                try { LLMNPCPlugin.Log?.LogInfo("kk-serve: process stopped"); } catch (Exception) { }
            }
        }

        private static bool ProcAlive()
        {
            var p = _proc;
            if (p == null) return false;
            try { return !p.HasExited; } catch (Exception) { return false; }
        }

        private static void PollHealth()
        {
            // An ADOPTED server (proton launch-options route) is never ours:
            // ownership checks alone must not clear adoption state/notes — the
            // adoption loop re-probes /health itself each tick.
            if (_adoptedExternal && _proc == null) return;
            if (!ProcAlive())
            {
                if (_proc == null || _proc.HasExited)
                {
                    if (_status == "running" || _status == "starting")
                    {
                        _status = "stopped";
                        _note = "the companion process exited — see its log below";
                        try { LLMNPCPlugin.Log?.LogWarning("kk-serve: process exited"); } catch (Exception) { }
                    }
                }
                _healthy = false;
                return;
            }
            int port = _cfgPort != null ? _cfgPort.Value : 8339;
            string body = HttpGetString("http://127.0.0.1:" + port + "/health", 2500);
            if (body != null)
            {
                _healthJson = body;
                _healthy = true;
                if (_spawnTries != 0)
                {
                    _spawnTries = 0;
                    _nextSpawnUtc = 0;
                }
                if (_status != "running")
                {
                    _status = "running";
                    _lastError = "";
                    _note = "";
                    try { LLMNPCPlugin.Log?.LogInfo("kk-serve: healthy on port " + port); } catch (Exception) { }
                }
            }
            else
            {
                _healthy = false;
                if (_status == "starting") { /* still booting or wrong port — keep waiting */ }
            }
        }

        private static void MaybeFetchModels(bool force)
        {
            if (!_healthy) return;
            long now = DateTime.UtcNow.Ticks;
            if (!force && _modelsJson.Length > 0 && now - _modelsFetchedUtc < 15000000L) return; // 15s cache
            int port = _cfgPort != null ? _cfgPort.Value : 8339;
            string body = HttpGetString("http://127.0.0.1:" + port + "/serve/models", 8000);
            if (body != null)
            {
                _modelsJson = body;
                _modelsFetchedUtc = now;
            }
        }

        private static string HttpGetString(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
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

        internal static string HttpPostJson(string url, string body, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException e)
            {
                try
                {
                    var resp = e.Response;
                    if (resp != null)
                        using (var stream = resp.GetResponseStream())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            return reader.ReadToEnd();
                }
                catch (Exception) { }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // WebUI API
        // ------------------------------------------------------------------
        // NPCInstance LLM call sites consult this per request; cheap + lock-free.
        private static void MaybeFetchDevices()
        {
            if (!_healthy) return;
            long now = DateTime.UtcNow.Ticks;
            if (_devicesJson.Length > 0 && now - _modelsFetchedUtc < 30000000L) return; // 30s cache
            int port = _cfgPort != null ? _cfgPort.Value : 8339;
            string body = HttpGetString("http://127.0.0.1:" + port + "/serve/devices", 5000);
            if (body != null) _devicesJson = body;
        }

        // python resolution per the selected device: the device list the current
        // server reports carries each device's backend, and a device under the
        // 'other' vendor's build is exactly the one needing a different python.
        private static string ResolvePython(string device)
        {
            string backend = "";
            try
            {
                if (_devicesJson.Length > 0)
                {
                    var root = Json.Parse(_devicesJson) as Dictionary<string, object>;
                    var list = root != null ? root.GetValueOrDefault("devices") as List<object> : null;
                    if (list != null)
                        foreach (var dObj in list)
                        {
                            var d = dObj as Dictionary<string, object>;
                            if (d == null) continue;
                            string id = d.GetValueOrDefault("id") as string;
                            if (string.Equals(id, device, StringComparison.OrdinalIgnoreCase))
                            {
                                backend = (d.GetValueOrDefault("backend") as string) ?? "";
                                break;
                            }
                        }
                }
            }
            catch (Exception) { }
            return ServeArgs.PythonForDevice(device, backend,
                _cfgPython != null && _cfgPython.Value.Trim().Length > 0 ? _cfgPython.Value.Trim() : "python3",
                _cfgPythonCpu != null ? _cfgPythonCpu.Value.Trim() : "",
                _cfgPythonCuda != null ? _cfgPythonCuda.Value.Trim() : "",
                _cfgPythonRocm != null ? _cfgPythonRocm.Value.Trim() : "");
        }

        internal static string EndpointOverride()
        {
            return ServeArgs.EndpointForLoopback(_cfgAutoEndpoint != null && _cfgAutoEndpoint.Value, _healthy, _cfgPort != null ? _cfgPort.Value : 0);
        }

        internal static string SnapshotJson()
        {
            object health = null;
            if (_healthJson.Length > 0)
            {
                try { health = Json.Parse(_healthJson); }
                catch (Exception) { health = _healthJson; }
            }
            object models = null;
            if (_modelsJson.Length > 0)
            {
                try { models = Json.Parse(_modelsJson); }
                catch (Exception) { models = _modelsJson; }
            }
            var lines = _log.Tail(300);
            var logArr = new List<string>(lines);
            var d = new Dictionary<string, object>
            {
                ["status"] = _status,
                ["host"] = HostKind(),
                ["adopted"] = _adoptedExternal,
                ["healthy"] = _healthy,
                ["enabled"] = _cfgEnabled != null && _cfgEnabled.Value,
                ["autoEndpoint"] = _cfgAutoEndpoint != null && _cfgAutoEndpoint.Value,
                ["port"] = _cfgPort != null ? _cfgPort.Value : 0,
                ["selected"] = _cfgSelected != null ? _cfgSelected.Value : "auto",
                ["locations"] = _cfgLocations != null ? _cfgLocations.Value : "",
                ["implicitDirs"] = ImplicitDirs(),
                ["python"] = _cfgPython != null ? _cfgPython.Value : "python3",
                ["script"] = _string(_scriptPath),
                ["vramLimitGb"] = _cfgVramLimit != null ? _cfgVramLimit.Value : 0f,
                ["extra"] = _cfgExtra != null ? _cfgExtra.Value : "",
                ["pid"] = _safePid(),
                ["health"] = health,
                ["models"] = models,
                ["device"] = _cfgDevice != null ? _cfgDevice.Value : "auto",
                ["pythonMap"] = new Dictionary<string, string>
                {
                    ["bin"] = _cfgPython != null ? _cfgPython.Value : "python3",
                    ["cpu"] = _cfgPythonCpu != null ? _cfgPythonCpu.Value : "",
                    ["cuda"] = _cfgPythonCuda != null ? _cfgPythonCuda.Value : "",
                    ["rocm"] = _cfgPythonRocm != null ? _cfgPythonRocm.Value : "",
                },
                ["devices"] = _devicesJson.Length > 0 ? _parseSafe(_devicesJson) : null,
                ["catalog"] = _catalogJson.Length > 0 ? _parseSafe(_catalogJson) : null,
                ["pythons"] = _pythonsJson.Length > 0 ? _parseSafe(_pythonsJson) : null,
                ["pyScan"] = new Dictionary<string, object>
                {
                    ["state"] = _pyScanState,
                    ["note"] = _pyScanNote,
                },
                ["fetch"] = new Dictionary<string, object>
                {
                    ["state"] = _fetchState,
                    ["note"] = _fetchNote,
                    ["log"] = _fetchLog.Tail(200),
                },
                ["lastError"] = _lastError,
                ["note"] = _note,
                ["spawnTries"] = _spawnTries,
                ["log"] = logArr,
            };
            return Json.Write(d);
        }

        private static string _string(string s) { return s ?? ""; }

        private static object _parseSafe(string json)
        {
            try { return Json.Parse(json); } catch (Exception) { return json; }
        }

        private static object _safePid()
        {
            try { return (_proc != null && !_proc.HasExited) ? (object)_proc.Id : null; }
            catch (Exception) { return null; }
        }

        private static void Save(string note)
        {
            _note = note;
            try { _plugin.Config.Save(); } catch (Exception) { }
        }

        internal static string HandlePost(string payload)
        {
            try
            {
                var d = Json.Parse(payload) as Dictionary<string, object>;
                if (d == null) return "{\"ok\":false,\"error\":\"bad json\"}";
                string action = Convert.ToString(d.ContainsKey("action") ? d["action"] : "");
                if (action == "restart")
                {
                    _spawnTries = 0;
                    _nextSpawnUtc = 0;
                    _stopRequested = false;
                    _cfgEnabled.Value = true;
                    if (_adoptedExternal) ShutdownExternal();
                    KillProc();
                    Spawn();
                    Save("restart issued");
                    return "{\"ok\":true,\"note\":\"restarting kk-serve\"}";
                }
                if (action == "stop")
                {
                    _stopRequested = true;
                    if (_adoptedExternal) ShutdownExternal();
                    KillProc();
                    _status = "stopped";
                    Save("stop issued");
                    return "{\"ok\":true,\"note\":\"stopped — it stays down until you press start\"}";
                }
                if (action == "start")
                {
                    _stopRequested = false;
                    _spawnTries = 0;
                    _nextSpawnUtc = 0;
                    _cfgEnabled.Value = true;
                    Save("start issued");
                    Spawn();
                    return "{\"ok\":true,\"note\":\"starting kk-serve\"}";
                }
                if (action == "rescan")
                {
                    MaybeFetchModels(true);
                    return "{\"ok\":true,\"note\":\"model list refreshed\"}";
                }
                if (action == "select_device")
                {
                    string device = Convert.ToString(d.ContainsKey("device") ? d["device"] : "auto") ?? "auto";
                    if (device.Length == 0) device = "auto";
                    _cfgDevice.Value = device;
                    if (device == "auto" || !string.Equals(_cfgDevice.Value, "auto"))
                        _cfgDevice.Value = device;
                    Save("device saved: " + device);
                    // device choice is process-level: restart under the right python
                    _spawnTries = 0;
                    _nextSpawnUtc = 0;
                    _stopRequested = false;
                    KillProc();
                    Spawn();
                    return "{\"ok\":true,\"note\":\"restarting on device " + Json.Write(device).Trim().Trim('"') + "\"}";
                }
                if (action == "find_pythons")
                {
                    StartPyScan();
                    return "{\"ok\":true,\"note\":\"scanning PATH + common install roots for working interpreters…\"}";
                }
                if (action == "select_python")
                {
                    string py = Convert.ToString(d.ContainsKey("path") ? d["path"] : "").Trim();
                    if (py.Length == 0) return "{\"ok\":false,\"error\":\"path required\"}";
                    // verify BEFORE saving: the version probe rejects typos
                    // (z:/usr/bin/python instead of python3) and, under Proton,
                    // wrong drive spellings, with the exact correction in the note
                    string verify = RunSimple(py, "import sys; print(sys.executable)", 6000);
                    if (string.IsNullOrEmpty(verify))
                    {
                        return "{\"ok\":false,\"error\":\"'" + Json.Write(py).Trim().Trim('"')
                            + "' did not run a version probe. Under Proton the interpreter is"
                            + " addressed through the Z: drive (Z: = Linux root): use"
                            + " Z:/usr/bin/python3 (python3 — not python), or a venv's actual"
                            + " python path.\"}";
                    }
                    try { LLMNPCPlugin.Log?.LogInfo("kk-serve python verified: " + verify.Trim()); } catch (Exception) { }
                    _cfgPython.Value = py;
                    Save("python set: " + py);
                    if (_cfgEnabled != null && _cfgEnabled.Value
                        && (_proc != null || _status == "failed"))
                    {
                        // the running companion keeps its interpreter — a restart applies
                        _spawnTries = 0;
                        _nextSpawnUtc = 0;
                        _stopRequested = false;
                        KillProc();
                        Spawn();
                        return "{\"ok\":true,\"note\":\"[Serve].PythonBin saved — companion restarting\"}";
                    }
                    return "{\"ok\":true,\"note\":\"[Serve].PythonBin saved — applies on the next start\"}";
                }
                if (action == "set_python_map")
                {
                    foreach (var key in new[] { "cpu", "cuda", "rocm" })
                    {
                        if (!d.ContainsKey(key)) continue;
                        string v = Convert.ToString(d[key]) ?? "";
                        if (key == "cpu" && _cfgPythonCpu != null) _cfgPythonCpu.Value = v;
                        if (key == "cuda" && _cfgPythonCuda != null) _cfgPythonCuda.Value = v;
                        if (key == "rocm" && _cfgPythonRocm != null) _cfgPythonRocm.Value = v;
                    }
                    Save("python map saved");
                    return "{\"ok\":true,\"note\":\"per-device pythons saved — applied on the next start/restart\"}";
                }
                if (action == "scan_catalog")
                {
                    bool started = StartFetch(new Dictionary<string, object>(), true);
                    return started ? "{\"ok\":true,\"note\":\"reading the candidate catalog\"}"
                                   : "{\"ok\":false,\"error\":\"a fetch/scan is already running\"}";
                }
                if (action == "fetch")
                {
                    string model = Convert.ToString(d.ContainsKey("model") ? d["model"] : "");
                    if (string.IsNullOrEmpty(model))
                        return "{\"ok\":false,\"error\":\"model repo id required\"}";
                    var opts = new Dictionary<string, object> { ["model"] = model };
                    bool started = StartFetch(opts, false);
                    return started ? "{\"ok\":true,\"note\":\"downloading " + Json.Write(model).Trim().Trim('"') + "\"}"
                                   : "{\"ok\":false,\"error\":\"a fetch/scan is already running\"}";
                }
                if (action == "fetch_bepinex")
                {
                    bool started = StartFetch(new Dictionary<string, object>(), false, true);
                    return started ? "{\"ok\":true,\"note\":\"fetching BepInEx binaries\"}"
                                   : "{\"ok\":false,\"error\":\"a fetch/scan is already running\"}";
                }
                if (action == "select")
                {
                    string model = Convert.ToString(d.ContainsKey("model") ? d["model"] : "auto") ?? "auto";
                    if (model.Length == 0) model = "auto";
                    _cfgSelected.Value = model;
                    if (ProcAlive() && _healthy)
                    {
                        int port = _cfgPort != null ? _cfgPort.Value : 8339;
                        var body = new Dictionary<string, object> { ["model"] = model };
                        string resp = HttpPostJson("http://127.0.0.1:" + port + "/serve/load", Json.Write(body), 300000);
                        bool ok = false;
                        try
                        {
                            var rd = resp != null ? Json.Parse(resp) as Dictionary<string, object> : null;
                            ok = rd != null && rd.ContainsKey("ok") && Convert.ToBoolean(rd["ok"]);
                        }
                        catch (Exception) { }
                        if (ok)
                        {
                            Save("hot-swapped to " + model);
                            MaybeFetchModels(true);
                            return "{\"ok\":true,\"note\":\"hot-swapped to " + Json.Write(model).Trim() + "\"}";
                        }
                        // hot-swap failed (load error / unknown id) — note it; the
                        // selection is saved, a restart applies it cleanly
                        Save("hot-swap to " + model + " failed — applies on restart");
                        try { LLMNPCPlugin.Log?.LogWarning("kk-serve hot-swap failed: " + (resp ?? "no response")); } catch (Exception) { }
                        return "{\"ok\":true,\"note\":\"saved, but the live hot-swap failed — restart to apply (" + Json.Write(model).Trim() + ")\"}";
                    }
                    Save("selection saved: " + model);
                    return "{\"ok\":true,\"note\":\"saved — applies on the next start\"}";
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