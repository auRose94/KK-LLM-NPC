// Tests for OllamaArgs.cs — the pure Ollama host logic: endpoint parsing, /api/tags and
// /api/show JSON parsing, context-window fit math. Pure C#, no UnityEngine; compiled as part
// of run_tests.sh.
using System;
using KKLLMNPC;

internal static class TestOllama
{
    private static int _passed;
    private static int _failed;

    private static void Eq(object want, object got, string what)
    {
        bool ok = (want == null) ? got == null : want.Equals(got);
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what + " — want " + want + ", got " + got); }
        else _passed++;
    }

    // Strip the OpenAI suffix to reach the API root.
    private static void TestBaseUrl()
    {
        Eq("http://127.0.0.1:11434", OllamaArgs.BaseUrl("http://127.0.0.1:11434/v1/chat/completions"), "base: strip chat/completions");
        Eq("http://127.0.0.1:11434", OllamaArgs.BaseUrl("http://127.0.0.1:11434"), "base: bare root");
        Eq("http://127.0.0.1:11434", OllamaArgs.BaseUrl("http://127.0.0.1:11434/"), "base: trailing slash");
        Eq("https://ollama.example.com", OllamaArgs.BaseUrl("https://ollama.example.com/v1/completions"), "base: completions suffix");
        Eq("", OllamaArgs.BaseUrl(""), "base: empty");
        Eq("", OllamaArgs.BaseUrl("   "), "base: whitespace");
    }

    // /api/tags → installed model list.
    private static void TestParseTags()
    {
        string json = "{\"models\":[" +
            "{\"name\":\"tev1:4b\",\"family\":\"qwen2\",\"format\":\"gguf\",\"size\":4871190000,\"digest\":\"sha256:abc\"}," +
            "{\"name\":\"c16k-4b\",\"family\":\"llama\",\"format\":\"gguf\",\"size\":4600000000,\"digest\":\"sha256:def\"}]}";
        var m = OllamaArgs.ParseTags(json);
        Eq(2, m.Count, "tags: two models");
        Eq("tev1:4b", m[0].Name, "tags: first name");
        Eq("qwen2", m[0].Family, "tags: first family");
        Eq("gguf", m[0].Format, "tags: first format");
        Eq(4871190000L, m[0].Size, "tags: first size");
        Eq("sha256:abc", m[0].Digest, "tags: first digest");
    }

    // /api/tags that isn't a tags payload → null.
    private static void TestParseTagsBad()
    {
        Eq(null, OllamaArgs.ParseTags("{}"), "tags: empty object");
        Eq(null, OllamaArgs.ParseTags("not json"), "tags: garbage");
        Eq(null, OllamaArgs.ParseTags(null), "tags: null");
    }

    // /api/show → context window + family + quantization (real shape: details + model_info).
    private static void TestParseShow()
    {
        string json = "{\"model\":\"tev1:4b\"," +
            "\"details\":{\"family\":\"qwen35\",\"format\":\"gguf\",\"quantization_level\":\"Q8_0\",\"parameter_size\":\"4.3B\"}," +
            "\"model_info\":{\"general.architecture\":\"qwen35\",\"qwen35.context_length\":262144,\"general.file_type\":\"8\"}}}";
        var s = OllamaArgs.ParseShow(json);
        Eq("qwen35", s.Architecture, "show: family");
        Eq(262144, s.ContextLength, "show: qwen35.context_length");
        Eq("Q8_0", s.Quantization, "show: quantization");
    }

    // /api/show with model_info lacking a .context_length suffix — falls back to flat "context_length".
    private static void TestParseShowFlat()
    {
        string json = "{\"model\":\"tiny\"," +
            "\"details\":{\"family\":\"llama\",\"quantization_level\":\"Q4\"}," +
            "\"model_info\":{\"general.architecture\":\"llama\",\"context_length\":8192}}}";
        var s = OllamaArgs.ParseShow(json);
        Eq("llama", s.Architecture, "show: flat architecture");
        Eq(8192, s.ContextLength, "show: flat context");
    }

    // Effective context prefers the /api/show value, falls back to the detected value.
    private static void TestEffectiveContext()
    {
        Eq(32768, OllamaArgs.EffectiveContext(new ShowInfo("qwen2", 32768, "Q4"), 8192), "ctx: show wins");
        Eq(8192, OllamaArgs.EffectiveContext(null, 8192), "ctx: fallback");
        Eq(0, OllamaArgs.EffectiveContext(null, 0), "ctx: none");
    }

    // Context fit math: floor at 1024, cap at the real window.
    private static void TestFitContext()
    {
        Eq(16384, OllamaArgs.FitContext(32768, 0.5), "fit: half window");
        Eq(31130, OllamaArgs.FitContext(32768, 0.95), "fit: 95% window");
        Eq(1024, OllamaArgs.FitContext(512, 0.5), "fit: floor");
        Eq(0, OllamaArgs.FitContext(0, 0.5), "fit: no window");
    }

    // Human-readable byte size.
    private static void TestHumanSize()
    {
        Eq("4.5 GiB", OllamaArgs.HumanSize((long)(4.5 * 1024 * 1024 * 1024)), "size: 4.5 GiB");
        Eq("", OllamaArgs.HumanSize(0), "size: zero");
    }

    internal static int Run()
    {
        TestBaseUrl();
        TestParseTags();
        TestParseTagsBad();
        TestParseShow();
        TestParseShowFlat();
        TestEffectiveContext();
        TestFitContext();
        TestHumanSize();

        if (_failed > 0)
        {
            Console.WriteLine("ollama: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("ollama: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
