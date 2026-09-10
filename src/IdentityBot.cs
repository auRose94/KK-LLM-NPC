// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// NPC room identity — a SECOND Photon client that joins the current room under the NPC's
// own name, so the NPC's chat renders as "KoboldName: text" to EVERYONE instead of being
// attributed to the plugin owner ("OwnerName: KoboldName: text").
//
// Why a second client: the game's chat handler (NetworkManager.OnEvent) renders
//   sender.NickName + ": " + text
// where sender is the Photon player who raised the event. The NPC's speech is sent over
// the owner's connection, so the sender is always the owner. The only way to make the
// sender BE the NPC is to give the NPC its own room player — a separate LoadBalancingClient
// that joins the room as "KoboldName". PUN2's LoadBalancingClient is instantiable and uses
// a per-instance peer (PhotonNetwork.NetworkingClient itself is one), so a second client is
// supported.
//
// OPT-IN (Multiplayer.IdentityBot, default OFF). If the bot can't join (region/auth/room
// issues), ToolSay falls back to the safe owner-attributed body-prefix, so this can never
// break NPC speech — worst case it just looks like the pre-bot behavior.
using System;
using System.Collections.Generic;
using System.Threading;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace KKLLMNPC
{
    internal sealed class NpcIdentityBot : IConnectionCallbacks, IMatchmakingCallbacks
    {
        private readonly NPCInstance _npc;
        private LoadBalancingClient _client;
        private Thread _pump;
        private volatile bool _running;
        private volatile bool _inRoom;
        private string _roomName;
        private string _nickName;
        private int _retries;
        private int _backoffSeconds = 2; // exponential backoff: 2s → 4s → 8s … cap 30s
        private const int MaxBackoffSeconds = 30;
        private DateTime _lastStartUtc = DateTime.MinValue;
        private readonly object _stateLock = new object();

        internal NpcIdentityBot(NPCInstance npc) { _npc = npc; }

        internal bool InRoom { get { return _inRoom; } }

        // The room this bot is bound to (target even while still connecting). Callers
        // compare it against the current room to decide if the bot is stale.
        internal string RoomName { get { return _roomName; } }

        // Start (or keep) the bot for this room+name. No-op if already up for the same
        // room/name. Safe to call repeatedly; it won't spawn duplicate clients.
        internal void EnsureStarted(string roomName, string nickName)
        {
            if (string.IsNullOrEmpty(roomName) || string.IsNullOrEmpty(nickName)) return;
            lock (_stateLock)
            {
                if (_inRoom && _roomName == roomName && _nickName == nickName) return;
                // Exponential backoff: while the bot is down, the fallback (owner-attributed
                // chat) covers us, so there's no point hammering Photon every turn.
                if ((DateTime.UtcNow - _lastStartUtc).TotalSeconds < _backoffSeconds) return;
                if (_running || _client != null)
                {
                    // A previous attempt is stalled or gave up (retries exhausted) — tear it
                    // down so this call can start a fresh client instead of returning forever.
                    _running = false;
                    try { if (_client != null) _client.Disconnect(DisconnectCause.DisconnectByClientLogic); } catch (Exception) { }
                    if (_pump != null) { try { _pump.Join(200); } catch (Exception) { } _pump = null; }
                    _client = null;
                }
                _lastStartUtc = DateTime.UtcNow;
                _running = true;
                _roomName = roomName;
                _nickName = nickName;
                _npc.Logger.LogInfo("[identity-bot] starting (room='" + roomName + "' nick='" + nickName + "')");
                try
                {
                    _client = new LoadBalancingClient(ConnectionProtocol.Udp);
                    _client.AddCallbackTarget(this);
                    _client.NickName = nickName;
                    bool ok = _client.ConnectUsingSettings(BuildAppSettings());
                    if (!ok)
                    {
                        _npc.Logger.LogWarning("[identity-bot] ConnectUsingSettings returned false — chat stays owner-attributed");
                        _running = false;
                    }
                }
                catch (Exception e)
                {
                    _npc.Logger.LogWarning("[identity-bot] start failed: " + e.Message + " — chat stays owner-attributed");
                    _running = false;
                    _client = null;
                    return;
                }
                if (_pump == null || !_pump.IsAlive)
                {
                    _pump = new Thread(PumpLoop) { IsBackground = true, Name = "KKLLMNPC-IdentityBot" };
                    _pump.Start();
                }
            }
        }

        // Mirror the game client's connection settings (same AppId, same region) so the bot
        // lands in the same region as the room. Without the matching region the join fails.
        private AppSettings BuildAppSettings()
        {
            AppSettings s = new AppSettings();
            try
            {
                var nc = PhotonNetwork.NetworkingClient;
                if (nc != null)
                {
                    s.AppIdRealtime = nc.AppId;
                    s.AppVersion = nc.AppVersion;
                    s.UseNameServer = nc.IsUsingNameServer;
                    try { if (nc.LoadBalancingPeer != null) s.Protocol = nc.LoadBalancingPeer.TransportProtocol; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            try { string region = PhotonNetwork.CloudRegion; if (!string.IsNullOrEmpty(region)) s.FixedRegion = region; } catch (Exception) { }
            string overrideAppId = _npc._cfgIdentityAppId != null ? _npc._cfgIdentityAppId.Value : null;
            if (!string.IsNullOrWhiteSpace(overrideAppId)) s.AppIdRealtime = overrideAppId;
            s.NetworkLogging = DebugLevel.ERROR;
            return s;
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try { if (_client != null) _client.Service(); } catch (Exception) { }
                Thread.Sleep(50);
            }
        }

        // Send the NPC's line as the bot. Body is the PLAIN text (no name prefix) — the
        // game renders the bot's nickname as the prefix, so it shows "KoboldName: text".
        // Returns false if the bot isn't in the room (caller falls back to owner-attributed).
        internal bool SendChat(string text)
        {
            if (!_inRoom) return false;
            LoadBalancingClient c = _client;
            if (c == null) return false;
            try
            {
                var opts = new RaiseEventOptions { CachingOption = EventCaching.DoNotCache, Receivers = ReceiverGroup.Others };
                return c.OpRaiseEvent(NetworkManager.CustomChatEvent, text, opts, SendOptions.SendReliable);
            }
            catch (Exception e)
            {
                _npc.Logger.LogWarning("[identity-bot] SendChat: " + e.Message);
                return false;
            }
        }

        internal void Stop()
        {
            lock (_stateLock)
            {
                _running = false;
                _inRoom = false;
                // Let the pump thread park (it sleeps 50ms/loop) BEFORE we disconnect —
                // 200ms caps the main-thread stall (it's called from the game's main thread).
                try { if (_pump != null) _pump.Join(200); } catch (Exception) { }
                _pump = null;
                try { if (_client != null) _client.Disconnect(DisconnectCause.DisconnectByClientLogic); } catch (Exception) { }
                _client = null;
            }
        }

        // ---- IConnectionCallbacks ----
        public void OnConnected() { }
        public void OnConnectedToMaster()
        {
            // Authenticated & on the master — join the room the NPC should be in.
            try
            {
                if (string.IsNullOrEmpty(_roomName)) return;
                bool ok = _client.OpJoinRoom(new EnterRoomParams { RoomName = _roomName });
                _npc.Logger.LogInfo("[identity-bot] OpJoinRoom('" + _roomName + "') -> " + ok);
            }
            catch (Exception e) { _npc.Logger.LogWarning("[identity-bot] join: " + e.Message); }
        }
        public void OnRegionListReceived(RegionHandler regionHandler) { /* client auto-connects to the fixed region */ }
        public void OnDisconnected(DisconnectCause cause)
        {
            _inRoom = false;
            _retries++;
            _npc.Logger.LogWarning("[identity-bot] disconnected: " + cause + " (attempt " + _retries + ", backoff=" + _backoffSeconds + "s) — falling back to owner-attributed chat");
            // Exponential backoff: 2s → 4s → 8s → 16s → 30s cap.
            // After 3 consecutive failures, stop trying for this turn (fallback covers us).
            if (_running && _retries <= 3)
            {
                try { if (_client != null) _client.ConnectUsingSettings(BuildAppSettings()); }
                catch (Exception e) { _npc.Logger.LogWarning("[identity-bot] reconnect: " + e.Message); }
                // Double backoff for next failure (cap at 30s).
                _backoffSeconds = Math.Min(_backoffSeconds * 2, MaxBackoffSeconds);
            }
        }
        public void OnCustomAuthenticationResponse(Dictionary<string, object> data) { }
        public void OnCustomAuthenticationFailed(string debugMessage)
        {
            _npc.Logger.LogWarning("[identity-bot] auth failed: " + debugMessage);
        }

        // ---- IMatchmakingCallbacks ----
        public void OnFriendListUpdate(List<FriendInfo> friendList) { }
        public void OnCreatedRoom() { }
        public void OnCreateRoomFailed(short returnCode, string message)
        {
            _npc.Logger.LogWarning("[identity-bot] create failed: " + returnCode + " " + message);
        }
        public void OnJoinedRoom()
        {
            _inRoom = true;
            _retries = 0;
            _backoffSeconds = 2; // reset backoff on success
            _npc.Logger.LogInfo("[identity-bot] in room as '" + _nickName + "' — NPC chat now attributed to the NPC");
        }
        public void OnJoinRoomFailed(short returnCode, string message)
        {
            _inRoom = false;
            _npc.Logger.LogWarning("[identity-bot] join failed (" + returnCode + " " + message + ") — chat stays owner-attributed");
        }
        public void OnJoinRandomFailed(short returnCode, string message) { }
        public void OnLeftRoom() { _inRoom = false; }
    }
}
