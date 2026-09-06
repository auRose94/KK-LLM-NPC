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
        // (player lines + our own ToolSay lines), trimmed to the last ChatLogLines
        // conversation lines. Strip HTML color tags and drop non-speech notices.
        // Returns a JSON array (or "[]" if empty / disabled).
        private string ChatLogJson()
        {
            try
            {
                if (_cfgChatLogLines == null || _cfgChatLogLines.Value <= 0) return "[]";
                string output;
                try { output = CheatsProcessor.GetOutput(); }
                catch (Exception) { return "[]"; }
                if (string.IsNullOrEmpty(output)) return "[]";

                var lines = new System.Collections.Generic.List<string>();
                string myName = MyName();
                foreach (var raw in output.Split('\n'))
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    string l = raw.Trim();
                    if (l.Length == 0) continue;
                    // Keep only human-readable speech lines: "<speaker>: <text>". Strip
                    // BBCode-ish color tags like "<color=yellow>…</color>".
                    int colon = l.IndexOf(':');
                    if (colon <= 0 || colon > 32) continue;
                    string speaker = l.Substring(0, colon).Trim();
                    string body = l.Substring(colon + 1).Trim();
                    if (speaker.Length == 0 || body.Length == 0) continue;
                    if (speaker.StartsWith("<color", StringComparison.Ordinal) ||
                        speaker.StartsWith("<", StringComparison.Ordinal)) continue;
                    // Drop cheat/system commands so the NPC never has to interpret them.
                    if (IsCheatCommand(body)) continue;
                    // Skip own messages — the model already knows what it said.
                    if (string.Equals(speaker, myName, StringComparison.OrdinalIgnoreCase)) continue;
                    string clean = StripChatMarkup(l);
                    lines.Add(clean);
                }

                int keep = Math.Min(_cfgChatLogLines.Value, MaxChatLog);
                int start = lines.Count - keep;
                if (start < 0) start = 0;

                var sb = new StringBuilder("[");
                bool first = true;
                for (int i = start; i < lines.Count; i++)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Json.Write(lines[i]));
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch (Exception) { return "[]"; }
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
            string text = p.S("text", "");
            if (text.Length == 0) return new { ok = false, reason = "empty" };
            text = Sanitize(text);
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

                    // 2) The real chat window: same Photon event the ChatPanel raises,
                    // so it lands in everyone's chat history. The receiver renders it as
                    // "<sender nickname>: <message>" — and since we own the kobold's
                    // PhotonView, the sender is YOUR username. So we prefix the kobold's
                    // name in the message text itself so chat reads clearly.
                    try
                    {
                        string senderName = MyName();
                        string chatText = senderName + ": " + text;
                        if (PhotonNetwork.InRoom)
                        {
                            var opts = new Photon.Realtime.RaiseEventOptions {
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
                    catch (Exception e) { Logger.LogWarning("say chat: " + e.Message); }
                }
                catch (Exception e) { Logger.LogWarning("say async: " + e.Message); }
            });
            return new { ok = true, said = text };
        }
    }
}
