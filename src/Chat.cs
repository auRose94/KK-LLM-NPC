// KKLLMNPC — a BepInEx plugin for KoboldKare that lets an LLM embody and play
// as an unoccupied Kobold NPC.
//
// The plugin runs inside the game process. It:
//   1. Hijacks the nearest wild (AIPlayer) Kobold, takes Photon ownership and
//      suppresses its built-in wander/look AI.
//   2. Gives the LLM two senses:
//        - a frustum fan of raycasts around the kobold's facing  (structure)
//        - a first-person camera render read back as a base64 PNG (vision)
//   3. Reports kobold stats/genes/energy + world position.
//   4. Exposes tool commands (move/turn/jump/look/interact/grab/drop/eat...)
//      by driving the same KoboldCharacterController/User/Grabber the local
//      player uses, so movement & interaction behave exactly like a player.
//   5. Talks to an OpenAI-compatible chat-completions endpoint with tool
//      calling: it pushes perceptions and executes returned tool_calls in a
//      loop on its own thread, so the LLM continuously plays the NPC.
//
// Build against BepInEx + UnityEngine + Photon + Assembly-CSharp (see build.sh).
// Drop the DLL into <game>/BepInEx/plugins/ and configure the endpoint in
// BepInEx/config/com.kk.llmnpc.cfg after first launch.

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
    public partial class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
    {

        // ------------------------------------------------------------------
        // Photon events: hear what players type into the chat window
        // ------------------------------------------------------------------
        public void OnEvent(ExitGames.Client.Photon.EventData ev)
        {
            try
            {
                if (ev.Code != NetworkManager.CustomChatEvent) return;
                var msg = ev.CustomData as string;
                if (string.IsNullOrEmpty(msg)) return;

                string senderName = null;
                try
                {
                    var sender = PhotonNetwork.CurrentRoom?.GetPlayer(ev.Sender);
                    if (sender != null) senderName = sender.NickName;
                }
                catch (Exception) { }

                // Ignore our own speech: our chat text is prefixed "MyName: ".
                string myPrefix = MyName() + ":";
                if (msg.StartsWith(myPrefix, StringComparison.OrdinalIgnoreCase))
                    return;

                // The local player is who we care about — but capture anyone.
                bool isLocal = false;
                try { var lp = PhotonNetwork.LocalPlayer; isLocal = lp != null && lp.ActorNumber == ev.Sender; } catch (Exception) { }

                string heard = (isLocal ? "player" : (senderName ?? "someone")) + ": " + msg;
                _playerChat = heard;
                _playerChatTime = Time.unscaledTime;
                Logger.LogInfo("heard chat: " + heard);
            }
            catch (Exception e) { Logger.LogWarning("onEvent: " + e.Message); }
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
        private string MyName()
        {
            return string.IsNullOrEmpty(_npcName) ? (_kobold != null ? CleanName(_kobold.name) : "NPC") : _npcName;
        }


        // Chat the player typed within the last ~30s — null otherwise, and only once
        // per distinct message so we don't keep responding to the same line.
        private string _lastDeliveredChat;

        private string RecentPlayerChat()
        {
            if (_playerChat == null || Time.unscaledTime - _playerChatTime > 30f) return null;
            if (_playerChat == _lastDeliveredChat) return null;
            _lastDeliveredChat = _playerChat;
            return _playerChat;
        }


        private object ToolSay(JsonObj p)
        {
            string text = p.S("text", "");
            if (text.Length == 0) return new { ok = false, reason = "empty" };
            string who = CleanName(_kobold != null ? _kobold.name : "NPC");
            Logger.LogInfo("[NPC] " + who + ": " + text); // always visible in the console/log
            RunOnMainThreadAsync(() =>
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
            });
            return new { ok = true, said = text };
        }
    }
}
