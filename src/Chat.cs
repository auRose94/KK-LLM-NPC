// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Real chat via Photon RaiseEvent + local bubble + ambient voice; hears the player.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Photon.Pun;
using Photon.Realtime;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // OnEvent is handled by the plugin and distributed to instances via HandleChat.

        // Cross-instance name registry: two agents must never share a chat name — the
        // old failure was two NPCs on the SAME avatar model both falling back to the
        // prefab name and flooding chat with indistinguishable "LoonaDZ" lines. An
        // in-memory set (all instances of this plugin) plus a small shared file under
        // BepInEx/config so renamed DLL copies (KKLLMNPC2.dll) also collide-check.
        // Player chat names are reserved (MarkTaken) so an NPC can't impersonate
        // "Rosemary" or "Yipper" either.
        internal static class NameRegistry
        {
            private static readonly object _lock = new object();
            private static readonly HashSet<string> _taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private static string _file;
            private static bool _initialized;

            private static void Init()
            {
                if (_initialized) return;
                try
                {
                    string data = Application.dataPath;
                    if (!string.IsNullOrEmpty(data))
                    {
                        string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(data), "BepInEx", "config");
                        System.IO.Directory.CreateDirectory(dir);
                        _file = System.IO.Path.Combine(dir, "kkllmnpc_names.txt");
                        if (System.IO.File.Exists(_file))
                            foreach (var line in System.IO.File.ReadAllLines(_file))
                            {
                                string n = line.Trim();
                                if (n.Length > 0) _taken.Add(n);
                            }
                    }
                }
                catch (Exception) { _file = null; }
                _initialized = true;
            }

            // Reserve a name. False when it's already taken (by us, another agent,
            // or a reserved player name).
            internal static bool TryReserve(string name)
            {
                lock (_lock)
                {
                    Init();
                    if (string.IsNullOrEmpty(name) || _taken.Contains(name)) return false;
                    _taken.Add(name);
                    if (_file != null)
                    {
                        try { System.IO.File.AppendAllText(_file, name + "\n"); } catch (Exception) { }
                    }
                    return true;
                }
            }

            // Reserve WITHOUT a collision check — for names that must be usable
            // (player chat names) and for re-claiming our own after a world reload.
            internal static void MarkTaken(string name)
            {
                lock (_lock)
                {
                    Init();
                    if (!string.IsNullOrEmpty(name)) _taken.Add(name);
                }
            }

            internal static bool IsTaken(string name)
            {
                lock (_lock)
                {
                    Init();
                    return !string.IsNullOrEmpty(name) && _taken.Contains(name);
                }
            }

            internal static string TakenList()
            {
                lock (_lock)
                {
                    Init();
                    return string.Join(", ", _taken).Trim();
                }
            }

            // Guaranteed-unique fallback name: base, then base+suffix, then base-NNN.
            internal static string Unique(string baseName, string hint)
            {
                lock (_lock)
                {
                    Init();
                    string b = string.IsNullOrEmpty(baseName) ? "Kobold" : baseName;
                    if (TryReserve(b)) return b;
                    if (hint != null && TryReserve(b + "-" + hint)) return b + "-" + hint;
                    for (int i = 0; i < 900; i++)
                    {
                        string cand = b + "-" + (100 + i);
                        if (TryReserve(cand)) return cand;
                    }
                    string last = b + "-" + UnityEngine.Random.Range(1000, 9999);
                    TryReserve(last);
                    return last;
                }
            }
        }

        // Pick an identity for the body: species hint from its name (e.g. "AbsolB"
        // → base name), plus a short suffix derived from its equipment so it's stable
        // per body within this session without asking the model.
        private string PickName(Kobold target)
        {
            string raw = CleanName(target.name);            // "AbsolB(Clone)" -> "AbsolB"
            if (raw.Length == 0) raw = "Kobold";
            return raw;
        }

        // What the player should call it in chat / what's in perception as "me".
        // The kobold's chosen identity — the body prefab name + instance suffix (e.g.
        // 'AbsolB2'), so multiple NPCs stay distinguishable in chat and logs.
        private string MyName()
        {
            return string.IsNullOrEmpty(_npcName) ? (_kobold != null ? CleanName(_kobold.name) : "NPC") : _npcName;
        }

        // Chat the player typed within the last ~30s — null otherwise, and only once
        // per distinct message so we don't keep responding to the same line.
        private string RecentPlayerChat()
        {
            if (_playerChat == null || Time.unscaledTime - _playerChatTime > 30f) return null;
            if (_playerChat == _lastDeliveredChat) return null;
            _lastDeliveredChat = _playerChat;
            return _playerChat;
        }

        // True when a chat message is a cheat/system command (starts with '/') rather
        // than something the NPC should treat as real conversation. The NPC shouldn't
        // recognize these as speech at all.
        private static bool IsCheatCommand(string body)
        {
            if (body == null) return true;
            string b = body.Trim().Trim('"', '\'');
            return b.Length > 0 && b[0] == '/';
        }

        // The full conversation as far as the game's chat panel has accumulated it
        // (player lines + our own ToolSay lines), trimmed to the newest entries.
        // Each entry includes a timestamp for age calculation and an ack flag.
        // Returns a JSON array of {from, text, age, new} objects (or "[]" if empty).
        private string ChatLogJson()
        {
            try
            {
                if (_cfgChatLogLines == null || _cfgChatLogLines.Value <= 0) return "[]";
                float now = Time.unscaledTime;
                string myName = MyName();

                // Prune old entries and cap by count.
                lock (_chatEntries)
                {
                    // Remove entries older than 60s.
                    for (int i = _chatEntries.Count - 1; i >= 0; i--)
                    {
                        if (now - _chatEntries[i].Time > MaxChatEntriesAge)
                            _chatEntries.RemoveAt(i);
                    }
                    // Cap to newest N entries.
                    int keep = Math.Min(_cfgChatLogLines.Value, MaxChatEntriesCount);
                    while (_chatEntries.Count > keep)
                        _chatEntries.RemoveAt(0);
                }

                var sb = new StringBuilder("[");
                bool first = true;
                lock (_chatEntries)
                {
                    foreach (var e in _chatEntries)
                    {
                        // Skip own messages — the model already knows what it said.
                        if (string.Equals(e.From, myName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        double age = Math.Round(now - e.Time, 1);
                        bool isNew = !_seenChatAcks.Contains(e.AckId);
                        sb.Append("{\"from\":");
                        sb.Append(Json.Write(e.From));
                        sb.Append(",\"text\":");
                        sb.Append(Json.Write(e.Text));
                        sb.Append(",\"age\":");
                        sb.Append(age.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append(",\"new\":");
                        sb.Append(isNew ? "true" : "false");
                        sb.Append('}');
                    }
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch (Exception) { return "[]"; }
        }

        // Record a chat entry with timestamp for the timestamped chat log.
        private void RecordChatEntry(string speaker, string text)
        {
            if (string.IsNullOrEmpty(speaker) || string.IsNullOrEmpty(text)) return;
            string clean = StripChatMarkup(text);
            if (clean.Length == 0) return;
            string ackId = ComputeAckId(speaker, clean);
            lock (_chatEntries)
            {
                _chatEntries.Add(new ChatEntry
                {
                    From = speaker,
                    Text = clean,
                    Time = Time.unscaledTime,
                    AckId = ackId,
                });
            }
        }

        // Compute a stable ack ID for a chat entry (hash of from+text+t).
        private static string ComputeAckId(string from, string text)
        {
            // Simple FNV-1a hash for a stable, collision-resistant ID.
            uint hash = 2166136261;
            foreach (char c in from)
            {
                hash ^= (uint)c;
                hash *= 16777619;
            }
            foreach (char c in text)
            {
                hash ^= (uint)c;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }

        // Mark unseen chat entries that fuzzy-match the given text as "seen" (ack).
        private void MarkChatAcks(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_chatEntries)
            {
                foreach (var e in _chatEntries)
                {
                    if (_seenChatAcks.Contains(e.AckId)) continue;
                    if (ChatSimilarity.FuzzyMatchesChat(text, e.Text))
                    {
                        _seenChatAcks.Add(e.AckId);
                        // Cap the seen-set.
                        while (_seenChatAcks.Count > MaxSeenAcks)
                        {
                            // Remove oldest entries (first in set — arbitrary but bounded).
                            var first = default(string);
                            foreach (var k in _seenChatAcks) { first = k; break; }
                            if (first != null) _seenChatAcks.Remove(first);
                        }
                    }
                }
            }
        }

        private static string StripChatMarkup(string s)
        {
            // Remove <color=…>…</color> pairs and stray "<…>" tags.
            var sb = new StringBuilder(s.Length);
            int i = 0, n = s.Length;
            while (i < n)
            {
                if (s[i] == '<')
                {
                    int gt = s.IndexOf('>', i);
                    if (gt >= 0) { i = gt + 1; continue; }
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        // Speech to the world, three ways at once: floating Chatter bubble above the
        // body, the game's real chat window (same Photon event the ChatPanel raises),
        // and a local echo so the player sees their own window too.
        private object ToolSay(JsonObj p)
        {
            // The act schema names the parameter 'say', but salvage paths and some
            // models use 'text' or 'message' — accept all three.
            string text = p.S("say", "");
            if (text.Length == 0) text = p.S("text", "");
            if (text.Length == 0) text = p.S("message", "");
            if (text.Length == 0) return new { ok = false, reason = "empty" };
            text = Sanitize(text);
            // Repeat suppression: check exact match, Jaccard, and Levenshtein ratio
            // against the last 3 says to catch near-duplicate loops.
            lock (_sayLock)
            {
                // Exact match within 15s.
                if (Time.unscaledTime - _lastSayTime < 15f && string.Equals(_lastSayText, text, StringComparison.Ordinal))
                {
                    Logger.LogInfo("[NPC] say suppressed (exact repeat within 15s): " + text);
                    _pendingSayNudge = true;
                    return new { ok = true, said = text, reason = "say_repeat", note = "you already said this line — don't repeat it; do something else" };
                }
                // Similarity check against last 3 says.
                foreach (var prev in _lastSays)
                {
                    double jaccard = ChatSimilarity.JaccardSimilarity(text, prev);
                    double levRatio = ChatSimilarity.LevenshteinRatio(text, prev);
                    if (jaccard > 0.65 || levRatio > 0.8)
                    {
                        Logger.LogInfo("[NPC] say suppressed (similar to recent): jaccard=" + jaccard.ToString("0.00") + " lev=" + levRatio.ToString("0.00") + " text=" + text);
                        _pendingSayNudge = true;
                        return new { ok = true, said = text, reason = "say_repeat", note = "you just said something very similar — say something different or take an action" };
                    }
                }
                // Not a repeat — add to rolling list.
                _lastSays.Enqueue(text);
                while (_lastSays.Count > MaxLastSays) _lastSays.Dequeue();
                _lastSayText = text;
                _lastSayTime = Time.unscaledTime;
            }
            // Mark unseen chat entries that this say fuzzy-matches as "seen" (ack).
            MarkChatAcks(text);
            string who = _kobold != null ? CleanName(_kobold.name) : "NPC";
            Logger.LogInfo("[NPC] " + who + ": " + text); // always visible in the console/log
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    // 1) Floating bubble above the kobold (local flavor). Force-activate
                    //    the chatter hierarchy so AI kobolds' bubbles actually show.
                    try
                    {
                        var chatter = _kobold != null ? _kobold.GetComponentInChildren<Chatter>(true) : null;
                        if (chatter != null)
                        {
                            if (!chatter.gameObject.activeSelf) chatter.gameObject.SetActive(true);
                            var node = chatter.transform;
                            for (var par = node.parent; par != null; par = par.parent) if (!par.gameObject.activeSelf) par.gameObject.SetActive(true);
                            chatter.DisplayMessage(text, 4f);
                        }
                    }
                    catch (Exception e) { Logger.LogWarning("say bubble: " + e.Message); }

                    // 2) The real chat window, attributed to the NPC. Two paths:
                    //    a) Identity bot (opt-in): the NPC has its own room player, so its
                    //       event renders "KoboldName: text" to everyone. We send the PLAIN
                    //       text (no prefix) and do NOT locally echo (we'd receive the bot's
                    //       event and the game renders it — echoing would double it).
                    //    b) Fallback (default): send over the owner's connection with the
                    //       kobold name prefixed in the body, and locally echo (we don't
                    //       receive our own event). Renders "OwnerName: KoboldName: text" to
                    //       others, "KoboldName: text" locally.
                    try
                    {
                        string senderName = MyName();
                        bool botSent = false;
                        if (_cfgIdentityBot != null && _cfgIdentityBot.Value)
                        {
                            EnsureIdentityBot();
                            if (_identityBot != null && _identityBot.InRoom)
                                botSent = _identityBot.SendChat(text);
                        }
                        if (botSent)
                        {
                            // Owner client receives the bot's event → the game renders
                            // "KoboldName: text". No local echo (would double it).
                        }
                        else
                        {
                            // Fallback: owner-attributed, body-prefixed.
                            string chatText = senderName + ": " + text;
                            if (PhotonNetwork.InRoom)
                            {
                                var opts = new Photon.Realtime.RaiseEventOptions
                                {
                                    CachingOption = Photon.Realtime.EventCaching.DoNotCache,
                                    Receivers = Photon.Realtime.ReceiverGroup.Others, // everyone else
                                };
                                bool sent = PhotonNetwork.RaiseEvent(
                                    NetworkManager.CustomChatEvent,
                                    chatText.TrimEnd(),
                                    opts,
                                    ExitGames.Client.Photon.SendOptions.SendReliable);
                                if (!sent) Logger.LogWarning("say: RaiseEvent returned false");
                            }
                            // Local echo: we won't receive our own event, so push it into the
                            // chat log directly the same way NetworkManager.OnEvent does.
                            try { CheatsProcessor.AppendText(chatText + "\n"); } catch (Exception e) { Logger.LogWarning("say local echo: " + e.Message); }
                            if (!PhotonNetwork.InRoom) Logger.LogInfo("say (offline, not in a room): " + text);
                        }
                    }
                    catch (Exception e) { Logger.LogWarning("say chat: " + e.Message); }
                }
                catch (Exception e) { Logger.LogWarning("say async: " + e.Message); }
            });
            return new { ok = true, said = text };
        }
    }
}
