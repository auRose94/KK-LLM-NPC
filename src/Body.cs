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
        private void NotifyDickIn(KKPenetratorListener listener, string hole, bool entered)
        {
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
            try
            {
                lock (_dickIn)
                {
                    PenState st;
                    if (!_dickIn.TryGetValue(listener, out st))
                    {
                        st = new PenState { name = hole, hole = hole, lastT = Time.unscaledTime };
                        _dickIn[listener] = st;
                    }
                    st.src = listener.lastTarget;
                    float d = Mathf.Max(0f, 0.05f - Mathf.Min(0f, dist)); // distToHole<0 => inside
                    float dt = Mathf.Max(0.001f, Time.unscaledTime - st.lastT);
                    st.vel = Mathf.Lerp(st.vel, (d - st.depth) / dt, 0.2f);
                    st.depth = d;
                    st.lastT = Time.unscaledTime;
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
                int total = 0, ai = 0, claimed = 0, playerControlled = 0;
                Kobold best = null; float bestD = float.MaxValue;
                foreach (var k in SceneCache.Find<Kobold>(2f))
                {
                    if (k == null) continue;
                    total++;
                    int id = k.GetInstanceID();
                    if (LLMNPCPlugin.IsClaimedByAnyLLM(id)) { claimed++; continue; }
                    var desc = k.GetComponent<CharacterDescriptor>();
                    if (desc == null) continue;
                    var ct = desc.GetPlayerControlled();
                    if (ct == CharacterDescriptor.ControlType.AIPlayer) ai++;
                    else playerControlled++;
                    if (ct != CharacterDescriptor.ControlType.AIPlayer) continue;
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
                _npcName = PickName(target);   // base: prefab name — the LLM finalizes
                // A body seen before (same scene save, an earlier session) wakes as
                // the SAME person: load its saved life instead of rebuilding persona
                // and re-asking the LLM for a name.
                if (!TryRestoreBodyMemory(target, BodyKey(target)))
                {
                    _persona = BuildPersona(); // it in FinalizeIdentity (LLM thread) so
                                               // the main thread never blocks on HTTP
                }
            }
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

        // Persistent identity key for a body — the kobold's own serialized object
        // name, which lives in the world save. Two sessions meet the same body here.
        private static string BodyKey(Kobold k) => TextUtil.AsciiSafe(CleanName(k.name));

        // Snapshot the current body's life to disk (teardown path: sold, lost,
        // world change). Facts/goal/persona, keyed by the body.
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
                MemoryStore.Save(bodyKey, _npcName, _persona, facts, goal);
                try { Logger.LogInfo("KKLLMNPC: saved '" + _npcName + "' — " + facts.Count + " facts keyed to body '" + bodyKey + "'"); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // Fresh-bind restore: load the body's saved life. False → no record, wake fresh.
        private bool TryRestoreBodyMemory(Kobold target, string bodyKey)
        {
            try
            {
                var rec = MemoryStore.Load(bodyKey);
                if (rec == null) return false;
                // The recorded name IS this body's own past name — the flat names
                // file never releases reservations, so IsTaken would permanently
                // block every restore. Reclaim it instead (MarkTaken is the
                // NameRegistry comment's documented "re-claiming our own" path).
                // A genuinely stolen name (a LIVE different body wearing it) still
                // refuses below.
                lock (_facts) if (_facts.Count > 0 || _tick > 3) return false; // already live as someone else
                int liveBody = BodyIdForAgentName(rec.Name);
                if (liveBody >= 0 && liveBody != _currentKoboldId) return false; // another agent owns that name right now
                _npcName = rec.Name;
                NameRegistry.MarkTaken(_npcName);
                _persona = rec.Persona;
                lock (_facts)
                {
                    foreach (var t in rec.Facts) _facts.Add(new FactRec { Text = t, Tick = _tick });
                    while (_facts.Count > FactCap) _facts.RemoveAt(0);
                }
                if (!string.IsNullOrEmpty(rec.Goal)) lock (_goalLock) { _goal = rec.Goal; _goalTick = _tick; }
                _identityFinalized = true; // name already chosen — skip the LLM re-pick
                // Identity bot may be needed again for chat attribution.
                RunOnMainThreadAsync(() => { try { EnsureIdentityBot(); } catch (Exception) { } });
                try { Logger.LogInfo("KKLLMNPC: '" + _npcName + "' woke up here before — restored " + rec.Facts.Count + " facts from a previous life of this body"); } catch (Exception) { }
                return true;
            }
            catch (Exception) { return false; }
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
                if (penis) parts.Add("penis");
                if (penetrables > 0) parts.Add(hole + " penetrable" + (penetrables > 1 ? "s" : ""));
                if (g.breastSize > 0.15f) parts.Add("breasts");
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
                        }
                        st.src = penetrator.gameObject != null ? penetrator.transform : null;
                        float d = worldSpaceDistanceToPenetrator;
                        float dt = Mathf.Max(0.001f, now - st.lastT);
                        st.vel = Mathf.Lerp(st.vel, (d - st.depth) / dt, 0.2f);
                        st.depth = d;
                        st.lastT = now;
                    }
                    else if (_pen.TryGetValue(penetrator, out st))
                    {
                        _pen.Remove(penetrator); // pulled out
                        Logger.LogInfo(whoPen + " pulled out");
                    }
                }
            }
            catch (Exception) { }
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
