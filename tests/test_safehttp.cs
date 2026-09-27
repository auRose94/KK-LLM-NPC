// Tests for SafeHttp — retry/backoff behaviour against a real local HTTP server.
//
// Why this file exists: SafeHttp used to build ONE HttpWebRequest outside its
// retry loop and call GetRequestStream()/GetResponse() on it repeatedly. A
// submitted HttpWebRequest can never be resent — the second attempt throws
// InvalidOperationException("request started") without touching the network —
// so every retry was a silent no-op that still paid the backoff sleep. These
// tests count actual server hits to keep that from regressing.
//
// Pure C# (System.Net only) — no Unity/BepInEx. Needs a loopback listener, so
// it self-skips if HttpListener can't bind.
//
// Build: mcs -target:exe -out:/tmp/t_http.exe tests/test_safehttp.cs src/SafeHttp.cs && mono /tmp/t_http.exe
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using KKLLMNPC;

static class SafeHttpTests
{
    static int _passed, _failed;

    static void Assert(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("  PASS: " + name); }
        else { _failed++; Console.WriteLine("  FAIL: " + name); }
    }

    // A scripted one-shot server: replies with the queued (status, body) pairs
    // in order, then keeps answering 200 "done" so an over-eager retry loop
    // still terminates.
    class ScriptedServer : IDisposable
    {
        HttpListener _listener;
        Thread _thread;
        volatile bool _stop;
        public int Port;
        public string Prefix;
        public readonly List<string> Received = new List<string>();
        public readonly List<int> Statuses = new List<int>();
        readonly Queue<int[]> _script = new Queue<int[]>();

        public ScriptedServer(int port, params int[] statusSequence)
        {
            Port = port;
            Prefix = "http://127.0.0.1:" + port + "/";
            foreach (var s in statusSequence) _script.Enqueue(new[] { s });
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(Prefix);
                _listener.Start();
            }
            catch (Exception e)
            {
                Dispose();
                throw new Exception("bind failed on port " + port + ": " + e.Message, e);
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "test-http-" + port };
            _thread.Start();
        }

        void Loop()
        {
            while (!_stop)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { return; }
                int status = 200;
                string body = "done";
                lock (_script)
                {
                    Received.Add("hit");
                    if (_script.Count > 0) status = _script.Dequeue()[0];
                    Statuses.Add(status);
                }
                try
                {
                    var buf = new byte[4096];
                    int n = ctx.Request.InputStream.Read(buf, 0, buf.Length);
                    lock (Received) { Received[Received.Count - 1] = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)); }
                }
                catch (Exception) { }
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    if (status == 400) body = "{\"error\":\"context length exceeded\"}";
                    bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch (Exception) { }
            }
        }

        public int Hits { get { lock (_script) return Statuses.Count; } }

        public void Dispose()
        {
            _stop = true;
            try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch (Exception) { }
        }
    }

    static int _port = 18300 + (int)(DateTime.Now.Ticks % 400);
    static string NextUrl() { return "http://127.0.0.1:" + (_port++) + "/v1/chat/completions"; }

    // ---- the regression that started all this ----

    static void PostRetriesActuallyReachTheServer()
    {
        // Two 503s then 200: the server must be hit 3 times and the 200 body returned.
        using (var srv = new ScriptedServer(_port++, 503, 503))
        {
            var retries = new List<string>();
            string body = SafeHttp.Post(NextUrl2(srv), "{\"a\":1}", null, 3,
                TimeSpan.FromSeconds(5), null, retries.Add, null);
            Assert(body == "done", "POST: recovers after transient 503s (body=" + (body ?? "null") + ")");
            Assert(srv.Hits == 3, "POST: server actually received 3 attempts (got " + srv.Hits + ")");
            Assert(retries.Count == 2, "POST: onRetry fired once per retry (got " + retries.Count + ")");
        }
    }

    static string NextUrl2(ScriptedServer srv) { return srv.Prefix + "v1/chat/completions"; }

    static void PostFailsFastOnPermanent4xx()
    {
        // A 400 will never succeed on retry. We must not burn backoff sleeps on it.
        using (var srv = new ScriptedServer(_port++, 400))
        {
            string err = null;
            var sw = StopwatchStart();
            string body = SafeHttp.Post(NextUrl2(srv), "{\"a\":1}", null, 3,
                TimeSpan.FromSeconds(5), null, null, e => err = e);
            double ms = sw();
            Assert(body == null, "POST: 400 returns null");
            Assert(srv.Hits == 1, "POST: 400 is not retried (hits=" + srv.Hits + ", want 1)");
            Assert(err != null && err.Contains("400"), "POST: 400 status reported (err=" + (err ?? "null") + ")");
            Assert(err != null && err.Contains("context length exceeded"),
                "POST: error BODY surfaced for diagnosis (err=" + (err ?? "null") + ")");
            Assert(ms < 2500, "POST: 400 fails fast, no backoff sleep (" + (int)ms + "ms)");
        }
    }

    static void PostRetriesWhenServerIsDown()
    {
        // Nothing listening: connection refused every time. Should retry, give up, report.
        int deadPort = _port++;
        string err = null;
        int tries = 0;
        string body = SafeHttp.Post("http://127.0.0.1:" + deadPort + "/", "{}", null, 2,
            TimeSpan.FromSeconds(2), null, r => tries++, e => err = e);
        Assert(body == null, "POST: dead server returns null");
        Assert(err != null, "POST: dead server reports an error");
    }

    static void PostSuccessNeedsNoRetry()
    {
        using (var srv = new ScriptedServer(_port++))
        {
            var retries = new List<string>();
            string body = SafeHttp.Post(NextUrl2(srv), "{\"hello\":\"world\"}", null, 3,
                TimeSpan.FromSeconds(5), null, retries.Add, null);
            Assert(body == "done", "POST: happy path returns body");
            Assert(srv.Hits == 1, "POST: happy path is a single request (hits=" + srv.Hits + ")");
            Assert(retries.Count == 0, "POST: no retry callback on success");
            Assert(srv.Hits == 1 && srv.Statuses[0] == 200, "POST: server saw one 200");
        }
    }

    static void PostSendsBodyAndAuthHeader()
    {
        using (var srv = new ScriptedServer(_port++))
        {
            SafeHttp.Post(NextUrl2(srv), "{\"model\":\"x\"}", "secret-token", 1, TimeSpan.FromSeconds(5));
            Assert(srv.Received.Count == 1 && srv.Received[0].Contains("\"model\""),
                "POST: request body reaches the server (" + (srv.Received.Count > 0 ? srv.Received[0] : "none") + ")");
        }
    }

    static void GetRetriesActuallyReachTheServer()
    {
        using (var srv = new ScriptedServer(_port++, 503))
        {
            string body = SafeHttp.Get(NextUrl2(srv), null, 2, TimeSpan.FromSeconds(5));
            Assert(body == "done", "GET: recovers after transient 503 (body=" + (body ?? "null") + ")");
            Assert(srv.Hits == 2, "GET: server actually received 2 attempts (got " + srv.Hits + ")");
        }
    }

    static void GetFailsFastOn404()
    {
        using (var srv = new ScriptedServer(_port++, 404))
        {
            string err = null;
            string body = SafeHttp.Get(NextUrl2(srv), null, 3, TimeSpan.FromSeconds(5), null, null, e => err = e);
            Assert(body == null, "GET: 404 returns null");
            Assert(srv.Hits == 1, "GET: 404 not retried (hits=" + srv.Hits + ")");
            Assert(err != null && err.Contains("404"), "GET: 404 reported (err=" + (err ?? "null") + ")");
        }
    }

    static void GetSuccess()
    {
        using (var srv = new ScriptedServer(_port++))
        {
            string body = SafeHttp.Get(NextUrl2(srv), null, 2, TimeSpan.FromSeconds(5));
            Assert(body == "done", "GET: happy path returns body");
            Assert(srv.Hits == 1, "GET: happy path is a single request");
        }
    }

    // Response size cap still honoured after the retry rewrite.
    static void GetRespectsMaxResponseBytes()
    {
        using (var srv = new ScriptedServer(_port++))
        {
            string body = SafeHttp.Get(NextUrl2(srv), null, 1, TimeSpan.FromSeconds(5), 2);
            Assert(body != null && body.Length <= 8192, "GET: maxResponseBytes caps the read (len="
                + (body == null ? -1 : body.Length) + ")");
        }
    }

    static Func<double> StopwatchStart()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        return () => { return sw.Elapsed.TotalMilliseconds; };
    }

    static int RunAll()
    {
        Console.WriteLine("== SafeHttp Tests ==");
        try
        {
            PostRetriesActuallyReachTheServer();
            PostFailsFastOnPermanent4xx();
            PostRetriesWhenServerIsDown();
            PostSuccessNeedsNoRetry();
            PostSendsBodyAndAuthHeader();
            GetRetriesActuallyReachTheServer();
            GetFailsFastOn404();
            GetSuccess();
            GetRespectsMaxResponseBytes();
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine("  FAIL: harness error — " + e.Message);
            Console.WriteLine("         (HttpListener unavailable; tests need a loopback socket)");
        }
        Console.WriteLine();
        Console.WriteLine("Results: " + _passed + " passed, " + _failed + " failed");
        return _failed;
    }

    static int Main()
    {
        // 0 = pass, 1 = fail. (Not "exit code = failure count": 256 failures would
        // wrap to 0 and report success.)
        return RunAll() == 0 ? 0 : 1;
    }
}
