// Unit tests for Json.cs — hand-rolled JSON parser.
// These are standalone tests that can be run with any C# test runner (e.g., NUnit, xUnit).
// To run: compile with -r:nunit.framework.dll (or your test framework).
//
// This file is for documentation and future test automation.
// The parser is tested via integration with the plugin at runtime.

namespace KKLLMNPC.Tests
{
    using KKLLMNPC;
    using System;
    using System.Collections.Generic;

    public static class JsonTests
    {
        // --- Parse tests ---

        public static void TestParseNull()
        {
            var result = Json.Parse("null");
            if (result != null) throw new Exception("Parse(null) should return null");
        }

        public static void TestParseTrue()
        {
            var result = Json.Parse("true");
            if (!(result is bool b) || !b) throw new Exception("Parse(true) should return true");
        }

        public static void TestParseFalse()
        {
            var result = Json.Parse("false");
            if (!(result is bool b) || b) throw new Exception("Parse(false) should return false");
        }

        public static void TestParseInt()
        {
            var result = Json.Parse("42");
            if (!(result is long l) || l != 42) throw new Exception("Parse(42) should return 42");
        }

        public static void TestParseFloat()
        {
            var result = Json.Parse("3.14");
            if (!(result is double d) || Math.Abs(d - 3.14) > 0.001) throw new Exception("Parse(3.14) should return 3.14");
        }

        public static void TestParseString()
        {
            var result = Json.Parse("\"hello world\"");
            if (!(result is string s) || s != "hello world") throw new Exception("Parse(\"hello world\") failed");
        }

        public static void TestParseEmptyObject()
        {
            var result = Json.Parse("{}");
            if (!(result is Dictionary<string, object> dict) || dict.Count != 0)
                throw new Exception("Parse({}) should return empty dict");
        }

        public static void TestParseObject()
        {
            var result = Json.Parse("{\"name\":\"test\",\"value\":42}");
            if (!(result is Dictionary<string, object> dict))
                throw new Exception("Parse object failed");
            if (!dict.ContainsKey("name") || dict["name"] as string != "test")
                throw new Exception("Parse object: name mismatch");
            if (!dict.ContainsKey("value"))
                throw new Exception("Parse object: value missing");
        }

        public static void TestParseEmptyArray()
        {
            var result = Json.Parse("[]");
            if (!(result is System.Collections.Generic.List<object> list) || list.Count != 0)
                throw new Exception("Parse([]) should return empty list");
        }

        public static void TestParseArray()
        {
            var result = Json.Parse("[1, 2, 3]");
            if (!(result is System.Collections.Generic.List<object> list) || list.Count != 3)
                throw new Exception("Parse([1,2,3]) should return list of 3");
        }

        public static void TestParseNested()
        {
            var result = Json.Parse("{\"outer\": {\"inner\": \"value\"}}");
            if (!(result is Dictionary<string, object> dict))
                throw new Exception("Parse nested failed");
            var outer = dict["outer"] as Dictionary<string, object>;
            if (outer == null || outer["inner"] as string != "value")
                throw new Exception("Parse nested: inner value mismatch");
        }

        public static void TestParseTruncated()
        {
            // Truncated JSON should not throw (graceful degradation);
            // may return a partial result or null — both are acceptable.
            Json.Parse("{\"name\":\"test");
        }

        public static void TestParseWhitespace()
        {
            var result = Json.Parse("  {  }  ");
            if (!(result is Dictionary<string, object> dict) || dict.Count != 0)
                throw new Exception("Parse whitespace failed");
        }

        public static void TestWriteNull()
        {
            var result = Json.Write(null);
            if (result != "null") throw new Exception("Write(null) should return \"null\"");
        }

        public static void TestWriteString()
        {
            var result = Json.Write("hello");
            if (result != "\"hello\"") throw new Exception("Write(hello) should return \"hello\"");
        }

        public static void TestWriteBool()
        {
            if (Json.Write(true) != "true") throw new Exception("Write(true) failed");
            if (Json.Write(false) != "false") throw new Exception("Write(false) failed");
        }

        public static void TestWriteFloat()
        {
            // NaN and Infinity should serialize to null
            var nanResult = Json.Write(float.NaN);
            if (nanResult != "null") throw new Exception("Write(NaN) should return null");
            var infResult = Json.Write(float.PositiveInfinity);
            if (infResult != "null") throw new Exception("Write(Infinity) should return null");
        }

