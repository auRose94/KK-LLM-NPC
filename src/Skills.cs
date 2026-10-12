// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Skills — agent-authored reusable procedures. A skill is a NAME over a short list of
// console command lines the model wrote once and validated at save time; `skill <name>`
// executes the steps, `cat skills` lists them, and the whole set:
//   * saves per agent through the body's MemoryStore record,
//   * TRANSFERS when the agent is swallowed and absorbed (the eater inherits the
//     digested person's skills),
//   * rides the rebirth copy (a hatched child egg wakes with its parent's skills).
// Pure parsing lives in ConsoleShell.SkillParseAdd — unit-tested.
using System;
using System.Threading;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        internal class SkillRec
        {
            public string Name;
            public List<string> Steps;
            public int Uses;
        }

        private readonly List<SkillRec> _skills = new List<SkillRec>();
        private readonly object _skillLock = new object();
        private const int SkillMaxCount = 12;
        private const int SkillMaxSteps = 10;
        private const int SkillNameMax = 24;
        private const int SkillStepMaxChars = 90;

        // ---- skill command surface (console) ----

        internal string CmdSkillImpl(ConsoleLine line)
        {
            string payload = line.Payload;
            if (string.IsNullOrWhiteSpace(payload) || payload.Trim().Length == 0)
                return SkillListImpl();
            var d = line.Args ?? new string[0];
            string a0 = d.Length > 0 ? d[0].ToLowerInvariant() : "";
            if (a0 == "add")
                return SkillAddImpl(payload);
            if (a0 == "forget" || a0 == "delete" || a0 == "rm")
            {
                // payload: "forget <name>" → drop the keyword.
                string rest = (line.Payload ?? "").Trim();
                if (rest.StartsWith("forget", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(6).Trim();
                else if (rest.StartsWith("delete", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(6).Trim();
                else if (rest.StartsWith("rm", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(2).Trim();
                return SkillForgetImpl(rest);
            }
            // `<name>` (args[0] may carry trailing punctuation — the parse below trims).
            if (d.Length > 0) return SkillRunImpl(d[0].Trim());
            return SkillListImpl();
        }

        internal string SkillListImpl()
        {
            lock (_skillLock)
            {
                if (_skills.Count == 0)
                    return "(no skills yet — define one: skill add <name> | <step> ; <step> ; ...)";
                var sb = new System.Text.StringBuilder();
                sb.Append(_skills.Count).AppendLine(" skill(s) — run with: skill <name>");
                foreach (var s in _skills)
                {
                    sb.Append("  ").Append(s.Name);
                    sb.Append("  (").Append(s.Steps.Count).Append(" steps, used ").Append(s.Uses).Append("x)");
                    sb.Append(": ").AppendLine(string.Join(" ; ", s.Steps.ToArray()));
                }
                return sb.ToString().TrimEnd();
            }
        }

        internal string SkillRunImpl(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return SkillListImpl();
            string norm = NormalizeSkillName(name);
            SkillRec rec = null;
            lock (_skillLock)
                foreach (var s in _skills)
                    if (NormalizeSkillName(s.Name) == norm) { rec = s; break; }
            if (rec == null) return "no skill named '" + name + "' — " + (SkillListImpl());
            var outp = new System.Text.StringBuilder();
            // A saved skill's steps were VALIDATED at save; they run as a single
            // batch (one history entry), independent of the per-turn command budget
            // — a skill IS budget.
            foreach (var step in rec.Steps)
            {
                if (!IsAlive(_kobold)) break;
                var parsed = ConsoleShell.Parse(step);
                if (parsed == null || parsed.IsUnknown) continue;
                outp.Append("$ ").AppendLine(parsed.Raw);
                string output = RunConsoleLine(parsed);
                outp.AppendLine(output);
                try { Thread.Sleep(200); } catch (Exception) { } // body beats settle between steps
            }
            lock (_skillLock) rec.Uses++;
            if (outp.Length == 0) return "skill '" + rec.Name + "' had no executable steps";
            PushHistory("skill", rec.Name);
            return outp.ToString().TrimEnd();
        }

        internal string SkillAddImpl(string payload)
        {
            var parsed = ConsoleShell.SkillParseAdd(payload, SkillNameMax, SkillMaxSteps, SkillStepMaxChars);
            if (parsed.Error != null) return "skill: " + parsed.Error;
            lock (_skillLock)
            {
                string norm = NormalizeSkillName(parsed.Name);
                foreach (var s in _skills)
                    if (NormalizeSkillName(s.Name) == norm)
                    {
                        s.Steps = parsed.Steps; // overwrite = redefine
                        return "skill '" + s.Name + "' redefined: " + string.Join(" ; ", parsed.Steps.ToArray());
                    }
                if (_skills.Count >= SkillMaxCount)
                    return "skill box full (" + SkillMaxCount + ") — forget one (skill forget <name>) before adding";
                _skills.Add(new SkillRec { Name = parsed.Name, Steps = parsed.Steps });
                return "skill saved: " + parsed.Name + " = " + string.Join(" ; ", parsed.Steps.ToArray())
                    + " — run with: skill " + parsed.Name;
            }
        }

        internal string SkillForgetImpl(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SkillListImpl();
            string norm = NormalizeSkillName(name);
            lock (_skillLock)
            {
                for (int i = 0; i < _skills.Count; i++)
                    if (NormalizeSkillName(_skills[i].Name) == norm)
                    {
                        string gone = _skills[i].Name;
                        _skills.RemoveAt(i);
                        return "skill forgotten: " + gone;
                    }
            }
            return "no skill named '" + name + "'";
        }

        // ---- swallow transfer + persistence ----

        // Absorb another person's skills into this mind (the eater ate them —
        // knowledge, moves and muscle memory included). Collisions get a suffix, not
        // a silent loss.
        internal void SkillsAbsorbFrom(string victimName, List<string> skillLines)
        {
            if (skillLines == null || skillLines.Count == 0) return;
            int taken = 0;
            lock (_skillLock)
            {
                foreach (var line in skillLines)
                {
                    var parsed = ConsoleShell.SkillParseAdd(line, SkillNameMax, SkillMaxSteps, SkillStepMaxChars);
                    if (parsed.Error != null) continue;
                    string name = parsed.Name;
                    string norm = NormalizeSkillName(name);
                    for (int i = 0; i < _skills.Count; i++)
                        if (NormalizeSkillName(_skills[i].Name) == norm)
                        { name = name + " (absorbed)"; break; }
                    if (_skills.Count >= SkillMaxCount) break;
                    _skills.Add(new SkillRec { Name = name, Steps = parsed.Steps });
                    taken++;
                }
            }
            if (taken > 0)
            {
                try
                {
                    RememberFact("absorbed: " + taken + " skill(s) of " + (victimName ?? "someone")
                        + " — run them with: skill <name> (cat skills)");
                }
                catch (Exception) { }
                try { PushHistory("skill", "absorbed " + taken + " from " + (victimName ?? "?")); } catch (Exception) { }
                try { Logger.LogInfo("swallow: absorbed " + taken + " skill(s) from '" + (victimName ?? "?") + "'"); } catch (Exception) { }
            }
        }

        // Flat storage form ("name|step1;step2") for MemoryStore + transfers.
        internal List<string> SkillsSnapshotLines()
        {
            var outp = new List<string>();
            lock (_skillLock)
                foreach (var s in _skills)
                    outp.Add(s.Name + "|" + string.Join(" ; ", s.Steps.ToArray()));
            return outp;
        }

        internal void SkillsLoadFrom(List<string> lines)
        {
            if (lines == null) return;
            lock (_skillLock) _skills.Clear();
            if (lines.Count > 0) SkillsAbsorbFrom(null, lines);
        }

        private static string NormalizeSkillName(string n)
        {
            if (n == null) return "";
            var sb = new System.Text.StringBuilder(n.Length);
            foreach (char c in n.ToLowerInvariant().Trim())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // skills: N for the state anchor / whoami.
        internal string SkillsCountLine()
        {
            lock (_skillLock)
                return _skills.Count > 0 ? _skills.Count + " skill(s) (cat skills)" : null;
        }
    }
}