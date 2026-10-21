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
        // The objective OBJECT on the scroll (not just its title string): title-only
        // diffing misreads autoAdvance chains — quest finishes, next letter switches
        // in, and all we ever saw was "progress". Reference identity catches the
        // hand-off and lets us say "that quest is FINISHED".
        private static DragonMailObjective _lastObj;
        // Consecutive poll exceptions = scene teardown (the manager singleton dies
        // mid-throw). Retiring state only after a streak avoids one bad call
        // masquerading as "quest completed".
        private static int _errStreak;
        private static float _lastGoodPoll = -99f;

        // Main-thread poll from the plugin's Update, 1/s.
        internal static void Tick(LLMNPCPlugin plugin)
        {
            try
            {
                if (plugin == null || !plugin._mainReady || !plugin.Running) return;
                if (Time.unscaledTime - _lastPoll < 1f) return;
                _lastPoll = Time.unscaledTime;

                string title = null, body = null;
                DragonMailObjective obj = null;
                try
                {
                    obj = ObjectiveManager.GetCurrentObjective();
                    if (obj != null)
                    {
                        title = obj.GetTitle();
                        body = obj.GetTextBody() ?? "";
                        if (string.IsNullOrEmpty(title)) { obj = null; title = null; body = null; }
                    }
                }
                catch (Exception)
                {
                    // The manager is per-scene: a throw usually means scene teardown.
                    // Retain state across the gap; only a streak retires it silently.
                    try
                    {
                        if (++_errStreak >= 2)
                            lock (_lock) { _lastObj = null; _title = null; _body = null; _hasMail = false; _errStreak = 0; }
                    }
                    catch (Exception) { }
                    return;
                }
                _errStreak = 0;
                bool sceneStable = Time.unscaledTime - _lastGoodPoll < 5f;
                _lastGoodPoll = Time.unscaledTime;
                bool hasMail = false;
                try { hasMail = ObjectiveManager.HasMail(); } catch (Exception) { }
                int stars = -1;
                try { stars = ObjectiveManager.GetStars(); } catch (Exception) { }

                string ev = null, doneTitle = null;
                lock (_lock)
                {
                    bool had = _lastObj != null || !string.IsNullOrEmpty(_title);
                    bool has = !string.IsNullOrEmpty(title);
                    if (has)
                    {
                        if (_lastObj != null && _lastObj != obj)
                        {
                            // A different letter is on the scroll. On an unstable poll
                            // window (scene change) we can't trust this to mean the old
                            // one completed — announce the new letter only.
                            if (had && sceneStable)
                            {
                                doneTitle = string.IsNullOrEmpty(_title) ? "(previous letter)" : _title;
                                ev = "OBJECTIVE COMPLETED: '" + doneTitle + "' — that quest is FINISHED and can't be worked again."
                                    + " If your current goal (set_goal) was that quest, run complete_goal now and forget any facts you kept about it."
                                    + " The scroll now shows a NEW letter: '" + title + "' — " + body
                                    + " That is the PLAYER'S current game objective; offer to help, take it on as your goal, and report progress.";
                            }
                            else
                                ev = "A NEW OBJECTIVE LETTER is live in the top-right scroll: '" + title + "' — " + body
                                    + " That is the PLAYER'S current game objective; offer to help, take it on as your goal, and report progress.";
                        }
                        else if (_lastObj == null)
                        {
                            if (!had)
                                ev = "A NEW OBJECTIVE LETTER is live in the top-right scroll: '" + title + "' — " + body
                                    + " That is the PLAYER'S current game objective; offer to help, take it on as your goal, and report progress.";
                            else if (!string.Equals(title, _title, StringComparison.Ordinal))
                                ev = "objective progress: '" + title + "' — " + body;
                        }
                        else if (!string.Equals(title, _title, StringComparison.Ordinal))
                        {
                            ev = "objective progress: '" + title + "' — " + body;
                        }
                    }
                    else if (_lastObj != null && had && sceneStable)
                    {
                        // Scroll emptied by the manager itself: completion, not teardown
                        // (teardown exits above via the error streak; an unstable poll
                        // window never gets to claim a completion).
                        doneTitle = string.IsNullOrEmpty(_title) ? "(previous letter)" : _title;
                        ev = "OBJECTIVE COMPLETED: '" + doneTitle + "' (stars: " + (_stars < 0 ? "?" : _stars.ToString()) + ")"
                            + " — that quest is FINISHED and can't be worked again. If your current goal (set_goal) was that quest,"
                            + " run complete_goal now and forget any facts you kept about it. New letters arrive at the MAILBOX.";
                    }
                    else if (hasMail && !_hasMail && !has)
                    {
                        ev = "MAIL is waiting at the mailbox — get it by USING the mailbox (the letter box; NOT the sell machine) — the next objective letter is inside, and YOU can fetch it for your player";
                    }
                    _title = title; _body = body; _hasMail = hasMail; _lastObj = obj;
                    if (stars >= 0) _stars = stars;
                }
                if (ev != null) try { plugin.FanoutWorldEvent(ev); } catch (Exception) { }
                // Belt for the prompt event: actively shed quest-scoped facts and retire
                // a goal that was that quest, so nobody keeps grinding a dead objective.
                if (doneTitle != null) try { plugin.FanoutQuestFinished(doneTitle); } catch (Exception) { }
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