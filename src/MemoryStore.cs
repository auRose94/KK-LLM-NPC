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
            public string Pronouns;  // the NPC's declared pronouns (set_pronouns); null = body default
            public List<string> Skills; // "name|step;step" lines
            public string SavedUtc;
            // Body identity: gene fingerprint + species prefab name captured at save
            // time. Restore refuses a record whose fingerprint doesn't match the body
            // being bound — two bodies that serialize to the same object name (every
            // spawned 'Kobold(Clone)' did) can no longer wake up as each other.
            public string BodyFp;    // short hash of the body's gene fingerprint, may be null (legacy)
            public string Species;   // the body's prefab/species name, may be null (legacy)
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
            Save(bodyKey, name, persona, facts, goal, null);
        }

        // skills: the agent's SAVED PROCEDURES ("name|step;step") — part of the
        // body's record: restored on claim, transferred on swallow-absorb, riding
        // the rebirth copy (a hatched child egg wakes with the parent's skills).
        // bodyFp/species: the body's identity stamp (gene-fingerprint hash + species
        // name); validated at Load time by the claim path so a body can never wake
        // up as a stranger who happened to share its serialized object name.
        internal static void Save(string bodyKey, string name, string persona, List<string> facts, string goal, string pronouns,
            List<string> skills = null, string bodyFp = null, string species = null)
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
                if (!string.IsNullOrEmpty(pronouns)) d["pronouns"] = pronouns;
                if (skills != null && skills.Count > 0)
                {
                    var ss = new List<object>();
                    foreach (var sk in skills) if (!string.IsNullOrEmpty(sk)) ss.Add(sk);
                    if (ss.Count > 0) d["skills"] = ss.ToArray();
                }
                if (!string.IsNullOrEmpty(bodyFp)) d["body_fp"] = bodyFp;
                if (!string.IsNullOrEmpty(species)) d["species"] = species;
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

        // Filenames of every saved life (WebUI ops; "" when the dir is missing).
        internal static List<string> ListFiles()
        {
            var outp = new List<string>();
            try
            {
                if (Dir() == null || !System.IO.Directory.Exists(_dir)) return outp;
                lock (_lock)
                    foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json"))
                        outp.Add(System.IO.Path.GetFileName(f));
            }
            catch (Exception) { }
            outp.Sort(StringComparer.OrdinalIgnoreCase);
            return outp;
        }

        // Delete ALL saved lives ("delete memory files" op). Returns count removed.
        internal static int DeleteAll()
        {
            int n = 0;
            try
            {
                if (Dir() == null || !System.IO.Directory.Exists(_dir)) return 0;
                lock (_lock)
                    foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json"))
                    {
                        try { System.IO.File.Delete(f); n++; } catch (Exception) { }
                    }
            }
            catch (Exception) { }
            return n;
        }

        // Erase a body's saved life entirely. Swallow-vore's no-rebirth modes
        // (Grow/Destroy) call this — the person was consumed for good, so their
        // records go too and the effect isn't undercut by them re-appearing whole.
        // No-ops when nothing is recorded; lock-serialized like Save.
        internal static void Delete(string bodyKey)
        {
            try
            {
                string file = FileName(bodyKey ?? "");
                if (file == null || Dir() == null) return;
                lock (_lock)
                {
                    string path = System.IO.Path.Combine(Dir(), file);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
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
                rec.Pronouns = raw.GetValueOrDefault("pronouns") as string;
                rec.SavedUtc = raw.GetValueOrDefault("saved_utc") as string;
                rec.BodyFp = raw.GetValueOrDefault("body_fp") as string;
                rec.Species = raw.GetValueOrDefault("species") as string;
                if (raw.TryGetValue("skills", out var skObj) && skObj is List<object> skl)
                {
                    rec.Skills = new List<string>();
                    foreach (var o in skl) { string s = o as string; if (!string.IsNullOrEmpty(s)) rec.Skills.Add(s); }
                }
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