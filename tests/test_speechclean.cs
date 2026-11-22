// ADVERSARIAL tests for SpeechClean — the JSON-in-speech hygiene. Rose's ask:
// "try to use the systems wrong to see if we can't catch it. Try to break it
// on purpose." Every case here is a real model failure mode (field report
// 2026-11-21: roleplay quoted AS JSON reached the say tool and the player's
// chat). The pinned invariant: NO input shape ever results in a parseable
// JSON document reaching the player — nested or not, quoted or not, fenced
// or not; and genuine roleplay ({{user}} placeholders, prose braces) survives.
// Run via run_tests.sh (suite "speechclean").
using System;
using System.Collections.Generic;
using KKLLMNPC;

internal static class TestSpeechClean
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

    // THE INVARIANT — run against every adversarial input: the cleaned output
    // must never be a parseable JSON document (object or array). A kobold may
    // be silent, never a JSON parser's mouthpiece.
    private static void NeverJson(string input, string what)
    {
        string outp = SpeechClean.CleanSpokenJson(input);
        if (string.IsNullOrEmpty(outp)) return; // refusal is always acceptable
        bool looksShaped = (outp[0] == '{' && outp[outp.Length - 1] == '}')
            || (outp[0] == '[' && outp[outp.Length - 1] == ']');
        if (!looksShaped) return;
        var parsed = Json.Parse(outp);
        if (parsed is Dictionary<string, object> || parsed is List<object>)
        {
            _failed++;
            Console.WriteLine("FAIL INVARIANT " + what + ": JSON document reached chat: '" + outp + "'");
        }
    }

    public static void Main()
    {
        // ---- THE field-report hole: roleplay quoted AS JSON inside the say
        // field (the old cleaner returned the voice field UNCLEANED) ----
        Eq("*waves* hi there",
            SpeechClean.CleanSpokenJson("{\"say\": \"{\\\"roleplay\\\": {\\\"line\\\": \\\"*waves* hi there\\\"}}\"}"),
            "roleplay JSON nested in the say field unwraps to spoken words");
        Eq("*pants* Oh wow... okay, give me a second",
            SpeechClean.CleanSpokenJson("{\"say\": \"{\\\"response\\\": \\\"*pants* Oh wow... okay, give me a second\\\"}\"}"),
            "response-wrapped roleplay unwraps");
        // doubly nested — the model wrapping the wrapper
        Eq("deep breath, and in",
            SpeechClean.CleanSpokenJson("{\"say\": \"{\\\"rp\\\": \\\"{\\\\\\\"line\\\\\\\": \\\\\\\"deep breath, and in\\\\\\\"}\\\"}\"}"),
            "doubly-nested JSON unwraps through the depth chain");
        // roleplay JSON with NO voice field inside → refusal, not machinery
        Eq("", SpeechClean.CleanSpokenJson("{\"say\": \"{\\\"data\\\": {\\\"x\\\": 1}}\"}"),
            "voice field that is pure data → refuse, never speak JSON");
        // the voice key itself named "roleplay" (the field report's shape)
        Eq("*tail wag* hello!",
            SpeechClean.CleanSpokenJson("{\"roleplay\": \"*tail wag* hello!\"}"),
            "voice key 'roleplay' spoken");

        // ---- whole act object as the say text: the voice field extracts ----
        Eq("*waves*", SpeechClean.CleanSpokenJson(
            "{\"progress\":\"done\",\"why\":\"player waved\",\"thought\":\"wave back\",\"action\":\"say\",\"say\":\"*waves*\"}"),
            "full act object as say text → the say value speaks");

        // ---- quoted JSON string as the whole text ----
        Eq("", SpeechClean.CleanSpokenJson("\"{\\\"progress\\\": \\\"done\\\", \\\"action\\\": \\\"say\\\"}\""),
            "quoted act JSON → refuse (no voice field worth speaking)");
        Eq("*waves*", SpeechClean.CleanSpokenJson("\"{\\\"say\\\": \\\"*waves*\\\"}\""),
            "quoted say-echo JSON unwraps to the words");

        // ---- fenced variants ----
        Eq("hi!", SpeechClean.CleanSpokenJson("```json\n{\"say\": \"hi!\"}\n```"),
            "fenced say-echo unwraps");
        Eq("plain words", SpeechClean.CleanSpokenJson("```\nplain words\n```"),
            "fenced prose survives");

        // ---- arrays: never a JSON document in chat ----
        Eq("", SpeechClean.CleanSpokenJson("[{\"line\": \"*waves*\"}, {\"line\": \"hi\"}]"),
            "array-of-objects say → refuse");
        Eq("", SpeechClean.CleanSpokenJson("[{\"nested\": {\"deep\": {\"x\": 1}}}]"),
            "nested array say → refuse");

        // ---- mid-text JSON fragments (flat + NESTED — the old regex hole) ----
        // Strip semantics: mid-prose fragments are NOISE and vanish; only a
        // WHOLE-reply JSON gets the voice-field unwrap. (The first draft of
        // these tests expected unwrapping here — the tests were wrong, not the
        // stripper: extracting speech from a fragment embedded mid-sentence is
        // ambiguous, so it is stripped instead.)
        Eq("Hello!",
            SpeechClean.CleanSpokenJson("Hello! {\"action\":\"say\",\"say\":\"*waves*\"}"),
            "flat JSON fragment stripped from prose");
        Eq("Hello!",
            SpeechClean.CleanSpokenJson("Hello! {\"wrapper\": {\"nested\": {\"deep\": {\"say\": \"*waves*\"}}}}"),
            "NESTED JSON fragment stripped from prose (the old regex hole)");
        Eq("I choose the red one",
            SpeechClean.CleanSpokenJson("I choose {\"options\": {\"a\": \"red\"}} the red one"),
            "nested data in mid-prose stripped");

        // ---- what must SURVIVE ----
        Eq("*growls* back off, {{user}}!",
            SpeechClean.CleanSpokenJson("*growls* back off, {{user}}!"),
            "RP placeholders survive verbatim");
        Eq("I wrote {waves} in the sand",
            SpeechClean.CleanSpokenJson("I wrote {waves} in the sand"),
            "prose braces survive (no quoted-key colon look)");
        Eq("I need to find food", SpeechClean.CleanSpokenJson("I need to find food"),
            "ordinary sentence with a need passes");
        Eq("", SpeechClean.CleanSpokenJson("{\"progress\": \"done\"}"),
            "bare act fragment with no voice field → refuse");
        Eq("", SpeechClean.CleanSpokenJson(
            "We need to output a proper JSON action object with the schema keys."),
            "schema narration refused");

        // ---- mixed block: machinery lines dropped, roleplay kept ----
        Eq("*pants* oh wow",
            SpeechClean.CleanSpokenJson("{\"progress\":\"done\",\"why\":\"x\",\"thought\":\"y\",\"action\":\"say\",\"say\":\"*pants* oh wow\"}"),
            "act object with one voice field");

        // ---- THE INVARIANT over a battery of wrong usages ----
        string[] battery =
        {
            "{\"say\": \"{\\\"roleplay\\\": {\\\"line\\\": \\\"x\\\"}}\"}",
            "{\"say\": \"{\\\"a\\\": {\\\"b\\\": {\\\"c\\\": \\\"d\\\"}}}\"}",
            "{\"text\": \"{\\\"say\\\": \\\"{\\\\\\\"msg\\\\\\\": \\\\\\\"hi\\\\\\\"}\\\"}\"}",
            "[{\"say\": \"x\"}]",
            "{\"a\": {\"b\": {\"c\": {\"d\": {\"e\": \"f\"}}}}}",
            "{\"nested\": [{\"deep\": [{\"deeper\": \"value\"}]}]}",
            "prose {\"a\": {\"b\": \"c\"}} more prose {\"d\": {\"e\": \"f\"}} end",
            "{\"dialog\": \"{\\\"line\\\": \\\"*tail thump*\\\"}\"}",
            "```json\n[{\"a\": 1}]\n```",
            "{\"roleplay\": \"{\\\"roleplay\\\": \\\"{\\\\\\\"roleplay\\\\\\\": \\\\\\\"x\\\\\\\"}\\\"}\"}",
        };
        foreach (var input in battery)
            NeverJson(input, "battery: " + input.Substring(0, Math.Min(48, input.Length)));

        // sanity: the unwrapped roleplay from the battery still carried words
        Eq("*tail thump*", SpeechClean.CleanSpokenJson("{\"dialog\": \"{\\\"line\\\": \\\"*tail thump*\\\"}\"}"),
            "dialog voice key unwraps");

        if (_failed == 0) Console.WriteLine("test_speechclean: all OK");
        else Console.WriteLine("test_speechclean: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}
