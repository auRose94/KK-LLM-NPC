// Unit tests for PromptSource — the prompt provenance decision behind the
// 2026-11-21 cleanup ("prompts read from files, not the config"). Pins the
// migration contract: an inline value equal to the file or embedded copy is
// the auto-written legacy default and is IGNORED; only a genuinely different
// inline text wins; file beats embedded; everything missing → null.
// Run via run_tests.sh (suite "promptresolve").
using System;
using System.IO;
using KKLLMNPC;

internal static class TestPromptResolve
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

    private static void True(bool b, string what)
    {
        if (!b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    public static void Main()
    {
        const string file = "PROMPT FROM FILE";
        const string embedded = "PROMPT FROM EMBEDDED RESOURCE";

        // ---- THE migration case: legacy blob stored in config == embedded copy
        // (old installs auto-wrote the whole prompt as the config default) —
        // recognized as untouched and ignored, the file flow applies. ----
        Eq(file, PromptSource.Choose(embedded, file, embedded),
            "legacy blob (== embedded) ignored → file used");
        Eq(embedded, PromptSource.Choose(embedded, null, embedded),
            "legacy blob (== embedded), no file → embedded (identical content anyway)");

        // ---- genuinely customized inline wins over everything ----
        Eq("MY CUSTOM TEXT", PromptSource.Choose("MY CUSTOM TEXT", file, embedded),
            "genuine inline override wins");
        Eq("MY CUSTOM TEXT", PromptSource.Choose("MY CUSTOM TEXT", null, embedded),
            "genuine inline override wins without a file");
        // a blob that matches the FILE (user copy-pasted) is not an override
        Eq(file, PromptSource.Choose(file, file, embedded), "inline == file → not custom → file");

        // ---- precedence: file beats embedded ----
        Eq(file, PromptSource.Choose(null, file, embedded), "file wins over embedded");
        Eq(file, PromptSource.Choose("", file, embedded), "empty inline → file");
        Eq(file, PromptSource.Choose("   ", file, embedded), "whitespace inline → file");

        // ---- embedded is the never-missing fallback ----
        Eq(embedded, PromptSource.Choose(null, null, embedded), "no file → embedded");
        Eq(null, PromptSource.Choose(null, null, null), "everything missing → null");
        Eq(null, PromptSource.Choose("", "  ", ""), "blank everything → null");

        // ---- shipped-name guard: custom paths never resolve to embedded ----
        True(PromptSource.IsShippedPromptName("system_prompt_default.txt"), "shipped name recognized");
        True(PromptSource.IsShippedPromptName("system_prompt_console.txt"), "console shipped name");
        True(PromptSource.IsShippedPromptName("system_prompt_vision.txt"), "vision shipped name");
        True(PromptSource.IsShippedPromptName("system_prompt_name.txt"), "name shipped name");
        True(!PromptSource.IsShippedPromptName("my_custom_prompt.txt"), "custom path NOT a shipped name");
        True(!PromptSource.IsShippedPromptName(null), "null path");
        True(!PromptSource.IsShippedPromptName("SYSTEM_PROMPT_DEFAULT.TXT"), "case differs → not shipped");

        // ---- embedded lookup on the REAL assembly (these tests run inside the
        // compiled test exe — the mod's own resources aren't present, so the
        // lookup must fail SAFE, never throw) ----
        True(PromptSource.Embedded("system_prompt_default.txt") == null,
            "missing resource → null (test assembly has no embedded prompts)");
        True(PromptSource.Embedded(null) == null, "null name → null");
        True(PromptSource.Embedded("") == null, "empty name → null");
        Eq(0, PromptSource.EmbeddedExtraNames().Length, "no extras in the test assembly → empty list");

        if (_failed == 0) Console.WriteLine("test_promptresolve: all OK");
        else Console.WriteLine("test_promptresolve: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}
