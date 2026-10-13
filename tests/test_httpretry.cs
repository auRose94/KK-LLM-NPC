// Unit tests for the HTTP-4xx recovery ladder (pure HttpRetryLadder + Json).
// Run via run_tests.sh (suite "httpretry").
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestHttpRetry
{
    private static int _failed;

    private static void Eq(object expect, object got, string what)
    {
        if (!string.Equals(Convert.ToString(expect), Convert.ToString(got), StringComparison.Ordinal))
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected '" + expect + "' got '" + got + "'");
        }
    }

    public static void Main()
    {
        // ---- StreamEngineError (500s inside 200-OK SSE) ----
        string sseError = "event: error\ndata: {\"error\":{\"message\":\"Engine protocol predict stream returned an error: {\\\"code\\\":500,\\\"message\\\":\\\"The model produced output that does not match the expected peg-gemma4 format\\\",\\\"type\\\":\\\"server_error\\\"}\"},\"message\":\"Engine protocol predict stream returned an error\"}";
        string serr = HttpRetryLadder.StreamEngineError(sseError);
        Assert(serr != null && serr.Contains("expected peg"), "stream engine error detected, got: " + serr);
        Assert(HttpRetryLadder.StreamEngineError("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}") == null, "plain SSE stream -> null");
        Assert(HttpRetryLadder.StreamEngineError(null) == null, "null stream -> null");

        // ---- Classify ----
        Eq("transient_engine", HttpRetryLadder.Classify(
            "HTTP 400 Bad Request — {\"error\":\"Engine protocol predict stream returned an error: {\\\"code\\\":500,\\\"message\\\":\\\"The model produced output that does not match the expected peg-native format\\\",\\\"type\\\":\\\"server_error\\\"}\"}"
            ), "peg-native engine error -> transient_engine");
        Eq("transient_engine", HttpRetryLadder.Classify(
            "HTTP 400 Bad Request — {\"error\":\"Engine protocol predict request failed: fetch failed\"}"
            ), "engine predict REQUEST fetch-failed (no nested 500) -> transient_engine, don't shrink");
        Eq("transient_engine", HttpRetryLadder.Classify("HTTP 400 — {\"error\":{\"code\":500,\"type\":\"server_error\"}}"),
            "nested 500 server_error -> transient_engine");
        Eq("image", HttpRetryLadder.Classify("HTTP 400 — {\"error\":\"this model cannot process image_url parts\"}"), "image -> image");
        Eq("image", HttpRetryLadder.Classify("HTTP 400 — {\"error\":\"vision not supported by loaded model\"}"), "vision -> image");
        Eq("response_format", HttpRetryLadder.Classify("HTTP 400 — {\"error\":\"unknown field: response_format\"}"), "response_format -> response_format");
        Eq("max_tokens", HttpRetryLadder.Classify("HTTP 400 — {\"error\":\"too large max_tokens for this model\"}"), "max_tokens -> max_tokens");
        Eq("context", HttpRetryLadder.Classify("HTTP 400 Bad Request — {\"error\":\"context length exceeded\"}"), "context length -> context");
        Eq("context", HttpRetryLadder.Classify("HTTP 413 Payload Too Large"), "413 -> context");
        Eq("context", HttpRetryLadder.Classify("HTTP 422 — {\"error\":\"token limit\"}"), "422 token limit -> context");
        Eq("context", HttpRetryLadder.Classify("HTTP 400 — unrecognized body"), "unknown 400 -> context (best guess = shrink)");
        Eq(null, HttpRetryLadder.Classify("HTTP 401 Unauthorized"), "401 -> null (not a payload problem)");
        Eq(null, HttpRetryLadder.Classify("HTTP 404 Not Found"), "404 -> null");
        Eq(null, HttpRetryLadder.Classify(null), "null -> null");
        Eq(null, HttpRetryLadder.Classify("connection refused"), "transport -> null");

        // ---- ShrinkPerceptionText ----
        string percep = "perception:{\"ok\":true,\"radar\":\"@@@\",\"rays\":[1,2,3],\"nearby\":[{\"a\":1},{\"a\":2},{\"a\":3},{\"a\":4},{\"a\":5},{\"a\":6},{\"a\":7},{\"a\":8}],\"chat_log\":[{\"x\":1},{\"x\":2},{\"x\":3},{\"x\":4}],\"history\":\"h\",\"memory\":\"m\",\"facts\":[\"f1\",\"f2\",\"f3\"]}";
        var sh1 = HttpRetryLadder.ShrinkPerceptionText(percep, 1);
        Assert(!sh1.Contains("radar") && !sh1.Contains("rays"), "stage1: radar+rays removed");
        Assert(sh1.Contains("\"nearby\"") && sh1.Contains("facts"), "stage1: nearby/facts kept");
        var sh2 = HttpRetryLadder.ShrinkPerceptionText(percep, 2);
        Assert(!sh2.Contains("\"history\"") && !sh2.Contains("\"memory\"") && !sh2.Contains("\"facts\""), "stage2: history/memory/facts removed");
        Assert(sh2.Contains("\"ok\""), "stage2: ok survives");
        // head/tail preservation (act loop appends an [INSTRUCTION] tail sometimes)
        string wrapped = "perception:{\"ok\":true,\"rays\":[1]}\n[INSTRUCTION] reply again";
        string w1 = HttpRetryLadder.ShrinkPerceptionText(wrapped, 1);
        Assert(w1.StartsWith("perception:{") && w1.EndsWith("[INSTRUCTION] reply again"), "head/tail preserved");

        // ---- TextPartsOnly ----
        var parts = new object[]
        {
            new Dictionary<string, object> { ["type"] = "text", ["text"] = "hello " },
            new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = "data:image/jpeg;base64,BIGG" } },
            new Dictionary<string, object> { ["type"] = "text", ["text"] = "world" },
        };
        string merged = HttpRetryLadder.TextPartsOnly(parts);
        Assert(merged == "hello \nworld", "text parts merged, image dropped — got '" + merged + "'");

        // ---- ShrinkMessages on a console-like payload ----
        Func<int, string, Dictionary<string, object>> msg = (i, text) => new Dictionary<string, object> { ["role"] = i == 0 ? "system" : "user", ["content"] = (object)text };
        var payloadDic = new Dictionary<string, object>
        {
            ["messages"] = new object[]
            {
                msg(0, "system"),
                msg(1, "one"), msg(2, "two"), msg(3, "three"), msg(4, "four"), msg(5, "five"),
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = "perception:{\"rays\":[9,8]}" },
                        new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = "data:…" } },
                    },
                },
            },
        };
        HttpRetryLadder.ShrinkMessages(payloadDic, 1);
        var msgs = (object[])payloadDic["messages"];
        Assert(msgs.Length == 7, "stage1: message count unchanged, got " + msgs.Length);
        var imgMsg = (Dictionary<string, object>)msgs[6];
        string imgContent = (string)imgMsg["content"];
        Assert(!imgContent.Contains("image_url") && imgContent.Contains("perception:") && !imgContent.Contains("\"rays\""),
            "stage1: image parts flattened; embedded perception blob also shrunk");
        HttpRetryLadder.ShrinkMessages(payloadDic, 2);
        var trimmed = (object[])payloadDic["messages"];
        Assert(trimmed.Length == 4, "stage2: oldest half dropped (sys+3), got " + trimmed.Length);
        Assert(string.Equals(((Dictionary<string, object>)trimmed[1])["content"], "four"), "stage2: kept the newest half in order");

        // "Model is unloaded."-class 400s are WAIT-AND-RETRY, never a payload shrink:
        // a llama-server that switched models mid-session reloads on the next request.
        Assert(HttpRetryLadder.Classify("HTTP 400 Bad Request — {\"error\":\"Model unloaded by user or API request.\"}") == "model_unloaded",
            "unloaded-by-user classifies as model_unloaded");
        Assert(HttpRetryLadder.Classify("HTTP 400 Bad Request — {\"error\":\"Model is unloaded.\"}") == "model_unloaded",
            "model is unloaded classifies as model_unloaded");
        Assert(HttpRetryLadder.Classify("HTTP 400 Bad Request — {\"error\":\"No models loaded.\"}") == "model_unloaded",
            "no models loaded classifies as model_unloaded");
        // An ordinary context complaint must NOT be swallowed by the new rung.
        Assert(HttpRetryLadder.Classify("HTTP 400 Bad Request — context length exceeded") == "context",
            "context errors still classify as context");

        Console.WriteLine(_failed == 0 ? "httpretry: all passed" : "httpretry: " + _failed + " FAILURES");
        Environment.Exit(_failed > 0 ? 1 : 0);
    }

    private static void Assert(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("FAIL " + what); }
    }
}