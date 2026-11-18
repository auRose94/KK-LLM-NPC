// Tests for SceneKnowledgeCore.cs — the scene-dump gazetteer: hierarchy
// flattening (verified against Rose's real dump shape), scene probing,
// scored nearest-first search, and the grounding summary. Pure C#, no
// UnityEngine; compiled as part of run_tests.sh.
using System;
using KKLLMNPC;

internal static class TestSceneKnowledge
{
    private const string DumpJson = "{ \"format_version\": 2, \"scene\": \"MainMap++\", \"dump_time\": \"x\", \"full\": true," +
        "\"objects\": [" +
        "{ \"name\": \"FarmRegion\", \"path\": \"FarmRegion\", \"instanceID\": -10, \"active\": true," +
        "  \"worldPosition\": {\"x\":0,\"y\":0,\"z\":0}," +
        "  \"children\": [" +
        "    { \"name\": \"EggItem(Clone)\", \"path\": \"FarmRegion/EggItem(Clone)\", \"instanceID\": -11, \"active\": true," +
        "      \"worldPosition\": {\"x\": 12.5, \"y\": 0.1, \"z\": -4.0}, \"children\": [] }," +
        "    { \"name\": \"NestStation\", \"path\": \"FarmRegion/NestStation\", \"instanceID\": -12, \"active\": true," +
        "      \"worldPosition\": {\"x\": 60.0, \"y\": 0.2, \"z\": 8.0} }," +
        "    { \"name\": \"HiddenEgg\", \"path\": \"FarmRegion/HiddenEgg\", \"instanceID\": -13, \"active\": false," +
        "      \"worldPosition\": {\"x\": 1.0, \"y\": 1.0, \"z\": 1.0} }" +
        "  ] }," +
        "{ \"name\": \"Mailbox\", \"path\": \"Mailbox\", \"instanceID\": -20, \"active\": true," +
        "  \"worldPosition\": {\"x\": -3.0, \"y\": 0.0, \"z\": 2.0} }" +
        "] }";

    private static int _passed;
    private static int _failed;

    private static void Eq(object want, object got, string what)
    {
        bool ok = (want == null) ? got == null : want.Equals(got);
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what + " — want " + want + ", got " + got); }
        else _passed++;
    }

    private static void TestParseAndScene()
    {
        Eq("MainMap++", SceneKnowledgeCore.DumpScene(DumpJson), "dump: scene field");
        Eq(null, SceneKnowledgeCore.DumpScene("not json"), "dump: garbage → null scene");
        var reg = SceneKnowledgeCore.ParseDump(DumpJson);
        Eq(4, reg.Count, "parse: 4 active nodes (inactive HiddenEgg skipped)");
        var egg = reg.Find(e => e.Name.Contains("EggItem"));
        Eq(-11, egg.InstanceId, "parse: instanceID kept");
        Eq(12.5f, egg.X, "parse: worldPosition x");
        Eq(-4.0f, egg.Z, "parse: worldPosition z");
        Eq(null, SceneKnowledgeCore.ParseDump("{}"), "parse: no objects → null");
    }

    private static void TestSearch()
    {
        var reg = SceneKnowledgeCore.ParseDump(DumpJson);
        // "egg" matches EggItem(Clone) despite the (Clone) suffix; the inactive
        // HiddenEgg never enters the registry at all.
        var hits = SceneKnowledgeCore.Search(reg, "egg", 0f, 0f, 5);
        Eq(1, hits.Count, "search: egg match, inactive ignored");
        Eq("EggItem(Clone)", hits[0].Name, "search: (Clone) suffix ignored for matching");
        // exact name beats prefix beats substring, then distance
        var hits2 = SceneKnowledgeCore.Search(reg, "nest", 0f, 0f, 5);
        Eq(1, hits2.Count, "search: nest");
        // nearest-first among ties
        var reg2 = new System.Collections.Generic.List<SceneKnowledgeCore.Entry>
        {
            new SceneKnowledgeCore.Entry { Name = "Rock A", X = 100f, Z = 0f },
            new SceneKnowledgeCore.Entry { Name = "Rock B", X = 3f, Z = 0f },
            new SceneKnowledgeCore.Entry { Name = "Rock C", X = 50f, Z = 0f },
        };
        var near = SceneKnowledgeCore.Search(reg2, "rock", 0f, 0f, 2);
        Eq(2, near.Count, "search: take cap");
        Eq("Rock B", near[0].Name, "search: nearest first");
        Eq("Rock C", near[1].Name, "search: then farther");
        Eq(0, SceneKnowledgeCore.Search(reg, "  ", 0, 0, 5).Count, "search: whitespace query");
        Eq(0, SceneKnowledgeCore.Search(null, "egg", 0, 0, 5).Count, "search: null registry");
    }

    private static void TestSummary()
    {
        var reg = SceneKnowledgeCore.ParseDump(DumpJson);
        var s = SceneKnowledgeCore.Summary(reg);
        bool hasEggs = false, hasNest = false;
        foreach (var line in s)
        {
            if (line.Contains("egg")) hasEggs = true;
            if (line.Contains("nest")) hasNest = true;
        }
        Assert(hasEggs, "summary: egg bucket");
        Assert(hasNest, "summary: nest bucket");
    }

    private static void Assert(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("  FAIL: " + what); }
        else _passed++;
    }

    internal static int Run()
    {
        TestParseAndScene();
        TestSearch();
        TestSummary();

        if (_failed > 0)
        {
            Console.WriteLine("sceneknowledge: " + _failed + " assertion(s) failed");
            return 1;
        }
        Console.WriteLine("sceneknowledge: all OK");
        return 0;
    }

    internal static void Main()
    {
        int rc = Run();
        if (rc != 0) Environment.Exit(rc);
    }
}
