// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// VoreControls — the PER-AGENT vore preferences: each mind picks its own entry
// hole (vore_target) and fate mode (vore_mode); the preferences persist with the
// body's saved life and ride the rebirth copy. The operator's globals
// ([LittleTreats] SwallowDestination = F7/WebUI, SwallowEffect = F8/WebUI) remain
// the PLAYER'S separate control and the default for everyone without an override;
// [LittleTreats] SwallowEnabled stays the one master switch above all.
using System;
using System.Collections.Generic;
using KKLLMNPC;
using UnityEngine;

namespace KKLLMNPC
{
    internal static class VoreControlsModule
    {
        private static bool _registered;
        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            var schemas = new Dictionary<string, Dictionary<string, object>>
            {
                ["target"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string", "null" },
                    ["description"] = "vore_target: mouth | anal | pussy | dick | nipple — YOUR personal entry hole for swallows you start; omit to ask, 'off'/'global' = follow the human's setting again"
                },
                ["mode"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string", "null" },
                    ["description"] = "vore_mode: egg | bellyegg | grow | destroy | milk | cum — YOUR personal fate for meals you take (milk/cum = they churn into that fluid in your belly, genes riding it); omit to ask, 'off'/'global' = follow the human's setting again"
                },
            };
            ModuleRegistry.Tool("vore_target", (n, p) => n.ToolVoreTarget(p), schemas);
            ModuleRegistry.Tool("vore_mode", (n, p) => n.ToolVoreMode(p), schemas);

