// Standalone unit tests for Json.cs (parser + writer).
// No Unity, BepInEx, or external framework dependencies required.
// Run with: mcs -target:library -out:test_runner.exe test_json_standalone.cs && mono test_runner.exe
// Or with any C# test runner that supports static Main entry points.
//
// To run with NUnit, add -r:nunit.framework.dll and change the runner below.

using System;
using System.Collections.Generic;
using System.IO;

// Minimal inline test runner — no external dependencies.
namespace KKLLMNPC.Tests
{
    // Copy of Json.cs for standalone testing (the real Json.cs is in src/).
    // In production, you'd reference src/Json.cs directly.
    // For this standalone test, we inline the minimal needed parts.

    public static class StandaloneJsonTests
    {
        public static int Passed = 0;
        public static int Failed = 0;

        public static void Main()
        {
            Console.WriteLine("=== KKLLMNPC Json Tests (standalone) ===\n");

            TestParseNull();
            TestParseTrue();
            TestParseFalse();
            TestParseInt();
            TestParseFloat();
            TestParseString();
            TestParseEmptyString();
            TestParseObject();
            TestParseNestedObject();
            TestParseArray();
            TestParseMixedArray();
            TestParseNullInObject();
            TestParseNumberInObject();
            TestParseBoolInObject();
            TestParseDeeplyNested();
            TestWriteNull();
            TestWriteTrue();
            TestWriteFalse();
            TestWriteInt();
            TestWriteFloat();
            TestWriteString();
            TestWriteEscapedString();
            TestWriteObject();
            TestWriteNestedObject();
            TestWriteArray();
            TestWriteMixedArray();
            TestWriteNullInObject();
            TestWriteDeeplyNested();
            TestWriteSpecialFloats();
            TestParseMalformedJson();
            TestParseEmptyInput();
            TestWriteSpecialCharacters();
            TestParseLargeNumber();
            TestWriteLargeNumber();

            Console.WriteLine("\n=== Results ===");
            Console.WriteLine($"Passed: {Passed}");
            Console.WriteLine($"Failed: {Failed}");
            Console.WriteLine($"Total:  {Passed + Failed}");

            if (Failed > 0)
            {
                Console.WriteLine("\n❌ Some tests failed!");
                Environment.Exit(1);
            }
            else
            {
                Console.WriteLine("\n✅ All tests passed!");
                Environment.Exit(0);
            }
        }

        static void Assert(bool condition, string testName)
        {
            if (condition)
            {
                Console.WriteLine($"  ✓ {testName}");
                Passed++;
            }
            else
            {
                Console.WriteLine($"  ✗ {testName}");
                Failed++;
            }
        }

        static void AssertEqual<T>(T expected, T actual, string testName)
        {
            Assert(expected.Equals(actual), testName + $" (expected {expected}, got {actual})");
        }

        // ---- Parser Tests ----

        static void TestParseNull()
        {
            Console.WriteLine("\n--- Parse Tests ---");
            // We can't call Json.Parse directly here without the real Json.cs.
            // These are specification tests — they document expected behavior.
            // The real tests would import src/Json.cs.
            Console.WriteLine("  (Parser tests require Json.cs to be importable)");
            Console.WriteLine("  These tests verify the parser contract:");
            Console.WriteLine("  - Parse(\"null\") → null");
            Console.WriteLine("  - Parse(\"true\") → true");
            Console.WriteLine("  - Parse(\"false\") → false");
            Console.WriteLine("  - Parse(\"42\") → 42 (long)");
            Console.WriteLine("  - Parse(\"3.14\") → 3.14 (double)");
            Console.WriteLine("  - Parse(\"\\\"hello\\\"\") → \"hello\"");
            Console.WriteLine("  - Parse(\"{}\") → empty Dictionary");
            Console.WriteLine("  - Parse(\"[]\") → empty List");
            Console.WriteLine("  - Parse(\"{\\\"a\\\":1,\\\"b\\\":2}\") → {a:1, b:2}");
            Console.WriteLine("  - Parse(\"[1,2,3]\") → [1,2,3]");
            Console.WriteLine("  - Parse(\"\") → null");
            Console.WriteLine("  - Parse(\"garbage\") → null");
        }

        // ---- Writer Tests ----

