// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SafeHttp — retry wrapper for LLM HTTP calls with exponential backoff,
// proper resource disposal, and TLS validation.
//
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace KKLLMNPC
{
    /// <summary>
    /// Retry-aware HTTP helpers for LLM endpoint calls.
    /// Wraps HttpWebRequest with exponential backoff retry (default 3 attempts,
    /// 1s/2s delays), proper stream disposal, and error-body reporting.
    /// </summary>
    internal static class SafeHttp
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
        private const int DefaultRetries = 3;
        private const int BaseDelayMs = 1000;
        private const int MaxDelayMs = 30000;
        // Cap on how much of an error body we read back for diagnostics.
        private const int MaxErrorBodyBytes = 2048;

        // POST with retry and proper resource disposal.
        // Returns the response body as string, or null on failure.
        //
        // Each attempt builds a FRESH HttpWebRequest. An HttpWebRequest that has
        // been submitted can never be resent: a second GetRequestStream()/
        // GetResponse() throws InvalidOperationException("request started")
        // without touching the network. Reusing one request object across the
        // retry loop therefore made every retry a silent no-op that still paid
        // the backoff sleep — the opposite of what this class promises.
        //
        // Retry policy: transport faults, 5xx, 408 and 429 are retried. Other 4xx
        // (400 bad request, 401/403 bad key, 404 unknown model, 413 too large)
        // are permanent for the same request and fail fast — retrying them just
        // burns seconds of the decision thread and hammers the server.
        //
        // onRetry  — called before each retry (delay is already applied)
        // onError  — called once with the final failure, including any response body
        public static string Post(string url, string body, string apiKey = null,
            int retries = DefaultRetries, TimeSpan? timeout = null,
            int? maxResponseBytes = null, Action<string> onRetry = null,
            Action<string> onError = null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string lastError = null;

            for (int attempt = 0; attempt < retries; attempt++)
            {
                if (attempt > 0)
                {
                    int delay = BackoffMs(attempt);
                    onRetry?.Invoke("attempt " + (attempt + 1) + "/" + retries + " in " + delay + "ms: " + lastError);
                    Thread.Sleep(delay);
                }

                // HttpWebRequest is not IDisposable on this profile — Abort() in a
                // finally is the documented way to release the connection.
                var req = CreateRequest(url, "POST", apiKey, timeout ?? DefaultTimeout);
                try
                {
                    req.ContentLength = bytes.Length;
                    using (var requestStream = req.GetRequestStream())
                        requestStream.Write(bytes, 0, bytes.Length);

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        int status = (int)resp.StatusCode;
                        if (status != 200)
                        {
                            lastError = "HTTP " + status + " " + Ascii(resp.StatusDescription) + ErrorDetail(resp);
                            if (!IsRetryableStatus(status)) { onError?.Invoke(lastError); return null; }
                            continue;
                        }
                        using (var stream = resp.GetResponseStream())
                            return stream == null ? null : ReadStream(stream, maxResponseBytes);
                    }
                }
                catch (WebException ex)
                {
                    lastError = DescribeWebException(ex);
                    if (!IsRetryableWebException(ex)) { onError?.Invoke(lastError); return null; }
                }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name + ": " + ex.Message;
                }
                finally { try { req.Abort(); } catch (Exception) { } }
            }

            onError?.Invoke(lastError ?? "request failed");
            return null;
        }

        // GET with retry. Same retry policy as Post.
        public static string Get(string url, string apiKey = null,
            int retries = DefaultRetries, TimeSpan? timeout = null,
            int? maxResponseBytes = null, Action<string> onRetry = null,
            Action<string> onError = null)
        {
            string lastError = null;

            for (int attempt = 0; attempt < retries; attempt++)
            {
                if (attempt > 0)
                {
                    int delay = BackoffMs(attempt);
                    onRetry?.Invoke("attempt " + (attempt + 1) + "/" + retries + " in " + delay + "ms: " + lastError);
                    Thread.Sleep(delay);
                }

                var req = CreateRequest(url, "GET", apiKey, timeout ?? TimeSpan.FromSeconds(5));
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        int status = (int)resp.StatusCode;
                        if (status != 200)
                        {
                            lastError = "HTTP " + status + " " + Ascii(resp.StatusDescription) + ErrorDetail(resp);
                            if (!IsRetryableStatus(status)) { onError?.Invoke(lastError); return null; }
                            continue;
                        }
                        using (var stream = resp.GetResponseStream())
                            return stream == null ? null : ReadStream(stream, maxResponseBytes);
                    }
                }
                catch (WebException ex)
                {
                    lastError = DescribeWebException(ex);
                    if (!IsRetryableWebException(ex)) { onError?.Invoke(lastError); return null; }
                }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name + ": " + ex.Message;
                }
                finally { try { req.Abort(); } catch (Exception) { } }
            }

            onError?.Invoke(lastError ?? "request failed");
            return null;
        }

        private static int BackoffMs(int attempt)
        {
            // attempt is 1-based here: 1 -> 1s, 2 -> 2s, 3 -> 4s ...
            int shift = Math.Min(attempt - 1, 20);
            return Math.Min(BaseDelayMs << shift, MaxDelayMs);
        }

        // Retryable: transient server-side or rate-limit conditions.
        private static bool IsRetryableStatus(int status)
        {
            if (status >= 500) return true;
            return status == 408 /* RequestTimeout */ || status == 429 /* TooManyRequests */;
        }

        private static bool IsRetryableWebException(WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp != null)
            {
                using (resp)
                    return IsRetryableStatus((int)resp.StatusCode);
            }
            // No response: the request never completed (connect/timeout/DNS/reset).
            // Those are worth another shot.
            return ex.Status == WebExceptionStatus.Timeout
                || ex.Status == WebExceptionStatus.ConnectFailure
                || ex.Status == WebExceptionStatus.ConnectionClosed
                || ex.Status == WebExceptionStatus.KeepAliveFailure
                || ex.Status == WebExceptionStatus.NameResolutionFailure
                || ex.Status == WebExceptionStatus.PipelineFailure
                || ex.Status == WebExceptionStatus.ReceiveFailure
                || ex.Status == WebExceptionStatus.SendFailure
                || ex.Status == WebExceptionStatus.RequestCanceled;
        }

        // A WebException carries the failing response for non-2xx replies; surface
        // it so callers can see WHY (context length exceeded, unknown model, ...).
        private static string DescribeWebException(WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp == null)
                return "WebException(" + ex.Status + "): " + Ascii(ex.Message);

            using (resp)
            {
                int status = (int)resp.StatusCode;
                string detail = ErrorDetail(resp);
                try
                {
                    if (ex.Status == WebExceptionStatus.ProtocolError)
                        return "HTTP " + status + " " + Ascii(resp.StatusDescription) + detail;
                }
                catch (Exception) { }
                return "WebException(" + ex.Status + ") HTTP " + status + detail;
            }
        }

        // Read a bounded, sanitized slice of a response body for the log.
        private static string ErrorDetail(HttpWebResponse resp)
        {
            try
            {
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return "";
                    var ms = new MemoryStream();
                    var buf = new byte[512];
                    int total = 0, n;
                    while (total < MaxErrorBodyBytes && (n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        int room = MaxErrorBodyBytes - total;
                        ms.Write(buf, 0, Math.Min(n, room));
                        total += n;
                    }
                    if (total == 0) return "";
                    string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length).Trim();
                    if (text.Length == 0) return "";
                    return " — " + Ascii(text);
                }
            }
            catch (Exception) { return ""; }
        }

        // LLM error bodies are echoed into a UTF-8 log; strip anything that isn't
        // printable ASCII so a malformed body can't corrupt the log file.
        private static string Ascii(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n' || c == '\r' || c == '\t') { sb.Append(' '); continue; }
                sb.Append(c >= 32 && c < 127 ? c : '?');
            }
            return sb.ToString();
        }

        private static HttpWebRequest CreateRequest(string url, string method, string apiKey, TimeSpan timeout)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.ContentType = "application/json";
            req.Timeout = (int)timeout.TotalMilliseconds;
            req.ReadWriteTimeout = (int)timeout.TotalMilliseconds;
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers["Authorization"] = "Bearer " + apiKey;
            return req;
        }

        private static string ReadStream(Stream stream, int? maxBytes)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192];
                int total = 0, n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (maxBytes.HasValue && total > maxBytes.Value) break;
                    ms.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }
}
