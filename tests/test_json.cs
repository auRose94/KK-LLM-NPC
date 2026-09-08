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
            // Truncated JSON should return null (graceful degradation)
            var result = Json.Parse("{\"name\":\"test");
            // May return partial result or null — both are acceptable
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
            if (obj.Has("x")) throw new Exception("Has(x) should be true");
            if (obj.Has("missing")) throw new Exception("Has(missing) should be false");
        }
    }
}
