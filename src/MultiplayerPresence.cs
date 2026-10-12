// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// MultiplayerPresence — the mod-to-mod handshake, over the GAME'S OWN Photon room.
//
// Why: two players running KKLLMNPC in one room would each claim AI kobolds up to
// their own MaxNPCs — doubling the LLM load, fighting over the same bodies, and a
// crashed/hung client would leave its claimed bodies orphaned mid-drive forever.
//
// The handshake needs no new networking: PUN gives every client per-PLAYER custom
// properties. Each modded client publishes a little envelope under its own player
// properties:
//     kkllmnpc = { v, t (unix beat), claimed: [viewId...], player: <my player body
//                 viewId> }
// and every ~4 s re-publishes with a fresh beat. Every ~2 s every client reads the
// room's players and:
//   * counts ALIVE mod peers (beat < StaleSeconds) — the duty share for new claims
//     is ceil(MaxNPCs / aliveClients) (taking over duties TOGETHER),
//   * flags HUNG peers (beat stale but still in the room — a crashed process
//     usually disconnects, which Photon removes; a frozen client lingers) and marks
//     their claimed bodies reclaimable by the survivor's claim scan — the crash
//     mitigation: nobody plays with empty-headed puppets after a peer freezes.
//
// All Photon/room work runs on the MAIN thread from Main.Update, throttled.
// Try/catch everything: no handshake must ever break gameplay.
using System;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class MultiplayerPresence
    {
        internal const string PropKey = "kkllmnpc";
        internal const float HeartbeatSeconds = 4f;
        internal const float PeerScanSeconds = 2f;
        internal const float StaleSeconds = 15f;

        internal sealed class PeerPresence
        {
            public int ActorNr;
            public string NickName;
            public double BeatUnix;      // last published beat
            public List<int> Claimed = new List<int>(); // body viewIds they drive
            public int PlayerView = -1;  // their local player body's view (never reclaimable)
            public bool Alive { get { return (CurrentUnix() - BeatUnix) < StaleSeconds; } }
            public bool Hung { get { return (CurrentUnix() - BeatUnix) >= StaleSeconds && BeatUnix > 0; } }
        }

        private static LLMNPCPlugin plugin;
        private static float _lastBeat, _lastScan;
        private static bool _enabled;
        private static readonly List<PeerPresence> _peersNow = new List<PeerPresence>();
        private readonly static object _lock = new object();
        // Hung peers' claimed body ids — the claim scan consults this set.
        private static readonly HashSet<int> _hungClaimed = new HashSet<int>();

        internal static void Ensure(LLMNPCPlugin p)
        {
            plugin = p;
        }

        private static bool Enabled()
        {
            var cfg = plugin != null ? plugin._cfgHandshake : null;
            return cfg != null && cfg.Value;
        }

        private static double CurrentUnix()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        // Called from Main.Update (main thread, throttled).
        internal static void Tick(LLMNPCPlugin p)
        {
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null) return;
                if (plugin == null) plugin = p;
                bool want = Enabled();
                if (want != _enabled)
                {
                    _enabled = want;
                    if (!want)
                        try { PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { [PropKey] = null }); } catch (Exception) { }
                }
                if (!_enabled) return;

                float now = Time.unscaledTime;
                if (now - _lastBeat >= HeartbeatSeconds) { _lastBeat = now; Heartbeat(); }
                if (now - _lastScan >= PeerScanSeconds) { _lastScan = now; ScanPeers(); }
            }
            catch (Exception) { }
        }

        // Publish (or refresh) this client's envelope: claimed viewIds + player body + beat.
        private static void Heartbeat()
        {
            try
            {
                var claimed = new List<int>();
                int playerView = -1;
                lock (plugin._instancesLock)
                    foreach (var npc in plugin._instances)
                    {
                        try
                        {
                            if (npc == null || !npc.Running) continue;
                            var k = npc.KoboldObj as Kobold;
                            if (k == null) continue;
                            var pv = k.photonView;
                            if (pv != null) claimed.Add(pv.ViewID);
                        }
                        catch (Exception) { }
                    }
                try
                {
                    if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    {
                        var ppv = pp.kobold.photonView;
                        if (ppv != null) playerView = ppv.ViewID;
                    }
                }
                catch (Exception) { }
                var env = new Dictionary<string, object>
                {
                    ["v"] = 1,
                    ["t"] = CurrentUnix(),
                    ["claimed"] = claimed.ToArray(),
                    ["player"] = playerView,
                };
                PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { [PropKey] = KKLLMNPC.Json.Write(env) });
            }
            catch (Exception) { }
        }

        private static void ScanPeers()
        {
            var peers = new List<PeerPresence>();
            var hungClaimed = new HashSet<int>();
            var aliveClients = 1; // ourselves
            try
            {
                var room = PhotonNetwork.CurrentRoom;
                if (room == null || room.Players == null) return;
                foreach (var kv in room.Players)
                {
                    Player pl = kv.Value;
                    if (pl == null || pl.ActorNumber == PhotonNetwork.LocalPlayer.ActorNumber) { if (pl != null && IsModdedPlayer(pl)) aliveClients++; continue; }
                    if (!IsModdedPlayer(pl)) continue; // unmodded players are invisible to the handshake
                    peers.Add(ParsePeer(pl));
                }
            }
            catch (Exception) { }
            foreach (var p in peers)
                if (p.Hung && p.Claimed != null)
                    foreach (var v in p.Claimed) hungClaimed.Add(v);
            lock (_lock) { _peersNow.Clear(); _peersNow.AddRange(peers); _hungClaimed.Clear(); foreach (var v in hungClaimed) _hungClaimed.Add(v); }
        }

        private static bool IsModdedPlayer(Player pl)
        {
            try
            {
                object val;
                if (pl == null || !pl.CustomProperties.TryGetValue(PropKey, out val)) return false;
                if (val == null || (string)val == null) return false;
                return ((string)val).Length > 2;
            }
            catch (Exception) { return false; }
        }

        private static PeerPresence ParsePeer(Player pl)
        {
            var peer = new PeerPresence { ActorNr = pl.ActorNumber, NickName = pl.NickName };
            try
            {
                object val;
                if (pl.CustomProperties.TryGetValue(PropKey, out val))
                {
                    var env = KKLLMNPC.Json.Parse(val as string) as Dictionary<string, object>;
                    if (env != null)
                    {
                        object t; if (env.TryGetValue("t", out t)) peer.BeatUnix = Convert.ToDouble(t);
                        object cl;
                        if (env.TryGetValue("claimed", out cl)) { var arr = cl as List<object>; if (arr != null) foreach (var v in arr) peer.Claimed.Add(Convert.ToInt32(v)); }
                        object pv; if (env.TryGetValue("player", out pv)) peer.PlayerView = Convert.ToInt32(pv);
                    }
                }
            }
            catch (Exception) { }
            return peer;
        }

        // ---- the claim scan's hooks (main thread) ----

        // Duty sharing: have OTHERS already filled the room's reasonable share of
        // LLM-driven bodies, so this client should stand down before claiming more?
        // share = ceil(MaxNPCs / alive modded clients + 1) — no mod peers = unlimited.
        internal static bool DutyShareReached()
        {
            if (!_enabled) return false;
            List<PeerPresence> peers;
            lock (_lock) peers = new List<PeerPresence>(_peersNow);
            int alive = 1;
            foreach (var p in peers) if (p.Alive) alive++;
            int myClaimed = 0;
            try
            {
                lock (plugin._instancesLock)
                    foreach (var npc in plugin._instances)
                        try { if (npc != null && npc.Running && (npc.KoboldObj as Kobold) != null) myClaimed++; }
                        catch (Exception) { }
            }
            catch (Exception) { }
            int cap = plugin._cfgMaxNPCs != null ? plugin._cfgMaxNPCs.Value : 1;
            int share = (cap + alive - 1) / alive; // ceil
            if (myClaimed < share) return false;
            if (HungOrphansAvailable()) return false; // crash mitigation outranks the share
            return true;
        }

        // Is this body view claimed by a HUNG (frozen, property-stale) peer — reclaimable?
        internal static bool IsHungPeerClaim(int viewId)
        {
            lock (_lock) return _hungClaimed.Contains(viewId);
        }

        private static bool HungOrphansAvailable()
        {
            lock (_lock) return _hungClaimed.Count > 0;
        }

        // Tests / log tail.
        internal static string StatusLine()
        {
            List<PeerPresence> peers;
            lock (_lock) peers = new List<PeerPresence>(_peersNow);
            int hung = 0; int alive = 0;
            foreach (var p in peers) { if (p.Alive) alive++; else hung++; }
            return peers.Count == 0 ? "no mod peers" : alive + " mod peer(s) alive" + (hung > 0 ? ", " + hung + " FROZEN (their bodies reclaimable)" : "");
        }
    }
}