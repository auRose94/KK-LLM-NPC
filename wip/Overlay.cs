// IMGUI overlay — config editor, live state debug, and per-instance on/off toggle.
// Toggle with Insert. Lives in every plugin instance (one per NPC), each with its own
// window; drag to move. State is in whatever ConfigEntry values are being read.
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
        private Vector2 _overlayScroll;
        private Rect _overlayRect = new Rect(20, 20, 560, 640);
        private string _overlayStatus = "";

        // Per-instance kill switch (config-backed so it persists).
        private ConfigEntry<bool> _cfgEnabled;

        // Bind a hotkey + enabled config on Awake (called from the existing Awake).
        private void InitOverlay()
        {
            _cfgEnabled = Config.Bind("General", "Enabled", true, "If false this instance is inert: no possession, no LLM calls");
        }

        private void OnGUI()
        {
            // Hotkey to toggle, only when in-game.
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Insert)
            {
                _overlayVisible = !_overlayVisible;
                Event.current.Use();
            }
            if (!_overlayVisible) return;

            _overlayRect = GUILayout.Window(GetInstanceID(), _overlayRect, DrawOverlayWindow,
                "KKLLMNPC" + LLMNPCPlugin.InstanceSuffix + " control");
        }

        private void DrawOverlayWindow(int id)
        {
            GUILayout.BeginVertical();

            // Top bar: enable + possessed-state + close
            GUILayout.BeginHorizontal();
            bool en = GUILayout.Toggle(_cfgEnabled.Value, " enabled");
            if (en != _cfgEnabled.Value)
            {
                _cfgEnabled.Value = en;
                if (!en) { StopAllNpcActivity(); }
                else { _running = true; try { _llmThread?.Start(); } catch { StartLLMThread(); } _overlayStatus = "enabled"; }
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(IsAlive(_kobold) ? "possessed: " + MyName() : "no body");
            if (GUILayout.Button("×", GUILayout.Width(24))) _overlayVisible = false;
            GUILayout.EndHorizontal();

            _overlayScroll = GUILayout.BeginScrollView(_overlayScroll);

            // ------ runtime debug ------
            GUILayout.Label("— state —");
            GUILayout.BeginVertical("box");
            GUILayout.Label("scene: " + (IsPlayableScene() ? "in-level" : "menu") + "  main-ready: " + _mainReady);
            GUILayout.Label("thread: " + (_llmThread != null && _llmThread.IsAlive ? "running" : "dead") +
                            "  vision: " + (_visionBusy ? "busy" : "idle"));
            GUILayout.Label("blocked: " + (string.IsNullOrEmpty(_blockedInfo) ? "—" : _blockedInfo));
            GUILayout.Label("bump: " + (string.IsNullOrEmpty(_bumpInfo) ? "—" : _bumpInfo));
            GUILayout.Label("yaw: " + F(_yawDeg) + "  pitch: " + F(_pitchDeg));
            GUILayout.Label("goal: " + _lastThought);
            GUILayout.Label("last: " + _lastAction + "  heard: " + (RecentPlayerChat() ?? "—"));
            GUILayout.EndVertical();

            GUILayout.Space(6);

            // ------ config ------
            GUILayout.Label("— config —");
            _overlayStatus = "";
            ConfigTextRow("Endpoint", _cfgEndpoint);
            ConfigTextRow("Model", _cfgModel);
            ConfigTextRow("ApiKey", _cfgApiKey, true);
            ConfigTextRow("System prompt", _cfgSystem, multiline: true);
            ConfigFloatRow("ThinkInterval", _cfgThinkInterval);
            ConfigIntRow("MaxTokens", _cfgMaxTokens);
            ConfigFloatRow("Temperature", _cfgTemperature);
            ConfigBoolRow("SendImage", _cfgSendImage);
            ConfigBoolRow("Vision enabled", _cfgVision);
            ConfigBoolRow("Vision debug frames", _cfgVisionDebug);
            ConfigTextRow("Vision model", _cfgVisModel);
            ConfigTextRow("Vision endpoint", _cfgVisEndpoint);
            ConfigTextRow("Vision prompt", _cfgVisionPrompt, multiline: true);
            ConfigIntRow("Image size", _cfgImageSize);
            ConfigFloatRow("CameraNearClip", _cfgCamNearClip);
            ConfigFloatRow("CameraForward", _cfgCamForward);

            if (!string.IsNullOrEmpty(_overlayStatus))
                GUILayout.Label(_overlayStatus);

            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0, 0, _overlayRect.width, 20));
        }

        // ------ per-type row helpers ------

        private void ConfigTextRow(string label, ConfigEntry<string> e, bool password = false, bool multiline = false)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120));
            string v = GUILayout.TextField(e.Value ?? "", multiline ? GUILayout.Height(60) : GUILayout.ExpandHeight(false));
            if (v != e.Value) { e.Value = v; _overlayStatus = label + " saved"; }
            GUILayout.EndHorizontal();
        }

        private void ConfigBoolRow(string label, ConfigEntry<bool> e)
        {
            GUILayout.BeginHorizontal();
            bool v = GUILayout.Toggle(e.Value, " " + label);
            if (v != e.Value) { e.Value = v; _overlayStatus = label + " = " + v; }
            GUILayout.EndHorizontal();
        }

        private void ConfigIntRow(string label, ConfigEntry<int> e)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120));
            string s = GUILayout.TextField(e.Value.ToString(CultureInfo.InvariantCulture));
            int n; if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n != e.Value) { e.Value = n; _overlayStatus = label + " = " + n; }
            GUILayout.EndHorizontal();
        }

        private void ConfigFloatRow(string label, ConfigEntry<float> e)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120));
            string s = GUILayout.TextField(e.Value.ToString(CultureInfo.InvariantCulture));
            float f; if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && !Mathf.Approximately(f, e.Value)) { e.Value = f; _overlayStatus = label + " = " + f; }
            GUILayout.EndHorizontal();
        }

        // ------ kill-switch plumbing ------

        private void StopAllNpcActivity()
        {
            _running = false;
            try { TeardownBody(); } catch (Exception) { }
            try { _llmThread?.Join(800); } catch (Exception) { }
            overlayNote = "stopped";
        }

        private void StartLLMThread()
        {
            if (_llmThread != null && _llmThread.IsAlive) return;
            _running = true;
            _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM" };
            _llmThread.Start();
        }

        private string overlayNote = "";
    }
}
