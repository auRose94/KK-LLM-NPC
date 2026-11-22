// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// OptionsMenuTab — the in-game options-menu integration (v1.13.0, Rose's ask:
// "a tab for the LLM system with categories like the WebUI").
//
// The game's options menu is built on naelstrof's UnityScriptableSettings
// framework: each sub-tab ("View - Graphics", "View - Audio", …) holds a
// ScriptableSettingSpawner that renders every Setting ScriptableObject whose
// group matches its targetGroup (sliders for clamped floats/ints, dropdowns
// for enum-ish Settings, text inputs for free floats/ints/strings), and
// SettingsManager.AddSetting() is the framework's own public registration
// API. This class rides that machinery instead of inventing a second UI:
//
//   1. one SettingGroup per category ("KKLLM", "KK Console", …),
//   2. config-backed Setting proxies (BepInEx ConfigEntry stays the source of
//      truth — the proxies read it on Load and write it on SetValue, so the
//      config file, the WebUI and this tab all edit the same keys),
//   3. a cloned "View - Graphics" per category (keeps ScrollRect +
//      AutoScrollToSelection + the widget templates) with its inherited rows
//      cleared and targetGroup pointed at our group,
//   4. a cloned "Button - Graphics" per category wired to hide-then-show,
//      registered on the panel's HideAllViews so every OTHER tab hides ours.
//
// Labels resolve through the game's own localization table ("GUID" — the name
// every in-game LocalizeStringEvent references): entries are added to the
// loaded StringTable at runtime and the LocalizedString points at them, so
// the spawner's label code just works. Build is best-effort: any failure
// logs once and the tab simply doesn't appear (the WebUI remains).
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityScriptableSettings;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.UI;

namespace KKLLMNPC
{
    // ------------------------------------------------------------------
    // Config-backed Setting proxies. BepInEx's ConfigEntry is the source of
    // truth: Load() reads it (never PlayerPrefs — the game's Save button
    // calls Save() on every Setting, and ours must not stomp the file),
    // SetValue() writes it (firing BepInEx's SettingChanged like any edit).
    // ------------------------------------------------------------------
    internal class KKSetGroup : SettingGroup { }

    internal class KKSetFloat : SettingFloatClamped
    {
        public ConfigEntry<float> Entry;
        private static readonly FieldInfo MinF = typeof(SettingFloatClamped).GetField("min", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo MaxF = typeof(SettingFloatClamped).GetField("max", BindingFlags.NonPublic | BindingFlags.Instance);

        public void Init(ConfigEntry<float> entry, float min, float max, LocalizedString label, SettingGroup group)
        {
            Entry = entry;
            try { MinF?.SetValue(this, min); MaxF?.SetValue(this, max); } catch (Exception) { }
            base.name = entry.Definition.Key + "_" + entry.Definition.Section;
            localizedName = label;
            this.group = group;
        }

        public override void SetValue(float value)
        {
            base.SetValue(value);
            if (Entry != null) Entry.Value = value;
        }

        public override void Load()
        {
            SetValue(Entry != null ? Entry.Value : 0f);
        }

        public override void Save() { } // config already written at SetValue time

        public override void ResetToDefault()
        {
            if (Entry != null) Entry.Value = (float)Convert.ChangeType(Entry.DefaultValue ?? 0f, typeof(float));
        }
    }

    internal class KKSetIntClamped : SettingIntClamped
    {
        public ConfigEntry<int> Entry;
        private static readonly FieldInfo MinF = typeof(SettingIntClamped).GetField("min", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo MaxF = typeof(SettingIntClamped).GetField("max", BindingFlags.NonPublic | BindingFlags.Instance);

        public void Init(ConfigEntry<int> entry, int min, int max, LocalizedString label, SettingGroup group)
        {
            Entry = entry;
            try { MinF?.SetValue(this, min); MaxF?.SetValue(this, max); } catch (Exception) { }
            base.name = entry.Definition.Key + "_" + entry.Definition.Section;
            localizedName = label;
            this.group = group;
        }

        public override void SetValue(int value)
        {
            base.SetValue(value);
            if (Entry != null) Entry.Value = value;
        }

        public override void Load()
        {
            SetValue(Entry != null ? Entry.Value : 0);
        }

        public override void Save() { }

        public override void ResetToDefault()
        {
            if (Entry != null) Entry.Value = (int)Convert.ChangeType(Entry.DefaultValue ?? 0, typeof(int));
        }
    }

