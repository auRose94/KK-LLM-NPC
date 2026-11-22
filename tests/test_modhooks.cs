// Unit tests for ModHooksCore — the fire plumbing behind the public mod event
// API (ModHooks). Contract under test: one broken subscriber must NEVER take
// the plugin down — every invocation is isolated, exceptions are counted, and
// the remaining subscribers still run in order. The typed events and payload
// classes live in ModHooks.cs (Unity side); this suite drives the pure Invoke
// walk with plain delegates.
// Run via run_tests.sh (suite "modhooks").
using System;
using System.Collections.Generic;
using System.Reflection;
using KKLLMNPC;

internal static class TestModHooks
{
    private static int _failed;

    private static void True(bool b, string what)
    {
        if (!b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static void Eq(object expect, object got, string what)
    {
        if (!string.Equals(Convert.ToString(expect), Convert.ToString(got), StringComparison.Ordinal))
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected '" + expect + "' got '" + got + "'");
        }
    }

    public static void Main()
    {
        // ---- null handler list: a no-op, never throws ----
        bool threw = false;
        try { ModHooksCore.Invoke(null, new object()); }
        catch (Exception) { threw = true; }
        True(!threw, "Invoke(null) does not throw");

        // ---- single subscriber receives the exact arg ----
        object received = null;
        int calls = 0;
        Action<object> one = a => { received = a; calls++; };
        ModHooksCore.Invoke(one, "payload");
        Eq(1, calls, "single subscriber called once");
        Eq("payload", received, "single subscriber got the arg");

        // ---- multicast: every subscriber runs, in subscription order ----
        var order = new List<string>();
        Action<object> a1 = _ => order.Add("a1");
        Action<object> a2 = _ => order.Add("a2");
        Action<object> a3 = _ => order.Add("a3");
        MulticastDelegate chain = (MulticastDelegate)Delegate.Combine(a1, Delegate.Combine(a2, a3));
        ModHooksCore.Invoke(chain, null);
        Eq("a1|a2|a3", string.Join("|", order.ToArray()), "multicast order preserved");
        ModHooksCore.Errors = 0;
        ModHooksCore.LastError = null;

        // ---- isolation: a throwing subscriber is counted, the rest still run ----
        order.Clear();
        Action<object> boom = _ => { throw new InvalidOperationException("subscriber exploded"); };
        Action<object> afterBoom1 = _ => order.Add("after1");
        Action<object> afterBoom2 = _ => order.Add("after2");
        MulticastDelegate mixed = (MulticastDelegate)Delegate.Combine(boom, Delegate.Combine(afterBoom1, afterBoom2));
        bool rethrown = false;
        try { ModHooksCore.Invoke(mixed, null); }
        catch (Exception) { rethrown = true; }
        True(!rethrown, "throwing subscriber never escapes Invoke");
        Eq("after1|after2", string.Join("|", order.ToArray()), "subscribers after the thrower still ran");
        Eq(1, ModHooksCore.Errors, "exception counted");
        True(ModHooksCore.LastError != null && ModHooksCore.LastError.Contains("subscriber exploded"),
            "LastError carries the message");

        // ---- TargetInvocationException unwrapping: DynamicInvoke wraps every
        // subscriber throw in a TIE, and Core unwraps ONE level — proven by the
        // boom test above (LastError carries "subscriber exploded", which is only
        // reachable through the unwrap). A subscriber that ITSELF throws a TIE
        // (exotic) lands one level deeper: still contained, still counted. ----
        long errsBefore = ModHooksCore.Errors;
        Action<object> wrapper = _ => { throw new TargetInvocationException(new Exception("inner truth")); };
        try { ModHooksCore.Invoke(wrapper, null); } catch (Exception) { True(false, "TIE must not escape"); }
        True(ModHooksCore.Errors == errsBefore + 1, "TIE counted");
        True(!string.IsNullOrEmpty(ModHooksCore.LastError), "TIE still recorded a message");

        // ---- every subscriber throwing still can't break the walk ----
        long errsBefore2 = ModHooksCore.Errors;
        MulticastDelegate allBad = (MulticastDelegate)Delegate.Combine(boom, boom, boom);
        try { ModHooksCore.Invoke(allBad, null); } catch (Exception) { True(false, "all-bad must not escape"); }
        Eq(errsBefore2 + 3, ModHooksCore.Errors, "each throw counted separately");

        // ---- arbitrary payload types pass through unchanged ----
        var boxed = new Dictionary<string, object> { ["k"] = 42 };
        object boxedOut = null;
        Action<object> grab = a => boxedOut = a;
        ModHooksCore.Invoke(grab, boxed);
        True(ReferenceEquals(boxed, boxedOut), "payload identity preserved");

        // ---- reset diagnostics so the suite leaves no residue ----
        ModHooksCore.Errors = 0;
        ModHooksCore.LastError = null;

        if (_failed == 0) Console.WriteLine("test_modhooks: all OK");
        else Console.WriteLine("test_modhooks: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}
