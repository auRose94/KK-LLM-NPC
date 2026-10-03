// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// HoldSense — awareness of what THIS body is holding in its own Grabber.
//
// The game's Grabber holds objects with a physics spring anchored ~1m in front of the
// view (Grabber.cs: DriverConstraint + ConfigurableJoint for kobold victims, hold point
// = viewPos + forward * defaultOffset, throw = TryActivate adds ~10 m/s then releases).
// The grabs live in Grabber's private `grabbedObjects` list with no public accessor, so
// the mod reads it via reflection (fields/props, tiny + guarded) and turns it into:
//   * perception 'holding' entries,
//   * world events on pickup/release ("you grabbed a PERSON — let them go"),
//   * concrete grab/drop/throw tool results,
//   * a status/`cat hold` line.
// This is what the old loop couldn't see: the AI grabbed things (sometimes the player)
// with no idea what was in its hands, so it could walk around for minutes holding a
// person, and it never learned the held-item physics.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Photon.Pun;

namespace KKLLMNPC
{
    internal partial class NPCInstance
    {
        // Held-thing identity (internal: PlayerTrail's activity snapshot references it).
        internal sealed class HeldInfo
        {
            public IGrabbable Grabbable;
            public Kobold Victim;   // non-null when the held thing is a PERSON
            public bool IsPlayerVictim;
            public string Name;
            public string Kind;     // person/player/fruit/seed/water bucket/watering can/tool/egg/thing
            public Vector3 Pos;
            public float Dist;
        }

        private bool _wasHoldingSomething;
        private readonly HashSet<int> _heldIds = new HashSet<int>();
        private readonly Dictionary<int, string> _heldNames = new Dictionary<int, string>();
        private float _lastPollHold;

        // Property getter reflection for GrabInfo (public getters, private setters).
        private static object GetProp(object obj, string name)
        {
            try
            {
                var pr = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                return pr != null ? pr.GetValue(obj, null) : null;
            }
            catch (Exception) { return null; }
        }

        // What's in OUR hands right now, identified. Thread-agnostic (reflection +
        // transform reads) — call from the main-thread poll or wrap in RunOnMainThread
        // next to TryGrab/TryDrop for fresh, consistent reads.
        private List<HeldInfo> HeldScan()
        {
            return HeldScanFor(_kobold);
        }