    // Free-range int → the spawner renders a TEXT INPUT (wider ranges are
    // nonsense as sliders: MaxTokens 18096).
    internal class KKSetIntText : SettingInt
    {
        public ConfigEntry<int> Entry;

        public void Init(ConfigEntry<int> entry, LocalizedString label, SettingGroup group)
        {
            Entry = entry;
            base.name = entry.Definition.Key + "_" + entry.Definition.Section;
            localizedName = label;
            this.group = group;
        }

        public override void SetValue(int value)
        {
            base.SetValue(value);
            if (Entry != null) Entry.Value = value;
        }

        public override void Load() { SetValue(Entry != null ? Entry.Value : 0); }
        public override void Save() { }
        public override void ResetToDefault()
        {
            if (Entry != null) Entry.Value = (int)Convert.ChangeType(Entry.DefaultValue ?? 0, typeof(int));
        }
    }

    // Bool → two-option dropdown (Disabled / Enabled) — the game's framework
    // has no toggle widget; the game's own bool-ish settings use dropdowns too.
    internal class KKSetBool : SettingDropdown
    {
        public ConfigEntry<bool> Entry;
        private static readonly FieldInfo OptsF = typeof(SettingDropdown).GetField("dropdownOptions", BindingFlags.NonPublic | BindingFlags.Instance);

        public void Init(ConfigEntry<bool> entry, LocalizedString label, SettingGroup group)
        {
            Entry = entry;
            try { OptsF?.SetValue(this, new string[] { "Disabled", "Enabled" }); } catch (Exception) { }
            base.name = entry.Definition.Key + "_" + entry.Definition.Section;
            localizedName = label;
            this.group = group;
        }

        public override void SetValue(int value)
        {
            base.SetValue(value);
            if (Entry != null) Entry.Value = value >= 1;
        }

        public override void Load() { SetValue(Entry != null && Entry.Value ? 1 : 0); }
        public override void Save() { }
        public override void ResetToDefault()
        {
            if (Entry != null) Entry.Value = Convert.ToBoolean(Entry.DefaultValue ?? false);
        }
    }

    // Enum → dropdown of the enum names (vore fates, POV, FSM mode, …).
    internal class KKSetEnum : SettingDropdown
    {
        public ConfigEntryBase Entry; // boxed: one class serves every enum key
        public Type EnumType;
        private static readonly FieldInfo OptsF = typeof(SettingDropdown).GetField("dropdownOptions", BindingFlags.NonPublic | BindingFlags.Instance);

        public void Init(ConfigEntryBase entry, Type enumType, LocalizedString label, SettingGroup group)
        {
            Entry = entry;
            EnumType = enumType;
            try { OptsF?.SetValue(this, Enum.GetNames(enumType)); } catch (Exception) { }
            base.name = entry.Definition.Key + "_" + entry.Definition.Section;
            localizedName = label;
            this.group = group;
        }

        public override void SetValue(int value)
        {
            base.SetValue(value);
            try
            {
                var vals = Enum.GetValues(EnumType);
                if (value >= 0 && value < vals.Length && Entry != null)
                    Entry.BoxedValue = vals.GetValue(value);
            }
            catch (Exception) { }
        }

        public override void Load()
        {
            try
            {
                var vals = Enum.GetValues(EnumType);
                for (int i = 0; i < vals.Length; i++)
                    if (Equals(vals.GetValue(i), Entry != null ? Entry.BoxedValue : null)) { SetValue(i); return; }
            }
            catch (Exception) { }
        }

