// IMGUI overlay — config editor, live state debug, mind viewer (console transcript),
// and per-instance on/off toggle. Toggle with F6 (Main.cs binds it; an older doc said
// Insert). Lives in every plugin instance (one per NPC), each with its own
// window; drag to move.
//
// Unity IMGUI in KoboldKare is limited: no TextField, TextArea, ScrollView,
// FlexibleSpace, BeginVertical. We implement custom text input via keyboard capture,
// custom scrollbar via GUI.Button, and use GUI.contentColor for status coloring.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace KKLLMNPC
{
    public partial class LLMNPCPlugin
    {
        private bool _overlayVisible = false;
        private Rect _overlayRect = new Rect(20, 20, 600, 500);
        private string _overlayStatus = "";

        // Tab selection (0=State, 1=Config, 2=Instances)
        private int _activeTab = 0;

        // Instance switching
        private int _activeInstanceIdx = 0;

        // Custom text input state (no GUI.TextField in this Unity version)
        private int _editingFieldId = -1;
        private string _editingValue = "";
        private int _editingCursor = 0;
        private int _editingSelStart = -1;
        private int _editingSelEnd = -1;
        private ConfigEntry _editingEntry = null;

        // Config section scroll
        private float _configScrollY = 0f;

        // Bind a hotkey on Awake (called from the existing Awake).
        private void InitOverlay()
        {
        }

        // Format a float to a short string.
        private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Main entry points - OnGUI is now in Main.cs (required by Unity)
        // ------------------------------------------------------------------
        // NOTE: the real draw path is LLMNPCPlugin.OnGUICallback() in Main.cs —
        // it owns tab routing, scrollbar and wheel handling THERE. A duplicate
        // "DrawOverlayWindow" used to exist here for a GUI.Window variant that
        // never shipped; it was deleted 2026-10-02 because dead duplicate routing
        // kept drifting from the live one (it broke the 4-tab switch once
        // already). Edit Main.cs for window-level chrome, this file for sections.

        // ------------------------------------------------------------------
        // Tab bar
        // ------------------------------------------------------------------

        private float DrawTabBar(float x, float y, float w)
        {
            string[] tabs = { "State", "Mind", "Config", "Instances" };
            float tabW = w / tabs.Length - 4;

            GUILayout.BeginHorizontal();
            for (int i = 0; i < tabs.Length; i++)
            {
                bool active = (_activeTab == i);
                Color col = active ? new Color(0.3f, 0.6f, 1.0f) : new Color(0.5f, 0.5f, 0.6f);
                GUI.contentColor = col;
                bool clicked = GUILayout.Button(tabs[i], GUILayout.Width(tabW));
                GUI.contentColor = Color.white;
                if (clicked) { _activeTab = i; _configScrollY = 0; }
            }
            GUILayout.EndHorizontal();

            return 24;
        }

        // ------------------------------------------------------------------
        // State tab
        // ------------------------------------------------------------------

        private float DrawStateSection(float x, float y, float w)
        {
            // Section header with color coding
            bool hasInstance = _currentInstance != null;
            bool isRunning = hasInstance && _currentInstance.Running;
            bool hasBody = hasInstance && _currentInstance.IsAliveObj(_currentInstance.KoboldObj);

            GUI.Box(new Rect(x, y, w, 16), "— state —");
            y += 18;

            // Status indicator dot
            string statusLabel = hasBody ? (isRunning ? "● running" : "● stopped") : "○ no body";
            GUI.contentColor = hasBody ? (isRunning ? new Color(0.3f, 0.9f, 0.3f) : new Color(0.9f, 0.5f, 0.2f)) : new Color(0.6f, 0.6f, 0.6f);
            GUI.Label(new Rect(x, y, w, 14), statusLabel);
            GUI.contentColor = Color.white;
            y += 16;

            // Content box (8 rows × 14px + padding)
            float boxH = 120;
            GUI.BeginGroup(new Rect(x, y, w, boxH), null, null);
            GUI.Box(new Rect(0, 0, w, boxH), "");

            float cy = 3;
            string sceneText = hasInstance && _currentInstance.IsPlayableScene() ? "in-level" : "menu";
            GUI.Label(new Rect(6, cy, w, 14), "scene: " + sceneText + "  main-ready: " + _mainReady);
            cy += 14;

            if (hasInstance)
            {
                GUI.Label(new Rect(6, cy, w, 14), "thread: " + (_currentInstance.IsThreadAlive ? "running" : "dead") +
                                "  vision: " + (_currentInstance.VisionBusy ? "busy" : "idle"));
                cy += 14;
                GUI.contentColor = _currentInstance.EndpointStatusText().StartsWith("ok") ? new Color(0.3f, 0.9f, 0.3f) : new Color(0.9f, 0.4f, 0.3f);
                GUI.Label(new Rect(6, cy, w, 14), "endpoint: " + _currentInstance.EndpointStatusText());
                GUI.contentColor = Color.white;
                cy += 14;
                GUI.Label(new Rect(6, cy, w, 14), "blocked: " + (string.IsNullOrEmpty(_currentInstance.BlockedInfo) ? "—" : _currentInstance.BlockedInfo));
                cy += 14;
                GUI.Label(new Rect(6, cy, w, 14), "bump: " + (string.IsNullOrEmpty(_currentInstance.BumpInfo) ? "—" : _currentInstance.BumpInfo));
                cy += 14;
                GUI.Label(new Rect(6, cy, w, 14), "yaw: " + F(_currentInstance.YawDeg) + "  pitch: " + F(_currentInstance.PitchDeg));
                cy += 14;
                GUI.Label(new Rect(6, cy, w, 14), "goal: " + _currentInstance.LastThought);
                cy += 14;
                GUI.Label(new Rect(6, cy, w, 14), "last: " + _currentInstance.LastAction + "  heard: " + (_currentInstance.GetRecentPlayerChat() ?? "—"));
            }
            else
            {
                GUI.Label(new Rect(6, cy, w, 14), "no active instance");
            }
            GUI.EndGroup();

            return boxH + 4;
        }

        // ------------------------------------------------------------------
        // Mind tab — the console transcript (what it typed / what came back)
        // plus the latest vision frame + caption. The NPC's whole mind used to
        // be visible only in the BepInEx log; this mirrors it in-game live.
        // ------------------------------------------------------------------

        private List<string> _mindLines;
        private int _mindVersion = -1;

        private float DrawMindSection(float x, float y, float w)
        {
            GUI.Box(new Rect(x, y, w, 16), "— mind — console transcript + eyes —");
            y += 18;

            var npc = _currentInstance;
            if (npc == null)
            {
                GUI.Label(new Rect(x, y, w, 16), "no active instance");
                return 22;
            }

            // Eyes: latest vision caption (this old IMGUI build has no image API —
            // GUIContent/DrawTexture are stripped — so the frame itself stays in
            // BepInEx/plugins/KKLLMNPC_frames/ when DebugDumpFrames is on).
            string caption = npc.VisionCaption ?? "";
            if (caption.Length > 110) caption = caption.Substring(0, 110) + "…";
            GUI.contentColor = new Color(0.7f, 0.8f, 0.9f);
            GUI.Label(new Rect(x, y, w, 16), "eyes: " + (caption.Length > 0 ? caption : "—"));
            GUI.contentColor = Color.white;
            y += 22;

            // Transcript: refresh only when the version moved (OnGUI is per-frame).
            if (_mindLines == null || npc.ConsoleVersion != _mindVersion)
            {
                _mindVersion = npc.ConsoleVersion;
                var copy = npc.ConsoleTranscriptCopy();
                var keep = new List<string>();
                for (int i = Math.Max(0, copy.Count - 18); i < copy.Count; i++) keep.Add(copy[i]);
                _mindLines = keep;
            }

            GUI.Box(new Rect(x, y, w, 264), "");
            GUI.BeginGroup(new Rect(x, y, w, 264), null, null);
            float cy = 3;
            foreach (var line in _mindLines)
            {
                if (cy > 250) break;
                // role| payload — color the model's own words white, prompts gray.
                bool isAssistant = line.StartsWith("assistant|");
                GUI.contentColor = isAssistant ? Color.white
                    : line.StartsWith("system|") ? new Color(0.5f, 0.5f, 0.6f)
                    : new Color(0.65f, 0.72f, 0.8f);
                GUI.Label(new Rect(6, cy, w - 12, 14), line);
                GUI.contentColor = Color.white;
                cy += 14;
            }
            GUI.EndGroup();

            return 22 + 264 + 6;
        }

        // ------------------------------------------------------------------
        // Config tab (with live editing)
        // ------------------------------------------------------------------

        private float DrawConfigSection(float x, float y, float w)
        {
            // Section header
            GUI.Box(new Rect(x, y, w, 16), "— config (click a value to edit) —");
            y += 18;

            // Reflection over every bound config field (_cfg*) — BepInEx handles
            // serialize/parse, so this covers ALL sections ([LLM], [Console],
            // [Vision], [Senses], …) instead of a hand-picked 16 that silently
            // clipped at the box height. This old BepInEx build's ConfigFile has no
            // public Bindings; the entry objects themselves (ConfigEntryBase:
            // GetSerializedValue/SetSerializedValue/Definition/SettingType) suffice.
            var entries = new List<ConfigEntry>();
            try
            {
                var fields = typeof(LLMNPCPlugin).GetFields(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                System.Array.Sort(fields, (fa, fb) => string.CompareOrdinal(fa.Name, fb.Name));
                foreach (var f in fields)
                {
                    if (f.Name == null || !f.Name.StartsWith("_cfg")) continue;
                    object ceb = null;
                    try { ceb = f.GetValue(this); } catch (Exception) { continue; }
                    if (ceb == null) continue;
                    var cebType = ceb.GetType();
                    var getSer = cebType.GetMethod("GetSerializedValue", Type.EmptyTypes);
                    var setSer = cebType.GetMethod("SetSerializedValue", new[] { typeof(string) });
                    var defProp = cebType.GetProperty("Definition");
                    var typeProp = cebType.GetProperty("SettingType");
                    if (getSer == null || setSer == null || defProp == null) continue;
                    string key = "?", section = "?";
                    Type valueType = typeof(string);
                    try
                    {
                        var def = defProp.GetValue(ceb, null);
                        if (def != null)
                        {
                            key = (def.GetType().GetProperty("Key").GetValue(def, null) as string) ?? key;
                            section = (def.GetType().GetProperty("Section").GetValue(def, null) as string) ?? section;
                        }
                        if (typeProp != null) valueType = (Type)typeProp.GetValue(ceb, null);
                    }
                    catch (Exception) { }
                    bool password = key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase);
                    bool isBool = valueType == typeof(bool);
                    // Long text (the system prompts, incl. vision prompt) goes multiline.
                    bool multiline = key.IndexOf("Prompt", StringComparison.Ordinal) >= 0;
                    string value = "";
                    try
                    {
                        value = (string)getSer.Invoke(ceb, null) ?? "";
                    }
                    catch (Exception) { }
                    string label = (section == "?" && key == "?" ? f.Name.Substring(3) : section + "." + key);
                    // BoolGetter/setter: use the captured entry object, not the field.
                    object entryObj = ceb;
                    entries.Add(new ConfigEntry(label, value, multiline, password, isBool, () =>
                    {
                        try { return (string)getSer.Invoke(entryObj, null) == "True"; } catch (Exception) { return false; }
                    }, v =>
                    {
                        try { setSer.Invoke(entryObj, new object[] { v }); } catch (Exception) { }
                    }));
                }
            }
            catch (Exception e) { _overlayStatus = "config list: " + e.Message; }

            float boxH = entries.Count * 18 + 10;
            if (boxH < 40) boxH = 40;
            GUI.BeginGroup(new Rect(x, y, w, boxH), null, null);
            GUI.Box(new Rect(0, 0, w, boxH), "");

            float cy = 3;
            int lineH = 18;

            foreach (var entry in entries)
            {
                if (cy > boxH - lineH) break;
                bool editing = (_editingFieldId == entry.Hash);

                // Label
                GUI.contentColor = editing ? new Color(0.5f, 0.8f, 1.0f) : new Color(0.8f, 0.8f, 0.85f);
                GUI.Label(new Rect(6, cy, 120, lineH), entry.Label + ":");
                GUI.contentColor = Color.white;

                // Value box (clickable)
                Rect valRect = new Rect(130, cy, w - 140, lineH);
                string display = entry.Value;
                if (entry.IsPassword && !string.IsNullOrEmpty(entry.Value))
                    display = new string('*', Math.Max(1, entry.Value.Length));

                Color boxColor = editing ? new Color(0.2f, 0.4f, 0.7f, 0.5f) : new Color(0.15f, 0.15f, 0.15f, 0.3f);
                GUI.color = boxColor;
                GUI.Box(valRect, display);
                GUI.color = Color.white;

                // Click to start editing
                if (Event.current.type == EventType.MouseDown && valRect.Contains(Event.current.mousePosition))
                {
                    _editingFieldId = entry.Hash;
                    _editingEntry = entry;
                    _editingValue = entry.RawValue;
                    _editingCursor = _editingValue.Length;
                    _editingSelStart = -1;
                    _editingSelEnd = -1;
                    Event.current.Use();
                }

                // Render text input if editing
                if (editing)
                {
                    Rect inputRect = new Rect(valRect.x + 2, valRect.y + 2, valRect.width - 4, valRect.height - 4);

                    // Handle keyboard input
                    HandleTextInput(inputRect);

                    // Draw cursor (thin white line via Box)
                    int cursorX = TextToPixelX(_editingValue, _editingCursor, inputRect.width);
                    GUI.contentColor = new Color(1f, 1f, 1f, 0.9f);
                    GUI.Box(new Rect(inputRect.x + cursorX, inputRect.y, 2, inputRect.height), "");
                    GUI.contentColor = Color.white;

                    // Draw text
                    string visible = _editingValue;
                    if (entry.IsPassword && !string.IsNullOrEmpty(visible))
                        visible = new string('*', visible.Length);
                    GUI.Label(inputRect, visible);
                }

                // Double-click to select all
                if (Event.current.type == EventType.MouseUp && Event.current.clickCount >= 2 && valRect.Contains(Event.current.mousePosition))
                {
                    _editingSelStart = 0;
                    _editingSelEnd = entry.RawValue.Length;
                    _editingCursor = _editingSelEnd;
                    Event.current.Use();
                }

                cy += lineH;
            }

            GUI.EndGroup();

            return boxH + 4;
        }

        // ------------------------------------------------------------------
        // Instances tab
        // ------------------------------------------------------------------

        private float DrawInstancesSection(float x, float y, float w)
        {
            // Section header
            GUI.Box(new Rect(x, y, w, 16), "— instances —");
            y += 18;

            float boxH;
            lock (_instancesLock) boxH = _instances.Count * 28 + 56; // rows + say-to row + padding
            GUI.BeginGroup(new Rect(x, y, w, boxH), null, null);
            GUI.Box(new Rect(0, 0, w, boxH), "");

            float cy = 3;

            // List of instances
            lock (_instancesLock)
            {
                for (int i = 0; i < _instances.Count; i++)
                {
                    var npc = _instances[i];
                    bool active = (_activeInstanceIdx == i);
                    bool hasBody = npc.IsAliveObj(npc.KoboldObj);
                    bool isRunning = hasBody && npc.Running;

                    // Instance button
                    string name = hasBody ? npc.GetMyName() : ("#" + (i + 1) + " (no body)");
                    Color textColor = active ? Color.white : new Color(0.7f, 0.7f, 0.8f);

                    Rect btnRect = new Rect(6, cy, w - 12, 24);
                    GUI.Box(btnRect, "");
                    GUI.contentColor = textColor;
                    GUI.Label(btnRect, " " + name);
                    GUI.contentColor = Color.white;

                    // Status dot
                    Color dotColor = hasBody ? (isRunning ? new Color(0.3f, 0.9f, 0.3f) : new Color(0.9f, 0.6f, 0.2f)) : new Color(0.5f, 0.5f, 0.5f);
                    GUI.contentColor = dotColor;
                    GUI.Label(new Rect(btnRect.x + btnRect.width - 16, btnRect.y + 4, 12, 16), hasBody ? (isRunning ? "●" : "◐") : "○");
                    GUI.contentColor = Color.white;

                    // Click to select
                    if (Event.current.type == EventType.MouseDown && btnRect.Contains(Event.current.mousePosition))
                    {
                        _activeInstanceIdx = i;
                        _currentInstance = npc;
                        _configScrollY = 0;
                        Event.current.Use();
                    }

                    // Stop/Start button
                    Rect ctrlRect = new Rect(btnRect.x + btnRect.width - 70, btnRect.y + 4, 60, 16);
                    string ctrlLabel = isRunning ? "Stop" : "Start";
                    Color ctrlColor = isRunning ? new Color(0.8f, 0.3f, 0.2f) : new Color(0.2f, 0.6f, 0.3f);
                    GUI.contentColor = ctrlColor;
                    GUI.Box(ctrlRect, "");
                    GUI.Label(ctrlRect, ctrlLabel);
                    bool clicked = ctrlRect.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown && Event.current.button == 0;
                    GUI.contentColor = Color.white;
                    if (clicked)
                    {
                        if (isRunning)
                        {
                            npc.SetRunning(false);
                            try { npc.Stop(); } catch { }
                            _overlayStatus = "stopped";
                        }
                        else
                        {
                            npc.SetRunning(true);
                            npc.ForceRestartThread();
                            _overlayStatus = "started";
                        }
                    }
                    GUI.contentColor = Color.white;

                    cy += 28;
                }
            }

            // Direct line to the selected NPC — one chat-only instance (the in-game
            // room chat always broadcasts; addressed lines land on one agent, but
            // this works even when the NPC doesn't know its own name yet).
            if (_currentInstance != null)
            {
                string targetName = "this NPC";
                try { targetName = _currentInstance.GetMyName() ?? targetName; } catch (Exception) { }
                string label = "say to " + targetName + ":";
                GUI.Label(new Rect(6, cy + 4, 150, 16), label);
                var injectEntry = new ConfigEntry("inject:" + targetName, "", false, false, false, () => false, v =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(v)) _currentInstance.InjectPlayerChat(v);
                        _overlayStatus = "sent to " + targetName + ": " + v;
                    }
                    catch (Exception) { }
                });
                Rect sayRect = new Rect(160, cy, w - 176, 20);
                bool editing = (_editingFieldId == injectEntry.Hash);
                GUI.color = editing ? new Color(0.2f, 0.4f, 0.7f, 0.5f) : new Color(0.15f, 0.15f, 0.15f, 0.3f);
                GUI.Box(sayRect, editing ? _editingValue : "click, type, Enter to send");
                GUI.color = Color.white;
                if (!editing && Event.current.type == EventType.MouseDown && sayRect.Contains(Event.current.mousePosition))
                {
                    _editingFieldId = injectEntry.Hash;
                    _editingEntry = injectEntry;
                    _editingValue = "";
                    _editingCursor = 0;
                    _editingSelStart = -1;
                    _editingSelEnd = -1;
                    Event.current.Use();
                }
                if (editing)
                {
                    Rect inputRect = new Rect(sayRect.x + 2, sayRect.y + 2, sayRect.width - 4, sayRect.height - 4);
                    HandleTextInput(inputRect);
                    GUI.Label(inputRect, _editingValue);
                }
                cy += 24;
            }

            GUI.EndGroup();

            return boxH + (cy > boxH ? cy - boxH + 28 : 0) + 4;
        }

        // ------------------------------------------------------------------
        // Custom text input (no GUI.TextField available)
        // ------------------------------------------------------------------

        private void HandleTextInput(Rect rect)
        {
            if (Event.current.type != EventType.KeyDown) return;
            if (rect.Contains(Event.current.mousePosition) != true) return;

            // Enter key: confirm
            if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
            {
                if (_editingEntry != null && _editingEntry.Setter != null)
                {
                    _editingEntry.Setter(_editingValue);
                    _overlayStatus = _editingEntry.Label + " = " + (_editingEntry.IsPassword ? "***" : _editingValue);
                }
                _editingFieldId = -1;
                _editingEntry = null;
                Event.current.Use();
                return;
            }

            // Escape: cancel
            if (Event.current.keyCode == KeyCode.Escape)
            {
                _editingFieldId = -1;
                _editingEntry = null;
                Event.current.Use();
                return;
            }

            // Backspace
            if (Event.current.keyCode == KeyCode.Backspace)
            {
                if (_editingSelStart >= 0 && _editingSelEnd >= 0)
                {
                    int start = Math.Min(_editingSelStart, _editingSelEnd);
                    int end = Math.Max(_editingSelStart, _editingSelEnd);
                    _editingValue = _editingValue.Substring(0, start) + _editingValue.Substring(end);
                    _editingCursor = start;
                    _editingSelStart = -1;
                    _editingSelEnd = -1;
                }
                else if (_editingCursor > 0)
                {
                    _editingValue = _editingValue.Substring(0, _editingCursor - 1) + _editingValue.Substring(_editingCursor);
                    _editingCursor--;
                }
                Event.current.Use();
                return;
            }

            // Delete
            if (Event.current.keyCode == KeyCode.Delete)
            {
                if (_editingSelStart >= 0 && _editingSelEnd >= 0)
                {
                    int start = Math.Min(_editingSelStart, _editingSelEnd);
                    int end = Math.Max(_editingSelStart, _editingSelEnd);
                    _editingValue = _editingValue.Substring(0, start) + _editingValue.Substring(end);
                    _editingCursor = start;
                    _editingSelStart = -1;
                    _editingSelEnd = -1;
                }
                else if (_editingCursor < _editingValue.Length)
                {
                    _editingValue = _editingValue.Substring(0, _editingCursor) + _editingValue.Substring(_editingCursor + 1);
                }
                Event.current.Use();
                return;
            }

            // Arrow keys
            if (Event.current.keyCode == KeyCode.LeftArrow)
            {
                if (_editingSelStart >= 0) { _editingCursor = Math.Min(_editingSelStart, _editingSelEnd); _editingSelStart = -1; _editingSelEnd = -1; }
                else _editingCursor = Math.Max(0, _editingCursor - 1);
                Event.current.Use();
                return;
            }
            if (Event.current.keyCode == KeyCode.RightArrow)
            {
                if (_editingSelStart >= 0) { _editingCursor = Math.Max(_editingSelStart, _editingSelEnd); _editingSelStart = -1; _editingSelEnd = -1; }
                else _editingCursor = Math.Min(_editingValue.Length, _editingCursor + 1);
                Event.current.Use();
                return;
            }
            if (Event.current.keyCode == KeyCode.Home)
            {
                _editingCursor = 0; _editingSelStart = -1; _editingSelEnd = -1;
                Event.current.Use();
                return;
            }
            if (Event.current.keyCode == KeyCode.End)
            {
                _editingCursor = _editingValue.Length; _editingSelStart = -1; _editingSelEnd = -1;
                Event.current.Use();
                return;
            }

            // Character input (in this Unity version, chars arrive via KeyDown)
            if (Event.current.type == EventType.KeyDown && !char.IsControl(Event.current.character) && Event.current.character != 0)
            {
                string ch = Event.current.character.ToString();
                if (_editingSelStart >= 0 && _editingSelEnd >= 0)
                {
                    int start = Math.Min(_editingSelStart, _editingSelEnd);
                    _editingValue = _editingValue.Substring(0, start) + ch + _editingValue.Substring(Math.Max(_editingSelStart, _editingSelEnd));
                    _editingCursor = start + 1;
                    _editingSelStart = -1;
                    _editingSelEnd = -1;
                }
                else
                {
                    _editingValue = _editingValue.Substring(0, _editingCursor) + ch + _editingValue.Substring(_editingCursor);
                    _editingCursor++;
                }
                Event.current.Use();
                return;
            }
        }

        private void ApplyTextInput()
        {
            // Find which config entry we're editing and apply
            // We re-iterate the config entries to find the matching one
            // This is a bit hacky but works for our purposes
        }

        // Approximate text width (monospace-ish estimate)
        private int TextToPixelX(string text, int cursorPos, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float charW = maxWidth / Mathf.Max(1, text.Length);
            return (int)(charW * Math.Min(cursorPos, text.Length));
        }

        // ------------------------------------------------------------------
        // Config entry helper
        // ------------------------------------------------------------------

        private class ConfigEntry
        {
            public string Label;
            public string Value;
            public string RawValue;
            public bool IsMultiline;
            public bool IsPassword;
            public bool IsBool;
            public Func<bool> BoolGetter;
            public Action<string> Setter;
            public int Hash;

            public ConfigEntry(string label, string value, bool multiline, bool password, bool isBool, Func<bool> boolGetter, Action<string> setter)
            {
                Label = label;
                Value = value;
                RawValue = value;
                IsMultiline = multiline;
                IsPassword = password;
                IsBool = isBool;
                BoolGetter = boolGetter;
                Setter = setter;
                Hash = label.GetHashCode();
            }
        }

        // ------------------------------------------------------------------
        // Kill-switch plumbing
        // ------------------------------------------------------------------

        private void StopAllNpcActivity()
        {
            if (_currentInstance != null)
            {
                _currentInstance.SetRunning(false);
                try { _currentInstance.Stop(); } catch (Exception) { }
                _overlayStatus = "stopped";
            }
        }

        private void StartLLMThread()
        {
            if (_currentInstance == null) return;
            _currentInstance.SetRunning(true);
            _currentInstance.ForceRestartThread();
        }
    }
}
