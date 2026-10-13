// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// Possession, teardown, camera, ownership; reagent/egg/penetration listeners and equipment awareness.
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

        // Fires when this kobold's penetrator enters something (and each tick).
        // While THIS body is being digested, its dick "entering" the eater's mouth
        // is not sex — the victim gets no [sex] events or rhythm from it either.
        private void NotifyDickIn(KKPenetratorListener listener, string hole, bool entered)
        {
            if (Swallowed && entered) return;
            if (entered && listener != null && InvolvesOwnBody(listener.lastTarget))
            {
                PurgeSelfDick(listener); // our own dick cannot penetrate our own body
                return;
            }
            try
            {
                lock (_dickIn)
                {
                    PenState st;
                    if (entered)
                    {
                        if (!_dickIn.TryGetValue(listener, out st))
                        {
                            st = new PenState { name = hole, hole = hole, depth = 0.05f, lastT = Time.unscaledTime };
                            _dickIn[listener] = st;
                        }
                        st.src = listener.lastTarget;
                        //Logger.LogInfo("dick in " + hole);
                        //EmitAmbient("haa~"); // their dick slid in
                        _stimSource = PartnerName(listener.lastTarget, hole);
                        _stimSourceT = Time.unscaledTime;
                    }
                    else _dickIn.Remove(listener);
                }
            }
            catch (Exception e) { Logger.LogDebug($"NotifyDickIn error: {e.Message}"); }
        }

        private void NotifyDickTick(KKPenetratorListener listener, string hole, float dist)
        {
            if (Swallowed) return;
            if (listener != null && InvolvesOwnBody(listener.lastTarget))
            {
                PurgeSelfDick(listener);
                return;
            }
            try
            {
                lock (_dickIn)
                {
                    PenState st;
                    if (!_dickIn.TryGetValue(listener, out st))
                    {
                        st = new PenState { name = hole, hole = hole, lastT = Time.unscaledTime };
                        _dickIn[listener] = st;
                        // Their own body event too: the model is the one penetrating.
                        PushWorldEvent("[sex] you are penetrating someone — " + hole + "; thrust to move with the rhythm (feel shows the live state)");
                    }
                    st.src = listener.lastTarget;
                    float d = Mathf.Max(0f, 0.05f - Mathf.Min(0f, dist)); // distToHole<0 => inside
                    float dt = Mathf.Max(0.001f, Time.unscaledTime - st.lastT);
                    st.vel = Mathf.Lerp(st.vel, (d - st.depth) / dt, 0.2f);
                    st.depth = d;
                    st.lastT = Time.unscaledTime;
                    float nT = Time.unscaledTime;
                    st.History.Add(nT);
                    st.History.Add(d);
                    while (st.History.Count > 2 && nT - st.History[0] > 5.0f) st.History.RemoveRange(0, 2);
                }
            }
            catch (Exception e) { Logger.LogDebug($"NotifyDickTick error: {e.Message}"); }
        }

        private string DickInInfo()
        {
            lock (_dickIn)
            {
                if (_dickIn.Count == 0) return null;
                var parts = new List<string>();
                foreach (var kv in _dickIn)
                {
                    var st = kv.Value;
                    int pid = PartnerId(st.src);
                    parts.Add(st.name + " depth=" + F(st.depth) + "m thrust=" + F(st.vel)
                              + (pid > 0 ? " (with id=" + pid + ")" : " (non-kobold)"));
                }
                return _dickIn.Count + " inside (" + string.Join("; ", parts.ToArray()) + ")";
            }
        }

        // Pick (or keep) a kobold the AI can drive. Skips already-LLM-claimed and player
        // bodies, prefers the nearest free AI kobold, falls back to map-wide if none in range.
        private bool EnsureBody()
        {
            if (!_running) return false; // retired mid-marshal — never claim after shutdown
            if (_kobold != null && IsAlive(_kobold)) return true;
            if (_kobold != null) TeardownBody(); // possessed body was destroyed
            if (_everBound)
            {
                // We already had a body and it's gone (destroyed / sold / removed).
                // This instance does NOT re-possess a different kobold — the NPC's
                // identity dies with its body. The plugin retires us on its next
                // reconcile pass; a fresh instance starts if a target appears.
                if (!_bodyLostLogged)
                {
                    _bodyLostLogged = true;
                    Logger.LogInfo("KKLLMNPC: body for '" + (_npcName ?? "?") + "' was destroyed/sold/lost — instance will be removed.");
                }
                BodyLost = true;
                return false;
            }
            try
            {
                var playerPos = Vector3.zero;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    playerPos = pp.kobold.transform.position;

                // Debug: count kobolds by control type so we can tell "none on map" from
                // "exists but not AI-controlled" from "all claimed".
                int total = 0, ai = 0, claimed = 0, playerControlled = 0, reclaimableBodies = 0;
                Kobold best = null; float bestD = float.MaxValue;
                foreach (var k in SceneCache.Find<Kobold>(2f))
                {
                    if (k == null) continue;
                    // Character still loading? The game DEACTIVATES a body while its
                    // descriptor loads assets (and only applies SAVED genes after) —
                    // claiming now would fingerprint seed-default genes and wake the
                    // person as a stranger. Wait for active; the scan re-runs shortly.
                    try { if (!k.gameObject.activeInHierarchy) continue; } catch (Exception) { }
                    total++;
                    int id = k.GetInstanceID();
                    if (LLMNPCPlugin.IsClaimedByAnyLLM(id)) { claimed++; continue; }
                    // REBIRTH HOLD: a body that will hatch into a swallowed person
                    // belongs to the hatch flow, not the claim race.
                    try { if (SwallowManager.IsAwaitingRebirth(k)) continue; } catch (Exception) { }
                    var desc = k.GetComponent<CharacterDescriptor>();
                    if (desc == null) continue;
                    var ct = desc.GetPlayerControlled();
                    bool reclaimable = false;
                    if (ct == CharacterDescriptor.ControlType.AIPlayer) ai++;
                    else
                    {
                        playerControlled++;
                        // FROZEN-PEER RECLAIM (crash mitigation): a body whose last
                        // mod-driver froze (their handshake beat is stale) is
                        // reclaimable by a live peer. Human player avatars NEVER
                        // match — a player's own body viewId is published separately
                        // from any claimed list.
                        try
                        {
                            var opv = k.photonView;
                            reclaimable = ct == CharacterDescriptor.ControlType.NetworkedPlayer && opv != null
                                && MultiplayerPresence.IsHungPeerClaim(opv.ViewID);
                        }
                        catch (Exception) { reclaimable = false; }
                        if (!reclaimable) continue;
                        reclaimableBodies++;
                    }
                    // DUTY SHARE: with mod peers present, this client claims up to its
                    // own share of the AI kobolds (no doubling of LLM load in a room).
                    try { if (!reclaimable && MultiplayerPresence.DutyShareReached()) continue; } catch (Exception) { }
                    float d = Vector3.Distance(k.transform.position, playerPos);
                    if (d < bestD) { bestD = d; best = k; }
                }
                if (best == null)
                {
                    // Throttled: this runs every tick of every unbound instance — without
                    // a limit it floods the log while the pool waits for a target.
                    if (Time.unscaledTime - _lastNoBodyLog > 10f)
                    {
                        _lastNoBodyLog = Time.unscaledTime;
                        Logger.LogInfo($"KKLLMNPC: no AI kobold found (total={total} ai={ai} claimed={claimed} player={playerControlled}).");
                    }
                    return false;
                }
                if (bestD > _cfgAutoFindRange.Value)
                    Logger.LogInfo($"KKLLMNPC: nearest AI kobold is {F(bestD)}m (AutoFindRange={_cfgAutoFindRange.Value}m) — possessing anyway.");
                Possess(best);
                if (!ReferenceEquals(_kobold, best))
                {
                // Lost the claim race to another instance (or the body died between
                // selection and possession) — don't pretend we have a body. This is
                // the DESIGNED race: the claim set is atomic, so the loser backs off
                // and retries; the winner is the only driver.
                Logger.LogInfo("KKLLMNPC: claim lost (another instance took '" + best.name + "' — expected race, winner keeps driving; we keep looking).");
                return false;
                }
                return true;
            }
            catch (Exception e) { Logger.LogError("EnsureBody: " + e); return false; }
        }

        // Take control of a wild kobold: claim ownership across instances, suppress its
        // wander/look AI, steal its PhotonView so our inputDir writes stick (controller only
        // drives when photonView.IsMine), attach senses, and wire belly/penetration listeners.
        private void Possess(Kobold target)
        {
            if (target == null) return;
            if (!_running) return; // retired mid-marshal — never claim after shutdown
            TeardownBody();
            if (!IsAlive(target)) return; // died between selection and possess
            if (!_running) return; // Shutdown (reconciler) may have raced this teardown
            int id = target.GetInstanceID();
            // Atomic claim: reject if another instance grabbed it first.
            lock (LLMNPCPlugin.ClaimedKobolds)
            {
                if (LLMNPCPlugin.ClaimedKobolds.Contains(id)) return;
                LLMNPCPlugin.ClaimedKobolds.Add(id);
            }
            BindBody(target, false);
            TryHookBodySwap();
        }

        // Wire the possessed body's drivable parts (controller, head, camera,
        // subscriptions, nav/ownership state). reuseIdentity=true keeps the NPC's
        // name + persona across a bodyswap machine — only its physical body changed.
        private void BindBody(Kobold target, bool reuseIdentity)
        {
            // Double-bind tripwire: if another live instance already drives this exact
            // body, something above us failed — log it loudly with both names so the
            // "two threads fighting over a body" case is diagnosable from the log.
            try
            {
                var other = Plugin.FindOtherInstanceHolding(target, this);
                if (other != null)
                    Logger.LogError("KKLLMNPC: DOUBLE BIND — '" + other.GetMyName() + "' and '" + (_npcName ?? "?") + "' both drive body '" + CleanName(target.name) + "'. Send us this log.");
            }
            catch (Exception) { }
            _kobold = target;
            _currentKoboldId = target.GetInstanceID();
            _everBound = true;
            if (!reuseIdentity)
            {
                // A brand-new possession = a brand-new mind. Wipe every trace of the
                // previous life (facts, goals, chat, console history) so a sold/lost
                // body's context can never leak into the next claim. (Bodyswap passes
                // reuseIdentity=true and keeps the mind — that's its whole point.)
                // Applied SYNCHRONOUSLY here, not queued: this runs inside the LLM
                // thread's own RunOnMainThread marshal (LLMLoop → EnsureBody), so the
                // loop is blocked and can't be mid-turn; and a queued wipe would
                // otherwise fire AFTER TryRestoreBodyMemory and erase the restored
                // life one think tick later.
                _pendingMindReset = null;
                ResetMindNow("fresh body");
                SeedDefaultFacts();
                _npcName = PickName(target);   // base: prefab name — the LLM finalizes
                // Capture the body identity NOW (gene fingerprint + key) so every
                // save/restore/match keys the same body the same way, and remember
                // the key we tried (the late re-check compares against it).
                _boundBodyFp = BodyFullFp(target); // genes may be mid-load; re-stamped below
                _identityKeyMemo = BodyKey(target);
                if (!TryRestoreBodyMemory(target, _identityKeyMemo))
                {
                    _persona = BuildPersona(); // it in FinalizeIdentity (LLM thread) so
                                               // the main thread never blocks on HTTP
                    // The saved genes may land a few seconds after the body binds
                    // (deferred character load) — try again once they settle.
                    try
                    {
                        Plugin.RunOnMainThreadLater(6f, () =>
                        {
                            try { if (IsAlive(target)) _boundBodyFp = BodyFullFp(target); } catch (Exception) { }
                            try { LateMemoryRecheck(target, _identityKeyMemo); } catch (Exception) { }
                        });
                    }
                    catch (Exception) { }
                }
            }
            // WAKE CONTEXT (no moving!): a body that woke up inside/next to a bed gets
            // told so; otherwise the boot line describes the actual surroundings. The
            // mod used to TELEPORT fresh spawns beside a bed — the body then sat stuck
            // inside the bed's colliders (and the sleeper grabbed it). Possess where
            // they are; the words are the fix, not the position.
            try { WakeContextIfAny(target); } catch (Exception e) { try { Logger.LogWarning("wake context: " + e.Message); } catch (Exception) { } }
            RegisterAgentBody(_currentKoboldId, _npcName); // siblings can see/hear this agent
            _yawDeg = target.transform.eulerAngles.y; // start from current facing
            _controller = target.GetComponent<KoboldCharacterController>();
            _descriptor = target.GetComponent<CharacterDescriptor>();
            _grabber = target.GetComponentInChildren<Grabber>(true);
            _charAnimator = target.GetComponentInChildren<CharacterControllerAnimator>(true);
            // Kill any in-flight rotation coroutines started before we took the body — they
            // write rigidbody.rotation outside our control and fight with the steering.
            if (_charAnimator != null)
            {
                try { _charAnimator.StopAllCoroutines(); } catch (Exception) { }
            }

            // Deterministic ownership: transfer to us *and* request (covers both
            // Takeover and Request Photon transfer modes). Re-assert until IsMine.
            _photonView = target.GetComponent<PhotonView>();
            try { if (_photonView != null && !_photonView.IsMine) _photonView.TransferOwnership(PhotonNetwork.LocalPlayer); } catch (Exception) { }
            try { if (_photonView != null && !_photonView.IsMine) _photonView.RequestOwnership(); } catch (Exception) { }

            if (_descriptor != null) _descriptor.SetPlayerControlled(CharacterDescriptor.ControlType.NetworkedPlayer);
            // Remove the built-in AI entirely — disabling leaves them free to re-activate
            // (station exit, ragdoll recover, scene refresh) and fight our input stream.
            var ai = target.GetComponentInChildren<KoboldAIPossession>(true);
            if (ai != null) { try { UnityEngine.Object.Destroy(ai); } catch (Exception) { } }
            var seeker = target.GetComponentInChildren<KoboldSeeker>(true);
            if (seeker != null) { try { UnityEngine.Object.Destroy(seeker); } catch (Exception) { } }
            // Stop the game's LookAtHandler from driving the head/body — we manage it,
            // otherwise it fights our rigidbody steering and produces the sideways drift.
            if (_charAnimator != null) { try { _charAnimator.SetLookEnabled(false); } catch (Exception) { } }
            _navTarget = null; _navTargetName = null; _path = null; _pathIdx = 0; _pathGoalSet = false; _stimSource = null;
            _horny = _cfgHornyBaseline != null ? _cfgHornyBaseline.Value : 0.08f;
            _hornyPrevStim = target != null && target.stimulationMax > 0f
                ? Mathf.Clamp01(target.stimulation / target.stimulationMax) : 0f;
            _hornySaturatedFor = 0f;
            ResetCarrySense();
            ResetHoldSense();

            // Reagent awareness: listen to the belly container for drink/spray/metabolize events.
            _bellySnapshotPending = true; // first OnChange after possessing = baseline, not a gift
            SubscribeBelly(target);
            // Penetration awareness: subscribe each penetrable on the body so we know
            // when something penetrates and how deep; and each penetrator so we know
            // when the kobold's own dick is inside something.
            SyncPenetrationSubscriptions(target);

            // Resolve the head bone for the eye/camera.
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    if (_kobold != target || !IsAlive(target)) return; // body changed meanwhile
                    var desc = _descriptor;
                    var anim = desc != null && IsAlive(desc) ? desc.GetDisplayAnimator() : null;
                    _head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : target.transform;
                    SetupCamera();
                    Logger.LogInfo($"KKLLMNPC: {(reuseIdentity ? "re-" : "")}possessed '{target.name}'");
                }
                catch (Exception e) { Logger.LogError("BindBody head/cam: " + e); }
            });
        }

        // Persistent identity key for a body. The old key was JUST the serialized
        // object name — every spawned body of one species is named "Species(Clone)",
        // so every kobold of a species shared ONE memory file: a hatched child
        // claimed a stranger's stale record, two agents overwrote each other's
        // saves, and "who woke up here before" was a coin flip.
        //
        // The key now blends three stable things:
        //   species — the prefab name (still survives the world save, and changes
        //             when a body is a different species);
        //   identity genes — the gene bytes that NEVER mutate on a body (hue,
        //             saturation, brightness, clothing hue, species index): sizes
        //             and groins grow through play/absorption, so they must NOT be
        //             part of a body's identity, or every grown kobold would wake
        //             up a stranger;
        //   photon view id — distinguishes the seeded-identical twins a mod/dispenser
        //             can spawn (same name → same seeded randomize), and the game's
        //             world save restores view ids verbatim (SaveManager), so a
        //             save-loaded body keeps its key across sessions.
        // All call sites (swallow, rebirth, absorb) key records through this.
        internal static string BodyKey(Kobold k)
        {
            try
            {
                string species = CleanName(k.name);
                if (string.IsNullOrEmpty(species)) species = "Kobold";
                string idPart = "unknown";
                int viewId = 0;
                try { viewId = k.photonView != null ? k.photonView.ViewID : 0; } catch (Exception) { }
                // Immutable identity bytes: hue/sat/bright/clothingHue/species.
                byte hue = 0, sat = 128, bright = 128, cloth = 0;
                try
                {
                    var g = k.GetGenes();
                    if (g != null) { hue = g.hue; sat = g.saturation; bright = g.brightness; cloth = g.clothingHue; }
                }
                catch (Exception) { }
                idPart = Hash16(hue, sat, bright, cloth) + "-" + viewId.ToString(CultureInfo.InvariantCulture);
                return TextUtil.AsciiSafe(species + "-" + idPart);
            }
            catch (Exception)
            {
                try { return TextUtil.AsciiSafe(CleanName(k.name)); } catch (Exception) { return null; }
            }
        }

        // 64-bit FNV-1a over the identity bytes (two lanes, different seeds) → hex.
        private static string Hash16(byte a, byte b, byte c, byte d)
        {
            ulong h1 = 14695981039346656037UL, h2 = 1099511628211UL;
            byte[] vals = { a, b, c, d };
            foreach (byte v in vals)
            {
                h1 ^= v; h1 *= 1099511628211UL;
                h2 ^= (byte)(v + 0x5b); h2 *= 1469598103934665603UL;
            }
            return h1.ToString("x8", CultureInfo.InvariantCulture) + h2.ToString("x8", CultureInfo.InvariantCulture);
        }

        // The body's FULL gene fingerprint — the volatile whole (sizes, groin, grab
        // slots). Stored INSIDE each record at save time so a key collision between
        // two bodies can still be told apart at restore (the record refuses a body
        // whose current genes don't match what was saved under it).
        internal static string BodyFullFp(Kobold k)
        {
            try { return NPCInstance.GenesFingerprint(k.GetGenes()); } catch (Exception) { return null; }
        }

        // Body identity captured at bind: the full gene fingerprint + species, for
        // record stamping/validation.
        private string _boundBodyFp;
        // The identity key this bind used (the late re-check compares against it —
        // when the deferred character load settles the saved genes, the key moves).
        private string _identityKeyMemo;

        // Snapshot the current body's life to disk (teardown path: sold, lost,
        // world change). Facts/goal/persona, keyed by the body's identity key and
        // stamped with its gene fingerprint (restore validates the stamp).
        private void SaveBodyMemory()
        {
            try
            {
                if (_kobold == null) return;
                string bodyKey = BodyKey(_kobold);
                if (string.IsNullOrEmpty(bodyKey)) return;
                string goal; lock (_goalLock) { goal = _goal; }
                // Save even with zero facts if a persona exists — a returned body at
                // least wakes up as the same person, not a renamed stranger.
                var facts = new List<string>();
                lock (_facts) foreach (var f in _facts) facts.Add(f.Text);
                string fp = _boundBodyFp;
                if (string.IsNullOrEmpty(fp)) fp = BodyFullFp(_kobold);
                MemoryStore.Save(bodyKey, _npcName, _persona, facts, goal, _pronounsOverride, SkillsSnapshotLines(),
                    bodyFp: fp, species: BodySpecies(_kobold));
                try { Logger.LogInfo("KKLLMNPC: saved '" + _npcName + "' — " + facts.Count + " facts keyed to body '" + bodyKey + "'"); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // The body's species/prefab label ("Kobold", "Maomi") for record stamping.
        private static string BodySpecies(Kobold k)
        {
            try { string s = CleanName(k.name); return string.IsNullOrEmpty(s) ? "kobold" : s; }
            catch (Exception) { return "kobold"; }
        }

        // Fresh-bind restore: load the body's saved life. False → no record, wake fresh.
        // The record's gene-fingerprint stamp must match the body being bound — two
        // bodies that serialize to the same key (seeded-identical spawns) may still
        // be told apart by their full genes at restore time.
        private bool TryRestoreBodyMemory(Kobold target, string bodyKey)
        {
            try
            {
                var rec = MemoryStore.Load(bodyKey);
                if (rec == null) return false;
                // Stranger-proofing: the record knows ITS body's full gene fingerprint.
                // A key collision (genetically identical spawn claiming an old body's
                // identity) fails here and wakes fresh instead of resurrecting someone
                // else. Grown bodies match their own last save (the game persists the
                // grown genes too).
                string myFp = BodyFullFp(target);
                string mySpecies = BodySpecies(target);
                if (!string.IsNullOrEmpty(rec.BodyFp) && !string.IsNullOrEmpty(myFp)
                    && !string.Equals(rec.BodyFp, myFp, StringComparison.Ordinal))
                {
                    try { Logger.LogWarning("KKLLMNPC: body-key restore refused — record under '" + bodyKey + "' was saved for different genes (a different body); waking fresh"); } catch (Exception) { }
                    return false;
                }
                if (!string.IsNullOrEmpty(rec.Species) && !string.Equals(rec.Species, mySpecies, StringComparison.Ordinal))
                {
                    try { Logger.LogWarning("KKLLMNPC: body-key restore refused — record under '" + bodyKey + "' belongs to species '" + rec.Species + "'"); } catch (Exception) { }
                    return false;
                }
                // The recorded name IS this body's own past name — the flat names
                // file never releases reservations, so IsTaken would permanently
                // block every restore. Reclaim it instead (MarkTaken is the
                // NameRegistry comment's documented "re-claiming our own" path).
                // A genuinely stolen name (a LIVE different body wearing it) still
                // refuses below. Seed facts don't count as a lived life.
                if (HasRealFacts() || _tick > 3) return false; // already live as someone else
                int liveBody = BodyIdForAgentName(rec.Name);
                if (liveBody >= 0 && liveBody != _currentKoboldId) return false; // another agent owns that name right now
                DropSeedFacts(); // the saved life replaces the boot seeds
                _npcName = rec.Name;
                NameRegistry.MarkTaken(_npcName);
                _persona = rec.Persona;
                _pronounsOverride = string.IsNullOrEmpty(rec.Pronouns) ? null : rec.Pronouns;
                lock (_facts)
                {
                    foreach (var t in rec.Facts) _facts.Add(new FactRec { Text = t, Tick = _tick });
                    while (_facts.Count > FactCap) _facts.RemoveAt(0);
                }
                if (!string.IsNullOrEmpty(rec.Goal)) lock (_goalLock) { _goal = rec.Goal; _goalTick = _tick; }
                SkillsLoadFrom(rec.Skills); // learned procedures come back with the body
                _identityFinalized = true; // name already chosen — skip the LLM re-pick
                // Identity bot may be needed again for chat attribution.
                RunOnMainThreadAsync(() => { try { EnsureIdentityBot(); } catch (Exception) { } });
                try { Logger.LogInfo("KKLLMNPC: '" + _npcName + "' woke up here before — restored " + rec.Facts.Count + " facts from a previous life of this body"); } catch (Exception) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        // Late re-check (main thread, ~6s after bind): a save-loaded body binds
        // BEFORE the game applied its saved genes (deferred character load) — the
        // identity key we tried at bind time was computed from seed defaults. Once
        // the real genes settle the key moves; if we woke as a blank slate, try the
        // restore again under the real key.
        private void LateMemoryRecheck(Kobold target, string triedKey)
        {
            try
            {
                if (target == null || !IsAlive(target) || _kobold != target) return;
                string keyNow = BodyKey(target);
                if (string.IsNullOrEmpty(keyNow) || string.Equals(keyNow, triedKey, StringComparison.Ordinal))
                {
                    _identityKeyMemo = keyNow; // settled — keep it as the stamp key
                    return; // unchanged: the first attempt already covered it
                }
                if (HasRealFacts()) { _identityKeyMemo = keyNow; return; } // someone's life is live already
                try { if (_identityFinalized) return; } catch (Exception) { }
                _identityKeyMemo = keyNow;
                Logger.LogInfo("KKLLMNPC: body identity settled late ('" + triedKey + "' -> '" + keyNow + "') — rechecking saved memory");
                if (!TryRestoreBodyMemory(target, keyNow))
                    _persona = BuildPersona(); // still no record — don't leave the body blank
            }
            catch (Exception e) { try { Logger.LogWarning("late memory recheck: " + e.Message); } catch (Exception) { } }
        }

        // Detect (never move) the wake context: when a body's spawn point lies right
        // at a bed, the boot says "woke up in the bed 'Name'"; otherwise the boot's
        // context line describes the surroundings (nearest named places + area prose).
        // Field feeds the console boot line only.
        private string _wokeInBedName;

        private void WakeContextIfAny(Kobold target)
        {
            if (target == null) return;
            try
            {
                foreach (var pl in BuildKeyPlaceList())
                {
                    if (pl.Kind != "bed" || !IsAlive(pl.Tf)) continue;
                    if ((pl.Pos - target.transform.position).sqrMagnitude <= 2.5f * 2.5f)
                    {
                        _wokeInBedName = pl.Name;
                        Logger.LogInfo("KKLLMNPC: wake context — body spawned at the bed '" + pl.Name + "' (no move)");
                        return;
                    }
                }
                Logger.LogInfo("KKLLMNPC: wake context — spawned away from any bed; the boot line describes the surroundings");
            }
            catch (Exception e)
            {
                try { Logger.LogWarning("wake context: " + e.Message); } catch (Exception) { }
            }
        }

        // The brain-swap machine swapped our NPC with whoever was on the other pod.
        // The Kobold objects don't move — the machine hands control of the other
        // body to whoever its flags say. Rebinding keeps our NPCInstance pointed at
        // the body it now inhabits, with name + persona intact.
        private void OnBodySwap(Kobold a, Kobold b)
        {
            Kobold next = null;
            if (a == _kobold) next = b;
            else if (b == _kobold) next = a;
            if (next == null || _kobold == null) return;
            string prevName = PickName(_kobold);
            string newName = PickName(next);
            Logger.LogInfo("KKLLMNPC: '" + (_npcName ?? "?") + "' body-swapping " + prevName + " -> " + newName);
            // Record swap for BodyControl perception (ring buffer + identity flag).
            try { RecordSwap(prevName, newName); } catch (Exception) { }
            RunOnMainThread(() =>
            {
                try { RebindAfterSwap(next); }
                catch (Exception e) { Logger.LogError("body swap rebind: " + e); }
                return true;
            });
        }

        private void RebindAfterSwap(Kobold next)
        {
            if (next == null || !IsAlive(next) || _kobold == next) return;
            int oldId = _currentKoboldId;
            int id = next.GetInstanceID();
            // Release the swapped-out body (it's the other party's now) and claim
            // the incoming one, unless another instance took it first.
            lock (LLMNPCPlugin.ClaimedKobolds)
            {
                if (oldId >= 0) LLMNPCPlugin.ClaimedKobolds.Remove(oldId);
                if (LLMNPCPlugin.ClaimedKobolds.Contains(id)) { LLMNPCPlugin.ClaimedKobolds.Add(oldId); return; }
                LLMNPCPlugin.ClaimedKobolds.Add(id);
            }
            // BindBody re-subscribes belly + penetration listeners on the new body
            // (both helpers unsubscribe the old ones first).
            BindBody(next, true);
            UnregisterAgentBody(oldId);
            RegisterAgentBody(_currentKoboldId, _npcName); // same mind, new body — keep the sibling registry current
            _persona = BuildPersona(); // physical gender/species changed — rebuild from the new body
        }

        // Follow body-swaps: the machine's AssignKobolds ends by raising the static
        // bodySwapped(a, b) event with the two pod occupants. One static handler per
        // instance — subscribe when we take a body, drop it on teardown.
        private bool _bodySwapHooked;

        private void TryHookBodySwap()
        {
            if (_bodySwapHooked) return;
            try { BrainSwapperMachine.bodySwapped += OnBodySwap; _bodySwapHooked = true; }
            catch (Exception e) { Logger.LogWarning("hook bodySwap: " + e.Message); }
        }

        private void TryUnhookBodySwap()
        {
            if (!_bodySwapHooked) return;
            try { BrainSwapperMachine.bodySwapped -= OnBodySwap; }
            catch (Exception e) { Logger.LogDebug($"TryUnhookBodySwap error: {e.Message}"); }
            _bodySwapHooked = false;
        }

        // ------------------------------------------------------------------
        // reagent awareness
        // ------------------------------------------------------------------
        // How much egg reagent the kobold is carrying (needs a laying station).
        // Game rule: OvipositionSpot.CanUse = belly egg volume > 5 AND energy > 1.
        private float GetEggVolume(Kobold k)
        {
            try
            {
                if (_eggReagent == null && !Database<ScriptableReagent>.TryGetAsset("Egg", out _eggReagent)) return 0f;
                if (_eggReagent == null) return 0f;
                var c = k.bellyContainer;
                return c != null ? c.GetVolumeOf(_eggReagent) : 0f;
            }
            catch (Exception) { return 0f; }
        }

        private bool IsReadyToLayEgg(Kobold k)
        {
            try { return GetEggVolume(k) > 5f && k.GetEnergy() > 1f; } catch (Exception) { return false; }
        }

        // The eater's current energy — the second half of the nest rule. Exposed for
        // the lay gates so a refusal names the REAL blocker: the game refuses nests
        // on egg ≤5ml OR energy ≤1, and a "belly not full" note while carrying a
        // poured egg reads like a bug (the exact mistake a playtest eater made — it
        // went to a play station to "make eggs").
        private float LayEnergy(Kobold k)
        {
            try { return k.GetEnergy(); } catch (Exception) { return 0f; }
        }

        // bellyContainer.OnChange fires on every fluid event (drink/spray/flood/metabolism).
        // The FIRST call after possession is just a snapshot of existing contents — treated
        // as baseline, never reported as a 'drank X' event.
        private void SubscribeBelly(Kobold target)
        {
            UnsubscribeBelly();
            try
            {
                var c = target.bellyContainer;
                if (c != null)
                {
                    c.OnChange += OnBellyReagentsChanged;
                    _bellySubscribed = c;
                    Logger.LogInfo("KKLLMNPC: listening to belly container on '" + target.name + "'.");
                }
            }
            catch (Exception e) { Logger.LogWarning("subscribe belly: " + e.Message); }
        }

        private void UnsubscribeBelly()
        {
            try { if (_bellySubscribed != null) { _bellySubscribed.OnChange -= OnBellyReagentsChanged; _bellySubscribed = null; } }
            catch (Exception) { }
        }

        // Fired by the game when the kobold's belly container changes. We translate
        // the inject type + reagent mix into a natural-language event the LLM sees:
        //   drank/metabolized/sprayed/flooded/vacuumed "Water" (5ml) ...
        // Translate belly OnChange into natural language and queue for next perception.
        // Dedupes identical mixes so unchanged contents don't re-report.
        private void OnBellyReagentsChanged(ReagentContents contents, GenericReagentContainer.InjectType injectType)
        {
            try
            {
                string what = DescribeContents(contents);
                if (string.IsNullOrEmpty(what)) return;

                // The very first OnChange after possession just reports whatever the
                // belly already contained (game seeds initial contents) — don't phrase
                // that as "drank"/"filled up", or the NPC thinks it was just fed.
                if (_bellySnapshotPending)
                {
                    _bellySnapshotPending = false;
                    _lastBellySummary = what;
                    Logger.LogInfo("reagent baseline (existing belly): " + what);
                    return;
                }

                string how;
                switch (injectType)
                {
                    case GenericReagentContainer.InjectType.Metabolize: how = "metabolized"; break;
                    case GenericReagentContainer.InjectType.Spray: how = "sprayed"; break;
                    case GenericReagentContainer.InjectType.Flood: how = "filled up with"; break;
                    case GenericReagentContainer.InjectType.Vacuum: how = "pumped out"; break;
                    default: how = "injected"; break; // Inject = consume
                }
                string ev = how + " " + what;

                // Dedupe: if nothing materially changed, don't re-report the same mix.
                if (what == _lastBellySummary) return;
                _lastBellySummary = what;

                lock (_reagentEvents)
                {
                    _reagentEvents.Enqueue(ev);
                    while (_reagentEvents.Count > 6) _reagentEvents.Dequeue();
                }
                RememberFact(how + " " + what.Split(' ')[0]); // remember *what*, not the volume
                if (!_cfgDisableReagentMessages.Value)
                {
                    Logger.LogInfo("reagent: " + ev);
                }
            }
            catch (Exception e) { Logger.LogWarning("reagent event: " + e.Message); }
        }

        // Enumerate a ReagentContents ("Water x5ml" or "Water+Cum x3ml") — falls back
        // to "something" if the database lookup misses.
        private string DescribeContents(ReagentContents contents)
        {
            try
            {
                if (contents == null || contents.Count == 0) return null;
                var parts = new List<string>();
                foreach (Reagent r in contents)
                {
                    string name = "fluid#" + r.id;
                    ScriptableReagent sr;
                    if (Database<ScriptableReagent>.TryGetAsset((short)r.id, out sr) && sr != null)
                    {
                        // Localization assemblies aren't referenced — fall back to the
                        // asset's Unity object name (that's what modders use in-game).
                        try { if (!string.IsNullOrEmpty(sr.name)) name = sr.name; } catch (Exception) { }
                    }
                    parts.Add(name + " " + F(r.volume) + "ml");
                }
                return string.Join(", ", parts.ToArray());
            }
            catch (Exception e) { Logger.LogDebug($"DescribeContents: {e.Message}"); return null; }
        }

        // Drain queued reagent + world events as a list of strings, clearing them.
        // World events (CarrySense: picked up/carried/thrown/sell-machine danger)
        // ride the same [event] channel the console and legacy perception drain.
        private List<object> DrainReagentEvents()
        {
            lock (_reagentEvents)
            {
                var outp = new List<object>();
                while (_reagentEvents.Count > 0) outp.Add(_reagentEvents.Dequeue());
                var world = DrainWorldEvents();
                if (world != null) outp.AddRange(world);
                return outp;
            }
        }

        // True when the possessed kobold is inside an animation station (bed, mount, etc.).
        // The game tracks this on CharacterControllerAnimator; used to tell the model
        // it's stuck and to gate movement (you can't walk out of a station).
        private bool IsInAnimationStation()
        {
            try { return _charAnimator != null && _charAnimator.IsAnimating(); }
            catch (Exception) { return false; }
        }

        // Release all hooks on the current body: camera, belly reagent subscriptions,
        // penetrable/penetrator listeners, ownership claim — restore native AI control.
        private void TeardownBody()
        {
            if (_cam != null) { try { Destroy(_cam.gameObject); } catch (Exception) { } _cam = null; }
            if (_rt != null) { try { _rt.Release(); Destroy(_rt); } catch (Exception) { } _rt = null; }
            if (_camR != null) { try { Destroy(_camR.gameObject); } catch (Exception) { } _camR = null; }
            if (_rtR != null) { try { _rtR.Release(); Destroy(_rtR); } catch (Exception) { } _rtR = null; }
            if (_texPoolL != null) { try { Destroy(_texPoolL); } catch (Exception) { } _texPoolL = null; }
            if (_texPoolR != null) { try { Destroy(_texPoolR); } catch (Exception) { } _texPoolR = null; }
            if (_texPoolStereo != null) { try { Destroy(_texPoolStereo); } catch (Exception) { } _texPoolStereo = null; }
            UnsubscribeBelly();
            UnsubscribePenetrables();
            TryUnhookBodySwap();
            // The body is leaving us (sold/despawned/world change) — persist the
            // life it carried so the same body can wake up remembering later.
            SaveBodyMemory();
            if (_charAnimator != null) { try { _charAnimator.SetLookEnabled(true); } catch (Exception) { } }
            if (_descriptor != null)
            {
                try { _descriptor.SetPlayerControlled(CharacterDescriptor.ControlType.AIPlayer); } catch (Exception) { }
            }
            _kobold = null; _controller = null; _descriptor = null; _grabber = null;
            _charAnimator = null; _head = null; _photonView = null;
            _navTarget = null; _navTargetName = null; _path = null; _pathIdx = 0; _pathGoalSet = false; _stimSource = null;
            if (_currentKoboldId >= 0) { lock (LLMNPCPlugin.ClaimedKobolds) { LLMNPCPlugin.ClaimedKobolds.Remove(_currentKoboldId); } UnregisterAgentBody(_currentKoboldId); _currentKoboldId = -1; }
            StopMove();
        }

        // First-person eye camera: sits slightly in FRONT of the head to avoid face
        // clipping, disabled during gameplay, renders on demand. Near clip + forward offset
        // are config-tuned per kobold body model (some snouts are longer).
        private void SetupCamera()
        {
            // Rebind (bodyswap) reuses this path — free any camera still attached to
            // the previous body's head before wiring up the new one.
            if (_cam != null) { try { Destroy(_cam.gameObject); } catch (Exception) { } _cam = null; }
            if (_rt != null) { try { _rt.Release(); Destroy(_rt); } catch (Exception) { } _rt = null; }
            if (_camR != null) { try { Destroy(_camR.gameObject); } catch (Exception) { } _camR = null; }
            if (_rtR != null) { try { _rtR.Release(); Destroy(_rtR); } catch (Exception) { } _rtR = null; }
            if (_head == null) return;
            float halfIPD = (_cfgStereo != null && _cfgStereo.Value) ? _cfgStereoIPD.Value * 0.5f : 0f;
            float camFwd = _cfgCamForward != null ? _cfgCamForward.Value : 0.22f;
            float nearClip = _cfgCamNearClip != null ? _cfgCamNearClip.Value : 0.10f;
            float farClip = _cfgCamFarClip != null ? _cfgCamFarClip.Value : 80f;
            int imgSize = _cfgImageSize != null ? _cfgImageSize.Value : 192;

            // Left eye camera (center when stereo is off).
            var go = new GameObject("LLMNPC_Cam_L");
            go.transform.SetParent(_head, false);
            go.transform.localPosition = new Vector3(-halfIPD, 0.06f, camFwd);
            go.transform.localRotation = Quaternion.identity;
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = 90f;
            _cam.nearClipPlane = nearClip;
            _cam.farClipPlane = farClip;
            _cam.enabled = false;
            if (RtCreated(_rt)) { try { _rt.Release(); Destroy(_rt); } catch (Exception) { } }
            _rt = new RenderTexture(imgSize, imgSize, 16, RenderTextureFormat.ARGB32);
            _rt.Create();
            _cam.targetTexture = _rt;

            // Right eye camera (only when stereo is enabled).
            if (halfIPD > 0f)
            {
                var goR = new GameObject("LLMNPC_Cam_R");
                goR.transform.SetParent(_head, false);
                goR.transform.localPosition = new Vector3(halfIPD, 0.06f, camFwd);
                goR.transform.localRotation = Quaternion.identity;
                _camR = goR.AddComponent<Camera>();
                _camR.fieldOfView = 90f;
                _camR.nearClipPlane = nearClip;
                _camR.farClipPlane = farClip;
                _camR.enabled = false;
                if (RtCreated(_rtR)) { try { _rtR.Release(); Destroy(_rtR); } catch (Exception) { } }
                _rtR = new RenderTexture(imgSize, imgSize, 16, RenderTextureFormat.ARGB32);
                _rtR.Create();
                _camR.targetTexture = _rtR;
            }
            else
            {
                if (_camR != null) { try { Destroy(_camR.gameObject); } catch (Exception) { } _camR = null; }
                if (_rtR != null) { try { _rtR.Release(); Destroy(_rtR); } catch (Exception) { } _rtR = null; }
            }
        }

        // ------------------------------------------------------------------
        // body awareness: what equipment does the possessed kobold have
        // ------------------------------------------------------------------
        private object DescribeEquipment()
        {
            try
            {
                var g = _kobold.GetGenes();
                bool penis = g.dickSize > 0.01f || (_kobold.activeDicks != null && _kobold.activeDicks.Count > 0);
                int penetrables = 0;
                try { penetrables = _kobold.penetratables != null ? _kobold.penetratables.Count : 0; } catch (Exception) { }
                string hole;
                if (penetrables >= 2) hole = "both";
                else if (penetrables == 1) hole = "one";
                else hole = "none";

                var parts = new List<string>();
                if (penis) parts.Add("YOUR OWN penis"); // gained or bred — it is never someone else's
                if (penetrables > 0) parts.Add(hole + " penetrable" + (penetrables > 1 ? "s" : "") + " (yours: vagina/an ass/tits — feel names them)");
                if (g.breastSize > 0.15f) parts.Add("breasts (yours)");
                if (parts.Count == 0) parts.Add("none");
                return new { with = string.Join("+", parts.ToArray()), penis, penetrables };
            }
            catch (Exception) { return new { with = "unknown", penis = false, penetrables = 0 }; }
        }

        // Physical gender inferred from the body's genes (the game defines sex by
        // equipment): a penis marks male, penetrables mark female, both/none = nonbinary.
        private string InferGender()
        {
            bool penis = false, female = false;
            try
            {
                penis = _kobold.activeDicks != null && _kobold.activeDicks.Count >= 1;
                female = _kobold.penetratables != null && _kobold.penetratables.Count >= 3;
            }
            catch (Exception) { }
            if (penis && !female) return "male";
            if (female && !penis) return "female";
            return "nonbinary";
        }

        private string InferPronouns()
        {
            // The NPC's own declaration wins (set_pronouns) — the body's anatomy is a
            // default, not an order ("let them change their pronouns").
            if (!string.IsNullOrEmpty(_pronounsOverride)) return _pronounsOverride;
            string g = InferGender();
            if (g == "male") return "he/him";
            if (g == "female") return "she/her";
            return "they/them";
        }

        // The species-form of the body model, hinted by its name. Fallback "kobold".
        private static string SpeciesForm(string name)
        {
            string n = name == null ? "" : name.ToLowerInvariant();
            if (n.Length == 0) return "kobold";
            if (n.Contains("drake") || n.Contains("spinel") || n.Contains("dragon") || n.Contains("sky") || n.Contains("wing") || n.Contains("wyrm") || n.Contains("wyvern") || n.Contains("draconic")) return "dragon";
            if (n.Contains("fennec") || n.Contains("fox") || n.Contains("kitsune") || n.Contains("expe") || n.Contains("expie") || n.Contains("vul")) return "fox";
            if (n.Contains("dog") || n.Contains("loona") || n.Contains("canine") || n.Contains("wolf") || n.Contains("husky") || n.Contains("pup") || n.Contains("doberman") || n.Contains("bark")) return "dog";
            if (n.Contains("snake") || n.Contains("serpent") || n.Contains("viper")) return "snake";
            if (n.Contains("lizar") || n.Contains("ander") || n.Contains("saur") || n.Contains("scal") || n.Contains("rept") || n.Contains("argonian") || n.Contains("gater") || n.Contains("claw") || n.Contains("croc")) return "lizard";
            if (n.Contains("deer") || n.Contains("stag") || n.Contains("buck")) return "deer";
            if (n.Contains("bunny") || n.Contains("rabbit") || n.Contains("hare")) return "rabbit";
            if (n.Contains("utaur") || n.Contains("bull") || n.Contains("ox")) return "bull";
            if (n.Contains("horse") || n.Contains("equine") || n.Contains("stallion") || n.Contains("mare")) return "horse";
            if (n.Contains("cat") || n.Contains("kitten") || n.Contains("pussy") || n.Contains("tigre") || n.Contains("tiger") || n.Contains("yorha") || n.Contains("feline") || n.Contains("purr")) return "cat";
            if (n.Contains("bear") || n.Contains("urs")) return "bear";
            if (n.Contains("rat") || n.Contains("rodent") || n.Contains("vermin")) return "rat";
            if (n.Contains("mouse") || n.Contains("mice")) return "mouse";
            if (n.Contains("bat") || n.Contains("vampire")) return "bat";
            if (n.Contains("possum")) return "possum";
            if (n.Contains("shark") || n.Contains("fish") || n.Contains("fin")) return "shark";
            if (n.Contains("bird") || n.Contains("avian") || n.Contains("wing")) return "bird";
            if (n.Contains("pig") || n.Contains("swine") || n.Contains("hog")) return "pig";
            if (n.Contains("cow") || n.Contains("cattle") || n.Contains("moo") || n.Contains("bovine")) return "cow";
            if (n.Contains("sheep") || n.Contains("torial") || n.Contains("lamb") || n.Contains("ram")) return "sheep";
            if (n.Contains("goat") || n.Contains("capra")) return "goat";
            if (n.Contains("monkey") || n.Contains("ape") || n.Contains("primate")) return "monkey";
            if (n.Contains("tiger") || n.Contains("lion") || n.Contains("panther")) return "big cat";
            if (n.Contains("otter") || n.Contains("mustelid") || n.Contains("weasel")) return "otter";
            if (n.Contains("seal") || n.Contains("walrus") || n.Contains("pinniped")) return "seal";
            if (n.Contains("hena") || n.Contains("yeen") || n.Contains("hyena")) return "yeen";
            if (n.Contains("kangaroo") || n.Contains("marsupial")) return "kangaroo";
            if (n.Contains("hedgehog") || n.Contains("spiny")) return "hedgehog";
            if (n.Contains("raccoon") || n.Contains("trash panda")) return "raccoon";
            if (n.Contains("chicken") || n.Contains("rooster") || n.Contains("hen")) return "chicken";
            if (n.Contains("duck") || n.Contains("drake") || n.Contains("mallard")) return "duck";
            if (n.Contains("goose") || n.Contains("gander")) return "goose";
            if (n.Contains("frog") || n.Contains("toad")) return "frog";
            if (n.Contains("bee") || n.Contains("wasp") || n.Contains("hornet")) return "insect";
            if (n.Contains("cyborg") || n.Contains("mecha") || n.Contains("robot") || n.Contains("android") || n.Contains("tasque") || n.Contains("proto")) return "robot";
            if (n.Contains("alien") || n.Contains("extraterrestrial") || n.Contains("xeno")) return "alien";
            if (n.Contains("demon") || n.Contains("imp") || n.Contains("hell")) return "demon";
            if (n.Contains("angel") || n.Contains("seraph") || n.Contains("celestial")) return "angel";
            if (n.Contains("vampire") || n.Contains("nosferatu")) return "vampire";
            if (n.Contains("werewolf") || n.Contains("lycanthrope")) return "werewolf";
            if (n.Contains("human") || n.Contains("humanoid") || n.Contains("jenny")) return "human";
            if (n.Contains("absol")) return "absol";
            if (n.Contains("snorlax")) return "snorlax";
            if (n.Contains("zoroark")) return "zoroark";
            if (n.Contains("mewtwo")) return "mewtwo";
            if (n.Contains("renamon")) return "renamon";
            if (n.Contains("scp")) return "scp";
            return "kobold";
        }

        // Two grounded personality traits picked deterministically from the body name,
        // so each named body keeps a distinct, stable personality across sessions.
        private static readonly string[][] _traits = new string[][]
        {
            new string[] { "playful", "mischievous"},
            new string[] { "warm", "cheerful" },
            new string[] { "calm", "thoughtful" },
            new string[] { "prideful", "confident" },
            new string[] { "curious", "energetic" },
            new string[] { "loyal", "gentle" },
            new string[] { "bold", "adventurous" },
            new string[] { "snarky", "quick-witted" },
        };

        private string BuildPersona()
        {
            try
            {
                if (_kobold == null) return null;
                string name = MyName();
                string species = SpeciesForm(name);
                string gender = InferGender();
                string pronouns = InferPronouns();

                // Deterministic trait pick from the body name hash so it's stable.
                int h = 0;
                foreach (char c in name) h = h * 31 + c;
                if (h < 0) h = -h;
                string[] tr = _traits[h % _traits.Length];

                return "You identify as a " + species + " " + gender + " kobold (" + pronouns +
                       "). Personality: " + tr[0] + ", " + tr[1] + ".";
            }
            catch (Exception) { return null; }
        }

        // The penetrator-side mirror: when THIS kobold's dick is inside something.
        private class KKPenetratorListener : PenetrationTech.PenetratorListener
        {
            public NPCInstance owner;
            public PenetrationTech.Penetrator source;
            public Transform lastTarget; // the thing it's inside right now

            private string HoleName(PenetrationTech.Penetrable pen)
            {
                try
                {
                    return pen != null && pen.gameObject != null
                        ? CleanName(pen.gameObject.name) : "somewhere";
                }
                catch (Exception) { return "somewhere"; }
            }

            public override void OnPenetrationStart(PenetrationTech.Penetrable penetrable)
            {
                lastTarget = penetrable != null ? penetrable.transform : null;
                owner?.NotifyDickIn(this, HoleName(penetrable), true);
            }
            public override void OnPenetrationEnd(PenetrationTech.Penetrable penetrable)
            {
                lastTarget = null;
                owner?.NotifyDickIn(this, HoleName(penetrable), false);
            }
            public override void NotifyPenetrationUpdate(PenetrationTech.Penetrator self,
                                                         PenetrationTech.Penetrable penetrable,
                                                         float distToHole)
            {
                lastTarget = penetrable != null ? penetrable.transform : lastTarget;
                owner?.NotifyDickTick(this, HoleName(penetrable), distToHole);
            }
        }

        // Both directions: listen to every Penetrable on the body (someone entering us)
        // AND every Penetrator (our dick inside something). Subscriptions are torn down on
        // teardown / body-swap.
        private void SyncPenetrationSubscriptions(Kobold target)
        {
            UnsubscribePenetrables();
            _eggPassing = false;
            _lastEggMidNotice = 0f;
            try
            {
                _pen.Clear();
                foreach (var pen in target.GetComponentsInChildren<PenetrationTech.Penetrable>(true))
                {
                    if (pen == null) continue;
                    pen.penetrationNotify += OnPenetrationTick;
                    _subscribedPenetrables.Add(pen);
                }
                // And attach listeners to any penetrator appendages (dicks) so we can
                // feel penetrating others.
                _dickIn.Clear();
                foreach (var pr in target.GetComponentsInChildren<PenetrationTech.Penetrator>(true))
                {
                    try
                    {
                        if (pr == null) continue;
                        var listener = new KKPenetratorListener { owner = this, source = pr };
                        pr.listeners.Add(listener);
                        _penListeners.Add(listener);
                    }
                    catch (Exception) { }
                }
                Logger.LogInfo("KKLLMNPC: watching " + _subscribedPenetrables.Count + " penetrables + " + _penListeners.Count + " penetrators on '" + target.name + "'.");
            }
            catch (Exception e) { Logger.LogWarning("subscribe penetrables: " + e.Message); }
        }

        private void UnsubscribePenetrables()
        {
            try
            {
                foreach (var p in _subscribedPenetrables) if (p != null) p.penetrationNotify -= OnPenetrationTick;
                _subscribedPenetrables.Clear();
                foreach (var l in _penListeners) if (l != null) { try { if (l.source != null) l.source.listeners.Remove(l); } catch (Exception) { } }
                _penListeners.Clear();
                lock (_pen) _pen.Clear();
                lock (_dickIn) _dickIn.Clear();
                _eggPassing = false;
            }
            catch (Exception) { }
        }

        // ---- oviposition narration state (an egg currently passing through us) ----
        private bool _eggPassing;
        private float _lastEggMidNotice;

        // Egg passage = OVIPosition, narrated for what it is. Eggs are Penetrators
        // (OvipositionSpot pushes the laid egg out through the laying spline at a
        // fixed slow rate; loose egg props can also press against a body). This used
        // to be misread as a partner entering, so the model thought it was having sex
        // with an egg and panicked. Eggs never become sex state here — and their
        // passage is described honestly: slow, self-driven, no thrusting needed.
        private void HandleEggPassing(PenetrationTech.Penetrable pen, bool inside, float now)
        {
            try
            {
                string hole = ClassifyPenetrable(pen) ?? "the laying path";
                if (inside)
                {
                    if (_eggPassing)
                    {
                        if (now - _lastEggMidNotice > 5f)
                        {
                            _lastEggMidNotice = now;
                            PushWorldEvent("[lay] the EGG is still sliding out of your " + hole + " — this is egg-laying (oviposition), NOT sex. It moves slowly and steadily all by itself; nothing is thrusting, and it is not deepening like a partner would. Just let it pass.");
                        }
                        return;
                    }
                    _eggPassing = true;
                    _lastEggMidNotice = now;
                    Logger.LogInfo("egg passing (" + hole + ") — narrated as laying, not sex");
                    PushWorldEvent("[lay] an EGG is pressed into / emerging from your " + hole + " — that is an egg traveling the laying path (oviposition), OR an egg simply pressed against you. It is NOT a partner and NOT sex: it slides slowly on its own; no thrusting, no bracing, no panic. When it clears you completely it becomes a real egg item.");
                    return;
                }
                if (_eggPassing)
                {
                    _eggPassing = false;
                    // The delivery is the WHOLE point of a lay — the playtest showed the
                    // egg left at the nest forever when this line only said "real egg
                    // item". Give the concrete hand-off: grab → cd to a plot → use.
                    PushWorldEvent("[lay] the egg has cleared you completely — laying done, and now DELIVERY is your job: the egg is just an item at your feet. Look at it, get/grab it, cd to a farm plot (bare soil — `cat places` lists plots map-wide with ids), set it down on the dirt while facing it, then `use id:N` (or `plant` — the nearest-soil shortcut). It carries genes — it may well be someone reborn (your PLAYER, if they were swallowed): deliver it promptly; never sell, never eat it, never leave it lying around.");
                    Logger.LogInfo("egg cleared");
                }
            }
            catch (Exception) { }
        }

        // Fires per-frame while a penetrator is inside (and on exit with depth 0).
        private void OnPenetrationTick(PenetrationTech.Penetrable penetrable,
                                       PenetrationTech.Penetrator penetrator,
                                       float worldSpaceDistanceToPenetrator,
                                       PenetrationTech.Penetrable.SetClipDistanceAction clipAction)
        {
            try
            {
                if (penetrator == null) return;
                float now = Time.unscaledTime;

                // Eggs are Penetrators too (the game pushes them out through the laying
                // path, OvipositionSpot — and loose egg props can press against a body).
                // A Seed in the penetrator chain identifies them; they are OVIPosition,
                // NEVER sex: excluded from _pen (so the anchor, rhythm graph, feel and
                // penetration perception stay clean) and narrated as laying instead.
                bool isEgg;
                try { isEgg = penetrator.GetComponentInParent<Seed>(true) != null; }
                catch (Exception) { isEgg = false; }
                if (isEgg)
                {
                    HandleEggPassing(penetrable, worldSpaceDistanceToPenetrator > 0.001f, now);
                    return;
                }

                // A SWALLOWED person is penetrator machinery: their body (dick
                // included) passing down the eater's throat registers as a real
                // penetration — the eater got a [sex] event, a rhythm, and narrated
                // the meal as sex ("let us enjoy this rhythm"). The victim's parts
                // never count as penetration.
                try
                {
                    var srcTf = penetrator.transform;
                    if (srcTf != null)
                    {
                        var ownerKb = srcTf.GetComponentInParent<Kobold>();
                        // DIGESTING VICTIM: their body is penetrating machinery.
                        if (ownerKb != null && SwallowManager.IsDigestingVictim(ownerKb)) return;
                        // SELF-PENETRATION: a body's OWN parts never penetrate it.
                        // The new-dick bug: a gained penis spawns at the crotch and
                        // instantly registered in _pen against the owner's own
                        // penetrables — the agent read it as "someone is inside me"
                        // ("a phantom is fucking me"), begged not to stop.
                        if (ownerKb != null && ownerKb == _kobold) return;
                    }
                }
                catch (Exception) { }

                string whoPen = penetrator.gameObject != null ? CleanName(penetrator.gameObject.name) : "something";

                lock (_pen)
                {
                    PenState st;
                    if (worldSpaceDistanceToPenetrator > 0.001f)
                    {
                        if (!_pen.TryGetValue(penetrator, out st))
                        {
                            st = new PenState { name = whoPen, lastT = now, depth = worldSpaceDistanceToPenetrator, vel = 0f, hole = ClassifyPenetrable(penetrable) };
                            _pen[penetrator] = st;
                            Logger.LogInfo("penetrated by " + whoPen + " (" + st.hole + ")");
                            //EmitAmbient("hah~"); // being entered
                            _stimSource = whoPen; // name the source: partner or machine part
                            _stimSourceT = now;
                            // The model's OWN body event: sex reaches CONSOLE MODE as a
                            // world event now — models used to learn "there's an egg in
                            // me" minutes later and never knew they were being entered.
                            PushWorldEvent("[sex] you are BEING PENETRATED — " + whoPen + " entered your " + st.hole
                                + ". This is real and happening NOW; respond (say), match/ask the rhythm, or use `feel` + `thrust` to take part — you may be locked in an animation meanwhile");
                        }
                        st.src = penetrator.gameObject != null ? penetrator.transform : null;
                        float d = worldSpaceDistanceToPenetrator;
                        float dt = Mathf.Max(0.001f, now - st.lastT);
                        st.vel = Mathf.Lerp(st.vel, (d - st.depth) / dt, 0.2f);
                        st.depth = d;
                        st.lastT = now;
                        // Depth history for the rhythm read (TrimDegenerateRepeat-free):
                        st.History.Add(now);
                        st.History.Add(d);
                        while (st.History.Count > 2 && now - (float)st.History[0] > 5.0) st.History.RemoveRange(0, 2);
                    }
                    else if (_pen.TryGetValue(penetrator, out st))
                    {
                        _pen.Remove(penetrator); // pulled out
                        Logger.LogInfo(whoPen + " pulled out");
                        PushWorldEvent("[sex] " + whoPen + " withdrew — that round is over; your body is yours again (say how it was, if you like)");
                    }
                }
            }
            catch (Exception) { }
        }

        // True when this penetrable target belongs to this body itself.
        private bool InvolvesOwnBody(Transform target)
        {
            try
            {
                if (target == null) return false;
                var kb = target.GetComponentInParent<Kobold>();
                return kb != null && kb == _kobold;
            }
            catch (Exception) { return false; }
        }

        // Remove a self-dick listener entry (and stay silent about it).
        private void PurgeSelfDick(KKPenetratorListener listener)
        {
            try
            {
                lock (_dickIn)
                {
                    if (_dickIn.ContainsKey(listener)) _dickIn.Remove(listener);
                }
            }
            catch (Exception) { }
        }

        // Swallow-belt purge: drop every penetration state owned by / targeting
        // this victim — the eater's _pen entries (their body "penetrating" ours on
        // the way down) and our own _dickIn entries (our dick inside their mouth).
        internal void PurgePenForVictim(Kobold victim)
        {
            if (victim == null) return;
            try
            {
                lock (_pen)
                {
                    List<object> drop = new List<object>();
                    foreach (var kv in _pen)
                    {
                        var srcTf = kv.Value != null ? kv.Value.src : null;
                        if (srcTf == null) continue;
                        var ownerKb = srcTf.GetComponentInParent<Kobold>();
                        if (ownerKb == victim) drop.Add(kv.Key);
                    }
                    foreach (var k in drop) _pen.Remove(k);
                }
                lock (_dickIn)
                {
                    List<object> drop2 = new List<object>();
                    foreach (var kv in _dickIn)
                    {
                        var tgt = kv.Value != null ? kv.Value.src : null;
                        if (tgt == null) continue;
                        var ownerKb = tgt.GetComponentInParent<Kobold>();
                        if (ownerKb == victim) drop2.Add(kv.Key);
                    }
                    foreach (var k in drop2) _dickIn.Remove(k);
                }
            }
            catch (Exception) { }
        }

        // PenRhythmText reads OUR penetrables' history; this form takes the dict.
        private string RhythmFrom(Dictionary<object, PenState> dict)
        {
            try
            {
                lock (dict)
                {
                    foreach (var kv in dict)
                    {
                        var st = kv.Value;
                        int pairs = st == null || st.History == null ? 0 : st.History.Count / 2;
                        if (pairs < 4) continue;
                        float now = st.History[(pairs - 1) * 2];
                        float windowStart = now - 3f;
                        float min = float.MaxValue, max = float.MinValue;
                        float prevD = 0f, prevT = 0f, lastDelta = 0f, firstT = 0f;
                        bool first = true;
                        int reversals = 0;
                        for (int p = 0; p < pairs; p++)
                        {
                            float t = st.History[p * 2], d = st.History[p * 2 + 1];
                            if (t < windowStart) continue;
                            if (first) { first = false; prevD = d; prevT = t; min = d; max = d; firstT = t; continue; }
                            min = Mathf.Min(min, d); max = Mathf.Max(max, d);
                            float delta = d - prevD;
                            if (lastDelta != 0f && Mathf.Abs(lastDelta) > 0.02f && Mathf.Abs(delta) > 0.02f
                                && Mathf.Sign(delta) != Mathf.Sign(lastDelta))
                                reversals++;
                            if (Mathf.Abs(delta) > 0.01f) lastDelta = delta;
                            prevD = d;
                            prevT = t;
                        }
                        float span = Mathf.Max(0.25f, prevT - firstT);
                        float strokesPerSec = reversals * 0.5f / span;
                        string pace = strokesPerSec < 0.6f ? "slow" : strokesPerSec < 1.8f ? "steady" : "fast";
                        string depthWord = (max - min) < 0.05f
                            ? (max < 0.15f ? "shallow, holding" : "held steady at " + F(max) + "m")
                            : "depth varying " + F(min) + "m to " + F(max) + "m";
                        string trend = st.vel > 0.08f ? "deepening" : st.vel < -0.08f ? "pulling out" : "steady";
                        return st.name + ": " + pace + " (" + strokesPerSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                            + " strokes/s), " + depthWord + ", " + trend;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // The rhythm read in words (things inside ME): strokes/sec (depth reversals
        // over the last 3s), depth range, motion trend — a graph the model can react
        // to instead of guessing. Used by the feeling tool + the console anchor.
        private string PenRhythmText()
        {
            return RhythmFrom(_pen);
        }

        // One anchor line about the live intimacy state (console mode has no
        // perception payload — without it, being entered/filled went unnoticed while
        // it happened). null when nothing is going on / low stimulation.
        private string SexAnchorText()
        {
            try
            {
                // Wording matters: a swallowed guest in the stomach used to be read as
                // penetration because the fallback said "someone is inside you". These
                // lines are hole-sex ONLY and say so; the stomach guest names itself.
                if (_pen.Count > 0)
                {
                    string rhythm = RhythmFrom(_pen);
                    return "being PENETRATED sexually — a dick is inside one of your holes" + (rhythm != null ? ": " + rhythm : "")
                        + " — `feel` for details, thrust to ride the rhythm (a stomach guest is NOT this)";
                }
                if (_dickIn.Count > 0)
                {
                    string rhythm = RhythmFrom(_dickIn);
                    return "you are PENETRATING someone sexually — YOUR dick is inside their hole" + (rhythm != null ? ": " + rhythm : "")
                        + " — thrust to raise the rhythm";
                }
                if (_kobold.stimulation > 0.35f)
                    return "stimulation " + F(_kobold.stimulation) + " — you want play";
            }
            catch (Exception) { }
            return null;
        }

        // Rough hole classification from the penetrable's transform name/location.
        private string ClassifyPenetrable(PenetrationTech.Penetrable pen)
        {
            try
            {
                string n = (pen.gameObject != null ? pen.gameObject.name : "").ToLowerInvariant();
                if (n.Contains("mouth") || n.Contains("face") || n.Contains("head")) return "mouth";
                if (n.Contains("vag") || n.Contains("crotch")) return "vagina";
                if (n.Contains("anus") || n.Contains("ass") || n.Contains("butt") || n.Contains("rear")) return "ass";
                if (n.Contains("breast") || n.Contains("chest")) return "chest";
                return n.Length > 0 ? n : "hole";
            }
            catch (Exception) { return "hole"; }
        }

        // From a penetrator/penetrable transform, resolve the partner KOBOLD that owns it
        // (walking up the hierarchy), and return its stable id so the model can both know
        // WHO it's with and target them via `id=`. Returns -1 when no kobold owns it.
        private int PartnerId(Transform src)
        {
            try
            {
                if (src == null) return -1;
                var kb = src.GetComponentInParent<Kobold>();
                if (kb == null) return -1;
                return TargetIdFor(kb.transform.root, kb.name);
            }
            catch (Exception) { return -1; }
        }

        private string PenetrationInfo()
        {
            lock (_pen)
            {
                if (_pen.Count == 0) return null;
                var parts = new List<string>();
                float anyDepth = 0f, anyVel = 0f;
                foreach (var kv in _pen)
                {
                    var st = kv.Value;
                    int pid = PartnerId(st.src);
                    parts.Add(st.name + " " + st.hole + " depth=" + F(st.depth) + "m thrust=" + F(st.vel)
                              + (pid > 0 ? " (with id=" + pid + ")" : " (non-kobold)"));
                    if (st.depth > anyDepth) anyDepth = st.depth;
                    anyVel += st.vel;
                }
                return _pen.Count + " inside (" + string.Join("; ", parts.ToArray()) + ")";
            }
        }

        // True if any penetrator was inside within the last ~3 seconds.
        private bool IsPenetrated()
        {
            lock (_pen)
            {
                if (_pen.Count == 0) return false;
                float now = Time.unscaledTime;
                foreach (var kv in _pen) if (now - kv.Value.lastT < 3f) return true;
                return false;
            }
        }

        // Same for the kobold's own dick being inside something.
        private bool IsDickInside()
        {
            lock (_dickIn)
            {
                if (_dickIn.Count == 0) return false;
                float now = Time.unscaledTime;
                foreach (var kv in _dickIn) if (now - kv.Value.lastT < 3f) return true;
                return false;
            }
        }

        // Everyone the NPC is currently engaged with, from both directions (things
        // inside it AND the things its own dick is inside). Can be more than 2 in a
        // group scene. Each entry: who (partner kobold name or the appendage/hole),
        // id (stable target id if it's a kobold, for `interact id=`), role, and the
        // hole/depth. Returns a list; empty when engaged with nobody.
        private object PartnersList()
        {
            try
            {
                var list = new List<object>();
                float now = Time.unscaledTime;
                var seen = new HashSet<int>();
                lock (_pen)
                {
                    foreach (var kv in _pen)
                    {
                        var st = kv.Value;
                        if (now - st.lastT >= 3f) continue;
                        // Own equipment registering against own holes is a misread of a
                        // gained/bred body — it is never a partner.
                        try
                        {
                            var ownKb = st.src != null ? st.src.GetComponentInParent<Kobold>() : null;
                            if (ownKb != null && _kobold != null && ownKb == _kobold) continue;
                        }
                        catch (Exception) { }
                        int pid = PartnerId(st.src);
                        int key = pid > 0 ? pid : (st.src != null ? st.src.GetInstanceID() : 0);
                        if (!seen.Add(key)) continue;
                        string who = PartnerName(st.src, st.name);
                        list.Add(new { who, id = pid, role = "inside_me", hole = st.hole, depth = F(st.depth) });
                    }
                }
                lock (_dickIn)
                {
                    foreach (var kv in _dickIn)
                    {
                        var st = kv.Value;
                        if (now - st.lastT >= 3f) continue;
                        try
                        {
                            var ownKb = st.src != null ? st.src.GetComponentInParent<Kobold>() : null;
                            if (ownKb != null && _kobold != null && ownKb == _kobold) continue;
                        }
                        catch (Exception) { }
                        int pid = PartnerId(st.src);
                        int key = pid > 0 ? pid : (st.src != null ? st.src.GetInstanceID() : 0);
                        if (!seen.Add(key)) continue;
                        string who = PartnerName(st.src, st.name);
                        list.Add(new { who, id = pid, role = "im_inside", hole = st.hole, depth = F(st.depth) });
                    }
                }
                return list.Count == 0 ? null : (object)list;
            }
            catch (Exception) { return null; }
        }

        // Name a penetration partner: the owning kobold's clean name if it's a kobold,
        // else fall back to the appendage/hole name we already recorded. If the partner
        // is the human player's avatar (a mesh-named body, e.g. "AbsolB"), name them by
        // their chat name so the model doesn't treat the player as a separate kobold.
        private string PartnerName(Transform src, string fallback)
        {
            try
            {
                if (src == null) return fallback;
                var kb = src.GetComponentInParent<Kobold>();
                if (kb == null) return fallback;
                // THE OWN BODY IS NEVER A PARTNER: a freshly GAINED penis spawns at this
                // body's own crotch (and a body-load re-applies it) — without this the
                // partner render read "Kira is inside me" where Kira was ME, and the
                // mind concluded its new dick belonged to someone else. Name the truth.
                if (_kobold != null && kb == _kobold)
                    return "your OWN body (your own equipment — ignore this line, it is not a partner)";
                if (IsPlayerKobold(kb))
                {
                    string chat = PlayerChatName();
                    return !string.IsNullOrEmpty(chat) ? chat + " (the player)" : "the player";
                }
                return CleanName(kb.name);
            }
            catch (Exception) { return fallback; }
        }

        // The transform of whoever is currently penetrating us / we're inside —
        // most recently active one wins. Used to look at your partner during sex.
        private Transform MostRecentPenetrationSource()
        {
            float bestT = -1f; Transform best = null;
            lock (_pen) foreach (var kv in _pen)
            {
                var st = kv.Value;
                if (st.src != null && st.lastT > bestT) { bestT = st.lastT; best = st.src; }
            }
            lock (_dickIn) foreach (var kv in _dickIn)
            {
                var st = kv.Value;
                if (st.src != null && st.lastT > bestT) { bestT = st.lastT; best = st.src; }
            }
            if (best != null && IsAlive(best.gameObject)) return best;
            return null;
        }

        // Who/what is responsible for the pleasure right now, for perception's
        // 'stim_from'. Live partners win; otherwise the station/machine we last
        // used while still (or recently) mounted; nothing otherwise. This exists
        // so the model credits the actual source instead of hallucinating "player".
        private string StimFromText()
        {
            try
            {
                float now = Time.unscaledTime;
                if (IsPenetrated())
                {
                    lock (_pen)
                        foreach (var kv in _pen)
                        {
                            var st = kv.Value;
                            if (now - st.lastT < 3f)
                                return "being_filled_by:" + PartnerName(st.src, st.name);
                        }
                }
                if (IsDickInside())
                {
                    lock (_dickIn)
                        foreach (var kv in _dickIn)
                        {
                            var st = kv.Value;
                            if (now - st.lastT < 3f)
                                return "inside:" + PartnerName(st.src, st.name);
                        }
                }
                bool inStation = IsInAnimationStation();
                if (_stimSource != null && (inStation || now - _stimSourceT < 8f))
                    return "machine:" + _stimSource;
                return null;
            }
            catch (Exception) { return null; }
        }
    }
}