        public override void Save() { }
        public override void ResetToDefault()
        {
            if (Entry != null) { Entry.BoxedValue = Entry.DefaultValue; Load(); }
        }
    }

    internal class KKSetString : SettingString
    {
        public ConfigEntry<string> Entry;

        public void Init(ConfigEntry<string> entry, LocalizedString label, SettingGroup group)
        {
            Entry = entry;
            base.name = entry.Definition.Key + "_" + entry.Definition.Section;
            localizedName = label;
            this.group = group;
        }

        public override void SetValue(string value)
        {
            base.SetValue(value);
            if (Entry != null) Entry.Value = value;
        }

        public override void Load() { SetValue(Entry != null ? Entry.Value ?? "" : ""); }
        public override void Save() { }
        public override void ResetToDefault()
        {
            if (Entry != null) Entry.Value = Convert.ToString(Entry.DefaultValue ?? "");
        }
    }

    // ------------------------------------------------------------------
    // The tab builder.
    // ------------------------------------------------------------------
    internal static class OptionsMenuTab
    {
        private static LLMNPCPlugin Plugin;
        private static bool _built;
        private static int _throttle;
        private static bool _failed;

        // One row per exposed key: (section, key, label, sliderMin, sliderMax,
        // forceTextInput, needsRestart). Rows without a slider range render as
        // text inputs (float/int) or dropdowns (bool/enum). Label "" = the key.
        private struct Opt
        {
            public string S, K, L;
            public float Min, Max;
            public bool Text, Restart;
            public Opt(string s, string k, string l) : this(s, k, l, 0f, 0f, false, false) { }
            public Opt(string s, string k, string l, float min, float max, bool text = false, bool restart = false)
            { S = s; K = k; L = l; Min = min; Max = max; Text = text; Restart = restart; }
        }

        // (tab title, sections) → the curated keys. Sections mirror the WebUI's
        // grouping; ApiKey is deliberately NOT exposed (typed secrets on stream).
        private static readonly string[][] Tabs =
        {
            new[] { "KKLLM",         "LLM", "General", "Memory", "Autonomy" },
            new[] { "KK Console",    "Console", "AgentFSM" },
            new[] { "KK Senses",     "Senses", "Vision" },
            new[] { "KK Movement",   "Movement" },
            new[] { "KK Vore",       "LittleTreats", "Needs", "Intoxication" },
            new[] { "KK Servers",    "Ollama", "LMStudio", "VisionModel", "NonConsent", "Web" },
        };

