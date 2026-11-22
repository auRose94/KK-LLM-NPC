// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// L10n — the localization runtime (Unity side), riding the game's own locale.
//
// Files: BepInEx/plugins/KKLLMNPC/lang/<code>.json — flat JSON, English-text
// keys ("Keys ARE the English strings" — see L10nCore). en.json is written
// automatically on first boot as the translator template; any <locale>.json
// overrides per key, everything missing falls back to English. The locale
// follows KoboldKare's own language setting (Unity Localization) — switching
// the game's language switches the mod's user-facing text.
//
// What is localized: the options-menu tabs/labels, the spectate HUD, and
// (via [LLM] Language) the kobolds' SPOKEN language. What deliberately is
// NOT: LLM-facing protocol text (perception/world events/prompts — English
// is the protocol; translating it would degrade every model), config
// descriptions, logs. See docs/L10N.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

namespace KKLLMNPC
{
    internal static class L10n
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static string[] _roots;
        private static bool _loaded;
        private static bool _templateWritten;

        // The lang directory (next to the DLL), plus the game dir as a fallback
        // root — same search shape as TryReadPromptFile.
        internal static string LangDir
        {
            get
            {
                try
                {
                    string asm = Path.GetDirectoryName(typeof(NPCInstance).Assembly.Location);
                    if (!string.IsNullOrEmpty(asm)) return Path.Combine(asm, "KKLLMNPC", "lang");
                }
                catch (Exception) { }
                return Path.Combine("BepInEx", "plugins", "KKLLMNPC", "lang");
            }
        }

        internal static void Ensure(LLMNPCPlugin plugin)
        {
            _roots = null; // re-resolve on next load
            _loaded = false;
        }

        // Scan every root for <code>.json files and merge. Missing/corrupt
        // files log once and are skipped — localization can never take the
        // mod down (everything falls back to English).
        internal static void Reload()
        {
            lock (_lock)
            {
                _loaded = true;
                Tables.Clear();
                if (_roots == null)
                {
                    var r = new List<string> { LangDir };
                    try
                    {
                        string data = UnityEngine.Application.dataPath;
                        if (!string.IsNullOrEmpty(data))
                        {
                            string gameDir = Path.GetDirectoryName(data);
                            if (!string.IsNullOrEmpty(gameDir)) r.Add(Path.Combine(gameDir, "BepInEx", "plugins", "KKLLMNPC", "lang"));
                        }
                    }
                    catch (Exception) { }
                    _roots = r.ToArray();
                }
                int files = 0;
                try
                {
                    foreach (var root in _roots)
                    {
                        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                        foreach (var file in Directory.GetFiles(root, "*.json"))
                        {
                            string code = Path.GetFileNameWithoutExtension(file);
                            if (string.IsNullOrEmpty(code)) continue;
                            string json;
                            try { json = File.ReadAllText(file, Encoding.UTF8); }
                            catch (Exception e) { Warn("lang: cannot read " + file + ": " + e.Message); continue; }
                            var parsed = L10nCore.ParseJson(json);
                            if (parsed == null) { Warn("lang: not valid JSON, skipped: " + file); continue; }
                            L10nCore.Merge(Tables, code, parsed, m => Warn("lang: " + m));
                            files++;
                        }
                    }
                }
                catch (Exception e) { Warn("lang: scan failed: " + e.Message); }
                if (files > 0)
                {
                    try { LLMNPCPlugin.Log.LogInfo("lang: loaded " + files + " language file(s) from " + string.Join(", ", _roots)); }
                    catch (Exception) { }
                }
            }
        }

        private static void Warn(string msg)
        {
            try { LLMNPCPlugin.Log.LogWarning(msg); } catch (Exception) { }
        }

        // The game's current locale code ("en", "de", "pt-BR", …) — follows
        // KoboldKare's own language setting.
        internal static string CurrentLocale()
        {
            try
            {
                var loc = LocalizationSettings.SelectedLocale;
                string code = loc != null ? loc.Identifier.Code : null;
                return string.IsNullOrEmpty(code) ? L10nCore.FallbackLocale : code;
            }
            catch (Exception) { return L10nCore.FallbackLocale; }
        }

        // Translate for the CURRENT game locale (English-text key).
        internal static string T(string english)
        {
            return T(english, CurrentLocale());
        }

        // Translate for an explicit locale — the per-locale options-table
        // injection uses this once per available locale.
        internal static string T(string english, string localeCode)
        {
            if (!_loaded) Reload();
            return L10nCore.Translate(Tables, localeCode, english);
        }

        // Write en.json (the translator template: every known string mapped to
        // itself) on first boot so translators have a starting file on disk.
        // keys = the mod's full English string inventory (Options tab labels,
        // tab titles, HUD templates).
        internal static void WriteEnTemplate(IEnumerable<string> keys)
        {
            if (_templateWritten) return;
            _templateWritten = true;
            try
            {
                string dir = LangDir;
                string file = Path.Combine(dir, "en.json");
                if (File.Exists(file)) return; // never clobber a translator's work
                Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("{\n");
                bool first = true;
                foreach (var k in keys)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("  ").Append(Json.Write(k)).Append(": ").Append(Json.Write(k));
                }
                sb.Append("\n}\n");
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                try { LLMNPCPlugin.Log.LogInfo("lang: wrote the en.json translator template to " + file); } catch (Exception) { }
            }
            catch (Exception e) { Warn("lang: template write failed: " + e.Message); }
        }

        // [LLM] Language — the kobolds' SPOKEN-language steering. "auto"/empty
        // = no directive (the model matches the humans around it). Everything
        // else becomes a hard directive appended to every persona (act +
        // console): the protocol stays English, the speech does not.
        internal static string SpeechDirective(ConfigEntry<string> cfg)
        {
            string lang = cfg != null ? (cfg.Value ?? "").Trim() : "";
            if (lang.Length == 0 || lang.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || lang.Equals("off", StringComparison.OrdinalIgnoreCase)) return "";
            return "\nLANGUAGE: Every chat line you write (say/echo) MUST be written in " + lang +
                   ". Your understanding of commands, perception and tool results stays English — only the words you SPEAK are in " + lang +
                   ". Keep proper nouns (names, item names, station kinds) as they are.";
        }
    }
}