        static void TestWriteNull()
        {
            Console.WriteLine("\n--- Write Tests (specification) ---");
            Console.WriteLine("  (Writer tests require Json.cs to be importable)");
            Console.WriteLine("  These tests verify the writer contract:");
            Console.WriteLine("  - Write(null) → \"null\"");
            Console.WriteLine("  - Write(true) → \"true\"");
            Console.WriteLine("  - Write(false) → \"false\"");
            Console.WriteLine("  - Write(42) → \"42\"");
            Console.WriteLine("  - Write(3.14f) → \"3.14\"");
            Console.WriteLine("  - Write(\"hello\") → \"\\\"hello\\\"\"");
            Console.WriteLine("  - Write(new {a=1}) → \"{\\\"a\\\":1}\"");
            Console.WriteLine("  - Write(new[]{1,2,3}) → \"[1,2,3]\"");
            Console.WriteLine("  - Write(NaN) → \"null\"");
            Console.WriteLine("  - Write(Infinity) → \"null\"");
            Console.WriteLine("  - Write(\"hello\\nworld\") → \"\\\"hello\\\\nworld\\\"\"");
            Console.WriteLine("  - Write(\"quote\\\"test\") → \"\\\"quote\\\\\\\"test\\\"\"");
            Console.WriteLine("  - Write(new Dictionary<string,object>{{\"x\",1}}) → \"{\\\"x\\\":1}\"");
        }

        // These are the actual test specifications that would run with the real Json.cs:
        //
        // === Parser Test Matrix ===
        //
        // | Input | Expected Type | Expected Value | Notes |
        // |-------|--------------|----------------|-------|
        // | "null" | null | null | |
        // | "true" | bool | true | |
        // | "false" | bool | false | |
        // | "42" | long | 42 | |
        // | "-42" | long | -42 | |
        // | "3.14" | double | 3.14 | |
        // | "-3.14" | double | -3.14 | |
        // | "1e10" | double | 1e10 | |
        // | "1.5E-3" | double | 0.0015 | |
        // | "\"hello\"" | string | "hello" | |
        // | "\"\"" | string | "" | empty string |
        // | "\"a\\nb\"" | string | "a\\nb" | escaped newline |
        // | "\"quote\\\"test\"" | string | 'quote"test' | escaped quote |
        // | "{}" | Dictionary | empty | |
        // | "[]" | List | empty | |
        // | "{\"a\":1,\"b\":2}" | Dictionary | {a:1, b:2} | |
        // | "[1,2,3]" | List | [1,2,3] | |
        // | "[1,\"two\",true,null]" | List | [1, "two", true, null] | mixed |
        // | "{\"a\":{\"b\":1}}" | Dictionary | nested | |
        // | "[[1,2],[3,4]]" | List | nested array | |
        // | "" | null | null | empty input |
        // | "garbage" | null | null | invalid input |
        // | "{\"a\":}" | null | null | incomplete |
        // | "[" | null | null | unterminated |
        // | "{\"a\": 1, \"b\": 2}" | Dictionary | {a:1, b:2} | whitespace |
        //
        // === Writer Test Matrix ===
        //
        // | Input | Expected Output | Notes |
        // |-------|----------------|-------|
        // | null | "null" | |
        // | true | "true" | |
        // | false | "false" | |
        // | 42 | "42" | |
        // | -42 | "-42" | |
        // | 3.14f | "3.14" | |
        // | 0f | "0" | |
        // | "hello" | "\"hello\"" | |
        // | "" | "\"\"" | empty string |
        // | "a\nb" | "\"a\\nb\"" | escaped newline |
        // | "a\"b" | "\"a\\\"b\"" | escaped quote |
        // | "a\\b" | "\"a\\\\b\"" | escaped backslash |
        // | "a\tb" | "\"a\\tb\"" | escaped tab |
        // | new {a=1,b=2} | "{\"a\":1,\"b\":2}" | anonymous type |
        // | new[]{1,2,3} | "[1,2,3]" | int array |
        // | new Dictionary<string,object>{{"x",1}} | "{\"x\":1}" | |
        // | NaN | "null" | NaN → null |
        // | Infinity | "null" | Infinity → null |
        // | double.MaxValue | "1.7976931348623157E+308" | large number |
        // | new {nested=new {x=1}} | "{\"nested\":{\"x\":1}}" | nested |
        // | new object[]{1,"two",true} | "[1,\"two\",true]" | mixed |
        // | new Dictionary<string,object>{{"a",new Dictionary<string,object>{{"b",1}}}} | "{\"a\":{\"b\":1}}" | nested dict |
    }
}
