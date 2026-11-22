// Unit tests for L10nCore — the localization lookup core behind the mod's
// player-facing surfaces (options-menu tabs/labels, spectate HUD). Pins the
// contract the translator docs promise: English-text keys, per-locale JSON
// files, exact→base→en fallback chain, identity fallback for missing keys,
// {0} formatting that can't crash on bad templates.
// Run via run_tests.sh (suite "l10n").
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestL10n
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
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // ---- merge + nested flattening ----
        var de = L10nCore.ParseJson("{\"Temperature\":\"Temperatur\",\"hud\":{\"inside\":\"du bist in {0} — Zuschauen\"}}");
        True(de != null, "json parse of a flat+nested file");
        L10nCore.Merge(tables, "de", de, m => { _failed++; Console.WriteLine("FAIL unexpected warn: " + m); });
        Eq("Temperatur", L10nCore.Translate(tables, "de", "Temperature"), "de lookup");
        Eq("du bist in {0} — Zuschauen", L10nCore.Translate(tables, "de", "hud.inside"), "nested key flattened");

        // ---- fallback chain: pt-BR → pt → en → identity ----
        var pt = L10nCore.ParseJson("{\"Temperature\":\"Temperatura\"}");
        L10nCore.Merge(tables, "pt", pt, null);
        var ptBr = L10nCore.ParseJson("{\"hud.inside\":\"você está dentro de {0} — assistindo\"}");
        L10nCore.Merge(tables, "pt-BR", ptBr, null);
        Eq("você está dentro de {0} — assistindo", L10nCore.Translate(tables, "pt-BR", "hud.inside"), "exact locale wins");
        Eq("Temperatura", L10nCore.Translate(tables, "pt-BR", "Temperature"), "pt-BR falls to pt");
        Eq("Max tokens", L10nCore.Translate(tables, "pt-BR", "Max tokens"), "missing everywhere → English key identity");
        Eq("Model", L10nCore.Translate(tables, "de", "Model"), "de missing key → identity");

        // ---- locale normalization ----
        Eq("de", L10nCore.Normalize("DE"), "upper case code normalized");
        Eq("de-de", L10nCore.Normalize("de_DE"), "underscore form → dialect code (the chain resolves it)");
        Eq("pt", L10nCore.BaseOf("pt-br"), "base of pt-br");
        True(L10nCore.BaseOf("pt") == null, "base of bare code is null");
        True(L10nCore.Normalize(null) == null, "null locale → null");
        Eq("Max tokens", L10nCore.Translate(tables, null, "Max tokens"), "null locale → identity");

        // ---- merge override (a file may fix any key) ----
        var de2 = L10nCore.ParseJson("{\"Temperature\":\"TEMP\"}");
        L10nCore.Merge(tables, "de", de2, null);
        Eq("TEMP", L10nCore.Translate(tables, "de", "Temperature"), "later merge overrides");

        // ---- non-string values warn, never throw ----
        int warned = 0;
        var bad = L10nCore.ParseJson("{\"ok\":\"fine\",\"n\":5,\"nested\":{\"deep\":\"yes\",\"bad\":true}}");
        L10nCore.Merge(tables, "xx", bad, m => warned++);
        Eq(2, warned, "non-string values warned");
        True(L10nCore.Translate(tables, "xx", "ok") == "fine", "string values kept");
        True(L10nCore.Translate(tables, "xx", "nested.deep") == "yes", "nested string kept");
        True(L10nCore.Translate(tables, "xx", "n") == "n", "non-string key dropped (identity)");

        // ---- garbage json → null, no throw ----
        True(L10nCore.ParseJson("not json {") == null, "garbage json → null");
        True(L10nCore.ParseJson(null) == null, "null json → null");
        True(L10nCore.ParseJson("   ") == null, "blank json → null");

        // ---- formatting ----
        Eq("you are inside Kira — watching",
            L10nCore.Fmt("you are inside {0} — watching", "Kira"), "fmt single arg");
        Eq("42%", L10nCore.Fmt("{0}%", 42), "fmt int arg");
        Eq("{0}", L10nCore.Fmt("{0}"), "no args → template passthrough");
        Eq(null == L10nCore.Fmt(null, "x") ? "null" : "not-null", "null", "null template → null");
        Eq("x {1}", L10nCore.Fmt("x {1}", "a"), "missing placeholder stays literal");

        if (_failed == 0) Console.WriteLine("test_l10n: all OK");
        else Console.WriteLine("test_l10n: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}
