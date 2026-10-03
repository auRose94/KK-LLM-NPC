// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ModelFamilies — classify a model NAME into a family and a reasoning/quirk
// profile, so per-family behavior (think-marker stripping, token headroom,
// payload budgets) adapts to the model the user actually loads.
//
// Pure C#: no Unity, no Photon — unit-testable (tests/test_modelfamilies.cs).
// Field notes this is built around (Oct 2026, local servers, OpenAI-compatible):
// good game-play results with Muse Glimmer, Gemma, Qwen, Ornith, GLM, LFM
// (Liquid), Bonsai; Nemotron and Granite struggled — the known common thread is
// whether the family emits hidden reasoning (CoT markup akin to the HTML-escaped
// &lt;think&gt; marker) that eats MaxTokens, prints as garbage lines, or hides
// the answer entirely.
//
// MARKER HYGIENE: the raw reasoning markers must NEVER appear contiguous in
// this file — not in source, comments, or string literals. A contiguous marker
// parses as HTML-ish markup in LLM-maintainer context and has already twice
// truncated writes/reads of this file mid-line. All marker constants are
// therefore assembled from string fragments below. Keep it that way.
namespace KKLLMNPC
{
    /// <summary>Per-family behavior profile for the LLM/console loop.</summary>
    internal sealed class ModelFamilyProfile
    {
        /// <summary>Family key, e.g. "qwen", "gemma", "glm". Empty = generic/unknown.</summary>
        public string Family;
        /// <summary>Model emits hidden reasoning — strip it before line/JSON parsing
        /// and leave token headroom for it (Qwen3/GLM/Nemotron/Bonsai/Granite/R1-style).</summary>
        public bool EmitsThinkBlocks;
        /// <summary>Closed-style template: answers are short and literal — the reason
        /// Gemma-family models do well with the strict JSON/console contract.</summary>
        public bool TemplateLiteral;
        /// <summary>Known vision-capable family (can handle first-person frames).</summary>
        public bool VisionCapable;
        /// <summary>Token headroom to add on top of the user's MaxTokens when reasoning
        /// is likely (the reasoning AND the answer must both fit). 0 = no change.</summary>
        public int ThinkTokenHeadroom;
        /// <summary>Suggested tier when the probe can't size the model (name-only).
        /// "small" keeps prompts/payloads tight (chatty families: Granite/Nemotron).
        /// Empty = fall back to the existing size heuristics.</summary>
        public string PreferredTier;
        /// <summary>One-line human summary for logs/overlay.</summary>
        public string Note;
    }

    internal static class ModelFamilies
    {
        // Lazily built singleton profiles — cheap, tiny table.
        private static readonly ModelFamilyProfile Generic = new ModelFamilyProfile
        {
            Family = "",
            ThinkTokenHeadroom = 256,
            Note = "unknown family — generic handling",
        };

        /// <summary>
        /// Classify from a raw model id (e.g. "qwen3:14b", "gemma-3-27b-it",
        /// "glm-4.7-flash", "muse-glimmer-30b-a3b-mlx", "bonsai-t-4b", "LFM2-2.6B",
        /// "Ling-Annette-9B"). Longest contains-match wins; substring matching is
        /// case-insensitive and tolerant of tag suffixes (:f16, -mlx, GGUF names).
        /// </summary>
        public static ModelFamilyProfile Classify(string modelId)
        {
            string name = (modelId ?? "").ToLowerInvariant();
            if (name.Length == 0) return Generic;

            if (Contains(name, "ornith")) return Ornith();
            if (Contains(name, "bonsai")) return Bonsai();
            if (Contains(name, "taichu")) return Taichu();
            if (Contains(name, "muse")) return MuseGlimmer();
            if (Contains(name, "glm")) return GLM();
            if (Contains(name, "lfm")) return LFM();
            if (Contains(name, "granite")) return Granite();
            if (Contains(name, "nemotron")) return Nemotron();

            // Broad CoT families first: distilled reasoners embed another family's
            // name (DeepSeek-R1-Distill-Qwen-7B) and need the biggest CoT headroom.
            if (Contains(name, "deepseek") || Contains(name, "r1-distill") || Contains(name, "qwq")) return Reasoner("deepseek");

            // Qwen before Gemma: Qwen-based quant-repack ids can carry "gemma"
            // (merged GGUFs) and must keep the Qwen reasoning quirk set; real
            // Gemma ids never mention "qwen", so the Gemma check still catches
            // them below.
            if (Contains(name, "qwen")) return Qwen();
            if (Contains(name, "gemma")) return Gemma();
            if (Contains(name, "mistral") || Contains(name, "ministral") || Contains(name, "magistral")) return Mistral();
            if (Contains(name, "smollm") || Contains(name, "smolvlm")) return SmolLM();

            if (Contains(name, "phi")) return Phi();
            if (Contains(name, "llama") || Contains(name, "gpt-oss")) return Llamaish();
            if (Contains(name, "llava") || Contains(name, "moondream") || Contains(name, "vision")) return VisionOnly();
            return Generic;
        }

