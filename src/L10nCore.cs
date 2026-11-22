// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// L10nCore — the localization lookup core, pure C# (unit-tested).
//
// Design (2026-11-21, Rose: "localization support for the whole mod"):
//   * Keys ARE the English strings (no key registry to keep in sync) — a
//     translation file maps English text → translated text, and a missing
//     key falls back to the English source. en.json is the template.
//   * Locale resolution follows the GAME's own Unity Localization locale —
//     the mod speaks whatever language the player picked for KoboldKare.
//   * Files are plain flat JSON: BepInEx/plugins/KKLLMNPC/lang/<code>.json
//     ("de.json", "pt-BR.json", …). Anyone can contribute a language without
//     touching code; unknown keys and unknown locales are always safe.
//   * NOT localized (on purpose): the LLM-facing text (perception keys, world
//     events, prompts — English is the protocol, translating it would degrade
//     every model), config descriptions (developer docs), log lines. What the
//     player-facing surfaces get: the options-menu tabs/labels, the spectate
//     HUD, and the kobolds' SPOKEN language via [LLM] Language prompt steering.
//
// Build: mcs -target:exe -out:/tmp/t_l10n.exe tests/test_l10n.cs src/L10nCore.cs src/Json.cs && mono /tmp/t_l10n.exe
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    public static class L10nCore
    {
        public const string FallbackLocale = "en";

        // Merge a parsed flat JSON object (string→string) into one locale's
        // table. Nested objects are flattened as "outer.inner" so a translator
        // can group visually without breaking the flat lookup. Non-string
        // values are skipped (a warning callback gets them).
        public static void Merge(Dictionary<string, Dictionary<string, string>> tables,
            string locale, Dictionary<string, object> values, Action<string> warn)
        {
            if (string.IsNullOrEmpty(locale) || values == null) return;
            locale = Normalize(locale) ?? locale; // "pt-BR" and "pt-br" are one table
            lock (tables)
            {
                if (!tables.TryGetValue(locale, out var t) || t == null)
                {
                    t = new Dictionary<string, string>(StringComparer.Ordinal);
                    tables[locale] = t;
                }
                foreach (var kv in values)
                {
                    if (kv.Value is string s)
                    {
                        t[kv.Key] = s;
                        continue;
                    }
                    if (kv.Value is Dictionary<string, object> nested)
                    {
                        foreach (var n in nested)
                            if (n.Value is string s2) t[kv.Key + "." + n.Key] = s2;
                            else warn?.Invoke(locale + ": non-string value for " + kv.Key + "." + n.Key);
                        continue;
                    }
                    warn?.Invoke(locale + ": non-string value for " + kv.Key);
                }
            }
        }

        // The locale lookup chain: exact ("pt-br") → base language ("pt") →
        // English → the English key itself (identity — English is built in).
        public static string Translate(Dictionary<string, Dictionary<string, string>> tables,
            string locale, string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string code = Normalize(locale);
            if (code != null)
            {
                lock (tables)
                {
                    for (string l = code; l != null; l = BaseOf(l))
                    {
                        if (tables.TryGetValue(l, out var t) && t != null
                            && t.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                            return v;
                    }
                }
            }
            return key; // English-as-key: a missing translation IS the English text
        }

        // "pt-BR" → "pt-br"; "de" → "de"; null/empty → null (English identity).
        public static string Normalize(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return null;
            string c = locale.Trim().ToLowerInvariant().Replace('_', '-');
            return c.Length == 0 ? null : c;
        }

        // "pt-br" → "pt"; "pt" → null; "en-us" → "en".
        public static string BaseOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            int dash = code.IndexOf('-');
            if (dash <= 0) return null;
            string b = code.Substring(0, dash);
            return b.Length == 0 ? null : b;
        }

        // {0}/{1}… substitution; a null template passes through, missing
        // placeholders stay literal (translators can't crash the UI).
        public static string Fmt(string template, params object[] args)
        {
            if (string.IsNullOrEmpty(template) || args == null || args.Length == 0) return template;
            for (int i = 0; i < args.Length; i++)
            {
                string token = "{" + i + "}";
                if (template.IndexOf(token, StringComparison.Ordinal) < 0) continue;
                template = template.Replace(token, args[i] != null ? args[i].ToString() : "");
            }
            return template;
        }

        // Parse one lang file's JSON text into a flat dict (via the mod's
        // own Json parser — no dependencies). Returns null on garbage.
        public static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return Json.Parse(json) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }
    }
}
