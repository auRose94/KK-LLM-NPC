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
    public partial class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
    {

        // Penetration awareness: per-penetrator depth over time — the model can tell
        // how deep it is, whether it's thrusting, and roughly which hole.
        private class PenState { public string name; public float depth; public float lastT; public float vel; public string hole; public Transform src; }

        private readonly Dictionary<object, PenState> _pen = new Dictionary<object, PenState>();

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
                        Logger.LogInfo("dick in " + hole);
                        EmitAmbient("haa~"); // their dick slid in
                    }
                    else _dickIn.Remove(listener);
                }
            }
            catch (Exception) { }
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
            catch (Exception) { }
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
                    parts.Add(st.name + " depth=" + F(st.depth) + "m thrust=" + F(st.vel));
                }
                return _dickIn.Count + " inside (" + string.Join("; ", parts.ToArray()) + ")";
            }
        }

        // Same but for when THIS kobold's own dick is inside someone/something.
        private readonly Dictionary<object, PenState> _dickIn = new Dictionary<object, PenState>();

        // Pick (or keep) a kobold the AI can drive. Skips already-LLM-claimed and player
        // bodies, prefers the nearest free AI kobold, falls back to map-wide if none in range.
        private bool EnsureBody()
        {
            if (_kobold != null && IsAlive(_kobold)) return true;
            if (_kobold != null) TeardownBody(); // possessed body was destroyed
            try
            {
                var playerPos = Vector3.zero;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    playerPos = pp.kobold.transform.position;

                // Debug: count kobolds by control type so we can tell "none on map" from
                // "exists but not AI-controlled" from "all claimed".
                int total = 0, ai = 0, claimed = 0, playerControlled = 0;
                Kobold best = null; float bestD = float.MaxValue;
                foreach (var k in FindObjectsOfType<Kobold>())
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
                    Logger.LogInfo($"KKLLMNPC{InstanceSuffix}: no AI kobold found (total={total} ai={ai} claimed={claimed} player={playerControlled}).");
                    return false;
                }
                if (bestD > _cfgAutoFindRange.Value)
                    Logger.LogInfo($"KKLLMNPC{InstanceSuffix}: nearest AI kobold is {F(bestD)}m (AutoFindRange={_cfgAutoFindRange.Value}m) — possessing anyway.");
                Possess(best);
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
            TeardownBody();
            if (!IsAlive(target)) return; // died between selection and possess
            _kobold = target;
            _currentKoboldId = target.GetInstanceID();
            // Register so any other LLM instance won't grab this body.
            lock (LLMNPCPlugin.ClaimedKobolds) { LLMNPCPlugin.ClaimedKobolds.Add(_currentKoboldId); }
            _npcName = PickName(target);   // choose a name for this body
            Logger.LogInfo("KKLLMNPC: this kobold calls itself '" + _npcName + "'");
            _yawDeg = target.transform.eulerAngles.y; // start from current facing
            _controller = target.GetComponent<KoboldCharacterController>();
            _descriptor = target.GetComponent<CharacterDescriptor>();
            _grabber    = target.GetComponentInChildren<Grabber>(true);
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
            _navTarget = null; _navTargetName = null;

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
                    Logger.LogInfo($"KKLLMNPC: possessed '{target.name}'");
                }
                catch (Exception e) { Logger.LogError("Possess head/cam: " + e); }
            });
        }

        // ------------------------------------------------------------------
        // reagent awareness
        // ------------------------------------------------------------------
        private ScriptableReagent _eggReagent; // resolved lazily

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
                    case GenericReagentContainer.InjectType.Spray:      how = "sprayed"; break;
                    case GenericReagentContainer.InjectType.Flood:      how = "filled up with"; break;
                    case GenericReagentContainer.InjectType.Vacuum:     how = "pumped out"; break;
                    default:                                            how = "drank"; break; // Inject = consume
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
                Logger.LogInfo("reagent: " + ev);
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
            catch (Exception) { return null; }
        }

        // Drain queued reagent events as a list of strings, clearing them.
        private List<object> DrainReagentEvents()
        {
            lock (_reagentEvents)
            {
                var outp = new List<object>();
                while (_reagentEvents.Count > 0) outp.Add(_reagentEvents.Dequeue());
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
            UnsubscribeBelly();
            UnsubscribePenetrables();
            if (_charAnimator != null) { try { _charAnimator.SetLookEnabled(true); } catch (Exception) { } }
            if (_descriptor != null)
            {
                try { _descriptor.SetPlayerControlled(CharacterDescriptor.ControlType.AIPlayer); } catch (Exception) { }
            }
            _kobold = null; _controller = null; _descriptor = null; _grabber = null;
            _charAnimator = null; _head = null; _photonView = null;
            _navTarget = null; _navTargetName = null;
            if (_currentKoboldId >= 0) { lock (LLMNPCPlugin.ClaimedKobolds) { LLMNPCPlugin.ClaimedKobolds.Remove(_currentKoboldId); } _currentKoboldId = -1; }
            StopMove();
        }

        // First-person eye camera: sits slightly in FRONT of the head to avoid face
        // clipping, disabled during gameplay, renders on demand. Near clip + forward offset
        // are config-tuned per kobold body model (some snouts are longer).
        private void SetupCamera()
        {
            if (_head == null) return;
            var go = new GameObject("LLMNPC_Cam");
            go.transform.SetParent(_head, false);
            // In FRONT of the muzzle so we don't see the inside of the head/eyes/teeth,
            // with a near clip that starts past them.
            go.transform.localPosition = new Vector3(0f, 0.06f, _cfgCamForward.Value);
            go.transform.localRotation = Quaternion.identity;
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = 90f;
            _cam.nearClipPlane = _cfgCamNearClip.Value; // clip past face geometry (~eyes & snout)
            _cam.farClipPlane = 80f;
            _cam.enabled = false; // we render on demand
            if (RtCreated(_rt)) { try { _rt.Release(); Destroy(_rt); } catch (Exception) { } }
            _rt = new RenderTexture(_cfgImageSize.Value, _cfgImageSize.Value, 16, RenderTextureFormat.ARGB32);
            _rt.Create();
            _cam.targetTexture = _rt;
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

        // ------------------------------------------------------------------
        // penetration awareness
        // ------------------------------------------------------------------
        private readonly List<PenetrationTech.Penetrable> _subscribedPenetrables
            = new List<PenetrationTech.Penetrable>();

        private readonly List<KKPenetratorListener> _penListeners
            = new List<KKPenetratorListener>();

        // The penetrator-side mirror: when THIS kobold's dick is inside something.
        private class KKPenetratorListener : PenetrationTech.PenetratorListener
        {
            public LLMNPCPlugin owner;
            public PenetrationTech.Penetrator source;
            public Transform lastTarget; // the thing it's inside right now

            private string HoleName(PenetrationTech.Penetrable pen)
            {
                try
                {
                    return pen != null && pen.gameObject != null
                        ? LLMNPCPlugin.CleanName(pen.gameObject.name) : "somewhere";
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
                            EmitAmbient("hah~"); // being entered
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
                    parts.Add(st.name + " " + st.hole + " depth=" + F(st.depth) + "m thrust=" + F(st.vel));
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
    }
}