            // The act behind the drive: swallow the person you are HOLDING and FACING,
            // or NAME them — swallow(target=...) at hugging range, no grab dance.
            // Gated by the human's vore settings (SwallowEnabled + a non-Off END mode);
            // the entry hole/fate come from THIS agent's vore_target/vore_mode picks.
            var swallowSchema = new Dictionary<string, Dictionary<string, object>>
            {
                ["target"] = new Dictionary<string, object>
                {
                    ["type"] = new object[] { "string", "null" },
                    ["description"] = "swallow: WHO to eat — an agent's name, a player's chat name, a body name, or id:N from nearby; works at hugging range (~3m), no grab needed. This is how you follow 'eat <name>' orders. Omit to swallow the person you're HOLDING and facing (the classic form)."
                },
            };
            ModuleRegistry.Tool("swallow", (n, p) => n.ToolAgentSwallow(p), swallowSchema);
        }
    }

    internal partial class NPCInstance
    {
        // The agent's own picks (canonical lowercase words; null = no override).
        // Persisted with the body's life; read by SwallowManager.DestinationFor /
        // EffectFor whenever this body is the one INITIATING a swallow.
        internal string _voreTargetOverride;
        internal string _voreModeOverride;

        public string VoreTargetOverride
        {
            get
            {
                var s = _voreTargetOverride;
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }

        public string VoreModeOverride
        {
            get
            {
                var s = _voreModeOverride;
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }

        // The shared state line: my pick + the operator's global + whether vore is
        // even armed. All three surfaces (report, tool, prompts) read it.
        internal string VoreStateLine()
        {
            string mine = VorePreferencesText();
            return mine;
        }

        internal string VorePreferencesText()
        {
            string target = VoreTargetOverride;
            string mode = VoreModeOverride;
            string globalTarget = "off", globalMode = "egg";
            bool masterOn = false;
            try
            {
                var pl = SwallowManager.Plugin;
                if (pl != null)
                {
                    if (pl._cfgSwallowEnabled != null) masterOn = pl._cfgSwallowEnabled.Value;
                    if (pl._cfgSwallowEffect != null) globalMode = SwallowLogic.EffectLabel(pl._cfgSwallowEffect.Value).ToLowerInvariant();
                }
                if (masterOn && SwallowManager.Destination() != SwallowDestination.Off)
                    globalTarget = SwallowLogic.DestinationLabel(SwallowManager.Destination()).ToLowerInvariant();
            }
            catch (Exception) { }
            string line = "vore controls: "
                + (masterOn
                    ? "vore armed by the operator"
                    : "vore is DISABLED by the operator (SwallowEnabled is off — nothing you set takes effect until they enable it)")
                + "; your own target = " + (target ?? "(none — follows the operator's setting: " + globalTarget + ")")
                + "; your own fate = " + (mode ?? "(none — follows the operator's setting: " + globalMode + ")");
            return line;
        }

        internal string GlobalVoreTargetWord()
        {
            try
            {
                if (SwallowManager.Destination() == SwallowDestination.Off) return "off";
                return SwallowLogic.DestinationLabel(SwallowManager.Destination()).ToLowerInvariant();
            }
            catch (Exception) { return "off"; }
        }

        internal string GlobalVoreModeWord()
        {
            try
            {
                return SwallowLogic.EffectLabel(SwallowManager.Effect()).ToLowerInvariant();
            }
            catch (Exception) { return "egg"; }
        }

        // The act behind the drive: swallow the person you are HOLDING and FACING
        // (LLM-into-LLM vore — the absorb/egg fates merge the victim's memories into
        // the eater's context). Every gate is the human's: SwallowEnabled + a non-Off
        // END mode must be on, exactly like the player's INSERT. The entry hole/fate
        // resolve from this agent's vore_target/vore_mode picks inside SwallowManager.
        internal object ToolAgentSwallow(JsonObj p)
        {
            // MAIN-THREAD ONLY (the wave-41 lesson, same as eat_player): the swallow
            // touches Photon ownership/RPCs, grabber/Rigidbody surgery and reflection
            // into the grabber — calling those off the LLM worker thread corrupts the
            // process. Marshal the whole flow; fall back to a clean error if the main
            // thread is busy.
            try
            {
                return RunOnMainThread(() => (object)AgentSwallowOnMain(p), 10000)
                    ?? (object)new { ok = false, reason = "main_thread_busy", note = "the main thread did not answer in 10s — try again next turn" };
            }
            catch (Exception e)
            {
                return new { ok = false, reason = "main_thread_failed", note = TextUtil.Shorten(e.Message, 160) };
            }
        }

        // The swallow, guaranteed to run on the main thread.
        private object AgentSwallowOnMain(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            // kobold-into-kobold only: the local player is eat_player's domain.
            if (IsPlayerKobold(_kobold))
                return new { ok = false, reason = "player_body", note = "use eat_player to eat the local player" };

            // TARGETED FORM (2026-11-16): swallow(target=...) — eat a named person at
            // hugging range, no grab-first dance. The playtest's "couldn't be ordered
            // to eat by name": the model had to find, walk to, grab AND face a moving
            // person through four fragile steps.
            string want = null;
            try
            {
                if (p != null)
                {
                    want = p.S("target", "");
                    if (string.IsNullOrEmpty(want)) want = p.S("name", "");
                }
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(want))
            {
                Kobold victim = ResolveSwallowTarget(want.Trim());
                if (victim == null)
                    return new { ok = false, reason = "no_such_target",
                        note = "nobody called '" + TextUtil.Shorten(want.Trim(), 40) + "' is nearby — `ps` lists who's around (agents by name, players by chat name, wild kobolds by body name + id)" };
                try
                {
                    Vector3 dv = victim.transform.position - _kobold.transform.position;
                    float d = dv.magnitude;
                    if (d > 3.2f)
                        return new { ok = false, reason = "too_far",
                            note = want.Trim() + " is " + F(d) + "m away (" + RelBearing(dv) + ") — get within hugging range first: walk toward them (or go_to their name), then swallow again" };
                }
                catch (Exception) { }
                SwallowManager.InsertStatus tstatus;
                if (SwallowManager.TryInsertForAgentTargeted(_kobold, victim, out tstatus))
                {
                    try { Logger.LogInfo("[" + MyName() + "] agent swallow (targeted): pulled in " + NPCInstance.CleanName(victim.name)); } catch (Exception) { }
                    return new { ok = true, swallowed = true,
                        note = "they are inside you now — fate follows your vore_mode pick; their memories join yours per the fate" };
                }
                return new { ok = false, reason = tstatus.ToString(), note = InsertStatusNote(tstatus) };
            }

            SwallowManager.InsertStatus status;
            bool started = SwallowManager.TryInsertForAgent(_kobold, out status);
            if (started)
            {
                try { Logger.LogInfo("[" + MyName() + "] agent swallow: pulled in a held person (mode follows the human's settings)"); } catch (Exception) { }
                return new { ok = true, swallowed = true,
                    note = "they are inside you now — fate follows your vore_mode pick; their memories join yours per the fate" };
            }
            return new { ok = false, reason = status.ToString(), note = InsertStatusNote(status) };
        }

        private static string InsertStatusNote(SwallowManager.InsertStatus status)
        {
            switch (status)
            {
                case SwallowManager.InsertStatus.Disabled:
                    return "vore is DISABLED — the human has the master switch or END mode off; do not assume you can eat";
                case SwallowManager.InsertStatus.NoTarget:
                    return "you are not HOLDING someone within swallow range — grab a person first, face them, then swallow (or use swallow target='<name>' at hugging range)";
                case SwallowManager.InsertStatus.AlreadyInside:
                    return "that person is already inside someone — no second eater";
                case SwallowManager.InsertStatus.ReceiverBusy:
                    return "you are already digesting someone (or the nearest receiver is) — one meal at a time";
                case SwallowManager.InsertStatus.NoReceiver:
                    return "FORCE mode: no other live kobold near you to receive them";
                case SwallowManager.InsertStatus.TooBig:
                    return "they are TOO BIG for your stomach — your bellySize sets the ceiling (a bigger-stomached body could hold them). Size them up honestly, pick smaller meals, or ask the human to lift the limit ([LittleTreats].SwallowSizeGates)";
                default:
                    return "the swallow did not happen — hold a person you are facing and try again";
            }
        }

        // Resolve a swallow target by name or id:N — agents by chosen name first,
        // then player chat nicks, then body names; a matching id:N always wins.
        // Main thread only (scene scan). Returns null when nobody matches.
        private Kobold ResolveSwallowTarget(string want)
        {
            try
            {
                if (string.IsNullOrEmpty(want) || !IsAlive(_kobold)) return null;
                int id = -1;
                if (want.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(want.Substring(3).Trim(), out id))
                    want = "";
                var kobolds = SceneCache.Find<Kobold>(2f);
                if (kobolds == null) return null;
                Kobold byBody = null;
                foreach (var k in kobolds)
                {
                    if (k == null || k == _kobold) continue;
                    int kid = k.GetInstanceID();
                    if (id >= 0 && kid == id) return k;
                    if (want.Length == 0) continue;
                    string agent = NPCInstance.AgentNameForBody(kid);
                    if (!string.IsNullOrEmpty(agent) && string.Equals(agent, want, StringComparison.OrdinalIgnoreCase)) return k;
                    try
                    {
                        string nick = KoboldOwnerNick(k);
                        if (!string.IsNullOrEmpty(nick) && string.Equals(nick, want, StringComparison.OrdinalIgnoreCase)) return k;
                    }
                    catch (Exception) { }
                    if (byBody == null && string.Equals(NPCInstance.CleanName(k.name), want, StringComparison.OrdinalIgnoreCase)) byBody = k;
                }
                return byBody;
            }
            catch (Exception) { return null; }
        }

        internal object ToolVoreTarget(JsonObj p)
        {
            string arg = null;
            if (p != null && p.Has("target")) arg = p.S("target");
            if (arg == null) arg = p != null ? p.S("route", p.S("hole", p.S("to", null))) : null;
            if (string.IsNullOrEmpty(arg))
                return VorePreferenceReport("vore_target", GlobalVoreTargetWord(), VoreTargetOverride, "mouth | anal | pussy | dick | nipple | off");
            string word = arg.Trim().Trim('"', '\'');
            const string valid = "mouth | anal | pussy | dick | nipple | off";
            SwallowDestination d; bool clear, bad;
            if (!SwallowLogic.TryParseDestination(word, out d, out clear, out bad))
            {
                try { Logger.LogInfo("[" + MyName() + "] vore_target: didn't understand '" + word + "'"); } catch (Exception) { }
                return new { ok = false, reason = "unknown_target", valid = valid };
            }
            if (clear)
            {
                _voreTargetOverride = null;
                PersistVorePreferences("target cleared — follows the human's setting");
                return new { ok = true, my_target = (string)null, follows = "the human's global setting (" + GlobalVoreTargetWord() + ")" };
            }
            _voreTargetOverride = SwallowLogic.DestinationLabel(d).ToLowerInvariant();
            PersistVorePreferences("target = " + _voreTargetOverride);
            return new { ok = true, my_target = _voreTargetOverride, note = "your swallows will take their meal in through your " + (_voreTargetOverride == "dick" ? "cock" : _voreTargetOverride) };
        }

        internal object ToolVoreMode(JsonObj p)
        {
            string arg = null;
            if (p != null && p.Has("mode")) arg = p.S("mode");
            if (arg == null) arg = p != null ? p.S("fate", p.S("type", p.S("target", p.S("to", null)))) : null;
            if (string.IsNullOrEmpty(arg))
                return VorePreferenceReport("vore_mode", GlobalVoreModeWord(), VoreModeOverride, "egg | bellyegg | grow | destroy | off");
            string word = arg.Trim().Trim('"', '\'');
            const string valid = "egg | bellyegg | grow | destroy | off";
            SwallowEffect m; bool clear, bad;
            if (!SwallowLogic.TryParseEffect(word, out m, out clear, out bad))
            {
                try { Logger.LogInfo("[" + MyName() + "] vore_mode: didn't understand '" + word + "'"); } catch (Exception) { }
                return new { ok = false, reason = "unknown_mode", valid = valid };
            }
            if (clear)
            {
                _voreModeOverride = null;
                PersistVorePreferences("fate cleared — follows the human's setting");
                return new { ok = true, my_mode = (string)null, follows = "the human's global setting (" + GlobalVoreModeWord() + ")" };
            }
            _voreModeOverride = SwallowLogic.EffectLabel(m).ToLowerInvariant().Replace("belly-egg", "bellyegg");
            PersistVorePreferences("fate = " + _voreModeOverride);
            return new { ok = true, my_mode = _voreModeOverride };
        }

        private object VorePreferenceReport(string tool, string globalWord, string mine, string validList)
        {
            string how = tool == "vore_target"
                ? "call vore_target(target='anal'); 'off' clears back to the human's setting"
                : "call vore_mode(mode='grow'); 'off' clears back to the human's setting";
            return new
            {
                ok = true,
                tool = tool,
                my_setting = mine ?? "(none — follows the human)",
                operators_global = globalWord,
                how = how,
                valid = validList
            };
        }

        // Persist across body-loss/rebirth with the rest of the life record.
        private void PersistVorePreferences(string what)
        {
            try { Logger.LogInfo("[" + MyName() + "] vore preference: " + what); } catch (Exception) { }
            try { SaveBodyMemory(); } catch (Exception) { }
        }
    }
}