        private static readonly Opt[] Opts =
        {
            // ---- LLM ----
            new Opt("LLM", "Temperature", "Temperature", 0f, 2f),
            new Opt("LLM", "MaxTokens", "Max tokens", 0, 0, true),
            new Opt("LLM", "ThinkInterval", "Think interval (s)", 0.05f, 2f),
            new Opt("LLM", "PlanStepDelay", "Plan step delay (s)", 0f, 5f),
            new Opt("LLM", "PlanMaxSteps", "Plan max steps", 1, 16),
            new Opt("LLM", "ParallelCalls", "Parallel tool calls", 1, 16),
            new Opt("LLM", "ImageEveryNTicks", "Image every N ticks", 1, 10),
            new Opt("LLM", "ImageHistory", "Image history frames", 0, 6),
            new Opt("LLM", "SendImage", "Send vision image"),
            new Opt("LLM", "ImageOnBump", "Image on bump"),
            new Opt("LLM", "ImageOnTurn", "Image on big turn"),
            new Opt("LLM", "ChatLogLines", "Chat log lines", 0, 60),
            new Opt("LLM", "CommentEveryNTicks", "Commentary every N ticks", 0, 60),
            new Opt("LLM", "CommentTemp", "Commentary temperature", 0f, 2f),
            new Opt("LLM", "TimeoutSeconds", "Request timeout (s)", 10f, 600f),
            new Opt("LLM", "Model", "Model", 0, 0, true),
            new Opt("LLM", "Endpoint", "Endpoint URL", 0, 0, true),
            new Opt("LLM", "AutoSwitchModel", "Auto switch model"),
            new Opt("LLM", "LLMNameSelection", "LLM picks NPC names"),
            new Opt("LLM", "Thinking", "Thinking budget"),
            new Opt("LLM", "KvCacheType", "KV cache type", 0, 0, true),
            new Opt("LLM", "MtpEnabled", "MTP enabled"),
            // ---- General ----
            new Opt("General", "MaxNPCs", "Max NPC agents", 1, 8),
            new Opt("General", "InGameHud", "In-game HUD"),
            new Opt("General", "SpawnMissingKobolds", "Spawn missing kobolds"),
            new Opt("General", "DisableReagentMessages", "Mute reagent messages"),
            // ---- Memory ----
            new Opt("Memory", "FactCap", "Fact cap", 4, 200),
            new Opt("Memory", "FactMaxAgeHours", "Fact max age (h)", 0, 240),
            new Opt("Memory", "FactDecayTicks", "Fact decay ticks", 0, 10000),
            // ---- Autonomy ----
            new Opt("Autonomy", "IdleInitiativeSeconds", "Idle initiative (s)", 10, 600),
            new Opt("Autonomy", "SiblingNudgeGap", "Sibling nudge gap (s)", 0, 120),
            new Opt("Autonomy", "AgentChatPerMinute", "Agent chat / minute", 0, 20),
            new Opt("Autonomy", "AgentOverhear", "Agents overhear"),
            new Opt("Autonomy", "AgentOverhearRadiusM", "Overhear radius (m)", 2, 40),
            // ---- Console ----
            new Opt("Console", "Enabled", "Console mode"),
            new Opt("Console", "MaxRounds", "Console rounds / turn", 1, 10),
            new Opt("Console", "MaxTokens", "Console max tokens", 0, 0, true),
            new Opt("Console", "HistoryMessages", "Console history lines", 0, 60),
            new Opt("Console", "AutoScreenshotTicks", "Auto screenshot ticks", 0, 50),
            new Opt("Console", "NativeTools", "Native tools"),
            // ---- AgentFSM ----
            new Opt("AgentFSM", "Mode", "State machine mode"),
            // ---- Senses ----
            new Opt("Senses", "RayCount", "Ray count", 1, 32),
            new Opt("Senses", "RayRange", "Ray range (m)", 5, 100),
            new Opt("Senses", "RadarEnabled", "Sonar radar"),
            new Opt("Senses", "RadarSize", "Radar size", 5, 60),
            new Opt("Senses", "RadarScale", "Radar scale", 0.05f, 2f),
            new Opt("Senses", "ImageSize", "Vision image size", 64, 512),
            new Opt("Senses", "ImageQuality", "Vision image quality", 1, 100),
            new Opt("Senses", "CameraNearClip", "Camera near clip", 0.01f, 1f),
            new Opt("Senses", "CameraForward", "Camera forward offset", 0f, 1f),
            new Opt("Senses", "CameraFarClip", "Camera far clip", 20f, 200f),
            new Opt("Senses", "AutoFindRange", "Auto-find range (m)", 5, 50),
            // ---- Vision ----
            new Opt("Vision", "Enabled", "Vision captions"),
            new Opt("Vision", "Stereo", "Stereo vision"),
            new Opt("Vision", "StereoIPD", "Stereo IPD (m)", 0.02f, 0.15f),
            new Opt("Vision", "EveryNTicks", "Vision every N ticks", 1, 10),
            // ---- Movement ----
            new Opt("Movement", "PathfindingEnabled", "Pathfinding"),
            new Opt("Movement", "WorldMap3D", "3D octree nav"),
            new Opt("Movement", "WorldMapEnabled", "Shared world map"),
            new Opt("Movement", "TurnRate", "Turn rate (deg/s)", 30, 720),
            new Opt("Movement", "Acceleration", "Acceleration", 1, 200),
            new Opt("Movement", "Deceleration", "Deceleration", 1, 200),
            new Opt("Movement", "BrakeDistance", "Brake distance (m)", 0.1f, 10f),
            new Opt("Movement", "PathfindingCellSize", "Path cell size (m)", 0.25f, 2f),
            new Opt("Movement", "PathTimeBudgetMs", "Path budget (ms)", 1, 50),
            new Opt("Movement", "PathMaxExpansions", "Path max expansions", 500, 50000),
            new Opt("Movement", "WorldMapRayDepthM", "Map ray depth (m)", 20, 300),
            new Opt("Movement", "WorldMapCellSize", "Map cell size (m)", 0.25f, 2f),
            new Opt("Movement", "WorldMapCellsPerFrame", "Map cells / frame", 10, 2000),
            new Opt("Movement", "WorldMapLayers", "Map layers", 4, 64),
            new Opt("Movement", "WorldMapMaxSpan", "Map max span (m)", 10, 400),
            // ---- Vore / needs ----
            new Opt("LittleTreats", "SwallowEnabled", "Swallow vore"),
            new Opt("LittleTreats", "SwallowAgentEat", "Agents eat the player"),
            new Opt("LittleTreats", "SwallowPlayerVictims", "Agents eat players (NPCs)"),
            new Opt("LittleTreats", "SwallowAgentEatPOV", "Spectate POV"),
            new Opt("LittleTreats", "SwallowAgentEatRange", "Eat range (m)", 0.5f, 10f),
            new Opt("LittleTreats", "SwallowDigestSeconds", "Digest seconds", 5, 600),
            new Opt("LittleTreats", "SwallowEffect", "Vore fate"),
            new Opt("LittleTreats", "SwallowDestination", "Vore destination"),
            new Opt("LittleTreats", "SwallowSizeGates", "Size gates"),
            new Opt("LittleTreats", "SwallowSwellMode", "Swell mode"),
            new Opt("LittleTreats", "SwallowReincarnateChance", "Reincarnate chance", 0f, 1f),
            new Opt("LittleTreats", "SwallowGrowLarger", "Grow larger"),
            new Opt("LittleTreats", "SwallowGrowPercent", "Grow percent", 0f, 3f),
            new Opt("LittleTreats", "SwallowGrowSex", "Grow sex"),
            new Opt("LittleTreats", "SwallowGrowStrength", "Grow strength"),
            new Opt("LittleTreats", "SwallowInsertRangeMin", "Insert range min (m)", 0.1f, 3f),
            new Opt("LittleTreats", "SwallowInsertRangeMax", "Insert range max (m)", 0.1f, 6f),
            new Opt("LittleTreats", "SwallowLookUpDot", "Look-up trigger dot", 0.5f, 1f),
            new Opt("LittleTreats", "SwallowSafeDropKey", "Safe drop key", 0, 0, true),
            new Opt("Needs", "HornyBaseline", "Horny baseline", 0f, 1f),
            new Opt("Needs", "HornyClimbPerMin", "Horny climb / min", 0f, 1f),
            new Opt("Needs", "VoreAppetitePerMin", "Vore appetite / min", 0f, 1f),
            new Opt("Intoxication", "Enabled", "Intoxication"),
            new Opt("Intoxication", "Potency", "Drink potency", 0.1f, 5f),
            new Opt("Intoxication", "Reagents", "Alcohol reagents", 0, 0, true),
            // ---- Servers ----
            new Opt("Ollama", "Enabled", "Ollama adoption"),
            new Opt("Ollama", "AutoEndpoint", "Ollama auto endpoint"),
            new Opt("Ollama", "Replicas", "Ollama replicas", 1, 4),
            new Opt("Ollama", "KeepAlive", "Ollama keep-alive", 0, 0, true),
            new Opt("LMStudio", "Enabled", "LM Studio adoption"),
            new Opt("LMStudio", "AutoEndpoint", "LM Studio auto endpoint"),
            new Opt("LMStudio", "Replicas", "LM Studio replicas", 1, 4),
            new Opt("VisionModel", "Endpoint", "Vision endpoint URL", 0, 0, true),
            new Opt("VisionModel", "Model", "Vision model", 0, 0, true),
            new Opt("VisionModel", "MaxTokens", "Vision max tokens", 0, 0, true),
            new Opt("VisionModel", "Temperature", "Vision temperature", 0f, 2f),
            new Opt("NonConsent", "Enabled", "Non-consent detector"),
            new Opt("NonConsent", "SFWOnly", "Steer to SFW"),
            new Opt("NonConsent", "FlagThreshold", "Flag threshold", 0f, 1f),
            new Opt("Web", "Enabled", "Web debug UI"),
            new Opt("Web", "Port", "Web UI port", 1024, 65535),
        };

