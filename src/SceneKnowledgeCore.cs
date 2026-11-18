// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SceneKnowledgeCore — pure logic for the scene-dump gazetteer: parsing a live
// scene-dump JSON (from Rose's KodotKare port tooling) into a flat searchable
// registry of the world's static objects, plus query + summary math.
//
// Why: live senses only reach ~30m, so "find my egg" failed whenever the egg
// sat outside the ray fan, and relative-direction treks were the only way to
// reach far targets. The dump knows where EVERYTHING is (3k+ nodes per scene:
// stations, nests, eggs, soil, doors — with world positions), so ingesting it
// turns long-distance search into a lookup and go_to coords into a real path.
//
// Dump shape (verified against Rose's dumps): { format_version, scene,
// dump_time, full, sceneInfo, meta, objects: [ node ] } where node = {
// name, path, instanceID, active, worldPosition {x,y,z}, children: [node]… }.
// Inactive nodes are skipped. Pure C#: no UnityEngine — compiled into the
// headless test suite (tests/test_sceneknowledge.cs).
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class SceneKnowledgeCore
    {
        // One indexed world object.
        internal sealed class Entry
        {
            public string Name;
            public string Path;
            public int InstanceId;
            public float X, Y, Z;
        }

        // The scene name the dump declares (top-level "scene") — used to match
        // dumps to the running game scene.
        public static string DumpScene(string json)
        {
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;
            return root.TryGetValue("scene", out var s) ? s as string : null;
        }

        // Flatten the hierarchy into a registry (skipping inactive nodes and
        // anything without a worldPosition). Depth is bounded by the parser's
        // own cap; the dumps run ~12 deep.
        public static List<Entry> ParseDump(string json)
        {
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;
            if (!root.TryGetValue("objects", out var objs) || !(objs is List<object> list)) return null;
            var outp = new List<Entry>(2048);
            foreach (var item in list)
                Walk(item, outp);
            return outp;
        }

        private static void Walk(object nodeObj, List<Entry> outp)
        {
            var o = nodeObj as Dictionary<string, object>;
            if (o == null) return;
            try
            {
                bool active = !o.TryGetValue("active", out var a) || !bool.TryParse(a?.ToString(), out var ab) || ab;
                if (active && o.TryGetValue("worldPosition", out var wp) && wp is Dictionary<string, object> pos
                    && o.TryGetValue("name", out var n) && n is string name && name.Length > 0)
                {
                    var e = new Entry
                    {
                        Name = name,
                        Path = o.TryGetValue("path", out var p) ? p as string : null,
                        InstanceId = o.TryGetValue("instanceID", out var id) && int.TryParse(id?.ToString(), out var iv) ? iv : 0,
                        X = F(pos, "x"), Y = F(pos, "y"), Z = F(pos, "z"),
                    };
                    outp.Add(e);
                }
            }
            catch (Exception) { }
            if (o.TryGetValue("children", out var kids) && kids is List<object> kl)
                foreach (var k in kl)
                    Walk(k, outp);
        }

        private static float F(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return 0f;
            if (v is double dd) return (float)dd;
            if (v is long ll) return ll;
            float f;
            return float.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out f) ? f : 0f;
        }

        // Search score: exact name > prefix > substring; lower is better; -1 = no
        // match. Case-insensitive; the trailing "(Clone)" suffix is ignored on the
        // registry side so "absolb" matches "AbsolB(Clone)".
        internal static int Score(Entry e, string queryLower)
        {
            string n = (e.Name ?? "").ToLowerInvariant();
            if (n.EndsWith("(clone)")) n = n.Substring(0, n.Length - 7).TrimEnd();
            if (n == queryLower) return 0;
            if (n.StartsWith(queryLower)) return 1;
            int i = n.IndexOf(queryLower, StringComparison.Ordinal);
            return i >= 0 ? 2 + Math.Min(i, 8) : -1;
        }

        // Top matches for a query, nearest-first among equally-scoring hits.
        // Returns at most take entries. All matching entries are considered
        // map-wide — this is the long-distance search. (No local functions —
        // mcs predates C# 7.)
        public static List<Entry> Search(List<Entry> registry, string query, float fromX, float fromZ, int take)
        {
            var outp = new List<Entry>();
            if (registry == null || string.IsNullOrWhiteSpace(query) || take <= 0) return outp;
            string q = query.Trim().ToLowerInvariant();
            foreach (var e in registry)
                if (Score(e, q) >= 0) outp.Add(e);
            outp.Sort(delegate (Entry a, Entry b)
            {
                int c = Score(a, q).CompareTo(Score(b, q));
                if (c != 0) return c;
                float da = (a.X - fromX) * (a.X - fromX) + (a.Z - fromZ) * (a.Z - fromZ);
                float db = (b.X - fromX) * (b.X - fromX) + (b.Z - fromZ) * (b.Z - fromZ);
                if (da.CompareTo(db) != 0) return da.CompareTo(db);
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            if (outp.Count > take) outp.RemoveRange(take, outp.Count - take);
            return outp;
        }

        // Grounding summary: counts for the buckets the game's loop cares about,
        // for the boot "world intel" line. Order matters (most game-relevant first).
        private static readonly string[] Buckets = { "egg", "nest", "bed", "mailbox", "station", "soil", "blender", "water", "shop", "door" };

        public static List<string> Summary(List<Entry> registry)
        {
            var outp = new List<string>();
            if (registry == null) return outp;
            foreach (var b in Buckets)
            {
                int c = 0;
                foreach (var e in registry)
                    if ((e.Name ?? "").ToLowerInvariant().Contains(b)) c++;
                if (c > 0) outp.Add(c + " " + b + (c == 1 ? "" : "s"));
            }
            return outp;
        }
    }
}
