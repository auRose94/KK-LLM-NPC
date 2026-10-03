// MemoryStore — persistent per-body memory files. A possessed kobold whose body
// is sold/lost/world-reloaded keeps its life on disk under
// BepInEx/config/kkllmnpc_memory/<body>.json; when THIS body shows up again
// (same scene save, next session), its facts/persona/name restore onto the
// instance instead of the amnesiac rebuild. Keyed by the kobold's own
// serialized object name (CleanName) — that name survives the game's world save.
//
// Pure file I/O (no Unity APIs beyond the config-path lookup done at Init, same
// pattern as NameRegistry) — safe to call from any thread; writes are serialized
// on a lock.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class MemoryStore
    {
        internal sealed class Record
        {
            public string Name;      // the NPC's chosen name at save time
            public string Persona;   // the persona line the identity built
            public List<string> Facts;
            public string Goal;      // may be null/empty
            public string SavedUtc;
        }

        private static readonly object _lock = new object();
        private static string _dir;
        private static bool _initialized;

        private static string Dir()
        {
            if (_initialized) return _dir;
            _initialized = true;
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    _dir = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(data), "BepInEx", "config", "kkllmnpc_memory");
                    System.IO.Directory.CreateDirectory(_dir);
                }
            }
            catch (Exception) { _dir = null; }
            return _dir;
        }

        // File-safe body key: names are alphanumeric short strings, but be defensive.
        private static string FileName(string bodyKey)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in bodyKey)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            string f = sb.ToString();
            return f.Length == 0 ? null : f + ".json";
        }

        internal static void Save(string bodyKey, string name, string persona, List<string> facts, string goal)
        {
            try
            {
                string file = FileName(bodyKey ?? "");
                if (file == null || Dir() == null) return;
                var d = new Dictionary<string, object>();
                d["name"] = name ?? "";
                d["persona"] = persona ?? "";
                var fs = new List<object>();
                if (facts != null) foreach (var f in facts) if (!string.IsNullOrEmpty(f)) fs.Add(f);
                d["facts"] = fs.ToArray();
                d["goal"] = goal ?? "";
                d["saved_utc"] = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                lock (_lock)
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(Dir(), file),
                        Json.Write(d));
                }
            }
            catch (Exception) { }
        }

        internal static Record Load(string bodyKey)
        {
            try
            {
                string file = FileName(bodyKey ?? "");
                if (file == null || Dir() == null) return null;
                string path = System.IO.Path.Combine(Dir(), file);
                if (!System.IO.File.Exists(path)) return null;
                string text;
                lock (_lock) { text = System.IO.File.ReadAllText(path); }
                var raw = Json.Parse(text) as Dictionary<string, object>;
                if (raw == null) return null;
                var rec = new Record();
                rec.Name = raw.GetValueOrDefault("name") as string;
                rec.Persona = raw.GetValueOrDefault("persona") as string;
                rec.Goal = raw.GetValueOrDefault("goal") as string;
                rec.SavedUtc = raw.GetValueOrDefault("saved_utc") as string;
                if (raw.TryGetValue("facts", out var factsObj) && factsObj is List<object> fl)
                {
                    rec.Facts = new List<string>();
                    foreach (var o in fl) { string s = o as string; if (!string.IsNullOrEmpty(s)) rec.Facts.Add(s); }
                }
                if (rec.Facts == null) rec.Facts = new List<string>();
                if (string.IsNullOrEmpty(rec.Name) || string.IsNullOrEmpty(rec.Persona)) return null;
                return rec;
            }
            catch (Exception) { return null; }
        }
    }
}