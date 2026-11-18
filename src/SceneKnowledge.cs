// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SceneKnowledge — ingests a scene-dump JSON (from Rose's KodotKare port
// tooling) as a static WORLD GAZETTEER: every station, nest, egg, soil tile,
// door and structure with its world position, map-wide. Live senses reach
// ~30m; the dump knows the whole world, which is what makes long-distance
// search and far go_to paths possible ("find my egg" across the map).
//
// Files: the dump dir is scanned for *.full.json whose top-level "scene"
// matches the running scene (dumps are per-scene snapshots). Parsing runs on
// the thread pool (the big dumps are tens of MB); until it lands the mod
// behaves exactly as before, so a missing/unparseable dump costs nothing.
//
// Consumers: CmdFind's not-found fallback, ToolGoTo's name resolution fallback
// (long-distance paths to static objects), and the boot "world intel" event.
using System;
using System.Collections.Generic;
using System.IO;

namespace KKLLMNPC
{
    internal static class SceneKnowledge
    {
        // Config (bound in Main.cs, pushed here like WorldMap's knobs).
        internal static bool Enabled = true;
        internal static string DumpDir = "";

        private static readonly object _gate = new object();
        private static string _scene;                       // scene the registry is for
        private static List<SceneKnowledgeCore.Entry> _registry; // null until loaded
        private static volatile bool _loading;
        private static bool _groundedThisScene;             // boot intel sent once per scene

        internal static bool Ready
        {
            get { lock (_gate) return _registry != null; }
        }

        // Best-effort lookup: the nearest matching object to (x,z), or null.
        // Caller feeds the result into go_to coords — the path comes from the
        // same octree/layered A* every other destination uses.
        internal static SceneKnowledgeCore.Entry Nearest(string query, float x, float z)
        {
            if (!Enabled) return null;
            List<SceneKnowledgeCore.Entry> reg;
            lock (_gate) reg = _registry;
            if (reg == null) return null;
            var hits = SceneKnowledgeCore.Search(reg, query, x, z, 1);
            return hits.Count > 0 ? hits[0] : null;
        }

        // Multi-hit search for the `find`/`world` surfaces.
        internal static List<SceneKnowledgeCore.Entry> Search(string query, float x, float z, int take)
        {
            if (!Enabled) return null;
            List<SceneKnowledgeCore.Entry> reg;
            lock (_gate) reg = _registry;
            return reg == null ? null : SceneKnowledgeCore.Search(reg, query, x, z, take);
        }

        // Called from the plugin loop (like WorldMap.EnsureStarted). Kicks the
        // scene-matched load once per scene; sends the grounding event once the
        // registry lands. Main thread.
        internal static void EnsureStarted(string scene, Action<string> pushWorldEvent)
        {
            if (!Enabled || string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(DumpDir))
                return;
            bool justLoaded = false;
            lock (_gate)
            {
                if (_loading) return;
                if (_registry != null && string.Equals(_scene, scene, StringComparison.Ordinal))
                {
                    if (_groundedThisScene) return;
                    _groundedThisScene = true;
                    justLoaded = true;
                }
                else
                {
                    string file = FindDumpForScene(scene);
                    if (file == null) return; // no dump for this scene — silent
                    _loading = true;
                    var snap = file;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            string json = File.ReadAllText(snap);
                            var reg = SceneKnowledgeCore.ParseDump(json);
                            lock (_gate)
                            {
                                _scene = scene;
                                _registry = reg;
                                _groundedThisScene = false;
                            }
                            try { LLMNPCPlugin.Log?.LogInfo("KKLLMNPC: scene knowledge loaded — " + (reg?.Count.ToString() ?? "0")
                                + " world objects from " + Path.GetFileName(snap)); } catch (Exception) { }
                        }
                        catch (Exception e)
                        {
                            lock (_gate) _loading = false;
                            try { LLMNPCPlugin.Log?.LogWarning("KKLLMNPC: scene knowledge load failed: " + e.Message); } catch (Exception) { }
                        }
                    });
                    return;
                }
            }
            if (justLoaded && pushWorldEvent != null)
            {
                List<string> summary;
                lock (_gate) summary = SceneKnowledgeCore.Summary(_registry);
                if (summary.Count > 0)
                    pushWorldEvent("world intel: this area holds " + string.Join(", ", summary.ToArray())
                        + " — find <thing> now searches the WHOLE map (world intel comes from a scene survey, positions are static)");
            }
        }

        // Scene changed — drop the registry so the next scene loads its own dump.
        internal static void Invalidate(string newScene)
        {
            lock (_gate)
            {
                if (newScene == null || (_scene != null && !string.Equals(_scene, newScene, StringComparison.Ordinal)))
                {
                    _registry = null;
                    _scene = null;
                    _groundedThisScene = false;
                }
            }
        }

        // Scan DumpDir for a *.full.json whose declared scene matches. Dumps are
        // tens of MB, so match by FILENAME first (scene_dump filenames start with
        // the scene name in the observed dumps) and only open+check the "scene"
        // field as a fallback for the hash-named ones.
        private static string FindDumpForScene(string scene)
        {
            try
            {
                if (!Directory.Exists(DumpDir)) return null;
                var files = Directory.GetFiles(DumpDir, "*.full.json");
                foreach (var f in files)
                    if (Path.GetFileName(f).StartsWith(scene, StringComparison.OrdinalIgnoreCase))
                        return f;
                foreach (var f in files)
                {
                    string s = SceneKnowledgeCore.DumpScene(SafeHead(f));
                    if (string.Equals(s, scene, StringComparison.Ordinal)) return f;
                }
            }
            catch (Exception) { }
            return null;
        }

        // Read just the head of a huge file for the "scene" field probe (the field
        // sits before the objects array in the observed dumps).
        private static string SafeHead(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (var sr = new StreamReader(fs))
                {
                    var sb = new System.Text.StringBuilder(64 * 1024);
                    int b;
                    long read = 0;
                    while ((b = sr.Read()) >= 0 && read++ < 64 * 1024) sb.Append((char)b);
                    return sb.ToString();
                }
            }
            catch (Exception) { return ""; }
        }
    }
}
