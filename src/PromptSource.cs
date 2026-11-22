// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// PromptSource — where prompts come from, pure C# (unit-tested).
//
// The 2026-11-21 cleanup (Rose: "prompts read from a separate file, not the
// config"): every prompt previously existed in THREE places — the repo's
// system_prompt_*.txt (source of truth), the copy build.sh ships, AND a full
// copy as a BepInEx config default — so every install's config file carried
// a ~23KB blob. Now:
//
//   * config keeps only PATHS ([LLM] SystemPromptFile, [Console]
//     SystemPromptFile, [Vision] PromptFile, [LLM] NamePromptFile) plus a
//     deprecated inline override whose default is EMPTY;
//   * the prompt .txt files are compiled into the DLL as embedded resources
//     ("KKLLMNPC.prompts.<filename>") — the on-disk copy stays the editable
//     override, the embedded copy is the never-missing fallback, and the
//     config blob is gone;
//   * Choose() implements the migration honestly: old installs have the
//     legacy blob STORED as their SystemPrompt value — it equals the embedded
//     copy, so it is recognized as the untouched default (not a customization)
//     and ignored, while a genuinely different inline value still wins.
//
// Build: mcs -target:exe -out:/tmp/t_ps.exe tests/test_promptresolve.cs src/PromptSource.cs && mono /tmp/t_ps.exe
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KKLLMNPC
{
    public static class PromptSource
    {
        // The decision. inline = the deprecated config text override;
        // fileContent = the on-disk prompt file (user-editable, wins);
        // embedded = the DLL resource (always shipped, never stale).
        public static string Choose(string inline, string fileContent, string embedded)
        {
            bool fileOk = !string.IsNullOrWhiteSpace(fileContent);
            bool embOk = !string.IsNullOrWhiteSpace(embedded);
            if (!string.IsNullOrWhiteSpace(inline))
            {
                // An inline value that matches the file or the embedded copy is
                // the auto-written legacy default, not a customization — skip it
                // so the file flow applies. Only a DIFFERENT text is a real override.
                bool sameFile = fileOk && inline == fileContent;
                bool sameEmb = embOk && inline == embedded;
                if (!sameFile && !sameEmb) return inline;
            }
            if (fileOk) return fileContent;
            if (embOk) return embedded;
            return null;
        }

        // Embedded prompt resource lookup. Resource names are
        // "KKLLMNPC.prompts.<filename>" (the four system_prompt_*.txt) and
        // "KKLLMNPC.prompts.extras.<filename>" (prompt_extras/*.txt).
        public static string Embedded(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            try
            {
                using (var s = typeof(PromptSource).Assembly.GetManifestResourceStream("KKLLMNPC.prompts." + fileName))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch (Exception) { return null; }
        }

        // Embedded fallback is ONLY for the shipped default names — an explicit
        // custom path that fails to load must stay null (a missing custom file
        // must never silently resolve to some other prompt).
        public static readonly string[] ShippedPromptNames =
        {
            "system_prompt_default.txt",
            "system_prompt_console.txt",
            "system_prompt_vision.txt",
            "system_prompt_name.txt",
        };

        public static bool IsShippedPromptName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            foreach (var n in ShippedPromptNames)
                if (string.Equals(n, fileName, StringComparison.Ordinal)) return true;
            return false;
        }

        // The manifest names of the embedded prompt_extras ("KKLLMNPC.prompts.extras.<file>").
        public static string[] EmbeddedExtraNames()
        {
            try
            {
                var all = typeof(PromptSource).Assembly.GetManifestResourceNames();
                var outp = new List<string>();
                foreach (var n in all)
                    if (n != null && n.StartsWith("KKLLMNPC.prompts.extras.", StringComparison.Ordinal))
                        outp.Add(n.Substring("KKLLMNPC.prompts.extras.".Length));
                outp.Sort(StringComparer.Ordinal);
                return outp.ToArray();
            }
            catch (Exception) { return new string[0]; }
        }
    }
}
