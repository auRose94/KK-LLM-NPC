// Tests for LMStudioArgs.cs — the pure LM Studio host logic: endpoint parsing,
// /api/v0/models JSON parsing (with the OpenAI /v1/models false-positive guard),
// loaded-model pick, and keep-alive cadence math. Pure C#, no UnityEngine;
// compiled as part of run_tests.sh (together with OllamaArgs, whose duration
// grammar LMStudioArgs reuses).
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestLMStudio
{
    private static int _passed;
    private static int _failed;

    private static void Eq(object want, object got, string what)
    {
        bool ok = (want == null) ? got == null : want.Equals(got);
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what + " — want " + want + ", got " + got); }
        else _passed++;
    }

    // Strip the OpenAI suffix to reach the API root (1234 = LM Studio's port).
    private static void TestBaseUrl()
    {
        Eq("http://127.0.0.1:1234", LMStudioArgs.BaseUrl("http://127.0.0.1:1234/v1/chat/completions"), "base: strip chat/completions");
        Eq("http://127.0.0.1:1234", LMStudioArgs.BaseUrl("http://127.0.0.1:1234"), "base: bare root");
        Eq("http://127.0.0.1:1234", LMStudioArgs.BaseUrl("http://127.0.0.1:1234/"), "base: trailing slash");
        Eq("", LMStudioArgs.BaseUrl(""), "base: empty");
        Eq("", LMStudioArgs.BaseUrl(null), "base: null");
    }

    // /api/v0/models → model list with load state + real context windows.
    // Shape verified against the LM Studio docs: id, type, publisher, arch,
    // compatibility_type, quantization, state, max_context_length.
    private static void TestParseModels()
    {
        string json = "{\"object\":\"list\",\"data\":[" +
            "{\"id\":\"qwen/qwen3-8b\",\"object\":\"model\",\"type\":\"llm\",\"publisher\":\"qwen\"," +
            "\"arch\":\"qwen3\",\"compatibility_type\":\"gguf\",\"quantization\":\"Q4_K_M\"," +
            "\"state\":\"loaded\",\"max_context_length\":32768}," +
            "{\"id\":\"mlx-community/Llama-3.1-8B\",\"object\":\"model\",\"type\":\"llm\"," +
            "\"publisher\":\"mlx-community\",\"arch\":\"llama\",\"compatibility_type\":\"mlx\"," +
            "\"quantization\":\"4bit\",\"state\":\"not-loaded\",\"max_context_length\":131072}]}";
        var m = LMStudioArgs.ParseModels(json);
        Eq(2, m.Count, "models: two models");
        Eq("qwen/qwen3-8b", m[0].Id, "models: first id");
        Eq("llm", m[0].Type, "models: first type");
        Eq("qwen3", m[0].Arch, "models: first arch");
        Eq("gguf", m[0].Compatibility, "models: first compat");
        Eq("Q4_K_M", m[0].Quantization, "models: first quant");
        Eq("loaded", m[0].State, "models: first loaded");
        Eq(32768, m[0].MaxContextLength, "models: first ctx");
        Eq("not-loaded", m[1].State, "models: second not loaded");
        Eq(131072, m[1].MaxContextLength, "models: second ctx");
    }

    // A payload that ISN'T an LM Studio list must yield null so the watcher
    // never adopts the wrong server. The critical case: a plain OpenAI
    // /v1/models body (id/object/created/owned_by only) shares the "data"
    // wrapper but carries none of the LM-Studio-only fields.
    private static void TestParseModelsBad()
    {
        string openai = "{\"object\":\"list\",\"data\":[" +
            "{\"id\":\"local-model\",\"object\":\"model\",\"created\":1700000000,\"owned_by\":\"server\"}]}";
        Eq(null, LMStudioArgs.ParseModels(openai), "models: openai /v1/models body is not LM Studio");
        Eq(null, LMStudioArgs.ParseModels("{}"), "models: empty object");
        Eq(null, LMStudioArgs.ParseModels("not json"), "models: garbage");
        Eq(null, LMStudioArgs.ParseModels(null), "models: null");
        Eq(null, LMStudioArgs.ParseModels("{\"data\":\"nope\"}"), "models: data not a list");
    }

    // The loaded-model pick: JIT-enabled lists carry not-loaded entries, so
    // only state says what LM Studio is actually serving.
    private static void TestLoadedModel()
    {
        var models = new List<LmStudioModel>
        {
            new LmStudioModel("a", "llm", "x", "qwen3", "gguf", "Q4", "not-loaded", 8192),
            new LmStudioModel("b", "llm", "x", "llama", "gguf", "Q5", "Loaded", 4096),
        };
        Eq("b", LMStudioArgs.LoadedModel(models).Id, "loaded: skips not-loaded, case-insensitive");
        Eq(null, LMStudioArgs.LoadedModel(null), "loaded: null list");
        var none = new List<LmStudioModel> { new LmStudioModel("a", "llm", "", "", "", "", "not-loaded", 0) };
        Eq(null, LMStudioArgs.LoadedModel(none), "loaded: nothing loaded");
    }

    // Context resolution prefers the model's own max_context_length.
    private static void TestEffectiveContext()
    {
        var m = new LmStudioModel("a", "llm", "", "qwen3", "gguf", "Q4", "loaded", 32768);
        Eq(32768, LMStudioArgs.EffectiveContext(m, 8192), "ctx: model wins");
        Eq(8192, LMStudioArgs.EffectiveContext(null, 8192), "ctx: fallback");
        Eq(0, LMStudioArgs.EffectiveContext(null, 0), "ctx: none");
    }

    // [LMStudio].KeepAlive → seconds (Ollama's duration grammar) + ping cadence.
    // The cadence cap is 30 MINUTES (LM Studio TTLs are long), not Ollama's 60s.
    private static void TestKeepAlive()
    {
        Eq(1800L, LMStudioArgs.KeepAliveSeconds("30m"), "ka: 30m");
        Eq(7200L, LMStudioArgs.KeepAliveSeconds("2h"), "ka: 2h");
        Eq(-1L, LMStudioArgs.KeepAliveSeconds("-1"), "ka: pin");
        Eq(0L, LMStudioArgs.KeepAliveSeconds("off"), "ka: off word");
        Eq(0L, LMStudioArgs.KeepAliveSeconds(""), "ka: empty is off");
        Eq(900, LMStudioArgs.PingCadenceSecs(-1), "ping: pin uses the quiet 15m cadence");
        Eq(900, LMStudioArgs.PingCadenceSecs(1800), "ping: 30m window → half");
        Eq(1800, LMStudioArgs.PingCadenceSecs(7200), "ping: 2h window → capped at 30m");
        Eq(45, LMStudioArgs.PingCadenceSecs(90), "ping: half the window");
        Eq(5, LMStudioArgs.PingCadenceSecs(8), "ping: floor");
        Eq(0, LMStudioArgs.PingCadenceSecs(0), "ping: disabled never pings");
    }

    // LM Studio instances: ":N" keys (Rose-verified live — loading a model twice
    // yields model + model:2), base stripping, set building, agent round-robin.
    private static void TestInstances()
    {
        Eq("unseen_gemma_4_26b_nsfw:2", LMStudioArgs.InstanceName("unseen_gemma_4_26b_nsfw", 2), "instance: second copy is base:2");
        Eq("m:3", LMStudioArgs.InstanceName("m", 3), "instance: third copy");
        Eq("m", LMStudioArgs.InstanceName("m", 1), "instance: copyIndex<2 is the base");
        Eq("", LMStudioArgs.InstanceName("", 2), "instance: empty base stays empty");

        Eq("m", LMStudioArgs.InstanceBase("m:2"), "base: strips :2");
        Eq("m", LMStudioArgs.InstanceBase("m:12"), "base: strips multi-digit");
        Eq("m:latest", LMStudioArgs.InstanceBase("m:latest"), "base: non-numeric tail untouched");
        Eq("qwen/qwen3.8-27b", LMStudioArgs.InstanceBase("qwen/qwen3.8-27b:2"), "base: publisher/model key strips cleanly");
        Eq("", LMStudioArgs.InstanceBase(""), "base: empty passthrough");
        Eq("m:", LMStudioArgs.InstanceBase("m:"), "base: bare colon untouched");

        var set = LMStudioArgs.ReplicaSet("m", 3);
        Eq(3, set.Count, "instance set: base + 2 copies");
        Eq("m", set[0], "instance set: [0] is the base");
        Eq("m:3", set[2], "instance set: last copy");
        Eq(0, LMStudioArgs.ReplicaSet(null, 4).Count, "instance set: null base = empty");

        var r = LMStudioArgs.ReplicaSet("m", 2);
        Eq("m", LMStudioArgs.ReplicaFor(r, 0), "assign: agent 0 → base");
        Eq("m:2", LMStudioArgs.ReplicaFor(r, 1), "assign: agent 1 → instance 2");
        Eq("m", LMStudioArgs.ReplicaFor(r, 2), "assign: wraps around");
        Eq(null, LMStudioArgs.ReplicaFor(null, 0), "assign: no set → null");
    }

    // The ADOPTION scan: every loaded instance of a base (base first, then :N by
    // index; manual UI loads included; not-loaded siblings ignored; cap 8).
    // Shape verified against a live dual-instance server (2026-11-17).
    private static void TestLoadedInstancesOf()
    {
        var models = new List<LmStudioModel>
        {
            new LmStudioModel("google/gemma-4-e4b", "vlm", "google", "gemma4", "gguf", "QAT", "loaded", 131072),
            new LmStudioModel("google/gemma-4-e4b:2", "vlm", "google", "gemma4", "gguf", "QAT", "loaded", 131072),
            new LmStudioModel("google/gemma-4-e4b:3", "vlm", "google", "gemma4", "gguf", "QAT", "not-loaded", 131072),
            new LmStudioModel("unseen_gemma_4_26b_nsfw", "vlm", "x", "gemma", "gguf", "IQ2_M", "loaded", 262144),
            new LmStudioModel("qwen/qwen3.5-9b", "llm", "qwen", "qwen3", "gguf", "Q4", "not-loaded", 262144),
        };
        var pool = LMStudioArgs.LoadedInstancesOf(models, "google/gemma-4-e4b");
        Eq(2, pool.Count, "adopt: base + :2 loaded, :3 ignored");
        Eq("google/gemma-4-e4b", pool[0], "adopt: base first");
        Eq("google/gemma-4-e4b:2", pool[1], "adopt: second instance");

        // index ordering when instances load out of order
        var shuffled = new List<LmStudioModel>
        {
            new LmStudioModel("m:5", "llm", "", "", "", "", "loaded", 1),
            new LmStudioModel("m:2", "llm", "", "", "", "", "loaded", 1),
            new LmStudioModel("m", "llm", "", "", "", "", "loaded", 1),
        };
        var pool2 = LMStudioArgs.LoadedInstancesOf(shuffled, "m");
        Eq(3, pool2.Count, "adopt: three loaded");
        Eq("m:2", pool2[1], "adopt: ordered by index despite list order");
        Eq("m:5", pool2[2], "adopt: higher index last");

        // base not loaded → instances still adoptable on their own
        var noBase = new List<LmStudioModel> { new LmStudioModel("m:2", "llm", "", "", "", "", "loaded", 1) };
        Eq(1, LMStudioArgs.LoadedInstancesOf(noBase, "m").Count, "adopt: instance without base");

        Eq(0, LMStudioArgs.LoadedInstancesOf(null, "m").Count, "adopt: null models");
        Eq(0, LMStudioArgs.LoadedInstancesOf(models, "").Count, "adopt: empty base");
    }

    internal static int Run()
    {
        TestBaseUrl();
        TestParseModels();
        TestParseModelsBad();
        TestLoadedModel();
        TestEffectiveContext();
        TestKeepAlive();
        TestInstances();
        TestLoadedInstancesOf();

        if (_failed > 0)
        {
            Console.WriteLine("lmstudio: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("lmstudio: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
