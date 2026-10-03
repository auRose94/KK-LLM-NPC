// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// QuestSense — the objective scroll (DragonMail) seen by the AI.
//
// The game's real quest system: ObjectiveManager holds a chain of DragonMailObjective
// letters; the paper scroll (top-right) shows ObjectiveUIDisplay's title/description;
// new letters arrive at the MAILBOX (MailboxUsable.Use → ObjectiveManager.GetMail).
// GetTitle() embeds live progress ("Create 4 food 2/4").
//
// This polls the manager once a second on the main thread (it has no accessible tick
// hook and lives per-scene, so polling + change-detection beats subscribing to static
// events whose owner is destroyed on scene change), fans out change events to every
// instance, and exposes `quest` for perception + `cat quest`/status for the console.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class QuestSense
    {
        private static string _title, _body;
        private static bool _hasMail;
        private static int _stars = -1;
        private static float _lastPoll = -99f;
        private static readonly object _lock = new object();

        // Main-thread poll from the plugin's Update, 1/s.
        internal static void Tick(LLMNPCPlugin plugin)
        {
            try
            {
                if (plugin == null || !plugin._mainReady || !plugin.Running) return;
                if (Time.unscaledTime - _lastPoll < 1f) return;
                _lastPoll = Time.unscaledTime;

                string title = null, body = null;
                try
                {
                    var obj = ObjectiveManager.GetCurrentObjective();
                    if (obj != null) { title = obj.GetTitle(); body = obj.GetTextBody() ?? ""; }
                }
                catch (Exception) { }
                bool hasMail = false;
                try { hasMail = ObjectiveManager.HasMail(); } catch (Exception) { }
                int stars = -1;
                try { stars = ObjectiveManager.GetStars(); } catch (Exception) { }

                string ev = null;
                lock (_lock)
                {
                    bool had = _title != null;
                    bool has = !string.IsNullOrEmpty(title);
                    if (has && !had)
                        ev = "A NEW OBJECTIVE LETTER is live in the top-right scroll: '" + title + "' — " + body
                            + " That is the PLAYER'S current game objective; offer to help, take it on as your goal, and report progress.";
                    else if (has && !string.Equals(title, _title, StringComparison.Ordinal))
                        ev = "objective progress: '" + title + "' — " + body;
                    else if (!has && had)
                        ev = "the scroll OBJECTIVE was COMPLETED (stars: " + (_stars < 0 ? "?" : _stars.ToString()) + ") — new letters arrive at the MAILBOX";
                    if (hasMail && !_hasMail && !has)
                        ev = "MAIL is waiting at the mailbox — get it by USING the mailbox (the letter box; NOT the sell machine) — the next objective letter is inside, and YOU can fetch it for your player";
                    _title = title; _body = body; _hasMail = hasMail;
                    if (stars >= 0) _stars = stars;
                }
                if (ev != null) try { plugin.FanoutWorldEvent(ev); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // Perception entry — null unless a letter is out or mail waits.
        internal static object Perceive()
        {
            try
            {
                string title, body;
                bool mail;
                int stars;
                lock (_lock) { title = _title; body = _body; mail = _hasMail; stars = _stars; }
                if (string.IsNullOrEmpty(title) && !mail) return null;
                var d = new Dictionary<string, object>
                {
                    ["title"] = title,
                    ["text"] = body,
                    ["stars"] = stars,
                    ["mail_waiting"] = mail,
                };
                if (mail)
                    d["note"] = "mail waiting — use the mailbox (letter box — safe; NOT the sell machine) to get the next letter; fetching it for your player is real help";
                return d;
            }
            catch (Exception) { return null; }
        }

        // Status one-liner: the live title (progress included) or mail state.
        internal static string TitleLine()
        {
            try
            {
                string t; bool m;
                lock (_lock) { t = _title; m = _hasMail; }
                if (!string.IsNullOrEmpty(t)) return t;
                return m ? "(mail waiting at the mailbox)" : "(no objective letter)";
            }
            catch (Exception) { return ""; }
        }

        // Console `cat quest`.
        internal static string CatText()
        {
            try
            {
                string t, b;
                bool m;
                int s;
                lock (_lock) { t = _title; b = _body; m = _hasMail; s = _stars; }
                var sb = new StringBuilder();
                sb.Append("objective scroll (top-right paper): ").AppendLine(string.IsNullOrEmpty(t) ? "(no letter taken)" : t);
                if (!string.IsNullOrEmpty(b)) sb.AppendLine("  " + b);
                sb.Append("  stars: ").Append(s < 0 ? "?" : s.ToString())
                  .Append("  mail waiting: ").Append(m ? "YES" : "no").Append('\n');
                if (m)
                    sb.Append("  get it: use the mailbox (letter box — NEVER the sell machine); fetching it for your player is great help").Append('\n');
                sb.Append("  (this is the player's current GAME objective — offering help with it is top-tier playerlike)");
                return sb.ToString();
            }
            catch (Exception) { return "quest: unavailable"; }
        }
    }
}