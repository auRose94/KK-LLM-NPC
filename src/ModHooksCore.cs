// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ModHooksCore — the fire plumbing behind ModHooks (the public mod event API),
// pure C# so the isolation contract is unit-testable.
//
// Contract: ONE broken subscriber must never take the plugin (or the game
// loop) down — every invocation is isolated, exceptions are counted, and the
// remaining subscribers still run. ModHooks.cs (Unity side) keeps the typed
// events and payload classes; this file owns only the invocation walk.
using System;

namespace KKLLMNPC
{
    public static class ModHooksCore
    {
        // Diagnostics: how many subscriber exceptions were swallowed, and the
        // last message (surfaced in the WebUI/debug surfaces if it ever grows).
        public static long Errors;
        public static string LastError;

        // Invoke every subscriber of a multicast delegate, isolating each one.
        // Never throws. arg must match the delegate's parameter type.
        public static void Invoke(MulticastDelegate handlers, object arg)
        {
            if (handlers == null) return;
            Delegate[] list;
            try { list = handlers.GetInvocationList(); }
            catch (Exception) { return; }
            if (list == null) return;
            foreach (var d in list)
            {
                if (d == null) continue;
                try { d.DynamicInvoke(arg); }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    Record(tie.InnerException ?? tie);
                }
                catch (Exception e)
                {
                    Record(e);
                }
            }
        }

        private static void Record(Exception e)
        {
            Errors++;
            try { LastError = e != null ? (e.Message ?? e.GetType().Name) : "?"; }
            catch (Exception) { LastError = "?"; }
        }
    }
}