        // Entries that only apply on restart — the label says so honestly.
        private static readonly HashSet<string> RestartKeys = new HashSet<string>
        {
            "LLM|SystemPromptFile", "Console|SystemPromptFile", "Web|Port",
            "SceneKnowledge|DumpDir", "General|BlockedScenes",
        };

        internal static void Ensure(LLMNPCPlugin plugin)
        {
            Plugin = plugin;
        }

        // Cheap poll from the plugin's update loop: build once the menu prefab
        // and the SettingsManager singleton exist (a few seconds after boot).
        // Retry budget: a transient not-ready state (SettingsManager Awake
        // ordering, another mod mid-injection into the same panel — the VR rig
        // adds its tabs here too) must NOT permanently kill the tab, so
        // not-ready returns silently and failures retry up to _maxAttempts
        // before latching off for the session.
        private static int _attempts;
        private const int MaxAttempts = 20;

        private static bool SettingsReady()
        {
            try { SettingsManager.GetSettings(); return true; }
            catch (Exception) { return false; } // NRE when the singleton hasn't Awake'd
        }

        internal static void Tick()
        {
            if (_built || _failed) return;
            var cfg = Plugin != null ? Plugin._cfgOptionsMenu : null;
            if (cfg != null && !cfg.Value) { _built = true; return; }
            if (++_throttle % 30 != 0) return; // ~every 0.5s
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
                if (menu == null) return;
                if (!SettingsReady()) return;
                Build(menu);
                _built = true;
                L10n.WriteEnTemplate(AllStrings()); // translator template lands on disk once the key inventory is known
                try { LLMNPCPlugin.Log.LogInfo("options menu: KKLLM tabs injected"); } catch (Exception) { }
            }
            catch (Exception e)
            {
                _attempts++;
                if (_attempts >= MaxAttempts)
                {
                    _failed = true; // honest give-up — the WebUI remains the surface
                    try { LLMNPCPlugin.Log.LogWarning("options menu injection failed after " + _attempts + " attempts: " + e.Message); } catch (Exception) { }
                }
                else
                {
                    try { LLMNPCPlugin.Log.LogInfo("options menu: attempt " + _attempts + " not ready (" + e.Message + ") — retrying"); } catch (Exception) { }
                }
            }
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindChild(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // Template lookup that survives OTHER mods injecting into the same menu
        // (the VR rig adds its own KK VR tabs to this very panel): prefer the
        // exact prefab name, and never mistake another mod's un-renamed clone
        // ("(Clone)") or an injected KK* tab for the template.
        private static Transform FindTemplate(Transform parent, string exactName, string prefix)
        {
            Transform fallback = null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                string n = c.name;
                if (n == exactName) return c;
                if (fallback == null && n.StartsWith(prefix) && !n.Contains("(Clone)")
                    && !n.StartsWith("Button - KK") && !n.StartsWith("View - KK"))
                    fallback = c;
            }
            return fallback;
        }