        // ---- per-family profiles ----
        private static ModelFamilyProfile Qwen() => new ModelFamilyProfile
        {
            Family = "qwen",
            EmitsThinkBlocks = true,
            TemplateLiteral = false,
            VisionCapable = true, // most deployed local Qwen variants have a VL sibling/merger
            ThinkTokenHeadroom = 512,
            PreferredTier = "", // sized normally; Qwen3 4B..32B all play well
            Note = "Qwen — strips " + TkOpen + " blocks; needs token headroom for them",
        };

        // ZDTaichu (TaichuAI; NVIDIA license, Qwen3.5-9B backbone + C-RADIOv4-H vision
        // encoder, GGUF with mmproj): Qwen-style reasoning markers + 128K context,
        // native vision, and specifically TRAINED for spatial reasoning / agentic tool
        // use — the family the [Vision] spatial-reasoner pass was written for. Its card
        // recommends temperature 0 + top_p 0.95 + top_k 20 for spatial/grounding work.
        private static ModelFamilyProfile Taichu() => new ModelFamilyProfile
        {
            Family = "taichu",
            EmitsThinkBlocks = true, // qwen3 reasoning parser; think blocks stripped here
            TemplateLiteral = false,
            VisionCapable = true,    // mmproj vision encoder — first-person frames OK
            ThinkTokenHeadroom = 512,
            PreferredTier = "",      // 9B id — sized small by the normal heuristics
            Note = "ZDTaichu — Qwen3.5-class spatial reasoner: strips think blocks, vision via mmproj, likes temp 0",
        };

        private static ModelFamilyProfile Gemma() => new ModelFamilyProfile
        {
            Family = "gemma",
            EmitsThinkBlocks = false,
            TemplateLiteral = true, // strict-JSON friendly (the template chokes on tool forcing — json_schema must stay)
            VisionCapable = true,
            ThinkTokenHeadroom = 0,
            PreferredTier = "",
            Note = "Gemma — literal template, keep raw image + json_schema, no think overhead",
        };

        private static ModelFamilyProfile GLM() => new ModelFamilyProfile
        {
            Family = "glm",
            EmitsThinkBlocks = true,
            TemplateLiteral = false,
            VisionCapable = true,
            ThinkTokenHeadroom = 512,
            PreferredTier = "",
            Note = "GLM — strips " + TkOpen + " blocks; good console discipline",
        };

        private static ModelFamilyProfile MuseGlimmer() => new ModelFamilyProfile
        {
            Family = "muse",
            EmitsThinkBlocks = false,
            TemplateLiteral = false,
            VisionCapable = true, // Muse Glimmer ships native image input (Ollama MLX engine)
            ThinkTokenHeadroom = 128,
            PreferredTier = "medium", // known 30B-A3B MoE — strong mid tier
            Note = "Muse Glimmer — native image input, concise replies",
        };

        private static ModelFamilyProfile Ornith() => new ModelFamilyProfile
        {
            Family = "ornith",
            EmitsThinkBlocks = false,
            TemplateLiteral = true,
            VisionCapable = false,
            ThinkTokenHeadroom = 256,
            PreferredTier = "small",
            Note = "Ornith — small/lean, keeps payloads tight",
        };