        public static void TestWriteAnonymous()
        {
            var obj = new { name = "test", value = 42 };
            var result = Json.Write(obj);
            if (!result.Contains("\"name\":\"test\"")) throw new Exception("Write anonymous: name missing");
            if (!result.Contains("\"value\":42")) throw new Exception("Write anonymous: value missing");
        }

        public static void TestWriteDictionary()
        {
            var dict = new Dictionary<string, object> { ["a"] = 1, ["b"] = "two" };
            var result = Json.Write(dict);
            if (!result.Contains("\"a\":1")) throw new Exception("Write dict: a missing");
            if (!result.Contains("\"b\":\"two\"")) throw new Exception("Write dict: b missing");
        }

        public static void TestWriteList()
        {
            var list = new System.Collections.Generic.List<object> { 1, "two", 3.0 };
            var result = Json.Write(list);
            if (!result.StartsWith("[") || !result.EndsWith("]")) throw new Exception("Write list should start/end with brackets");
        }

        public static void TestRoundTrip()
        {
            var original = new Dictionary<string, object>
            {
                ["name"] = "test",
                ["value"] = 42,
                ["nested"] = new Dictionary<string, object> { ["flag"] = true },
            };
            var json = Json.Write(original);
            var parsed = Json.Parse(json);
            if (!(parsed is Dictionary<string, object> roundTrip))
                throw new Exception("Round-trip parse failed");
            if ((roundTrip["name"] as string) != "test")
                throw new Exception("Round-trip name mismatch");
            if ((long)roundTrip["value"] != 42)
                throw new Exception("Round-trip value mismatch");
        }

        public static void TestJsonObjAccessors()
        {
            var obj = new JsonObj("{\"x\": 42, \"y\": \"hello\", \"z\": true}");
            if (obj.F("x") != 42f) throw new Exception("F(x) failed");
            if (obj.S("y") != "hello") throw new Exception("S(y) failed");
            if (!obj.B("z")) throw new Exception("B(z) failed");
            if (obj.F("missing", 99f) != 99f) throw new Exception("F(missing) default failed");
            if (!obj.Has("x")) throw new Exception("Has(x) should be true");
            if (obj.Has("missing")) throw new Exception("Has(missing) should be false");
        }

        // --- Writer tests: collection shapes that reach tool results (2026-11-04) ---
        // Json.Write is the one choke point every tool response flows through; before
        // the IDictionary/KeyValuePair handling, any dict whose value type wasn't
        // object serialized its entries as KeyValuePair structs via ToString()
        // ("[k, System.Collections.Generic.List`1[System.String]]") — the type-name
        // garbage the agent saw instead of JSON. These tests call the real writer.

        private const string TypeNameLeak = "System.";

        private static void AssertNoTypeLeak(string json)
        {
            if (json.Contains(TypeNameLeak)) throw new Exception("type name leaked into JSON: " + json);
        }

        public static void TestWriteDictStringValues()
        {
            var dict = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
            var result = Json.Write(dict);
            if (result != "{\"a\":\"1\",\"b\":\"2\"}") throw new Exception("Dictionary<string,string> misshapen: " + result);
        }

        public static void TestWriteDictNestedList()
        {
            var dict = new Dictionary<string, List<string>> { ["k"] = new List<string> { "x", "y" } };
            var result = Json.Write(dict);
            if (result != "{\"k\":[\"x\",\"y\"]}") throw new Exception("Dictionary<string,List<string>> misshapen: " + result);
            AssertNoTypeLeak(result);
        }

        public static void TestWriteDictMixedValues()
        {
            var dict = new Dictionary<string, int> { ["hp"] = 5, ["age"] = 3 };
            var result = Json.Write(dict);
            if (result != "{\"hp\":5,\"age\":3}") throw new Exception("Dictionary<string,int> misshapen: " + result);
        }

        public static void TestWriteHashtable()
        {
            var ht = new System.Collections.Hashtable { ["h"] = "v" };
            var result = Json.Write(ht);
            if (result != "{\"h\":\"v\"}") throw new Exception("Hashtable misshapen: " + result);
            AssertNoTypeLeak(result);
        }

        public static void TestWriteKeyValuePair()
        {
            var kv = new KeyValuePair<string, int>("hp", 5);
            var result = Json.Write(kv);
            if (!result.Contains("\"hp\"") || !result.Contains(":5") || result[0] != '{')
                throw new Exception("KeyValuePair should be an object with the pair data: " + result);
            AssertNoTypeLeak(result);
        }

