// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Module registry: lets partial-class modules (BodyControl, Farming, Identity, ...)
// register tools, per-physics-tick hooks, and perception fields WITHOUT touching the
// shared files (Llm.cs, Movement.cs, Senses.cs). Modules register in a static
// constructor:  static MyModule() { ModuleRegistry.Tool("thrust", (n, p) => n.ToolThrust(p), schema); }
using System;
using System.Collections.Generic;
using System.IO;

namespace KKLLMNPC
{
    internal static class ModuleRegistry
    {
        // tool name → (instance, params) → result object (same contract as the
        // Tool* methods in Tools.cs: never throw, return {ok:false,...} on failure).
        private static readonly Dictionary<string, Func<NPCInstance, JsonObj, object>> _tools =
            new Dictionary<string, Func<NPCInstance, JsonObj, object>>(StringComparer.Ordinal);

        // tool name → JSON-schema property object (same shape as the Str/Num/Bool
        // helpers in Llm.cs ActionParamProps) — merged into the act schema.
        private static readonly Dictionary<string, Dictionary<string, object>> _toolParams =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

        // One physics-tick callback per module (called from Movement.FixedUpdateSafe).
        private static readonly List<Action<NPCInstance, float>> _physics = new List<Action<NPCInstance, float>>();

        // One perception callback per module (called from Senses.BuildPerceptionSafe
        // before the payload is returned — add keys to the dict).
        private static readonly List<Action<NPCInstance, Dictionary<string, object>>> _perception = new List<Action<NPCInstance, Dictionary<string, object>>>();

        private static bool _scanned;

        // Discover every module in this assembly and run its static Register()
        // (convention: internal/public static class with a parameterless static
        // Register() that calls into ModuleRegistry.*). Reflection-based so modules
        // never need to edit a shared file, and are found even though C# static
        // ctors are lazy. Call once at plugin start (Main.cs Awake).
        public static void Scan()
        {
            if (_scanned) return;
            _scanned = true;
            System.Type[] types;
            try { types = typeof(NPCInstance).Assembly.GetTypes(); }
            catch (Exception) { return; }
            foreach (var t in types)
            {
                if (t.IsInterface || t.IsGenericType) continue;
                var m = t.GetMethod("Register",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (m == null) continue;
                try { m.Invoke(null, null); }
                catch (Exception e)
                {
                    try
                    {
                        var lg = LLMNPCPlugin.Log;
                        if (lg != null) lg.LogWarning("module scan: " + t.Name + ": " + e.Message);
                    }
                    catch (Exception) { }
                }
            }
        }

        // ------------------------------------------------------------------
        // registration (call from a module's static constructor)
        // ------------------------------------------------------------------
        public static void Tool(string name, Func<NPCInstance, JsonObj, object> handler, params string[] paramNames)
        {
            lock (_tools) _tools[name.ToLowerInvariant()] = handler;
            foreach (var pn in paramNames)
            {
                if (!_toolParams.ContainsKey(pn)) _toolParams[pn] = null; // placeholder, filled below
            }
        }

        // Register a tool plus the schema descriptions for its parameters.
        // paramSchemas: param name → schema object (same shape as Llm.cs Str/Num/Bool).
        public static void Tool(string name, Func<NPCInstance, JsonObj, object> handler, Dictionary<string, Dictionary<string, object>> paramSchemas)
        {
            lock (_tools)
            {
                _tools[name.ToLowerInvariant()] = handler;
                if (paramSchemas != null)
                    foreach (var kv in paramSchemas)
                        if (!_toolParams.ContainsKey(kv.Key)) _toolParams[kv.Key] = kv.Value;
            }
        }

        public static void PhysicsTick(Action<NPCInstance, float> hook)
        {
            lock (_physics) _physics.Add(hook);
        }

        public static void Perception(Action<NPCInstance, Dictionary<string, object>> hook)
        {
            lock (_perception) _perception.Add(hook);
        }

        // ------------------------------------------------------------------
        // dispatch (called from Llm.cs / Movement.cs / Senses.cs)
        // ------------------------------------------------------------------
        public static bool TryTool(string name, NPCInstance n, JsonObj p, out object result)
        {
            if (name == null) { result = null; return false; }
            Func<NPCInstance, JsonObj, object> h;
            lock (_tools) { h = _tools.TryGetValue(name.ToLowerInvariant(), out h) ? h : null; }
            if (h == null) { result = null; return false; }
            try { result = h(n, p); return true; }
            catch (Exception e)
            {
                result = new { ok = false, reason = "module_tool_error", msg = e.Message };
                return true;
            }
        }

        public static bool IsModuleTool(string name)
        {
            if (name == null) return false;
            lock (_tools) return _tools.ContainsKey(name.ToLowerInvariant());
        }

        public static string[] ModuleToolNames()
        {
            lock (_tools)
            {
                var list = new List<string>(_tools.Keys);
                return list.ToArray();
            }
        }

        // param name → schema (null value = declared name without a description yet).
        public static IEnumerable<System.Collections.Generic.KeyValuePair<string, Dictionary<string, object>>> ModuleParamSchemas()
        {
            lock (_toolParams)
            {
                foreach (var kv in _toolParams)
                    if (kv.Value != null) yield return kv;
            }
        }

        public static void RunPhysics(NPCInstance n, float dt)
        {
            Action<NPCInstance, float>[] snap;
            lock (_physics) snap = _physics.ToArray();
            foreach (var h in snap)
            {
                try { h(n, dt); }
                catch (Exception e) { n.Logger.LogDebug("module physics hook: " + e.Message); }
            }
        }

        public static void RunPerception(NPCInstance n, Dictionary<string, object> result)
        {
            Action<NPCInstance, Dictionary<string, object>>[] snap;
            lock (_perception) snap = _perception.ToArray();
            foreach (var h in snap)
            {
                try { h(n, result); }
                catch (Exception e) { n.Logger.LogDebug("module perception hook: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------
        // prompt extras: each module may ship prompt_extras/<module>.txt next to
        // system_prompt_default.txt (plugin dir / game dir / CWD). They are
        // appended to the base system prompt in filename order, so modules never
        // edit the shared prompt file.
        // ------------------------------------------------------------------
        public static string PromptExtras()
        {
            var parts = new List<string>();
            try
            {
                var bases = new List<string>();
                try
                {
                    string data = UnityEngine.Application.dataPath;
                    if (!string.IsNullOrEmpty(data))
                    {
                        string gameDir = Path.GetDirectoryName(data);
                        if (!string.IsNullOrEmpty(gameDir)) bases.Add(gameDir);
                    }
                }
                catch (Exception) { }
                try
                {
                    string asm = Path.GetDirectoryName(typeof(NPCInstance).Assembly.Location);
                    if (!string.IsNullOrEmpty(asm)) bases.Add(asm);
                }
                catch (Exception) { }
                try
                {
                    string cwd = Directory.GetCurrentDirectory();
                    if (!string.IsNullOrEmpty(cwd)) bases.Add(cwd);
                }
                catch (Exception) { }

                foreach (var b in bases)
                {
                    string dir = Path.Combine(b, "prompt_extras");
                    if (!Directory.Exists(dir)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.txt"); } catch (Exception) { continue; }
                    Array.Sort(files, StringComparer.Ordinal);
                    foreach (var f in files)
                    {
                        try
                        {
                            string txt = File.ReadAllText(f);
                            if (!string.IsNullOrWhiteSpace(txt)) parts.Add(txt.Trim());
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return parts.Count == 0 ? "" : "\n" + string.Join("\n", parts);
        }
    }
}
