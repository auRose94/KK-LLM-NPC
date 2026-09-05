// NPCInstance — per-NPC state and behavior for a single LLM-driven kobold.
// One plugin manages many NPCInstances; each owns its kobold, camera, LLM thread, and memory.
//
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
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
        // Reference to the parent plugin for shared resources (config, main-thread, logger).
        internal readonly LLMNPCPlugin Plugin;
        internal readonly ManualLogSource Logger;

        // ---- config (copied from plugin so existing field-name references just work) ----
        internal ConfigEntry<string> _cfgEndpoint;
        internal ConfigEntry<string> _cfgModel;
        internal ConfigEntry<string> _cfgApiKey;
        internal ConfigEntry<string> _cfgSystem;
        internal ConfigEntry<float> _cfgThinkInterval;
        internal ConfigEntry<bool> _cfgSendImage;
        internal ConfigEntry<int> _cfgImageEvery;
        internal ConfigEntry<bool> _cfgImageOnBump;
        internal ConfigEntry<bool> _cfgImageOnTurn;
        internal ConfigEntry<int> _cfgImageHistory;
        internal ConfigEntry<int> _cfgMaxTokens;
        internal ConfigEntry<float> _cfgTemperature;
        internal ConfigEntry<float> _cfgStepDelay;
        internal ConfigEntry<int> _cfgMaxPlan;
        internal ConfigEntry<bool> _cfgVision;
        internal ConfigEntry<int> _cfgVisionEvery;
        internal ConfigEntry<string> _cfgVisionPrompt;
        internal ConfigEntry<bool> _cfgVisionDebug;
        internal ConfigEntry<string> _cfgVisModel;
        internal ConfigEntry<string> _cfgVisEndpoint;
        internal ConfigEntry<string> _cfgVisApiKey;
        internal ConfigEntry<int> _cfgVisMaxTokens;
        internal ConfigEntry<int> _cfgRayCount;
        internal ConfigEntry<float> _cfgRayRange;
        internal ConfigEntry<float> _cfgAutoFindRange;
        internal ConfigEntry<int> _cfgImageSize;
        internal ConfigEntry<int> _cfgImageQuality;
        internal ConfigEntry<float> _cfgCamNearClip;
        internal ConfigEntry<float> _cfgCamForward;
        internal ConfigEntry<string> _cfgBlockedScenes;
        internal ConfigEntry<int> _cfgCommentEvery;
        internal ConfigEntry<float> _cfgCommentTemp;
        internal ConfigEntry<int> _cfgChatLogLines;
        internal ConfigEntry<bool> _cfgStereo;
        internal ConfigEntry<float> _cfgStereoIPD;
        internal ConfigEntry<float> _cfgTurnRate;
        internal ConfigEntry<float> _cfgAccel;
        internal ConfigEntry<float> _cfgDecel;
        internal ConfigEntry<float> _cfgBrakeDist;

        // ---- runtime state ----
        private Thread _llmThread;
        private volatile bool _running;

        // The possessed body + its drivable parts.
        private int _currentKoboldId = -1;
        private Kobold _kobold;
        private KoboldCharacterController _controller;
        private CharacterDescriptor _descriptor;
        private CharacterControllerAnimator _charAnimator;
        private Grabber _grabber;
        private Transform _head;
        private Camera _cam;
        private RenderTexture _rt;
        private Camera _camR;
        private RenderTexture _rtR;
        private PhotonView _photonView;
        private float _lastOwnershipTry;
        private float _lastRelaunchTry;

        // Current go_to destination.
        private Vector3? _navTarget;
        private string _navTargetName;

        // Stable per-session ids for nearby targets.
        private readonly Dictionary<int, int> _targetIdByInst = new Dictionary<int, int>();
        private readonly Dictionary<int, WeakReference> _targetRefByInst = new Dictionary<int, WeakReference>();
        private int _nextTargetId = 1;

        // Memory / history.
        private string _lastThought = "just woke up";
        private string _lastAction = "none";
        private int _tick;
        private string _blockedInfo;
        private string _modelError;
        private int _lastCommentaryTick = -999;
        private float? _ledgeDrop;
        private string _npcName;
        private string _persona;

        // Chat.
        private string _playerChat;
        private float _playerChatTime;
        private string _lastDeliveredChat;

        // Reagent / belly awareness.
        private readonly Queue<string> _reagentEvents = new Queue<string>();
        private GenericReagentContainer _bellySubscribed;
        private bool _bellySnapshotPending;
        private string _lastBellySummary = "";

        // Short-term history.
        private readonly LinkedList<string> _history = new LinkedList<string>();
        private const int HistoryLen = 10;
        private readonly LinkedList<string> _thoughtHistory = new LinkedList<string>();
        private const int ThoughtHistoryLen = 6;

        // Long-term facts.
        private readonly List<string> _facts = new List<string>();
        private const int FactCap = 24;

        // Penetration awareness.
        private class PenState { public string name; public float depth; public float lastT; public float vel; public string hole; public Transform src; }
        private readonly Dictionary<object, PenState> _pen = new Dictionary<object, PenState>();
        private readonly Dictionary<object, PenState> _dickIn = new Dictionary<object, PenState>();
        private ScriptableReagent _eggReagent;
        private readonly List<PenetrationTech.Penetrable> _subscribedPenetrables = new List<PenetrationTech.Penetrable>();
        private readonly List<KKPenetratorListener> _penListeners = new List<KKPenetratorListener>();

        // Movement (LLM thread writes, FixedUpdate applies).
        private float _moveLocalZ;
        private float _moveLocalX;
        private float _moveTargetZ;
        private float _moveTargetX;
        private bool _moveJump;
        private bool _moveRun;
        private float _moveUntilTime;
        private float _crouch = 0f;
        private float _manualCrouchSet = -99f;
        private float _clipSince = -99f;
        private float _lastClipFix;
        private float _yawOffsetDeg;
        private float _pitchDeg;
        private float _yawDeg;
        private readonly object _stateLock = new object();
        private float _lastBumpTime = -99f;
        private string _bumpInfo;
        private int _lastDoorTried;
        private float _lastDoorTryTime = -99f;
        // Activity tracking for dynamic think interval
        private float _lastMoveTime = -999f;
        private bool _wasMovingLastTick;
        // Perception throttling
        private int _lastFullPerceptionTick = -999;
        private object _cachedPerception;
        // Nearby cache
        private List<object> _cachedNearby;
        private int _lastNearbyTick = -999;
        // Empty-reply tracking (aggregate spam, expose to history so the model self-corrects)
        private int _emptyReplies;
        private float _lastEmptyReplyLog = -99f;

        // Ambient / gaze.
        private float _lastAmbientTime = -99f;
        private float _ambientStimPrev = -1f;
        private float _gazeTimer;
        private float _gazeYaw;
        private float _gazePitch;
        private float _lastStim = -1f;
        private float _lastMoan;
        private float _lastGreetTime = -99f;
        private float _playerSeenTime = -99f;
        private float _lastStimLevel = -1f;

        // Vision.
        private string _sceneDesc = "unknown";
        private string _lastVisionCaption = "";
        private volatile string _lastVisionB64;
        private readonly System.Collections.Generic.List<string> _pastImages = new System.Collections.Generic.List<string>();
        // Texture pool for capture
        private Texture2D _texPoolL;
        private Texture2D _texPoolR;
        private Texture2D _texPoolStereo;
        private class VisionSteer { public float deg; public string reason; }
        private volatile VisionSteer _visionSteer;
        private volatile bool _visionBusy;
        private float _visionStartTime;
        private int _lastVisionTick = -999;
        private bool _visionModelLoggedOnce;
        private int _visionShot;
        private volatile bool _needImageAfterBump;
        private float _lastBigTurnTime = -99f;

        // LLM async (ask tool).
        private volatile string _pendingQuestion;
        private volatile string _lastAnswer;
        private volatile bool _answerBusy;

        // Constants.
        private const float WalkProbeRange = 4.0f;
        private const float InteractRange = 2.6f;

        // ------------------------------------------------------------------
        // constructor
        // ------------------------------------------------------------------
        internal NPCInstance(LLMNPCPlugin plugin)
        {
            Plugin = plugin;
            Logger = plugin.Logger;

            // Copy config entries so existing field-name references in partial class files just work.
            _cfgEndpoint       = plugin._cfgEndpoint;
            _cfgModel          = plugin._cfgModel;
            _cfgApiKey         = plugin._cfgApiKey;
            _cfgSystem         = plugin._cfgSystem;
            _cfgThinkInterval  = plugin._cfgThinkInterval;
            _cfgSendImage      = plugin._cfgSendImage;
            _cfgImageEvery     = plugin._cfgImageEvery;
            _cfgImageOnBump    = plugin._cfgImageOnBump;
            _cfgImageOnTurn    = plugin._cfgImageOnTurn;
            _cfgImageHistory   = plugin._cfgImageHistory;
            _cfgMaxTokens      = plugin._cfgMaxTokens;
            _cfgTemperature    = plugin._cfgTemperature;
            _cfgStepDelay      = plugin._cfgStepDelay;
            _cfgMaxPlan        = plugin._cfgMaxPlan;
            _cfgVision         = plugin._cfgVision;
            _cfgVisionEvery    = plugin._cfgVisionEvery;
            _cfgVisionPrompt   = plugin._cfgVisionPrompt;
            _cfgVisionDebug    = plugin._cfgVisionDebug;
            _cfgVisModel       = plugin._cfgVisModel;
            _cfgVisEndpoint    = plugin._cfgVisEndpoint;
            _cfgVisApiKey      = plugin._cfgVisApiKey;
            _cfgVisMaxTokens   = plugin._cfgVisMaxTokens;
            _cfgRayCount       = plugin._cfgRayCount;
            _cfgRayRange       = plugin._cfgRayRange;
            _cfgAutoFindRange  = plugin._cfgAutoFindRange;
            _cfgImageSize      = plugin._cfgImageSize;
            _cfgImageQuality   = plugin._cfgImageQuality;
            _cfgCamNearClip    = plugin._cfgCamNearClip;
            _cfgCamForward     = plugin._cfgCamForward;
            _cfgBlockedScenes  = plugin._cfgBlockedScenes;
            _cfgCommentEvery   = plugin._cfgCommentEvery;
            _cfgCommentTemp    = plugin._cfgCommentTemp;
            _cfgChatLogLines   = plugin._cfgChatLogLines;
            _cfgStereo         = plugin._cfgStereo;
            _cfgStereoIPD      = plugin._cfgStereoIPD;
            _cfgTurnRate       = plugin._cfgTurnRate;
            _cfgAccel          = plugin._cfgAccel;
            _cfgDecel          = plugin._cfgDecel;
            _cfgBrakeDist      = plugin._cfgBrakeDist;
        }

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        internal void Start()
        {
            _running = true;
            _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM-" + (_npcName ?? "??") };
            _llmThread.Start();
            Logger.LogInfo("KKLLMNPC: LLM loop started for '" + (_npcName ?? "?") + "'.");
        }

        internal void Stop()
        {
            _running = false;
            try { TeardownBody(); } catch (Exception) { }
            try { _llmThread?.Join(1500); } catch (Exception) { }
        }

        internal bool IsThreadAlive => _running && _llmThread != null && _llmThread.IsAlive;

        internal void MaybeRestartThread()
        {
            if (!_running || (_llmThread != null && _llmThread.IsAlive)) return;
            if (Time.unscaledTime - _lastRelaunchTry > 5f)
            {
                _lastRelaunchTry = Time.unscaledTime;
                Logger.LogWarning("KKLLMNPC: LLM thread died — relaunching for '" + (_npcName ?? "?") + "'.");
                try
                {
                    _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM-" + (_npcName ?? "??") };
                    _llmThread.Start();
                }
                catch (Exception e) { Logger.LogError("relaunch: " + e); }
            }
        }

        // ------------------------------------------------------------------
        // chat event handling (called from plugin's OnEvent)
        // ------------------------------------------------------------------
        internal bool IsMySpeech(string msg)
        {
            string myPrefix = MyName() + ":";
            return msg.StartsWith(myPrefix, StringComparison.OrdinalIgnoreCase);
        }

        internal void HandleChat(string msg, string senderName, bool isLocal)
        {
            string heard = (isLocal ? "player" : (senderName ?? "someone")) + ": " + msg;
            _playerChat = heard;
            _playerChatTime = Time.unscaledTime;
            Logger.LogInfo("heard chat: " + heard);
        }

        // ------------------------------------------------------------------
        // camera rebuild on config change
        // ------------------------------------------------------------------
        internal void MaybeRebuildCamera()
        {
            if (_rt != null && _rt.width != _cfgImageSize.Value) SetupCamera();
            else if (_cam != null && !Mathf.Approximately(_cam.nearClipPlane, _cfgCamNearClip.Value)) SetupCamera();
        }

        // ------------------------------------------------------------------
        // clear all memory on world reload
        // ------------------------------------------------------------------
        internal void ClearLogs()
        {
            lock (_stateLock) { _moveLocalZ = 0f; _moveJump = false; _moveUntilTime = 0f; _yawOffsetDeg = 0f; }
            _history.Clear();
            _thoughtHistory.Clear();
            _facts.Clear();
            _lastThought = "just woke up"; _lastAction = "none"; _tick = 0; _blockedInfo = null; _modelError = null;
            _playerChat = null; _lastDeliveredChat = null;
            _targetIdByInst.Clear();
            _targetRefByInst.Clear();
            _nextTargetId = 1;
            _sceneDesc = "unknown"; _lastVisionCaption = ""; _lastVisionB64 = null; _pastImages.Clear();
            _lastBellySummary = "";
            try { Logger.LogInfo("KKLLMNPC: world reloaded — cleared NPC logs/memory."); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // FixedUpdateSafe: movement physics (called from plugin's FixedUpdate)
        // ------------------------------------------------------------------
        // This is defined in Movement.cs partial class.
        partial void FixedUpdateSafeInit();

        // ------------------------------------------------------------------
        // delegated utilities (shared with plugin)
        // ------------------------------------------------------------------
        internal object RunOnMainThread(Func<object> fn, int timeoutMs = 120000) => Plugin.RunOnMainThread(fn, timeoutMs);
        internal void RunOnMainThreadAsync(Action fn) => Plugin.RunOnMainThreadAsync(fn);
        internal bool IsPlayableScene() => Plugin.IsPlayableScene();

        private static string Val(ConfigEntry<string> e)
        {
            return string.IsNullOrWhiteSpace(e.Value) ? (string)e.DefaultValue : e.Value;
        }

        private string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool IsAlive(UnityEngine.Object o) => o != null;

        private static float MoveAngleTowards(float current, float target, float maxDelta)
        {
            float d = Mathf.Repeat(target - current + 180f, 360f) - 180f;
            if (Mathf.Abs(d) <= maxDelta) return target;
            return Mathf.Repeat(current + Mathf.Sign(d) * maxDelta, 360f);
        }

        private static bool RtCreated(RenderTexture rt)
        {
            try { return rt != null && rt.width > 0 && rt.height > 0; }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------
        // memory (formerly on plugin, moved here for multi-NPC)
        // ------------------------------------------------------------------
        private int TargetIdFor(Transform root, string name)
        {
            if (root == null) return -1;
            int key = root.GetInstanceID();
            int id;
            if (_targetIdByInst.TryGetValue(key, out id))
            {
                _targetRefByInst[key] = new WeakReference(root);
                return id;
            }
            id = _nextTargetId++;
            _targetIdByInst[key] = id;
            _targetRefByInst[key] = new WeakReference(root);
            return id;
        }

        private Transform FindTargetById(int id)
        {
            foreach (var kv in _targetIdByInst)
            {
                if (kv.Value != id) continue;
                WeakReference wr;
                if (!_targetRefByInst.TryGetValue(kv.Key, out wr)) return null;
                var t = wr.Target as Transform;
                if (t == null) return null;
                try { if (t.name == null) return null; } catch (Exception) { return null; }
                if (t.GetInstanceID() != kv.Key) return null;
                return t;
            }
            return null;
        }

        private void PushHistory(string action, string summary)
        {
            if (string.IsNullOrEmpty(action) || action == "none") return;
            string entry = action + (string.IsNullOrEmpty(summary) ? "" : "->" + summary);
            // Deduplicate consecutive say actions to prevent echo loops.
            if (action == "say" && _history.Count > 0 && _history.Last.Value.StartsWith("say->", StringComparison.Ordinal))
                _history.RemoveLast();
            _history.AddLast(entry);
            while (_history.Count > HistoryLen) _history.RemoveFirst();
        }

        // Records a structural failure of the model's reply (empty/plain-text/unknown
        // tool) so the NEXT perception carries a prominent 'model_error' field showing
        // the model exactly what it did wrong. Cleared once it produces a valid act.
        private void SetModelError(string msg)
        {
            _modelError = msg;
            try { Logger.LogWarning("model error queued: " + (msg.Length > 140 ? msg.Substring(0, 140) + "..." : msg)); } catch (Exception) { }
        }

        private string HistoryJson()
        {
            if (_history.Count == 0) return "[]";
            var parts = new StringBuilder("[");
            bool first = true;
            foreach (var h in _history)
            {
                if (!first) parts.Append(',');
                first = false;
                parts.Append(Json.Write(h));
            }
            parts.Append(']');
            return parts.ToString();
        }

        private void PushThought(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            if (_thoughtHistory.Count == 0 || _thoughtHistory.Last.Value != t)
                _thoughtHistory.AddLast(t);
            while (_thoughtHistory.Count > ThoughtHistoryLen) _thoughtHistory.RemoveFirst();
        }

        private string ThoughtHistoryJson()
        {
            if (_thoughtHistory.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var t in _thoughtHistory)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Write(t));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private string FactsJson()
        {
            if (_facts.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var f in _facts)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Write(f));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private void RememberFact(string fact)
        {
            if (string.IsNullOrWhiteSpace(fact)) return;
            string f = Sanitize(fact.Trim());
            for (int i = 0; i < _facts.Count; i++)
                if (_facts[i].Split(':')[0] == f.Split(':')[0]) { _facts[i] = f; return; }
            _facts.Add(f);
            while (_facts.Count > FactCap) _facts.RemoveAt(0);
        }

        // ------------------------------------------------------------------
        // Destroy helper (NPCInstance is not a MonoBehaviour)
        // ------------------------------------------------------------------
        private static void Destroy(UnityEngine.Object obj)
        {
            try { if (obj != null) UnityEngine.Object.Destroy(obj); } catch (Exception) { }
        }

        // MainReady exposed from plugin for LLM loop / vision checks.
        internal bool MainReady => Plugin._mainReady;
    }
}