        public static void TestWriteStringArray()
        {
            var result = Json.Write(new[] { "x", "y" });
            if (result != "[\"x\",\"y\"]") throw new Exception("string[] misshapen: " + result);
        }

        public static void TestWriteMixedArray()
        {
            var result = Json.Write(new object[] { 1, "two", true, null });
            if (result != "[1,\"two\",true,null]") throw new Exception("object[] misshapen: " + result);
        }

        public static void TestWriteEnumAndChar()
        {
            if (Json.Write(TestMode.Run) != "\"Run\"") throw new Exception("Enum should stringify as its name");
            if (Json.Write('a') != "\"a\"") throw new Exception("char should stringify as a string");
        }

        public static void TestWriteStructFields()
        {
            var result = Json.Write(new Vec2 { x = 1.5f, y = 2.5f });
            if (!result.Contains("\"x\":1.5") || !result.Contains("\"y\":2.5"))
                throw new Exception("struct public fields should serialize: " + result);
        }

        public static void TestWriteCyclicTerminates()
        {
            var a = new Dictionary<string, object>();
            a["self"] = a; // cyclic — must terminate, not stack-overflow
            var result = Json.Write(a);
            if (string.IsNullOrEmpty(result) || result[0] != '{')
                throw new Exception("cyclic dict should still produce an object: " + result);
            if (!result.Contains("…")) throw new Exception("cyclic dict should end in the depth-cap marker");
        }

        public static void TestRoundTripDictStringValues()
        {
            var original = new Dictionary<string, string> { ["a"] = "1", ["b"] = "two" };
            var parsed = Json.Parse(Json.Write(original)) as Dictionary<string, object>;
            if (parsed == null) throw new Exception("Dictionary<string,string> round-trip should parse to a dict (was a list of garbage before the fix)");
            if ((parsed["a"] as string) != "1" || (parsed["b"] as string) != "two")
                throw new Exception("Dictionary<string,string> round-trip values mismatch");
        }

        public static void TestTextNeverLeaksTypeNames()
        {
            // Json.Text is the single-value helper the console formatter and tool
            // briefs use — collections render as JSON, never "System.Collections…".
            if (Json.Text(null) != "") throw new Exception("Text(null) should be empty");
            if (Json.Text("plain") != "plain") throw new Exception("Text(string) should pass through");
            if (Json.Text(5) != "5") throw new Exception("Text(primitive) should stringify");
            var list = new List<string> { "x", "y" };
            var t = Json.Text(list);
            if (t != "[\"x\",\"y\"]") throw new Exception("Text(list) should render JSON: " + t);
            var nested = new Dictionary<string, object> { ["k"] = list };
            var t2 = Json.Text(nested);
            AssertNoTypeLeak(t2);
            if (!t2.Contains("\"k\"")) throw new Exception("Text(dict) should render JSON: " + t2);
        }

        // --- Parser tests: \uXXXX escapes (2026-11-04) ---
        // Go-based OpenAI-compatible servers (Ollama, OpenAI) HTML-escape <, >, &
        // as \u003c/\u003e/\u0026 in every response content string. ParseStr had
        // no \u case, so those arrived as literal "u003c" text — the field bug
        // where a model echoing the name prompt's "Name: <the name>" line got
        // registered as the NAME "u003cthe" (clean alphanumerics to the salvage
        // regex). These tests compile against the real src/Json.cs.

        public static void TestParseUnicodeAngleBracket()
        {
            // The exact Ollama-escaped shape of the echoed name template.
            var result = Json.Parse("\"Name: \\u003cthe name>\"") as string;
            if (result != "Name: <the name>") throw new Exception("\\u003c should decode to '<', got: " + result);
        }

        public static void TestParseUnicodeGoEscapes()
        {
            // Go encoding/json escapes all three HTML chars; hex case must not matter.
            var result = Json.Parse("\"\\u003c\\u003e\\u0026 \\u0041\\u00e9\\u00C9\"") as string;
            if (result != "<>& AéÉ") throw new Exception("Go HTML escapes / mixed hex case misshapen: " + result);
        }

        public static void TestParseUnicodeSurrogatePair()
        {
            // Astral characters arrive as two \u escapes — must decode to one
            // UTF-16 pair, not strand a lone high surrogate.
            var result = Json.Parse("\"\\ud83d\\ude00\"") as string;
            if (result == null || result.Length != 2 || !char.IsSurrogatePair(result[0], result[1]))
                throw new Exception("surrogate pair should decode to a paired UTF-16 unit");
            // And survive a write→parse round-trip.
            var rt = Json.Parse(Json.Write(result)) as string;
            if (rt != result) throw new Exception("surrogate pair round-trip mismatch: " + rt);
        }