        private static void Build(MainMenu menu)
        {
            var optionsRoot = FindChild(menu.transform, "View - Options");
            if (optionsRoot == null) throw new Exception("View - Options not found in the menu hierarchy");
            var body = FindChild(optionsRoot, "Body");
            var panel = body != null ? FindChild(body, "Panel") : null;
            var buttonPanel = body != null ? FindChild(body, "ButtonPanel") : null;
            if (panel == null || buttonPanel == null) throw new Exception("options Panel/ButtonPanel not found");
            var gfxView = FindTemplate(panel, "View - Graphics", "View - Graphics");
            var gfxButton = FindTemplate(buttonPanel, "Button - Graphics", "Button - Graphics");
            if (gfxView == null || gfxButton == null) throw new Exception("View/Button - Graphics templates not found");
            var hideAll = buttonPanel.GetComponent<HideAllViews>();
            if (hideAll == null) throw new Exception("HideAllViews missing on ButtonPanel");

            foreach (var tab in Tabs)
            {
                string title = tab[0];
                var group = ScriptableObject.CreateInstance<KKSetGroup>();
                group.name = title.Replace(" ", "");
                group.localizedName = Loc(title);

                // VIEW: clone Graphics (ScrollRect + spawner + widget templates),
                // clear the game's inherited rows, point targetGroup at ours.
                var viewGo = UnityEngine.Object.Instantiate(gfxView.gameObject);
                viewGo.name = "View - " + title;
                viewGo.transform.SetParent(panel, false);
                viewGo.SetActive(false);
                var spawner = viewGo.GetComponentInChildren<ScriptableSettingSpawner>(true);
                if (spawner == null) throw new Exception("cloned view lost its ScriptableSettingSpawner");
                var tgF = typeof(ScriptableSettingSpawner).GetField("targetGroup", BindingFlags.NonPublic | BindingFlags.Instance);
                tgF?.SetValue(spawner, group);
                ClearRows(spawner, group.localizedName);

                // BUTTON: clone Graphics' button, label it through the game's own
                // localization (re-renders on language switch), hide-then-show.
                var btnGo = UnityEngine.Object.Instantiate(gfxButton.gameObject);
                btnGo.name = "Button - " + title;
                btnGo.transform.SetParent(buttonPanel, false);
                btnGo.SetActive(true);
                var btnLabelRef = Loc(title);
                var lse = btnGo.GetComponentInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true);
                if (lse != null)
                {
                    // The game's localizer re-renders per language; give it OUR entry.
                    lse.StringReference = btnLabelRef;
                }
                var tmp = btnGo.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tmp != null) tmp.text = L10n.T(title); // immediate render (the LSE refreshes on locale change)
                var btn = btnGo.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick = new Button.ButtonClickedEvent(); // drop the cloned persistent calls
                    var v = viewGo;
                    btn.onClick.AddListener(() =>
                    {
                        try { hideAll.HideThem(); } catch (Exception) { }
                        v.SetActive(true);
                    });
                }
                if (hideAll.views == null) hideAll.views = new List<GameObject>();
                hideAll.views.Add(viewGo);

