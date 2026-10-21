// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ServeArgs — pure logic for the kk-serve companion process control: location
// parsing, command-line building, log ring, endpoint gate. No game/BepInEx
// references so the headless test suite can compile it directly (the swappable
// process/thread side lives in KKServe.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKLLMNPC
{
    internal static class ServeArgs
    {
        // ---- locations: ';'- or newline-separated dirs, trimmed + deduped ----
        public static List<string> ParseLocations(string raw)
        {
            var out_ = new List<string>();
            var seen = new HashSet<string>();
            if (string.IsNullOrEmpty(raw)) return out_;
            var chunks = raw.Replace("\r", "").Replace("\n", ";").Split(';');
            foreach (var c in chunks)
            {
                string d = c.Trim();
                if (d.Length == 0 || seen.Contains(d)) continue;
                seen.Add(d);
                out_.Add(d);
            }
            return out_;
        }

        // Configured locations first, then implicit ones (deduped, order kept).
        public static List<string> MergeLocations(List<string> configured, List<string> implicitDirs)
        {
            var out_ = new List<string>();
            var seen = new HashSet<string>();
            foreach (var d in configured ?? new List<string>())
            {
                if (d.Length == 0 || seen.Contains(d)) continue;
                seen.Add(d); out_.Add(d);
            }
            foreach (var d in implicitDirs ?? new List<string>())
            {
                if (d.Length == 0 || seen.Contains(d)) continue;
                seen.Add(d); out_.Add(d);
            }
            return out_;
        }

        // ---- device → python ----
        // A mixed-vendor box (CPU+iGPU, a ROCm card, a CUDA card) needs a
        // vendor-matched python per device: a ROCM-BUILT torch only sees the
        // AMD cards, a CUDA one only the NVIDIA. `deviceBackend` is the
        // selected device's own backend from the live /serve/devices list
        // ("" = unknown → fallback).
        public static string PythonForDevice(string device, string deviceBackend,
            string fallback, string cpuPy, string cudaPy, string rocmPy)
        {
            if (string.IsNullOrWhiteSpace(device) || device == "auto") return fallback;
            switch (device)
            {
                case "cpu":
                    return !string.IsNullOrWhiteSpace(cpuPy) ? cpuPy : fallback;
            }
            if (device.StartsWith("cuda", StringComparison.Ordinal))
            {
                if (deviceBackend == "rocm") return !string.IsNullOrWhiteSpace(rocmPy) ? rocmPy : fallback;
                if (deviceBackend == "cuda") return !string.IsNullOrWhiteSpace(cudaPy) ? cudaPy : fallback;
                return fallback;
            }
            return fallback;
        }

        // ---- python candidates ----
        // Host-filesystem kind for python discovery/spawn. Three shapes:
        //   windows — native Windows: interpreters are python.exe/py.exe, bare
        //             PATH names often fail under Unity's Process.Start.
        //   wine    — Steam Proton: the game's mono REPORTS Windows, but the
        //             real filesystem is unix and bare names don't resolve in
        //             wine's CreateProcess at all — only absolute executable
        //             paths (wine unix-execs ELF files given with a path).
        //   unix    — native linux/mac hosting the game.
        public static string HostKind(bool osReportsWindows, bool binShExists)
        {
            if (osReportsWindows) return binShExists ? "wine" : "windows";
            return "unix";
        }

        public static bool IsAbsolute(string candidate)
        {
            // unix-absolute or windows drive path
            if (string.IsNullOrEmpty(candidate)) return false;
            if (candidate[0] == '/') return true;
            if (candidate.Length > 1 && candidate[1] == ':') return true;
            return false;
        }

        // Ordered interpreters for a host kind. `resolvedUnix` came from a
        // `command -v` probe through the wine host shell (proton only).
        // Under `wine` ANY failure falls through: an absolute configured path is
        // tried first (with its mapped/strip alternate spelling) but never ends
        // the ladder — the drive-letter mapping folklore (Z -> Linux root …) is
        // exactly why several spellings of the same interpreter exist.
        public static List<string> PythonCandidates(string bin, string hostKind, string resolvedUnix)
        {
            // mcs in the game profile rejects local functions — plain ladder
            var order = new List<string>();
            string b = (bin ?? "").Trim();
            if (b.Length > 0 && IsAbsolute(b))
            {
                order.Add(b);
                if (hostKind == "wine")
                {
                    // mapped form <-> unix form of the SAME interpreter
                    if (b[1] == ':') order.Add(b.Substring(2, b.Length - 2));
                    else order.Add("Z:" + b);
                }
            }
            if (hostKind == "windows")
            {
                if (b.Length > 0) order.Add(b);   // literal alias if it really exists
                order.Add("python");
                order.Add("py");
            }
            else if (hostKind == "wine")
            {
                // bare names cannot resolve under wine — absolutes only
                order.Add(resolvedUnix);
                order.Add("/usr/bin/python3");
                order.Add("/usr/local/bin/python3");
            }
            else
            {
                order.Add(b);
                order.Add(resolvedUnix);
                order.Add("python3");
                order.Add("python");
                order.Add("/usr/bin/python3");
                order.Add("/usr/local/bin/python3");
            }
            var out_ = new List<string>();
            var seen = new HashSet<string>();
            foreach (var c in order)
            {
                if (string.IsNullOrWhiteSpace(c) || seen.Contains(c)) continue;
                seen.Add(c);
                out_.Add(c);
            }
            return out_;
        }

        // PATH parsing for python discovery (';' on Windows, ':' on unix,
        // quotes stripped). Pure — tested.
        public static List<string> ParsePathDirs(string pathEnv, bool windows)
        {
            var out_ = new List<string>();
            var seen = new HashSet<string>();
            if (string.IsNullOrEmpty(pathEnv)) return out_;
            char sep = windows ? ';' : ':';
            foreach (var chunk in pathEnv.Split(sep))
            {
                string d = chunk.Trim().Trim('"');
                if (d.Length == 0) continue;
                string norm = windows ? d.ToLowerInvariant() : d;
                if (seen.Contains(norm)) continue;
                seen.Add(norm);
                out_.Add(d);
            }
            return out_;
        }

        // ---- fetch_assets.py command line ----
        public static List<string> BuildFetchArgs(string script, string gameDir,
            string model, bool bepinex, string variant, bool listMode)
        {
            var args = new List<string> { script };
            if (listMode) args.Add("--list");
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                args.Add("--game-dir");
                args.Add(gameDir);
            }
            if (!string.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model);
            }
            if (bepinex)
            {
                args.Add("--bepinex");
                if (!string.IsNullOrWhiteSpace(variant))
                {
                    args.Add("--bepinex-variant");
                    args.Add(variant);
                }
            }
            return args;
        }

        // ---- command line ----
        // Returns the argument list for: <python> <script> <args...>.
        // selected: "auto" or a discovered id/path. Locations spread into
        // repeated --scan-dir flags. vramLimitGb<=0 omitted. `extra` is the
        // operator's own text appended verbatim (their quoting).
        public static List<string> BuildArgs(string script, int port, string selected,
            List<string> locations, float vramLimitGb, string extra, string host, string device)
        {
            var args = new List<string> { script };
            args.Add("--port");
            args.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--host");
            args.Add(string.IsNullOrEmpty(host) ? "127.0.0.1" : host);
            args.Add("--model");
            args.Add(string.IsNullOrEmpty(selected) ? "auto" : selected);
            if (!string.IsNullOrEmpty(device) && device != "auto")
            {
                args.Add("--device");
                args.Add(device);
            }
            if (locations != null)
                foreach (var d in locations)
                {
                    args.Add("--scan-dir");
                    args.Add(d);
                }
            if (vramLimitGb > 0f)
                args.Add("--vram-limit-gb");
            if (vramLimitGb > 0f)
                args.Add(vramLimitGb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--log-level");
            args.Add("info");
            if (!string.IsNullOrWhiteSpace(extra)) args.Add(extra.Trim());
            return args;
        }

        // Quote one argument for ProcessStartInfo.Arguments (Windows-style
        // quoting is what mono's Process uses on all platforms it supports).
        public static string Quote(string arg)
        {
            if (arg == null) return "\"\"";
            bool needs = arg.Length == 0 || arg.IndexOf(' ') >= 0 || arg.IndexOf('"') >= 0;
            if (!needs) return arg;
            var sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '"') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string QuoteJoin(List<string> args)
        {
            var sb = new StringBuilder(args.Count * 16);
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Quote(args[i]));
            }
            return sb.ToString();
        }

        // ---- endpoint gate ----
        // The NPCInstance LLM POSTs consult this: when the mod manages kk-serve
        // and it is confirmed healthy, calls flow there regardless of
        // [LLM].Endpoint (opt-in AutoEndpoint). Null = use the configured one.
        public static string EndpointForLoopback(bool autoEndpoint, bool healthy, int port)
        {
            if (!autoEndpoint || !healthy || port <= 0) return null;
            return "http://127.0.0.1:" + port + "/v1/chat/completions";
        }

        // Find kk_serve.py: explicit config dir/file first, then practical
        // relative spots. Returns the script path or "" (with candidates logged).
        public static string FindScript(string serveDir, string pluginDir, string cwd)
        {
            var cands = new List<string>();
            string sd = (serveDir ?? "").Trim();
            if (sd.Length > 0)
            {
                if (sd.EndsWith("kk_serve.py", StringComparison.OrdinalIgnoreCase)) cands.Add(sd);
                else cands.Add(Path.Combine(sd, "kk_serve.py"));
            }
            foreach (var b in new[] { pluginDir, cwd })
            {
                if (string.IsNullOrEmpty(b)) continue;
                cands.Add(Path.Combine(b, "kk_serve.py"));
                cands.Add(Path.Combine(b, "serve", "kk_serve.py"));
            }
            if (!string.IsNullOrEmpty(pluginDir))
            {
                string up = Path.GetDirectoryName(Path.GetDirectoryName(pluginDir));
                if (!string.IsNullOrEmpty(up)) cands.Add(Path.Combine(up, "serve", "kk_serve.py"));
            }
            foreach (var c in cands)
                try { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; } catch (Exception) { }
            return "";
        }
    }

    // Small thread-safe log ring for the child process output (WebUI tail).
    internal sealed class LineRing
    {
        private readonly object _lock = new object();
        private readonly string[] _lines;
        private int _count, _head;

        public LineRing(int capacity) { _lines = new string[Math.Max(8, capacity)]; }

        public void Add(string line)
        {
            if (line == null) return;
            // keep it bounded per line: kk-serve logs are short; giant payloads
            // (someone curling a perception) shouldn't eat the panel
            if (line.Length > 400) line = line.Substring(0, 400) + "…";
            lock (_lock)
            {
                _lines[_head] = line;
                _head = (_head + 1) % _lines.Length;
                if (_count < _lines.Length) _count++;
            }
        }

        // oldest-first tail of up to n lines
        public List<string> Tail(int n)
        {
            lock (_lock)
            {
                n = Math.Min(n, _count);
                var out_ = new List<string>(n);
                int len = _lines.Length;
                for (int i = 0; i < n; i++)
                {
                    // oldest = head - count (+ i); the double % handles negatives
                    int idx = ((_head - _count + i) % len + len) % len;
                    out_.Add(_lines[idx]);
                }
                return out_;
            }
        }

        public void Clear() { lock (_lock) { _count = 0; _head = 0; } }
        public int Count { get { lock (_lock) { return _count; } } }
    }
}