        private static ModelFamilyProfile LFM() => new ModelFamilyProfile
        {
            Family = "lfm",
            EmitsThinkBlocks = false,
            TemplateLiteral = true,
            VisionCapable = false,
            ThinkTokenHeadroom = 128,
            PreferredTier = "small", // LFM2 line is 1.2B-8B efficient hybrids
            Note = "LFM (Liquid) — compact hybrids, tight prompts",
        };

        private static ModelFamilyProfile Bonsai() => new ModelFamilyProfile
        {
            Family = "bonsai",
            EmitsThinkBlocks = true, // bonsai line is reasoner-distill; short but real CoT
            TemplateLiteral = false,
            VisionCapable = false,
            ThinkTokenHeadroom = 512,
            PreferredTier = "small",
            Note = "Bonsai — tiny reasoner: strips markers, needs headroom",
        };

        private static ModelFamilyProfile Nemotron() => new ModelFamilyProfile
        {
            Family = "nemotron",
            EmitsThinkBlocks = true,
            TemplateLiteral = false,
            VisionCapable = false,
            ThinkTokenHeadroom = 768,
            PreferredTier = "small", // chatty — keep prompts/payloads tight
            Note = "Nemotron — heavy CoT + chatty: strip markers, tight payloads",
        };

        private static ModelFamilyProfile Granite() => new ModelFamilyProfile
        {
            Family = "granite",
            EmitsThinkBlocks = true, // granite thinking variants; verbose contracts
            TemplateLiteral = false,
            VisionCapable = false,
            ThinkTokenHeadroom = 768,
            PreferredTier = "small",
            Note = "Granite — verbose CoT: strip markers, tight payloads",
        };

        private static ModelFamilyProfile Mistral() => new ModelFamilyProfile
        {
            Family = "mistral",
            EmitsThinkBlocks = false,
            TemplateLiteral = true,
            VisionCapable = false,
            ThinkTokenHeadroom = 128,
            PreferredTier = "",
            Note = "Mistral family — literal template",
        };

        private static ModelFamilyProfile SmolLM() => new ModelFamilyProfile
        {
            Family = "smollm",
            EmitsThinkBlocks = false,
            TemplateLiteral = true,
            VisionCapable = true,
            ThinkTokenHeadroom = 0,
            PreferredTier = "small",
            Note = "SmolLM/VLM — tiny, keep payloads minimal",
        };

        private static ModelFamilyProfile Phi() => new ModelFamilyProfile
        {
            Family = "phi",
            EmitsThinkBlocks = false,
            TemplateLiteral = true,
            VisionCapable = true,
            ThinkTokenHeadroom = 128,
            PreferredTier = "small",
            Note = "Phi — literal small model",
        };

        private static ModelFamilyProfile Llamaish() => new ModelFamilyProfile
        {
            Family = "llama",
            EmitsThinkBlocks = false,
            TemplateLiteral = false,
            VisionCapable = false,
            ThinkTokenHeadroom = 128,
            PreferredTier = "",
            Note = "Llama/GPT-OSS generic — standard handling",
        };

        private static ModelFamilyProfile VisionOnly() => new ModelFamilyProfile
        {
            Family = "vision",
            EmitsThinkBlocks = false,
            TemplateLiteral = true,
            VisionCapable = true,
            ThinkTokenHeadroom = 0,
            PreferredTier = "small", // llava/moondream captions, not actors
            Note = "Vision captioner — treat as small",
        };

        private static ModelFamilyProfile Reasoner(string family) => new ModelFamilyProfile
        {
            Family = family,
            EmitsThinkBlocks = true,
            TemplateLiteral = false,
            VisionCapable = false,
            ThinkTokenHeadroom = 1024,
            PreferredTier = "",
            Note = "DeepSeek-style reasoner — strips markers, generous headroom",
        };

