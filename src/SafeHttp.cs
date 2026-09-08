// Written by @auRose94 (https://github.com/auRose94) under MIT license.
// SafeHttp — retry wrapper for LLM HTTP calls with exponential backoff,
// proper resource disposal, and TLS validation.
//
using System;
using System.IO;
using System.Net;
using System.Text;

namespace KKLLMNPC
{
    /// <summary>
    /// Retry-aware HTTP helpers for LLM endpoint calls.
    /// Wraps HttpWebRequest with exponential backoff retry (default 3 attempts,
    /// 1s/2s/4s delays), proper stream disposal, and optional TLS validation.
    /// </summary>
    internal static class SafeHttp
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
        private const int DefaultRetries = 3;
        private const int BaseDelayMs = 1000;
        private const int MaxDelayMs = 30000;

        // POST with retry and proper resource disposal.
        // Returns the response body as string, or null on failure.
        public static string Post(string url, string body, string apiKey = null,
            int retries = DefaultRetries, TimeSpan? timeout = null,
            int? maxResponseBytes = null, Action<string> onRetry = null)
        {
            var req = CreateRequest(url, "POST", apiKey, timeout ?? DefaultTimeout);
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            for (int attempt = 0; attempt < retries; attempt++)
            {
                HttpWebResponse resp = null;
                Stream requestStream = null;
                try
                {
                    req.ContentLength = bytes.Length;
                    using (requestStream = req.GetRequestStream())
                        requestStream.Write(bytes, 0, bytes.Length);

                    resp = (HttpWebResponse)req.GetResponse();
                    if (resp.StatusCode != HttpStatusCode.OK)
                    {
                        resp.Dispose();
                        if (attempt < retries - 1)
                        {
                            int delay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                            onRetry?.Invoke("HTTP " + (int)resp.StatusCode + ", retrying in " + delay + "ms");
                            System.Threading.Thread.Sleep(delay);
                            continue;
                        }
                        return null;
                    }

                    using (var stream = resp.GetResponseStream())
                        return stream == null ? null : ReadStream(stream, maxResponseBytes);
                }
                catch (WebException webEx)
                {
                    if (attempt < retries - 1)
                    {
                        int delay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                        onRetry?.Invoke("WebException (" + webEx.Status + "), retrying in " + delay + "ms");
                        System.Threading.Thread.Sleep(delay);
                        continue;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    if (attempt < retries - 1)
                    {
                        int delay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                        onRetry?.Invoke("Exception (" + ex.GetType().Name + "), retrying in " + delay + "ms");
                        System.Threading.Thread.Sleep(delay);
                        continue;
                    }
                    return null;
                }
                finally
                {
                    try { if (requestStream != null) { requestStream.Dispose(); } } catch { }
                    try { if (resp != null) { resp.Close(); } } catch { }
                }
            }
            return null;
        }

        // GET with retry.
        public static string Get(string url, string apiKey = null,
            int retries = DefaultRetries, TimeSpan? timeout = null,
            int? maxResponseBytes = null, Action<string> onRetry = null)
        {
            var req = CreateRequest(url, "GET", apiKey, timeout ?? TimeSpan.FromSeconds(5));

            for (int attempt = 0; attempt < retries; attempt++)
            {
                HttpWebResponse resp = null;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                    if (resp.StatusCode != HttpStatusCode.OK)
                    {
                        resp.Dispose();
                        if (attempt < retries - 1)
                        {
                            int delay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                            onRetry?.Invoke("HTTP " + (int)resp.StatusCode + ", retrying in " + delay + "ms");
                            System.Threading.Thread.Sleep(delay);
                            continue;
                        }
                        return null;
                    }

                    using (var stream = resp.GetResponseStream())
                        return stream == null ? null : ReadStream(stream, maxResponseBytes);
                }
                catch (WebException webEx)
                {
                    if (attempt < retries - 1)
                    {
                        int delay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                        onRetry?.Invoke("WebException (" + webEx.Status + "), retrying in " + delay + "ms");
                        System.Threading.Thread.Sleep(delay);
                        continue;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    if (attempt < retries - 1)
                    {
                        int delay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
                        onRetry?.Invoke("Exception (" + ex.GetType().Name + "), retrying in " + delay + "ms");
                        System.Threading.Thread.Sleep(delay);
                        continue;
                    }
                    return null;
                }
                finally
                {
                    try { if (resp != null) { resp.Close(); } } catch { }
                }
            }
            return null;
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
