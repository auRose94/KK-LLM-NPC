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
                    ["description"] = "vore_mode: egg | bellyegg | grow | destroy — YOUR personal fate for meals you take; omit to ask, 'off'/'global' = follow the human's setting again"
                },
            };
            ModuleRegistry.Tool("vore_target", (n, p) => n.ToolVoreTarget(p), schemas);
            ModuleRegistry.Tool("vore_mode", (n, p) => n.ToolVoreMode(p), schemas);
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