// Standalone entry point for the Json.cs tests (test_json.cs).
// No Unity/BepInEx/Photon deps — src/Json.cs is deliberately dependency-free.
// Run (from repo root):
//   mcs -target:exe -out:test_runner.exe tests/test_json.cs tests/test_json_standalone.cs src/Json.cs
//   mono test_runner.exe
// Exit code = number of failed tests (0 = all pass).

using System;

namespace KKLLMNPC.Tests
{
    public static class StandaloneJsonTests
    {
        public static int Main()
        {
            return JsonTests.RunAll();
        }
    }
}

// ---------------------------------------------------------------------------
// === Parser Test Matrix (specification) ===
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
// | "\"a\\nb\"" | string | "a\nb" | escaped newline |
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
// === Writer Test Matrix (specification) ===
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