                // SETTINGS: one proxy per curated key in this tab's sections.
                foreach (var opt in Opts)
                {
                    bool inTab = false;
                    foreach (var s in tab) if (s == opt.S) { inTab = true; break; }
                    if (!inTab) continue;
                    ConfigEntryBase entry = null;
                    try { entry = Plugin.Config[opt.S, opt.K]; } catch (Exception) { entry = null; }
                    if (entry == null) continue; // key renamed/removed upstream — skip quietly
                    RegisterProxy(group, opt, entry);
                }
            }
        }

        // Destroy the rows the game's own spawner spawned into the cloned view
        // (everything that is not one of the four widget templates).
        private static void ClearRows(ScriptableSettingSpawner spawner, LocalizedString label)
        {
            var keep = new HashSet<Transform>();
            foreach (var f in new[] { spawner.slider, spawner.dropdown, spawner.textInput, spawner.groupTitle })
                if (f != null) keep.Add(f.transform);
            for (int i = spawner.transform.childCount - 1; i >= 0; i--)
            {
                var c = spawner.transform.GetChild(i);
                if (!keep.Contains(c)) UnityEngine.Object.Destroy(c.gameObject);
            }
        }

        private static void RegisterProxy(SettingGroup group, Opt opt, ConfigEntryBase entry)
        {
            string label = string.IsNullOrEmpty(opt.L) ? opt.K : opt.L;
            if (opt.Restart || RestartKeys.Contains(opt.S + "|" + opt.K)) label += " (restart)";
            var loc = Loc(label);
            if (entry.SettingType == typeof(bool))
            {
                var p = ScriptableObject.CreateInstance<KKSetBool>();
                p.Init((ConfigEntry<bool>)entry, loc, group);
                SettingsManager.AddSetting(p);
            }
            else if (entry.SettingType.IsEnum)
            {
                var p = ScriptableObject.CreateInstance<KKSetEnum>();
                p.Init(entry, entry.SettingType, loc, group);
                SettingsManager.AddSetting(p);
            }
            else if (entry.SettingType == typeof(int))
            {
                if (opt.Max > opt.Min)
                {
                    var p = ScriptableObject.CreateInstance<KKSetIntClamped>();
                    p.Init((ConfigEntry<int>)entry, (int)opt.Min, (int)opt.Max, loc, group);
                    SettingsManager.AddSetting(p);
                }
                else
                {
                    var p = ScriptableObject.CreateInstance<KKSetIntText>();
                    p.Init((ConfigEntry<int>)entry, loc, group);
                    SettingsManager.AddSetting(p);
                }
            }
            else if (entry.SettingType == typeof(float))
            {
                var p = ScriptableObject.CreateInstance<KKSetFloat>();
                float min = opt.Max > opt.Min ? opt.Min : 0f;
                float max = opt.Max > opt.Min ? opt.Max : 1f;
                p.Init((ConfigEntry<float>)entry, min, max, loc, group);
                SettingsManager.AddSetting(p);
            }
            else if (entry.SettingType == typeof(string))
            {
                var p = ScriptableObject.CreateInstance<KKSetString>();
                p.Init((ConfigEntry<string>)entry, loc, group);
                SettingsManager.AddSetting(p);
            }
        }

