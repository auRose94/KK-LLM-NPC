// Unit tests for ModelFamilies.cs — pure C#, no UnityEngine.
// To run: mcs -target:exe -out:/tmp/t_mf.exe tests/test_modelfamilies.cs src/ModelFamilies.cs && mono /tmp/t_mf.exe
using System;
using KKLLMNPC;

namespace KKLLMNPC.Tests
{
    public static class ModelFamilyTests
    {
        private static int _passed = 0;
        private static int _failed = 0;

        private static void Assert(bool condition, string msg)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  PASS: " + msg);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL: " + msg);
            }
        }

        // Reasoning markers assembled from fragments — the contiguous spellings
        // must never be typed into test source (same marker hygiene rule as
        // ModelFamilies.cs; see its file header).
        private static readonly string TkOpen  = "<" + "think" + ">";
        private static readonly string TkClose = "<" + "/think" + ">";
        private static readonly string TgOpen  = "<" + "thinki" + "ng>";
        private static readonly string TgClose = "<" + "/thinki" + "ng>";

        public static void Main()
        {
            Console.WriteLine("=== ModelFamilies Unit Tests ===\n");

            TestClassifyBasics();
            TestClassifyQuirks();
            TestOrderingPrecedence();
            TestStripPaired();
            TestStripCloserOnly();
            TestStripUnclosedOpener();
            TestStripEdgeCases();
            TestEffectiveMaxTokens();
            TestHasThinkBlocks();

            Console.WriteLine("\n=== Results: " + _passed + " passed, " + _failed + " failed ===");
            Environment.Exit(_failed > 0 ? 1 : 0);
        }

        // ------------------------------------------------------------------
        // Classify: family detection from model ids
        // ------------------------------------------------------------------
        static void TestClassifyBasics()
        {
            Console.WriteLine("--- Classify: family detection ---");

            Assert(ModelFamilies.Classify("qwen3:14b").Family == "qwen", "qwen3:14b -> qwen");
            Assert(ModelFamilies.Classify("gemma-3-27b-it").Family == "gemma", "gemma-3-27b-it -> gemma");
            Assert(ModelFamilies.Classify("glm-4.7-flash").Family == "glm", "glm-4.7-flash -> glm");
            Assert(ModelFamilies.Classify("muse-glimmer-30b-a3b-mlx").Family == "muse", "muse id -> muse");
            Assert(ModelFamilies.Classify("ornith-4b").Family == "ornith", "ornith id -> ornith");
            // Case-insensitive + numeric suffix (LFM2 line).
            Assert(ModelFamilies.Classify("LFM2-2.6B").Family == "lfm", "LFM2-2.6B -> lfm (case-insensitive)");
            Assert(ModelFamilies.Classify("bonsai-t-4b").Family == "bonsai", "bonsai id -> bonsai");
            Assert(ModelFamilies.Classify("zdtaichu5.0-9b").Family == "taichu", "zdtaichu id -> taichu");
            Assert(ModelFamilies.Classify("ZDTaichu5.0-9B-Q4_K_M.gguf").Family == "taichu", "taichu gguf filename -> taichu (case-insensitive)");
            Assert(ModelFamilies.Classify("Nemotron-Nano-9B").Family == "nemotron", "nemotron id -> nemotron");
            Assert(ModelFamilies.Classify("granite-4.0-3b").Family == "granite", "granite id -> granite");
            Assert(ModelFamilies.Classify("mistral-7b-instruct").Family == "mistral", "mistral id -> mistral");
            Assert(ModelFamilies.Classify("SmolLM2-1.7B").Family == "smollm", "SmolLM2 -> smollm");
            Assert(ModelFamilies.Classify("llava-13b").Family == "vision", "llava -> vision");
            Assert(ModelFamilies.Classify("gpt-oss-20b").Family == "llama", "gpt-oss -> llama-generic");
            Assert(ModelFamilies.Classify("ling-annette-9b").Family == "", "unmatched finetune -> generic (empty family)");
            Assert(ModelFamilies.Classify("").Family == "", "empty id -> generic");
            Assert(ModelFamilies.Classify(null).Family == "", "null id -> generic");
        }

        // ------------------------------------------------------------------
        // Classify: per-family quirk profile fields
        // ------------------------------------------------------------------
        static void TestClassifyQuirks()
        {
            Console.WriteLine("--- Classify: quirk profiles ---");

            ModelFamilyProfile q = ModelFamilies.Classify("Qwen3-14B");
            Assert(q.EmitsThinkBlocks, "qwen: emits think blocks");
            Assert(q.ThinkTokenHeadroom == 512, "qwen: 512 token headroom");
            Assert(q.VisionCapable, "qwen: vision-capable");

            ModelFamilyProfile g = ModelFamilies.Classify("gemma-3-4b");
            Assert(g.TemplateLiteral, "gemma: template-literal");
            Assert(!g.EmitsThinkBlocks && g.ThinkTokenHeadroom == 0, "gemma: no reasoning overhead");
            Assert(g.VisionCapable, "gemma: vision-capable");

            ModelFamilyProfile m = ModelFamilies.Classify("muse-glimmer-30b-a3b-mlx");
            Assert(!m.EmitsThinkBlocks && m.VisionCapable, "muse: no think, vision-capable");
            Assert(m.PreferredTier == "medium", "muse: preferred tier medium");

            ModelFamilyProfile t = ModelFamilies.Classify("zdtaichu5.0-9b");
            Assert(t.EmitsThinkBlocks && t.ThinkTokenHeadroom == 512 && t.VisionCapable,
                "taichu: qwen-class reasoner (think + 512 headroom, vision-capable)");
            Assert(t.PreferredTier == "", "taichu: tier left to size heuristics (9B -> small)");

            ModelFamilyProfile n = ModelFamilies.Classify("Nemotron-Mini-4B");
            Assert(n.EmitsThinkBlocks && n.PreferredTier == "small" && n.ThinkTokenHeadroom == 768,
                "nemotron: think + small tier + 768 headroom");

            ModelFamilyProfile gr = ModelFamilies.Classify("granite-4.0-3b");
            Assert(gr.EmitsThinkBlocks && gr.PreferredTier == "small", "granite: think + small tier");

            ModelFamilyProfile b = ModelFamilies.Classify("bonsai-t-4b");
            Assert(b.EmitsThinkBlocks && b.PreferredTier == "small" && b.ThinkTokenHeadroom == 512,
                "bonsai: tiny reasoner (think + small tier + 512)");

            ModelFamilyProfile r = ModelFamilies.Classify("DeepSeek-R1-Distill-Qwen-7B");
            Assert(r.Family == "deepseek" && r.EmitsThinkBlocks && r.ThinkTokenHeadroom == 1024,
                "deepseek distill: reasoner profile, 1024 headroom");

            ModelFamilyProfile o = ModelFamilies.Classify("ornith-4b");
            Assert(o.PreferredTier == "small" && o.EmitsThinkBlocks == false, "ornith: small, no think");

            ModelFamilyProfile v = ModelFamilies.Classify("llava-13b");
            Assert(v.PreferredTier == "small" && v.VisionCapable, "vision captioner: small tier");

            ModelFamilyProfile gn = ModelFamilies.Classify("ling-annette-9b");
            Assert(gn.Family == "" && gn.ThinkTokenHeadroom == 256, "generic: modest 256 headroom for unknowns");
        }

        // ------------------------------------------------------------------
        // Classify: keyword precedence
        // ------------------------------------------------------------------
        static void TestOrderingPrecedence()
        {
            Console.WriteLine("--- Classify: precedence ---");

            // Qwen ids carrying "gemma" (quant-repack merges) must keep the Qwen
            // reasoning quirk set — stripping is the load-bearing behavior here.
            Assert(ModelFamilies.Classify("qwen3-4b-gemma-merge").Family == "qwen",
                "qwen repack with 'gemma' in the id -> qwen (not gemma)");
            Assert(ModelFamilies.Classify("deepseek-r1-distill-llama-8b").Family == "deepseek",
                "distilled reasoner checked before llama");
            Assert(ModelFamilies.Classify("qwq-32b").Family == "deepseek",
                "qwq -> reasoner profile");
            Assert(ModelFamilies.Classify("magistral-small").Family == "mistral",
                "magistral (contains no raw mistral substring) still matches");
        }

        // ------------------------------------------------------------------
        // StripThinkBlocks: paired marker runs
        // ------------------------------------------------------------------
        static void TestStripPaired()
        {
            Console.WriteLine("--- StripThinkBlocks: paired runs ---");

            Assert(ModelFamilies.StripThinkBlocks("cd house") == "cd house", "plain reply untouched");
            Assert(ModelFamilies.StripThinkBlocks(TkOpen + "reasoning" + TkClose + "cd house") == "cd house",
                "paired short markers stripped");
            Assert(ModelFamilies.StripThinkBlocks(TgOpen + "long reasoning" + TgClose + "answer") == "answer",
                "paired long markers stripped");
            Assert(ModelFamilies.StripThinkBlocks(TkOpen + "a\nb\nc" + TkClose + "go") == "go",
                "multiline reasoning pair stripped");
            Assert(ModelFamilies.StripThinkBlocks(TkOpen + "one" + TkClose + "mid" + TkOpen + "two" + TkClose + "end") == "midend",
                "multiple pairs stripped");
            // Case-insensitive marker spellings (<THINK> etc.).
            Assert(ModelFamilies.StripThinkBlocks("<" + "THINK" + ">" + "r" + "<" + "/THINK" + ">" + "ok") == "ok",
                "uppercase markers stripped");
            Assert(ModelFamilies.StripThinkBlocks("  padded  ") == "padded", "plain reply trimmed");
        }

        // ------------------------------------------------------------------
        // StripThinkBlocks: closer without opener (server ate the opener token)
        // ------------------------------------------------------------------
        static void TestStripCloserOnly()
        {
            Console.WriteLine("--- StripThinkBlocks: closer-only shape ---");

            Assert(ModelFamilies.StripThinkBlocks("reasoning" + TkClose + "cd house") == "cd house",
                "keep text after first closer");
            Assert(ModelFamilies.StripThinkBlocks("reason" + TgClose + "go west") == "go west",
                "long spelling closer without opener");
            Assert(ModelFamilies.StripThinkBlocks("stuff" + TkClose + "  ") == "",
                "everything before the first closer is treated as reasoning");
            // Closer leaks, then an unclosed opener after the salvaged answer.
            Assert(ModelFamilies.StripThinkBlocks("R" + TkClose + "ANSWER" + TkOpen + "cut") == "ANSWER",
                "post-closer answer survives an unclosed trailing opener");
        }

        // ------------------------------------------------------------------
        // StripThinkBlocks: unclosed opener (token cap cut mid-reasoning)
        // ------------------------------------------------------------------
        static void TestStripUnclosedOpener()
        {
            Console.WriteLine("--- StripThinkBlocks: unclosed opener ---");

            Assert(ModelFamilies.StripThinkBlocks(TkOpen + "deep analysis never closes") == "",
                "all-reasoning cut reply -> empty (retry path)");
            Assert(ModelFamilies.StripThinkBlocks(TgOpen + "cut") == "", "unclosed long spelling -> empty");
            Assert(ModelFamilies.StripThinkBlocks("salvage me" + TkOpen + "cut reasoning") == "salvage me",
                "out-of-order answer before a cut opener is salvaged");
            // Mismatched spellings (short opener, long closer) read as an unclosed
            // opener with an empty prefix — documented limitation, retried via "".
            Assert(ModelFamilies.StripThinkBlocks(TkOpen + "r" + TgClose + "ans") == "",
                "mismatched spelling pair -> empty (retry path)");
        }

        // ------------------------------------------------------------------
        // StripThinkBlocks: edge cases
        // ------------------------------------------------------------------
        static void TestStripEdgeCases()
        {
            Console.WriteLine("--- StripThinkBlocks: edge cases ---");

            Assert(ModelFamilies.StripThinkBlocks(null) == null, "null passthrough");
            Assert(ModelFamilies.StripThinkBlocks("") == "", "empty passthrough");
            Assert(ModelFamilies.StripThinkBlocks("   ") == "", "whitespace-only -> empty");
            Assert(ModelFamilies.StripThinkBlocks(TkOpen + "x" + TkClose) == "", "reasoning-only reply -> empty");
            Assert(ModelFamilies.StripThinkBlocks("clean line A\nclean line B") == "clean line A\nclean line B",
                "multi-line plain reply kept intact");
        }

        // ------------------------------------------------------------------
        // EffectiveMaxTokens: reasoning-aware budget floor
        // ------------------------------------------------------------------
        static void TestEffectiveMaxTokens()
        {
            Console.WriteLine("--- EffectiveMaxTokens ---");

            ModelFamilyProfile q = ModelFamilies.Classify("qwen3:14b");
            Assert(ModelFamilies.EffectiveMaxTokens(256, 256, q) == 768, "qwen: 256 user -> 768 floor (256+512)");
            Assert(ModelFamilies.EffectiveMaxTokens(1024, 1024, q) == 1536, "qwen: floor default+headroom");
            Assert(ModelFamilies.EffectiveMaxTokens(2000, 1024, q) == 2000, "user value above floor stands");

            ModelFamilyProfile g = ModelFamilies.Classify("gemma-3-4b");
            Assert(ModelFamilies.EffectiveMaxTokens(64, 1024, g) == 64,
                "no-reasoning family: user value unchanged (no silent raises)");

            ModelFamilyProfile grn = ModelFamilies.Classify("granite-4.0-3b");
            Assert(ModelFamilies.EffectiveMaxTokens(256, 256, grn) == 1024, "granite: 256 -> 1024 floor");

            ModelFamilyProfile gn = ModelFamilies.Classify("ling-annette-9b");
            Assert(ModelFamilies.EffectiveMaxTokens(64, 256, gn) == 512, "generic: floor default+256");

            Assert(ModelFamilies.EffectiveMaxTokens(256, 256, null) == 256, "null profile: unchanged");
        }

        // ------------------------------------------------------------------
        // HasThinkBlocks: diagnostics helper
        // ------------------------------------------------------------------
        static void TestHasThinkBlocks()
        {
            Console.WriteLine("--- HasThinkBlocks ---");

            Assert(ModelFamilies.HasThinkBlocks(TkOpen + "x"), "detects short opener");
            Assert(ModelFamilies.HasThinkBlocks(TgOpen + "x"), "detects long opener");
            Assert(!ModelFamilies.HasThinkBlocks("plain reply"), "plain reply: false");
            Assert(!ModelFamilies.HasThinkBlocks("stray" + TkClose), "closer-only text: false (no opener)");
            Assert(!ModelFamilies.HasThinkBlocks(null) && !ModelFamilies.HasThinkBlocks(""), "null/empty: false");
        }
    }
}