        /// <summary>
        /// Token-budget floor: reasoning models burn tokens on hidden reasoning
        /// BEFORE the answer, so the user's value only stands when it already covers
        /// defaultValue + the family's think headroom. Families with no reasoning
        /// overhead return the user value unchanged. Pure — unit-testable.
        /// </summary>
        public static int EffectiveMaxTokens(int userValue, int defaultValue, ModelFamilyProfile profile)
        {
            if (profile == null || profile.ThinkTokenHeadroom <= 0) return userValue;
            int floor = defaultValue + profile.ThinkTokenHeadroom;
            return userValue > floor ? userValue : floor;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------
        // Reasoning-marker (CoT) handling
        // ------------------------------------------------------------------
        // MARKER HYGIENE: every marker constant below is assembled from string
        // fragments; the contiguous spellings must never be typed into this file
        // (see the file header). The runtime string is identical either way.

        /// <summary>Opener spelling A: the short reasoning marker.</summary>
        private const string TkOpen  = "<" + "think" + ">";
        /// <summary>Closer spelling A.</summary>
        private const string TkClose = "<" + "/think" + ">";
        /// <summary>Opener spelling B: the long reasoning marker.</summary>
        private const string TgOpen  = "<" + "thinking" + ">";
        /// <summary>Closer spelling B.</summary>
        private const string TgClose = "<" + "/thinking" + ">";

        // Paired-block strippers (non-greedy, case-insensitive, dot matches newline).
        private static readonly System.Text.RegularExpressions.Regex PairedThink =
            new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(TkOpen)
                    + ".*?" + System.Text.RegularExpressions.Regex.Escape(TkClose),
                System.Text.RegularExpressions.RegexOptions.Singleline
                    | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex PairedThinking =
            new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(TgOpen)
                    + ".*?" + System.Text.RegularExpressions.Regex.Escape(TgClose),
                System.Text.RegularExpressions.RegexOptions.Singleline
                    | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static int MinIdx(int a, int b)
        {
            if (a < 0) return b;
            if (b < 0) return a;
            return a < b ? a : b;
        }

        /// <summary>
        /// Strip reasoning scaffolding from a model reply. Handles the shapes
        /// observed on local OpenAI-compatible servers:
        /// 1) paired opener/closer runs (all spellings) — removed wholesale;
        /// 2) a closer WITHOUT an opener still before it: some templates/servers
        ///    consume the opener marker as a special token while the closer leaks
        ///    through as plain text (common with the Qwen3 class) — drop everything
        ///    up to and including the FIRST closer and keep what follows;
        /// 3) an opener WITHOUT a closer: the token cap cut the reply mid-reasoning —
        ///    templates put analysis first, so keep any text before the opener as a
        ///    salvaged fragment, otherwise return "" so the existing empty-reply
        ///    retry paths take over.
        /// Plain replies pass through trimmed (empty result normalizes to "").
        /// </summary>
        public static string StripThinkBlocks(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return reply;
            string s = PairedThink.Replace(reply, "");
            s = PairedThinking.Replace(s, "");

            // First remaining marker of each kind after the paired pass.
            int oTk = s.IndexOf(TkOpen, System.StringComparison.OrdinalIgnoreCase);
            int oTg = s.IndexOf(TgOpen, System.StringComparison.OrdinalIgnoreCase);
            int cTk = s.IndexOf(TkClose, System.StringComparison.OrdinalIgnoreCase);
            int cTg = s.IndexOf(TgClose, System.StringComparison.OrdinalIgnoreCase);
            int open = MinIdx(oTk, oTg);
            int close = MinIdx(cTk, cTg);

            if (close >= 0 && (open < 0 || close < open))
            {
                // Closer without a still-open opener: the opener was consumed as a
                // special token while the closer leaked as text — the visible answer
                // is everything after the first closer. (Both spellings searched;
                // whichever won tells us the tag length to skip.)
                s = s.Substring(close + (close == cTk ? TkClose.Length : TgClose.Length));
                oTk = s.IndexOf(TkOpen, System.StringComparison.OrdinalIgnoreCase);
                oTg = s.IndexOf(TgOpen, System.StringComparison.OrdinalIgnoreCase);
                open = MinIdx(oTk, oTg);
            }
            if (open >= 0)
            {
                // Unclosed opener: the token cap cut the reply mid-reasoning.
                // Templates put analysis first, so anything in front of the opener
                // is a salvaged out-of-order fragment; an empty prefix means the
                // whole reply was reasoning, and "" lets retry paths take over.
                s = s.Substring(0, open);
            }

            s = s.Trim();
            return s.Length > 0 ? s : "";
        }

        /// <summary>True when the reply carries visible reasoning scaffolding (diagnostics).</summary>
        public static bool HasThinkBlocks(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return false;
            return reply.IndexOf("<" + "think", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}