// Tests for ServeArgs.cs — the pure kk-serve process-control logic (locations,
// command line, quoting, endpoint gate, log ring, script search).
namespace KKLLMNPC.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class ServeTests
    {
        static int _failed;

        static void Eq(object want, object got, string what)
        {
            bool ok = (want == null) ? got == null : want.Equals(got);
            if (!ok)
            {
                _failed++;
                Console.WriteLine("  FAIL: " + what + " — got " + (got == null ? "null" : got));
            }
            else Console.WriteLine("  PASS: " + what);
        }

        static void StringAssertContains(string needle, string hay, string what)
        {
            if (hay.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                _failed++;
                Console.WriteLine("  FAIL: " + what);
            }
            else Console.WriteLine("  PASS: " + what);
        }

        static void Contains(List<string> a, string flag, string value)
        {
            for (int i = 0; i < a.Count - 1; i++)
                if (a[i] == flag && a[i + 1] == value)
                {
                    Console.WriteLine("  PASS: " + flag + " " + value);
                    return;
                }
            _failed++;
            Console.WriteLine("  FAIL: " + flag + " " + value + " missing");
        }

        static bool AnyStartsWith(List<string> a, string prefix)
        {
            foreach (var v in a) if (v != null && v.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        static void AssertNone(List<string> a, string x, string what)
        {
            bool has = false;
            foreach (var v in a) if (v == x) has = true;
            if (has) { _failed++; Console.WriteLine("  FAIL: " + what); }
            else Console.WriteLine("  PASS: " + what);
        }

        static int CountOf(List<string> a, string x)
        {
            int n = 0;
            foreach (var v in a) if (v == x) n++;
            return n;
        }

        public static void Main()
        {
            // ---- locations ----
            var l = ServeArgs.ParseLocations(" /a/b \n;c/d;;  /a/b;\r\ne/f");
            Eq(3, l.Count, "3 locations parsed");
            Eq("/a/b", l[0], "first trimmed");
            Eq("c/d", l[1], "second");
            Eq("e/f", l[2], "third — no dupes");
            Eq(0, ServeArgs.ParseLocations(null).Count, "null -> empty");
            Eq(0, ServeArgs.ParseLocations("   ;  ").Count, "blank chunks skipped");

            // ---- command line ----
            var a = ServeArgs.BuildArgs("/x/kk_serve.py", 8339, null,
                new List<string>(), 0f, "", "127.0.0.1", null);
            Eq("/x/kk_serve.py", a[0], "script first");
            Eq("--log-level", a[a.Count - 2], "log-level before the level value");
            Contains(a, "--model", "auto");               // null selection -> auto
            Contains(a, "--port", "8339");
            Eq(false, a.Contains("--scan-dir"), "no locations -> no scan-dir");
            Eq(false, a.Contains("--vram-limit-gb"), "limit 0 -> omitted");

            var b = ServeArgs.BuildArgs("/x/kk_serve.py", 8339, "C:/models/My Model",
                new List<string> { "L1", "L2" }, 12f, "--kv-reuse off", "127.0.0.1", null);
            Contains(b, "--model", "C:/models/My Model");
            Eq(2, CountOf(b, "--scan-dir"), "one scan-dir per location");
            Contains(b, "--vram-limit-gb", "12.0");
            Eq("--kv-reuse off", b[b.Count - 1], "extra args appended last, verbatim");

            var c = ServeArgs.BuildArgs("/x/kk_serve.py", 1, "auto", new List<string>(), -5f, "", "127.0.0.1", null);
            Eq(false, c.Contains("--vram-limit-gb"), "negative limit omitted");

            // ---- device selection ----
            var d1 = ServeArgs.BuildArgs("/x/kk_serve.py", 1, "auto", new List<string>(), 0f, "", "127.0.0.1", "cpu");
            Contains(d1, "--device", "cpu");
            var d2 = ServeArgs.BuildArgs("/x/kk_serve.py", 1, "auto", new List<string>(), 0f, "", "127.0.0.1", "auto");
            Eq(false, d2.Contains("--device"), "device auto omitted");
            Eq("/srv/cpu-venv/bin/python",
               ServeArgs.PythonForDevice("cpu", "cpu", "python3", "/srv/cpu-venv/bin/python", "", ""), "cpu python");
            Eq("fallback", ServeArgs.PythonForDevice("cpu", "cpu", "fallback", "", "", ""), "cpu python missing -> fallback");
            Eq("/srv/cuda/bin/python",
               ServeArgs.PythonForDevice("cuda:0", "cuda", "python3", "", "/srv/cuda/bin/python", ""), "cuda-back device -> cuda python");
            Eq("/srv/rocm/bin/python",
               ServeArgs.PythonForDevice("cuda:1", "rocm", "python3", "", "/srv/cuda/bin/python", "/srv/rocm/bin/python"), "rocm-back device -> rocm python");
            Eq("python3", ServeArgs.PythonForDevice("cuda:0", "", "python3", "", "/srv/cuda/bin/python", ""), "unknown backend -> fallback");
            Eq("python3", ServeArgs.PythonForDevice("auto", "rocm", "python3", "", "", ""), "auto -> fallback");

            // ---- fetch command line ----
            var f1 = ServeArgs.BuildFetchArgs("/x/fetch_assets.py", "/game", "Qwen/Qwen3.5-4B", false, "", false);
            Eq("/x/fetch_assets.py", f1[0], "fetch script first");
            Contains(f1, "--model", "Qwen/Qwen3.5-4B");
            Contains(f1, "--game-dir", "/game");
            var f2 = ServeArgs.BuildFetchArgs("/x/fetch_assets.py", "/game", "", true, "win_x64", false);
            Eq(true, f2.Contains("--bepinex"), "bepinex flag present");
            Contains(f2, "--bepinex-variant", "win_x64");
            var f3 = ServeArgs.BuildFetchArgs("/x/fetch_assets.py", "/game", "", false, "", true);
            Eq(true, f3.Contains("--list"), "catalog scan uses --list");
            Eq(false, f3.Contains("--model"), "scan carries no model");

            // ---- host kinds ----
            Eq("windows", ServeArgs.HostKind(true, false), "native windows");
            Eq("wine", ServeArgs.HostKind(true, true), "proton: windows-reported + /bin/sh");
            Eq("unix", ServeArgs.HostKind(false, true), "native unix");

            // ---- python candidates ----
            var pc = ServeArgs.PythonCandidates(null, "windows", null);
            Eq(2, pc.Count, "windows: python, py");
            Eq("python", pc[0], "windows python first");
            Eq("py", pc[1], "windows py second");
            var pc2 = ServeArgs.PythonCandidates("python3", "windows", null);
            Eq(3, pc2.Count, "windows: literal python3 tried then walk");
            var pc3 = ServeArgs.PythonCandidates("C:/venvs/kk/python.exe", "windows", "/usr/bin/python3");
            Eq(3, pc3.Count, "explicit path tried first, ladder continues");
            Eq("C:/venvs/kk/python.exe", pc3[0], "explicit path first");
            var pcw = ServeArgs.PythonCandidates(null, "wine", "/opt/venv/bin/python3");
            Eq("/opt/venv/bin/python3", pcw[0], "wine: resolved probe first");
            AssertNone(pcw, "python3", "wine never tries bare names");
            Eq(false, AnyStartsWith(ServeArgs.PythonCandidates("python3", "wine", null), "py"), "wine skips bare py aliases");
            var pcu = ServeArgs.PythonCandidates(null, "unix", "/opt/venv/bin/python3");
            Eq("/opt/venv/bin/python3", pcu[0], "unix: resolved first");
            Eq(true, pcu.Contains("python3"), "unix still tries bare python3");
            var absOnly = ServeArgs.PythonCandidates("/a/bin/p3", "wine", "/opt/venv/bin/python3");
            Eq(5, absOnly.Count, "wine absolute + alt spelling + resolved + /usr fallbacks");
            Eq("/a/bin/p3", absOnly[0], "configured first");
            Eq("Z:/a/bin/p3", absOnly[1], "unix form gains Z: prefix under wine");
            var mappedOnly = ServeArgs.PythonCandidates("Z:/a/bin/p3", "wine", "/opt/venv/bin/python3");
            Eq(5, mappedOnly.Count, "mapped form also offers the unix form");
            Eq("/a/bin/p3", mappedOnly[1], "Z: form strips back to the unix form");

            // ---- PATH parsing ----
            var pd = ServeArgs.ParsePathDirs("/a b;\"x y\";/a b; ", true);
            Eq(2, pd.Count, "windows PATH deduped+unquoted");
            Eq("x y", pd[1], "quotes stripped");
            var pd2 = ServeArgs.ParsePathDirs("/usr/bin:/opt/bin:/usr/bin", false);
            Eq(2, pd2.Count, "unix PATH deduped");
            Eq(0, ServeArgs.ParsePathDirs(null, true).Count, "null PATH");

            // ---- quoting ----
            Eq("plain", ServeArgs.Quote("plain"), "plain unquoted");
            Eq("\"a b\"", ServeArgs.Quote("a b"), "space -> quoted");
            Eq("\"\"", ServeArgs.Quote(""), "empty quoted");
            Eq("\"a\\\"b\"", ServeArgs.Quote("a\"b"), "embedded quote escaped");
            Eq("\"\"", ServeArgs.Quote(null), "null quoted");

            string j = ServeArgs.QuoteJoin(ServeArgs.BuildArgs("/p with space/kk.py", 1, "auto",
                new List<string> { "/m odels" }, 0f, "", "127.0.0.1", null));
            StringAssertContains("\"/p with space/kk.py\"", j, "script with space quoted");
            StringAssertContains("\"/m odels\"", j, "scan dir with space quoted");

            // ---- endpoint gate ----
            Eq(null, ServeArgs.EndpointForLoopback(false, true, 8339), "auto off -> null");
            Eq(null, ServeArgs.EndpointForLoopback(true, false, 8339), "unhealthy -> null");
            Eq(null, ServeArgs.EndpointForLoopback(true, true, 0), "no port -> null");
            Eq("http://127.0.0.1:8339/v1/chat/completions",
                ServeArgs.EndpointForLoopback(true, true, 8339), "healthy + on -> url");

            // ---- log ring ----
            var r = new LineRing(8);
            for (int i = 0; i < 20; i++) r.Add("line" + i);
            var tail = r.Tail(200);
            Eq(8, tail.Count, "capacity respected");
            for (int i = 0; i < 8; i++)
                Eq("line" + (12 + i), tail[i], "oldest-first order");
            Eq(3, r.Tail(3).Count, "tail n respected");
            r.Clear();
            Eq(0, r.Tail(10).Count, "cleared");

            var r2 = new LineRing(4);
            r2.Add(new string('x', 500));
            bool truncated = r2.Tail(10)[0].Length <= 401;
            Eq(true, truncated, "long line truncated");

            // ---- locations merge (configured + implicit) ----
            var merged = ServeArgs.MergeLocations(
                ServeArgs.ParseLocations("A;A\nB"),
                new List<string> { "B", "C" });
            Eq(3, merged.Count, "merge dedupes + keeps order");
            Eq("A", merged[0], "configured first");
            Eq("C", merged[2], "implicit appended");

            // ---- script search ----
            string tmp = Path.Combine(Path.GetTempPath(), "kkserve_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                Eq("", ServeArgs.FindScript(null, tmp, tmp), "missing script -> empty");
                string sub = Path.Combine(tmp, "serve");
                Directory.CreateDirectory(sub);
                string f = Path.Combine(sub, "kk_serve.py");
                File.WriteAllText(f, "# serve");
                Eq(f, ServeArgs.FindScript(null, tmp, tmp), "serve/ subdir found");
                string got = ServeArgs.FindScript(tmp, tmp, tmp);
                bool okDir = got == Path.Combine(tmp, "kk_serve.py") || got == f;
                Eq(true, okDir, "explicit dir wins or falls back to subdir");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
            }

            if (_failed == 0) Console.WriteLine("test_serve: all OK");
            else Console.WriteLine("test_serve: " + _failed + " failure(s)");
            Environment.Exit(_failed);
        }
    }
}