        public static void TestParseUnicodeMalformedStaysLiteral()
        {
            // Lenient convention: bad hex is echoed as literal text, never throws.
            var r1 = Json.Parse("\"a\\uZZZZb\"") as string;
            if (r1 != "auZZZZb") throw new Exception("\\uZZZZ should stay literal, got: " + r1);
            var r2 = Json.Parse("\"\\u003x\"") as string;
            if (r2 != "u003x") throw new Exception("partial hex should stay literal, got: " + r2);
            var r3 = Json.Parse("\"\\u00\"") as string;
            if (r3 != "u00") throw new Exception("truncated \\u at end of string should stay literal, got: " + r3);
        }

        public static void TestParseBAndF()
        {
            // \b and \f were decoded as literal 'b'/'f'; WriteStr emits them for
            // control chars, so the reader must match the writer.
            var result = Json.Parse("\"a\\bb\\fc\"") as string;
            if (result != "a\bb\fc") throw new Exception("\\b/\\f should decode, got: " + result);
        }

        public static void TestWriteControlCharRoundTrip()
        {
            // WriteStr escapes control chars as \u00xx — the parser must read its
            // own output back (this loop was broken before the \u decode).
            string original = "a\u0001b";
            string json = Json.Write(original);
            if (json != "\"a\\u0001b\"") throw new Exception("writer should emit \\u0001, got: " + json);
            var parsed = Json.Parse(json) as string;
            if (parsed != original) throw new Exception("control-char round-trip mismatch: " + parsed);
        }

        public static void TestParseOllamaNameReplyShape()
        {
            // The full server response shape that produced the "u003cthe" name.
            var root = Json.Parse("{\"choices\":[{\"message\":{\"content\":\"Name: \\u003cthe name>\"}}]}")
                as Dictionary<string, object>;
            if (root == null) throw new Exception("response shape should parse to a dict");
            var choices = root["choices"] as List<object>;
            var msg = (choices[0] as Dictionary<string, object>)["message"] as Dictionary<string, object>;
            var content = msg["content"] as string;
            if (content != "Name: <the name>") throw new Exception("Ollama reply content misshapen: " + content);
        }

        private enum TestMode { Idle, Run }

        private struct Vec2 { public float x; public float y; }

        // Run every test above, print a summary, return the failure count (0 = pass).
        // This is the standalone entry point (see tests/test_json_standalone.cs shim).
        public static int RunAll()
        {
            var all = new System.Action[]
            {
                TestParseNull, TestParseTrue, TestParseFalse, TestParseInt, TestParseFloat,
                TestParseString, TestParseEmptyObject, TestParseObject, TestParseEmptyArray,
                TestParseArray, TestParseNested, TestParseTruncated, TestParseWhitespace,
                TestWriteNull, TestWriteString, TestWriteBool, TestWriteFloat,
                TestWriteAnonymous, TestWriteDictionary, TestWriteList, TestRoundTrip,
                TestJsonObjAccessors,
                TestWriteDictStringValues, TestWriteDictNestedList, TestWriteDictMixedValues,
                TestWriteHashtable, TestWriteKeyValuePair, TestWriteStringArray,
                TestWriteMixedArray, TestWriteEnumAndChar, TestWriteStructFields,
                TestWriteCyclicTerminates, TestRoundTripDictStringValues, TestTextNeverLeaksTypeNames,
                TestParseUnicodeAngleBracket, TestParseUnicodeGoEscapes, TestParseUnicodeSurrogatePair,
                TestParseUnicodeMalformedStaysLiteral, TestParseBAndF, TestWriteControlCharRoundTrip,
                TestParseOllamaNameReplyShape,
            };
            int passed = 0, failed = 0;
            Console.WriteLine("=== KKLLMNPC Json tests ===");
            foreach (var test in all)
            {
                string name = test.Method.Name;
                try { test(); Console.WriteLine("  PASS " + name); passed++; }
                catch (Exception e) { Console.WriteLine("  FAIL " + name + ": " + e.Message); failed++; }
            }
            Console.WriteLine("\n" + passed + " passed, " + failed + " failed");
            return failed;
        }
    }
}