        // The enum proxy takes ConfigEntryBase directly (one class serves every
        // enum key through BoxedValue — no per-enum generic dance).

        // Labels resolve through the game's own localization table (named
        // "GUID" — what every in-game LocalizeStringEvent references): add a
        // runtime entry, point the LocalizedString at it. The text is injected
        // into EVERY available locale's table — L10n's translation where a
        // lang file provides one, English otherwise — so the game's own
        // LocalizeStringEvent machinery re-renders the label when the player
        // switches language. Failure = blank label, tab still functional.
        private static LocalizedString Loc(string text)
        {
            var ls = new LocalizedString();
            try
            {
                var table = LocalizationSettings.StringDatabase.GetTable("GUID");
                if (table == null || table.SharedData == null) return ls;
                var keyEntry = table.SharedData.AddKey();
                var localeProvider = LocalizationSettings.AvailableLocales;
                var locales = localeProvider != null ? localeProvider.Locales : null;
                if (locales != null)
                {
                    foreach (var loc in locales)
                    {
                        try
                        {
                            var tbl = LocalizationSettings.StringDatabase.GetTable("GUID", loc);
                            if (tbl != null)
                                tbl.AddEntry(keyEntry.Key, L10n.T(text, loc != null ? loc.Identifier.Code : null));
                        }
                        catch (Exception) { }
                    }
                }
                ls.SetReference("GUID", keyEntry.Key);
            }
            catch (Exception) { }
            return ls;
        }

        // The full English string inventory (translator template source).
        internal static IEnumerable<string> AllStrings()
        {
            foreach (var tab in Tabs)
                yield return tab[0];
            foreach (var opt in Opts)
                yield return string.IsNullOrEmpty(opt.L) ? opt.K : opt.L;
            yield return SpectateLogic.InsideHudTemplate;
            yield return SpectateLogic.NoHatchHudTemplate;
            yield return SpectateLogic.UntilHudTemplate;
            yield return SpectateLogic.MashHudTemplate;
        }
    }
}
