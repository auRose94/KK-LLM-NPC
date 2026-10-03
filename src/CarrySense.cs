// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// CarrySense — notices when the body is picked up, carried, or thrown, keeps a
// position trail so the NPC knows WHERE it was moved, and guards against the
// mail/sell machine (it sucks in ragdolled bodies and destroys them).
// Runs on the main thread via FixedUpdateSafe; reports into the world-event
// queue the LLM drains each cycle ("[event]" lines, next to reagent events).
using System;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // ---- carry-sense state (main thread only) ----
        private bool _wasGrabbed;
        private float _grabStartT;
        private Vector3 _grabStartPos;
        private readonly List<Vector3> _carryTrail = new List<Vector3>(32);
        private float _lastTrailSample;
        private float _lastRecoverTry;
        private float _lastMailWarnT = -99f;

        // World events: things that HAPPENED to the body (picked up/thrown/danger),
        // drained into the LLM's next turn as [event] lines. Same pattern as the
        // reagent queue, separate so a pile of tosses can't drown belly events.
        private readonly Queue<string> _worldEvents = new Queue<string>();
        private const int MaxWorldEvents = 8;

        internal void PushWorldEvent(string ev)
        {
            if (string.IsNullOrEmpty(ev)) return;
            lock (_worldEvents)
            {
                _worldEvents.Enqueue(ev);
                while (_worldEvents.Count > MaxWorldEvents) _worldEvents.Dequeue();
            }
            WakeFromSleep(); // grabbed/thrown/danger ends `sleep <secs>` early
        }

        private List<object> DrainWorldEvents()
        {
            lock (_worldEvents)
            {
                var outp = new List<object>();
                while (_worldEvents.Count > 0) outp.Add(_worldEvents.Dequeue());
                return outp;
            }
        }

        // Main-thread poll from FixedUpdateSafe. Cheap: a bool read most frames;
        // heavier work only on grab/release transitions and while ragdolled.
        private void PollCarrySense()
        {
            if (_kobold == null || !IsAlive(_kobold)) return;

            bool grabbedNow; bool ragdolledNow; Vector3 pos; float vel = 0f;
            try
            {
                grabbedNow = _kobold.grabbed;
                var rd = _kobold.GetRagdoller();
                ragdolledNow = rd != null && rd.ragdolled;
                pos = _kobold.transform.position;
                // Release velocity: the game's own throw threshold is a release speed
                // above 3 m/s, and on release it applies that velocity to the RAGDOLL
                // rigidbodies — the main body may already be disabled, so read both.
                if (_kobold.body != null) vel = _kobold.body.velocity.magnitude;
                var parts = rd != null ? rd.GetRagdollBodies() : null;
                if (parts != null)
                    foreach (var b in parts)
                        if (b != null) vel = Mathf.Max(vel, b.velocity.magnitude);
            }
            catch (Exception) { return; }

            // Grab start: snapshot where the body was taken FROM, start the trail,
            // stop any queued movement (the controller is disabled while grabbed, so
            // a stale walk burst would just fire on release).
            if (grabbedNow && !_wasGrabbed)
            {
                _grabStartT = Time.unscaledTime;
                _grabStartPos = pos;
                _carryTrail.Clear();
                _carryTrail.Add(pos);
                _lastTrailSample = Time.unscaledTime;
                PushWorldEvent("you feel yourself being picked up — someone grabbed your body (you can't move while held; wait to be released or dropped)");
                StopMove();
            }

            // While carried: sample waypoints so the release report can describe the
            // PATH, not just the straight-line displacement.
            if (grabbedNow && Time.unscaledTime - _lastTrailSample > 0.5f)
            {
                _lastTrailSample = Time.unscaledTime;
                if (_carryTrail.Count == 0 || Vector3.Distance(_carryTrail[_carryTrail.Count - 1], pos) > 1.2f)
                {
                    _carryTrail.Add(pos);
                    while (_carryTrail.Count > 32) _carryTrail.RemoveAt(0);
                }
            }

            // Release: report where the body went, and how it got there.
            if (!grabbedNow && _wasGrabbed)
            {
                float held = Time.unscaledTime - _grabStartT;
                Vector3 delta = pos - _grabStartPos;
                float dist = delta.magnitude;
                bool thrown = vel > Consts.ThrowThreshold; // matches the game's ThrowRoutine
                var legs = new List<string>();
                for (int i = 1; i < _carryTrail.Count; i++)
                {
                    Vector3 d = _carryTrail[i] - _carryTrail[i - 1];
                    if (d.magnitude > 0.8f) legs.Add(Cardinal(d) + " " + F(d.magnitude) + "m");
                }
                string path = legs.Count > 0 ? " (path: " + string.Join(" → ", legs.ToArray()) + ")" : "";
                string ev;
                if (thrown && dist > 2f)
                {
                    ev = "you were THROWN — launched at ~" + F(vel) + " m/s, and you are now ~" + F(dist) + "m "
                        + Cardinal(delta) + " of where you were picked up" + path + ". You ragdolled on landing; that was startling and a bit rude. Re-orient yourself (look around / map / ls) before doing anything else.";
                    RememberFact("thrown:was thrown ~" + F(dist) + "m " + Cardinal(delta) + " from where I was grabbed");
                }
                else if (dist > 1.5f)
                {
                    ev = "you were picked up and carried ~" + F(dist) + "m " + Cardinal(delta) + path + " — you didn't walk this, someone moved you. Re-orient yourself (look around / map / ls) — your mental map is now off by that much.";
                    RememberFact("carried:was carried ~" + F(dist) + "m " + Cardinal(delta) + " from where I was grabbed");
                }
                else
                {
                    ev = "you were picked up for ~" + F(held) + "s and set back down about where you were.";
                }
                PushWorldEvent(ev);
            }
            _wasGrabbed = grabbedNow;

            // Ragdolled near the mail/sell machine: its intake pulls in loose
            // (ragdolled, unheld) bodies within a couple metres and destroys (sells)
            // them — recovery is faster than any think cycle, so pop the ragdoll
            // ourselves and warn the model off.
            if (ragdolledNow && !grabbedNow && Time.unscaledTime - _lastRecoverTry > 2f)
            {
                MailMachine danger = FindMailMachineNear(pos, Consts.SuckDangerRadius);
                if (danger != null)
                {
                    _lastRecoverTry = Time.unscaledTime;
                    try { _photonView?.RPC("PopRagdoll", RpcTarget.All); } catch (Exception) { }
                    if (Time.unscaledTime - _lastMailWarnT > 10f)
                    {
                        _lastMailWarnT = Time.unscaledTime;
                        PushWorldEvent("DANGER: you ragdolled right next to the MAIL/SELL machine — it pulls loose bodies in and destroys (sells) them. You just scrambled back up; RUN away from it and stay clear.");
                        RememberFact("danger:the mail/sell machine swallows and destroys bodies that come near it while loose — never approach, never climb into it");
                    }
                }
            }
        }

        // Reset carry tracking on (re)bind and world reload — the next body starts clean.
        internal void ResetCarrySense()
        {
            _wasGrabbed = false;
            _grabStartT = 0f;
            _grabStartPos = Vector3.zero;
            _carryTrail.Clear();
            _lastRecoverTry = 0f;
            lock (_worldEvents) _worldEvents.Clear();
        }

        // 8-way compass bearing of a world-space delta (+Z = north, matching the
        // yaw convention used everywhere else in the plugin).
        private static string Cardinal(Vector3 delta)
        {
            float yaw = Mathf.Repeat(Mathf.Atan2(delta.x, delta.z) * Consts.Rad2Deg, 360f);
            string[] dirs = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return dirs[(int)Mathf.Round(yaw / 45f) % 8];
        }

        private static MailMachine FindMailMachineNear(Vector3 pos, float maxDist)
        {
            try
            {
                var cache = SceneCache.Find<MailMachine>(3f);
                if (cache == null) return null;
                foreach (var m in cache)
                {
                    if (m == null) continue;
                    if (Vector3.Distance(m.transform.position, pos) <= maxDist) return m;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}