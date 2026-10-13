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
        /// Server-facing fields that request HIDDEN-REASONING suppression, one
        /// shape per known local server. Sent into every chat payload when
        /// [LLM] Thinking=off (merged by NPCInstance.ApplyThinkingParams); any
        /// field a server doesn't recognize is skipped by that server, so the
        /// shapes coexist harmlessly:
        ///   think=false                — Ollama (>= 0.9.3) reasoning toggle;
        ///                                false is accepted for non-thinking
        ///                                models too (no-op there)
        ///   chat_template_kwargs:{...} — llama.cpp / vLLM / SGLang: feeds the
        ///                                Qwen3-convention {enable_thinking}
        ///                                template variable directly
        ///   reasoning_budget=0         — llama.cpp hybrid-reasoning models
        ///                                (0 = thinking disabled)
        /// LM Studio / KoboldCpp expose the toggle in their model GUI; unknown
        /// request fields are ignored there. Client-side think-strip still
        /// catches any markers that leak anyway. Pure — unit-testable.
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, object> ThinkingOffPayloadPart()
        {
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["think"] = false,
                ["chat_template_kwargs"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["enable_thinking"] = false,
                },
                ["reasoning_budget"] = 0,
            };
        }

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

        // ------------------------------------------------------------------
        // Unmarked-CoT name salvage
        // ------------------------------------------------------------------
        // Some templates print the ENTIRE reasoning pass as plain text with no
        // reasoning markers at all — StripThinkBlocks has nothing to strip, and
        // with a tight token budget the reply dies mid-monologue so the name
        // never appears (observed 2026-10 on local name selection: three full
        // replies of "Here's a thinking process: 1. **Analyze the
        // constraints:**…" each cut off mid-sentence). When the reply DID
        // complete, the reasoning conventionally ends with a labeled or verbal
        // conclusion, so the last trustworthy candidate can be read out of it.

        /// <summary>Words reasoning text is full of — a candidate matching one
        /// of these is the model restating the prompt (or grammar), not
        /// answering. Includes the name-prompt's own vocabulary so echoed
        /// constraint bullets can't pass ("Name: one kobold character",
        /// "Gender: female.").</summary>
        private static readonly System.Collections.Generic.HashSet<string> CoTStopwords =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "the","a","an","i","me","my","we","you","your","she","he","her","him","his","it",
                "its","they","them","their","this","that","these","those","myself",
                "is","are","am","be","been","being","was","were","will","would","shall","should",
                "can","could","may","might","must","do","does","did","doing",
                "to","of","and","or","in","on","with","for","from","by","at","as","if","so","but",
                "then","than","when","what","which","who","whom","how","why","not","no","nor",
                "out","up","down","over","under","about","into","onto",
                "name","names","named","answer","answers","choice","choices","final","chosen",
                "pick","picked","thinking","process","analysis","constraints","constraint",
                "taken","use","used","using","reply","only","letter","letters","digit","digits",
                "never","number","numbers","attached","invent","actual","kobold","kobolds",
                "character","gender","species","personality","female","male","unknown",
                "house","setting","drink","eat",
            };

        /// <summary>
        /// Read the final name out of a reply that is visible chain-of-thought
        /// with NO reasoning markers (StripThinkBlocks has nothing to strip).
        /// Only call this on COMPLETE replies (finish_reason != "length"): a
        /// reply cut mid-reasoning has no answer in it at all, and any
        /// "Name: X" line it does show may have lost its tail mid-candidate.
        /// Scans from the last line backward — reasoning ends with its
        /// conclusion — and accepts three end-anchored shapes:
        /// labeled ("Final Answer: Ember" / "Name is: Ember"), verbal
        /// ("I'll call her Ember"), or bare (the very last line is a single
        /// token after markdown/list markers are stripped). Constraint
        /// restatements can't slip through: every shape is end-of-line
        /// anchored, and candidates matching prompt-echo stopwords, already-
        /// taken names, or the species unit label are dropped.
        /// Pure — unit-testable (tests/test_modelfamilies.cs).
        /// </summary>
        public static string SalvageNameFromCoT(string content, string takenList, string species)
        {
            if (string.IsNullOrEmpty(content)) return null;
            string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var taken = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(takenList))
                foreach (string t in takenList.Split(','))
                {
                    string trimmed = t.Trim();
                    if (trimmed.Length > 0) taken.Add(trimmed);
                }

            int lastNonEmpty = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Trim().Length > 0) { lastNonEmpty = i; break; }

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                string candidate = ValidSalvageCandidate(LabeledAnswerCandidate(line), taken, species);
                if (candidate != null) return candidate;
                candidate = ValidSalvageCandidate(VerbalAnswerCandidate(line), taken, species);
                if (candidate != null) return candidate;
                if (i == lastNonEmpty)
                {
                    candidate = ValidSalvageCandidate(BareTokenCandidate(line), taken, species);
                    if (candidate != null) return candidate;
                }
            }
            return null;
        }

        /// <summary>True when candidate is the species word (± digits/hyphen
        /// tail — "Kobold", "Kobold2", "Deuzear-3"): a unit label, not a name.
        /// Single source of truth for the Llm-side validator and the salvage
        /// filter. Pure.</summary>
        public static bool IsSpeciesUnitLabel(string candidate, string species)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(species)) return false;
            string spLower = species.ToLowerInvariant();
            if (spLower.Length == 0) return false;
            if (!candidate.ToLowerInvariant().StartsWith(spLower, System.StringComparison.Ordinal)) return false;
            string tail = candidate.Substring(spLower.Length).TrimStart('-');
            return tail.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(tail, "^[0-9]+$");
        }

        /// <summary>Meta-replies to a "pick a name" prompt: the model TALKED ABOUT
        /// choosing instead of choosing ("I want some time to think about it",
        /// "let me consider…" — the field playtest's most common name-selection
        /// failure). Detected after every other salvage pass failed, so it's pure
        /// diagnostics + the caller re-rolls with a hard "answer NOW" demand.
        /// Pure text; unit-testable.</summary>
        public static bool LooksLikeNameMeta(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return false;
            string low = reply.ToLowerInvariant();
            // Keep it narrow: hesitation/deferral/permission phrases, not any text
            // containing "think" (a salvageable CoT legitimately contains it).
            return low.Contains("time to think") || low.Contains("need time") || low.Contains("want time")
                || low.Contains("let me think") || low.Contains("give me a moment")
                || low.Contains("think about it") || low.Contains("come up with a name")
                || (low.Contains("hmm") && low.Contains("name"))
                || low.StartsWith("i'd like", System.StringComparison.Ordinal)
                || low.StartsWith("i would like", System.StringComparison.Ordinal)
                || low.StartsWith("sure, give me", System.StringComparison.Ordinal);
        }

        private static string ValidSalvageCandidate(string candidate,
            System.Collections.Generic.HashSet<string> taken, string species)
        {
            if (candidate == null || candidate.Length < 2 || candidate.Length > 12) return null;
            if (!System.Text.RegularExpressions.Regex.IsMatch(candidate, "^[a-zA-Z0-9]+$")) return null;
            if (CoTStopwords.Contains(candidate)) return null;
            if (taken.Contains(candidate)) return null;
            if (IsSpeciesUnitLabel(candidate, species)) return null;
            return candidate;
        }

        /// <summary>Candidate after the LAST ':' or '=' when the heading's
        /// last word is a label (name/answer/choice/pick/final) and the rest
        /// of the line is soft junk. Null otherwise.</summary>
        private static string LabeledAnswerCandidate(string line)
        {
            int colon = line.LastIndexOf(':');
            int eq = line.LastIndexOf('=');
            int sep = colon > eq ? colon : eq;
            if (sep < 1) return null;
            string head = line.Substring(0, sep).Trim().TrimEnd('*', '"', '\'');
            // Drop trailing copulas so "Name is:" / "Name will be:" still count
            // as labeled headings.
            head = System.Text.RegularExpressions.Regex.Replace(
                head, @"(\s+(?:is|will|be|am|'ll|to\s+be))+\s*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            string label = LastWord(head);
            if (label == null) return null;
            switch (label.ToLowerInvariant())
            {
                case "name":
                case "answer":
                case "choice":
                case "pick":
                case "final":
                    break;
                default:
                    return null;
            }
            return TrailingTokenCandidate(line.Substring(sep + 1));
        }

        /// <summary>Candidate from a "call her Ember" / "named him Puff" shape,
        /// verb + pronoun + candidate, end-of-line anchored. Null otherwise.</summary>
        private static string VerbalAnswerCandidate(string line)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line,
                @"(?:call|calls|calling|called|named|naming|name)\s+" +
                @"(?:her|him|them|it|myself|this\s+one|this\s+kobold|the\s+kobold|the\s+little\s+one)\s+" +
                @"[""']?([A-Za-z0-9]{2,12})[""']?[\s.,!;:\-*""']*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            return match.Groups[1].Value;
        }

        /// <summary>Candidate when the line is one bare token after markdown
        /// and list markers are stripped ("… 2. something short" / "Puddle").
        /// Null otherwise.</summary>
        private static string BareTokenCandidate(string line)
        {
            line = System.Text.RegularExpressions.Regex.Replace(
                line, @"^\s*(?:[-*>]+|[0-9]{1,3}[.)])\s*", "");
            line = line.Replace("*", "").Replace("_", "").Replace("\"", "").Replace("'", "").Trim();
            if (line.Length < 2 || line.Length > 12) return null;
            for (int i = 0; i < line.Length; i++)
                if (!char.IsLetterOrDigit(line[i])) return null;
            return line;
        }

        /// <summary>First alphanumeric token of the tail, accepted ONLY when
        /// the remainder is quote/bold/punctuation — mid-sentence candidates
        /// ("Name: one kobold character") fail this and return null.</summary>
        private static string TrailingTokenCandidate(string tail)
        {
            tail = tail.Trim();
            int start = 0;
            while (start < tail.Length && !char.IsLetterOrDigit(tail[start])) start++;
            int end = start;
            while (end < tail.Length && char.IsLetterOrDigit(tail[end])) end++;
            if (end == start) return null;
            string rest = tail.Substring(end).Trim();
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                if (char.IsLetterOrDigit(c)) return null;
                if (c == '.' && i + 1 < rest.Length && char.IsLetterOrDigit(rest[i + 1])) return null;
            }
            return tail.Substring(start, end - start);
        }

        /// <summary>Last word of s, trailing non-alphanumeric characters
        /// (bold markers, closing parens, colons) dropped. Null when s holds
        /// no word at all.</summary>
        private static string LastWord(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            int i = s.Length - 1;
            while (i >= 0 && !char.IsLetterOrDigit(s[i])) i--;
            if (i < 0) return null;
            int j = i;
            while (j >= 0 && (char.IsLetterOrDigit(s[j]) || s[j] == '-')) j--;
            return s.Substring(j + 1, i - j);
        }
    }
}