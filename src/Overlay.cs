// IMGUI overlay — config editor, live state debug, and per-instance on/off toggle.
// Toggle with Insert. Lives in every plugin instance (one per NPC), each with its own
// window; drag to move.
//
// Unity IMGUI in KoboldKare is limited: no TextField, TextArea, ScrollView,
// FlexibleSpace, BeginVertical. We implement custom text input via keyboard capture,
// custom scrollbar via GUI.Button, and use GUI.contentColor for status coloring.
using System;
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
        private bool _overlayVisible;
        private Rect _overlayRect = new Rect(20, 20, 600, 600);
        private string _overlayStatus = "";

        // Per-instance kill switch (config-backed so it persists).
        private ConfigEntry<bool> _cfgEnabled;

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

        // Bind a hotkey + enabled config on Awake (called from the existing Awake).
        private void InitOverlay()
        {
            _cfgEnabled = Config.Bind("General", "Enabled", true, "If false this instance is inert: no possession, no LLM calls");
        }

        // Format a float to a short string.
        private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Main entry points
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            // Hotkey to toggle, only when in-game.
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F6)
            {

                _overlayVisible = !_overlayVisible;
                Event.current.Use();
            }
            if (!_overlayVisible) return;

            _overlayRect = GUI.Window(GetInstanceID(), _overlayRect, DrawOverlayWindow,
                new GUIContent("KKLLMNPC" + InstanceSuffix + " control"), null);
        }

        private void DrawOverlayWindow(int id)
        {
            float x = 10, y = 25, w = _overlayRect.width - 20;

            // --- Tab bar ---
            y += DrawTabBar(x, y, w);

            // --- Content (scrollable) ---
            float contentHeight = _activeTab == 1 ? 420 : (_activeTab == 2 ? 200 : 140);
            float scrollH = _overlayRect.height - y - 10;
            _configScrollY = Mathf.Clamp(_configScrollY, 0, Mathf.Max(0, contentHeight - scrollH));

            GUI.BeginGroup(new Rect(x, y, w, scrollH), null, null);
            float cy = -_configScrollY;

            switch (_activeTab)
            {
                case 0: cy += DrawStateSection(x, cy, w); break;
                case 1: cy += DrawConfigSection(x, cy, w); break;
                case 2: cy += DrawInstancesSection(x, cy, w); break;
            }

            if (!string.IsNullOrEmpty(_overlayStatus))
            {
                GUI.contentColor = new Color(0.8f, 0.9f, 0.4f);
                GUI.Label(new Rect(6, cy + 4, w, 14), _overlayStatus);
                GUI.contentColor = Color.white;
                cy += 16;
            }

            GUI.EndGroup();

            // Scrollbar (right side)
            if (contentHeight > scrollH)
            {
                float sbX = x + w - 14;
                float sbY = y;
                float sbH = scrollH;
                float sbW = 12;
                float thumbH = Mathf.Max(20, sbH * (scrollH / contentHeight));
                float thumbY = sbY + (sbH - thumbH) * (_configScrollY / (contentHeight - scrollH));

                // Track
                GUI.Box(new Rect(sbX, sbY, sbW, sbH), "");

                // Thumb
                GUI.Box(new Rect(sbX + 1, thumbY, sbW - 2, thumbH), "");

                // Click on track
                Rect trackUp = new Rect(sbX, sbY, sbW, thumbY - sbY);
                Rect trackDown = new Rect(sbX, thumbY + thumbH, sbW, sbY + sbH - thumbY - thumbH);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    if (trackUp.Contains(Event.current.mousePosition))
                        _configScrollY -= scrollH * 0.5f;
                    if (trackDown.Contains(Event.current.mousePosition))
                        _configScrollY += scrollH * 0.5f;
                }
            }

            // Mouse wheel scroll
            if (Event.current.type == EventType.ScrollWheel && _overlayRect.Contains(Event.current.mousePosition))
            {
                _configScrollY -= Event.current.delta.y * 15;
                _configScrollY = Mathf.Clamp(_configScrollY, 0, Mathf.Max(0, contentHeight - scrollH));
                Event.current.Use();
            }

            // Drag handle at bottom
            GUI.DragWindow(new Rect(0, _overlayRect.height - 12, _overlayRect.width, 12));
        }

        // ------------------------------------------------------------------
        // Tab bar
        // ------------------------------------------------------------------

        private float DrawTabBar(float x, float y, float w)
        {
            string[] tabs = { "State", "Config", "Instances" };
            float tabW = w / 3f - 4;

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

            // Content box
            float boxH = 90;
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
        // Config tab (with live editing)
        // ------------------------------------------------------------------

        private float DrawConfigSection(float x, float y, float w)
        {
            // Section header
            GUI.Box(new Rect(x, y, w, 16), "— config (click a value to edit) —");
            y += 18;

            float boxH = 380;
            GUI.BeginGroup(new Rect(x, y, w, boxH), null, null);
            GUI.Box(new Rect(0, 0, w, boxH), "");

            float cy = 3;
            int lineH = 18;

            // Config entries
            ConfigEntry[] entries = new ConfigEntry[]
            {
                new ConfigEntry("Endpoint", _cfgEndpoint.Value, false, false, false, () => false, v => { _cfgEndpoint.Value = v; }),
                new ConfigEntry("Model", _cfgModel.Value, false, false, false, () => false, v => { _cfgModel.Value = v; }),
                new ConfigEntry("ApiKey", _cfgApiKey.Value, false, true, false, () => false, v => { _cfgApiKey.Value = v; }),
                new ConfigEntry("System prompt", _cfgSystem.Value, true, false, false, () => false, v => { _cfgSystem.Value = v; }),
                new ConfigEntry("ThinkInterval", _cfgThinkInterval.Value.ToString("F2", CultureInfo.InvariantCulture), false, false, false, () => false, v => {
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) _cfgThinkInterval.Value = Mathf.Clamp(f, 0.05f, 10f);
                }),
                new ConfigEntry("MaxTokens", _cfgMaxTokens.Value.ToString(CultureInfo.InvariantCulture), false, false, false, () => false, v => {
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) _cfgMaxTokens.Value = Mathf.Clamp(n, 64, 32768);
                }),
                new ConfigEntry("Temperature", _cfgTemperature.Value.ToString("F2", CultureInfo.InvariantCulture), false, false, false, () => false, v => {
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) _cfgTemperature.Value = Mathf.Clamp(f, 0f, 2f);
                }),
                new ConfigEntry("SendImage", _cfgSendImage.Value.ToString(), false, false, true, () => _cfgSendImage.Value, v => { _cfgSendImage.Value = v == "True"; }),
                new ConfigEntry("Vision", _cfgVision.Value.ToString(), false, false, true, () => _cfgVision.Value, v => { _cfgVision.Value = v == "True"; }),
                new ConfigEntry("Vision debug", _cfgVisionDebug.Value.ToString(), false, false, true, () => _cfgVisionDebug.Value, v => { _cfgVisionDebug.Value = v == "True"; }),
                new ConfigEntry("Vision model", _cfgVisModel.Value, false, false, false, () => false, v => { _cfgVisModel.Value = v; }),
                new ConfigEntry("Vision endpoint", _cfgVisEndpoint.Value, false, false, false, () => false, v => { _cfgVisEndpoint.Value = v; }),
                new ConfigEntry("Vision prompt", _cfgVisionPrompt.Value, true, false, false, () => false, v => { _cfgVisionPrompt.Value = v; }),
                new ConfigEntry("Image size", _cfgImageSize.Value.ToString(CultureInfo.InvariantCulture), false, false, false, () => false, v => {
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) _cfgImageSize.Value = Mathf.Max(1, n);
                }),
                new ConfigEntry("CamNearClip", _cfgCamNearClip.Value.ToString("F2", CultureInfo.InvariantCulture), false, false, false, () => false, v => {
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) _cfgCamNearClip.Value = f;
                }),
                new ConfigEntry("CamForward", _cfgCamForward.Value.ToString("F2", CultureInfo.InvariantCulture), false, false, false, () => false, v => {
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) _cfgCamForward.Value = f;
                }),
            };

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

            float boxH = 140;
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

            GUI.EndGroup();

            return boxH + 4;
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