        // Static form for any body — NPCInstance.Hands() for itself, PlayerTrail /
        // the player-activity readout for the host player's grabber.
        internal static List<HeldInfo> HeldScanFor(Kobold body)
        {
            var outp = new List<HeldInfo>();
            try
            {
                if (body == null || !IsAlive(body) || body.grabbed) return outp; // being held, not holding
                var grabber = body.GetComponentInChildren<Grabber>(true);
                if (grabber == null) return outp;
                var raw = GetField<object>(grabber, "grabbedObjects") as IEnumerable;
                if (raw == null) return outp;
                foreach (object g in raw)
                {
                    if (g == null) continue;
                    try
                    {
                        var grabbable = GetProp(g, "grabbable") as IGrabbable;
                        if (grabbable == null) continue;
                        var comp = grabbable as Component;
                        if (comp == null) continue;
                        var hi = new HeldInfo
                        {
                            Grabbable = grabbable,
                            Victim = GetProp(g, "kobold") as Kobold,
                            Name = CleanName(comp.name),
                        };
                        hi.IsPlayerVictim = hi.Victim != null && hi.Victim != body && IsPlayerKobold(hi.Victim);
                        try { hi.Pos = comp.transform.position; } catch (Exception) { }
                        hi.Dist = Vector3.Distance(hi.Pos, body.transform.position);
                        DescribeHeldKind(hi);
                        outp.Add(hi);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return outp;
        }

        private static void DescribeHeldKind(HeldInfo hi)
        {
            if (hi.Victim != null) { hi.Kind = hi.IsPlayerVictim ? "player" : "person"; return; }
            Transform t = hi.Grabbable != null ? hi.Grabbable.transform : null;
            if (t == null) { hi.Kind = "thing"; return; }
            try
            {
                if (t.GetComponentInParent<Fruit>() != null) { hi.Kind = "fruit"; return; }
                if (t.GetComponentInParent<Seed>() != null) { hi.Kind = "seed"; return; }
                if (t.GetComponentInParent<BucketWeapon>() != null) { hi.Kind = "water bucket"; return; }
                if (t.GetComponentInParent<WateringCanWeapon>() != null) { hi.Kind = "watering can"; return; }
                if (t.GetComponentInParent<GenericWeapon>() != null) { hi.Kind = "tool"; return; }
                string nm = (hi.Name ?? "").ToLowerInvariant();
                if (nm.Contains("egg")) { hi.Kind = "egg"; return; }
                string fw = FixtureWordFor(hi.Name);
                hi.Kind = fw ?? "thing";
            }
            catch (Exception) { hi.Kind = "thing"; }
        }

        // One line explaining the held thing + its physics, so the model learns the
        // mechanics from the object in hand instead of guessing.
        private string DescribeHeld(HeldInfo h, bool shortForm)
        {
            string basePart = h.Kind + " '" + h.Name + "'" + (shortForm ? "" : " (" + F(h.Dist) + "m from you)");
            switch (h.Kind)
            {
                case "player":
                    return basePart + " — THE PLAYER's body (they are limp while held); let go promptly when asked: drop";
                case "person":
                    return basePart + " — that is ANOTHER PERSON, not an item; let them go with drop when they ask, never throw them unless they ask to be thrown";
                case "fruit": return basePart + " — blends when it enters a blender's intake; can be eaten";
                case "seed": return basePart + " — plantable: hold/drop it within ~1m of bare soil, then use it";
                case "water bucket": return basePart + " — throw/activate SPRAYS ~10ml water blobs; contents never run out; dropping just sets the bucket down";
                case "watering can": return basePart + " — throw/activate squirts water at what you aim at";
                case "tool": return basePart + " — throw/activate fires/uses it";
                case "egg": return basePart + " — plantable in soil like a seed: grows a fresh EMPTY body (alive, nobody's home)";
                default: return basePart + " — floats ~1m ahead on a spring; bump it into things to use it physically";
            }
        }

        // Main-thread poll (FixedUpdateSafe): report grabs/releases as world events the
        // LLM drains next turn — the only way it ever learns what its hands did.
        internal void PollHoldSense()
        {
            if (_grabber == null || !IsAlive(_kobold) || _kobold.grabbed) { _heldIds.Clear(); _heldNames.Clear(); _wasHoldingSomething = false; return; }
            if (Time.unscaledTime - _lastPollHold < 0.3f) return;
            _lastPollHold = Time.unscaledTime;

            var held = HeldScan();
            var ids = new HashSet<int>();
            foreach (var h in held)
            {
                try { ids.Add(h.Grabbable.transform.GetInstanceID()); _heldNames[h.Grabbable.transform.GetInstanceID()] = h.Name; } catch (Exception) { }
            }

            foreach (var h in held)
            {
                int id;
                try { id = h.Grabbable.transform.GetInstanceID(); } catch (Exception) { continue; }
                if (_heldIds.Contains(id)) continue;
                PushWorldEvent("you grabbed " + DescribeHeld(h, false)
                    + " — it floats ~1m in front of your face, spring-held: it lags behind your turns, bumps/pushes things you walk into, and rides your momentum." + (h.Victim != null ? " YOU ARE HOLDING A PERSON." : ""));
            }
            if (_wasHoldingSomething && ids.Count == 0 && _heldNames.Count > 0)
            {
                var names = new List<string>(_heldNames.Values);
                PushWorldEvent("your hands are empty now — you released " + string.Join(", ", names.ToArray()));
            }
            foreach (int old in new List<int>(_heldIds))
            {
                if (ids.Contains(old)) continue;
                string nm;
                _heldNames.TryGetValue(old, out nm);
                _heldNames.Remove(old);
                PushWorldEvent("you let go of " + (nm ?? "something") + (nm != null && _heldNames.Count > 0 ? "; still holding " + string.Join(", ", new List<string>(_heldNames.Values).ToArray()) : ""));
            }
            _heldIds.Clear();
            foreach (int i in ids) _heldIds.Add(i);
            _wasHoldingSomething = ids.Count > 0;
        }

        internal void ResetHoldSense()
        {
            _heldIds.Clear();
            _heldNames.Clear();
            _wasHoldingSomething = false;
            _lastPollHold = 0f;
        }

        // One-line "what's in your hands" for status/whoami/console.
        internal string HoldingLine()
        {
            var held = HeldScan();
            if (held.Count == 0) return "nothing";
            var parts = new List<string>();
            foreach (var h in held)
                parts.Add(h.Kind + " " + h.Name + " @" + F(h.Dist) + "m" + (h.Victim != null ? " (PERSON — drop releases them)" : ""));
            return string.Join("; ", parts.ToArray());
        }

        // Hard release for a person still joint-locked after a normal drop — the
        // game's own OnReleaseRPC re-enables their controller, so belt-and-braces it
        // directly on the victim's view (idempotent when the normal path already ran).
        private void HardReleaseVictim(Kobold victim)
        {
            try
            {
                if (victim == null || !IsAlive(victim)) return;
                victim.photonView.RPC("OnReleaseRPC", RpcTarget.All, _photonView.ViewID, Vector3.zero);
            }
            catch (Exception e) { Logger.LogWarning("hard release: " + e.Message); }
        }
    }
}