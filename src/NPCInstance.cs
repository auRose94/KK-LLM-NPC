// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
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
        internal ConfigEntry<string> _cfgSystemPromptFile;
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
        internal ConfigEntry<float> _cfgVisTemperature;
        internal ConfigEntry<int> _cfgRayCount;
        internal ConfigEntry<float> _cfgRayRange;
        internal ConfigEntry<float> _cfgAutoFindRange;
        internal ConfigEntry<bool> _cfgDisableReagentMessages;
        internal ConfigEntry<int> _cfgImageSize;
        internal ConfigEntry<int> _cfgImageQuality;
        internal ConfigEntry<float> _cfgCamNearClip;
        internal ConfigEntry<float> _cfgCamFarClip;
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
        internal ConfigEntry<bool> _cfgPathEnabled;
        internal ConfigEntry<float> _cfgPathCell;
        internal ConfigEntry<float> _cfgPathSpan;
        internal ConfigEntry<int> _cfgPathCap;
        internal ConfigEntry<float> _cfgPathTimeBudget;
        internal ConfigEntry<int> _cfgPathMaxExpansions;
        internal ConfigEntry<bool> _cfgRadarEnabled;
        internal ConfigEntry<int> _cfgRadarSize;
        internal ConfigEntry<float> _cfgRadarScale;
        // AI feedback channel: report_issue(...) appends to a file when enabled.
        internal ConfigEntry<bool> _cfgAIReportLog;
        internal ConfigEntry<float> _cfgHornyRate;
        internal ConfigEntry<float> _cfgHornyBaseline;
        internal ConfigEntry<string> _cfgModelTier;
        internal ConfigEntry<bool> _cfgAutoSwitch;
        internal ConfigEntry<bool> _cfgNameSelection;
        internal ConfigEntry<int> _cfgFactDecay;
        internal ConfigEntry<bool> _cfgIdentityBot;
        internal ConfigEntry<string> _cfgIdentityAppId;
        internal ConfigEntry<float> _cfgFarmScanRadius;
        internal ConfigEntry<int> _cfgFarmScanMax;
        // ---- console (bash-like REPL) mode ----
        internal ConfigEntry<bool> _cfgConsoleEnabled;
        internal ConfigEntry<int> _cfgConsoleMaxRounds;
        internal ConfigEntry<int> _cfgConsoleMaxTokens;
        internal ConfigEntry<int> _cfgConsoleHistory;
        internal ConfigEntry<string> _cfgConsolePromptFile;

        // ---- runtime state ----
        private Thread _llmThread;
        private volatile bool _running;

        // Lifecycle: Time.unscaledTime when the plugin created this instance (the
        // pool reconciler uses it to retire unbound instances that never found a
        // target). Set from the constructor (main thread).
        internal float CreationTime;
        // True once this instance has possessed a body. After that, losing the body
        // (destroyed/sold/removed) retires the instance instead of re-possessing a
        // different kobold — the identity dies with the body.
        private bool _everBound;
        internal bool EverBound => _everBound;
        // Set when a bound body is gone (or we left the world): the plugin's
        // reconciler retires this instance on its next pass.
        internal volatile bool BodyLost;
        private bool _bodyLostLogged;

        // ---- agent registry (static: lets sibling NPCs see each other) ----
        // Bound body id → the chat name of the agent mind riding it. Maintained by
        // bind/unbind and identity finalization (names change after the LLM pick).
        private static readonly Dictionary<int, string> _agentNamesByBody = new Dictionary<int, string>();
        private static readonly object _agentNamesLock = new object();
        internal static void RegisterAgentBody(int koboldId, string name)
        {
            if (koboldId < 0 || string.IsNullOrEmpty(name)) return;
            lock (_agentNamesLock) _agentNamesByBody[koboldId] = name;
        }
        internal static void UnregisterAgentBody(int koboldId)
        {
            if (koboldId < 0) return;
            lock (_agentNamesLock) _agentNamesByBody.Remove(koboldId);
        }
        internal static string AgentNameForBody(int koboldId)
        {
            if (koboldId < 0) return null;
            lock (_agentNamesLock)
            {
                string n;
                return _agentNamesByBody.TryGetValue(koboldId, out n) ? n : null;
            }
        }
        // Which live body currently rides the given agent chat name (-1 = free) —
        // memory restore must not wake a name someone else is wearing right now.
        internal static int BodyIdForAgentName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            lock (_agentNamesLock)
            {
                foreach (var kv in _agentNamesByBody)
                    if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            return -1;
        }

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
        private float _lastOwnershipWarn = -99f; // warn every 30s while re-asserting every 3s
        private float _lastRelaunchTry;
        private float _lastNoBodyLog = -99f;

        // Current go_to destination.
        private Vector3? _navTarget;
        private string _navTargetName;

        // A* path for the active go_to: _navTarget points at _path[_pathIdx].
        private List<Vector3> _path;
        private int _pathIdx;
        private float _pathLastPlanTime = -999f;
        private Vector3 _pathGoal;
        private bool _pathGoalSet;
        // Set by the last FindPath when the planned route crosses a CLOSED door
        // (the path is still valid — the body opens it on arrival).
        internal bool _pathDoorBlocked;

        // FOLLOW MODE: stay within a band of the host player while the mind keeps
        // working. Player can trigger it by saying "follow"; the model can call
        // follow(on:true/false). Steering lives in FixedUpdateSafe (Movement.cs).
        internal volatile bool _followMode;
        internal float _followDist = -1f;
        internal float _followLastPath = -99f;

        // Set once FinalizeIdentity() has run (LLM-thread name selection + registry).
        internal bool _identityFinalized;

        // Stable per-session ids for nearby targets.
        private readonly Dictionary<int, int> _targetIdByInst = new Dictionary<int, int>();
        private readonly Dictionary<int, WeakReference> _targetRefByInst = new Dictionary<int, WeakReference>();
        private int _nextTargetId = 1;

        // Memory / history.
        private string _lastThought = "just woke up";
        private string _lastAction = "none";
        private int _tick;
        // Movement-phase state written on the main thread (FixedUpdate walk
        // validation), read on the LLM thread (perception/memory assembly).
        private volatile string _blockedInfo;
        // Structural reply failure signal (empty/plain-text/unknown tool in legacy
        // mode); volatile so ResetMind's cross-thread null write is safe too.
        private volatile string _modelError;

        // ---- endpoint circuit breaker (loop-level, outside SafeHttp's in-query
        // retries) ---- // consecutive failed chat queries; 0 = healthy. Written on
        // the LLM thread, read by the overlay (main) and the state anchor.
        private volatile int _endpointFails;
        // Don't issue a chat query until this Time.unscaledTime (escalating idle
        // after consecutive failures — a dead server otherwise costs a full
        // 3-attempt SafeHttp run every think tick forever).
        private volatile float _endpointBlockUntil;

        private int _lastCommentaryTick = -999;
        private float? _ledgeDrop;
        private string _npcName;
        private string _persona;

        // Chat.
        private string _playerChat;
        private float _playerChatTime;
        private string _lastDeliveredChat;
        // The game chat log as it read at the moment this NPC acquired its body ("awoke").
        // Repeat suppression for say: small models emit the exact same line several
        // turns in a row, spamming the in-game chat window with duplicates.
        private string _lastSayText;
        private float _lastSayTime = -99f;
        private readonly object _sayLock = new object();
        // Rolling list of last 3 say texts for similarity-based repeat detection.
        private readonly Queue<string> _lastSays = new Queue<string>();
        private const int MaxLastSays = 3;

        // Timestamped chat entries: (speaker, text, timeSeen). Feeds ChatLogJson()
        // with age info and ack tracking.
        private readonly List<ChatEntry> _chatEntries = new List<ChatEntry>();
        private const int MaxChatEntriesAge = 60; // seconds
        private const int MaxChatEntriesCount = 8;

        internal sealed class ChatEntry
        {
            internal string From;
            internal string Text;
            internal float Time;
            internal string AckId; // hash of from+text+t for dedup
        }

        // Ack tracking: seen chat entry IDs (cap 100). "new:true" until entry is seen.
        private readonly HashSet<string> _seenChatAcks = new HashSet<string>();
        private const int MaxSeenAcks = 100;

        // One-shot nudge: appended to next system prompt after say-repeat suppression.
        internal bool _pendingSayNudge;

        // Retry flag: set when LLM returns empty content (reasoning-only stream).
        // Next turn appends a hint to the user message so the model replies with content.
        internal bool _emptyContentRetry;

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

        // Long-term facts. Each fact carries the tick it was (re)remembered so we can
        // decay stale ones — the "forgetting" half of memory. Re-remembering a fact
        // (same category prefix) refreshes its age, so important facts the model keeps
        // asserting naturally outlive scratch notes.
        private sealed class FactRec { public string Text; public int Tick; }
        private readonly List<FactRec> _facts = new List<FactRec>();
        private const int FactCap = 24;

        // Penetration awareness.
        private class PenState { public string name; public float depth; public float lastT; public float vel; public string hole; public Transform src; }
        private readonly Dictionary<object, PenState> _pen = new Dictionary<object, PenState>();
        private readonly Dictionary<object, PenState> _dickIn = new Dictionary<object, PenState>();
        private ScriptableReagent _eggReagent;
        private readonly List<PenetrationTech.Penetrable> _subscribedPenetrables = new List<PenetrationTech.Penetrable>();
        private readonly List<KKPenetratorListener> _penListeners = new List<KKPenetratorListener>();

        // The thing currently making us feel good: a partner's name (kobold or
        // machine appendage) when penetrated/penetrating, else the station we last
        // used. Perception reports it as 'stim_from' so the model doesn't guess
        // "the player" every time arousal rises from a machine/station/egg-laying.
        private string _stimSource;
        private float _stimSourceT = -99f;

        // Station stay/exit tracking.
        // _stationPurpose: the classified type of station the NPC is in ("play", "bed", etc.)
        // _stationEntryTime: Time.unscaledTime when the NPC entered the station
        // _stayInStation: set by player chat command ("stay"/"remain"), cleared by "leave"/"exit"
        private string _stationPurpose;
        private float _stationEntryTime;
        private volatile bool _stayInStation;

        // Session clock: Time.unscaledTime when this NPC instance started.
        // Used to timestamp history entries and give the model a sense of duration.
        private float _sessionStartTime;

        // Dynamic context manager — tracks token pressure and applies compaction.
        private ContextManager _ctxMgr;

        // Optional second Photon client that gives the NPC its own room identity
        // (Multiplayer.IdentityBot, opt-in). Null until first needed.
        private NpcIdentityBot _identityBot;

        // Movement (LLM thread writes, FixedUpdate applies).
        private float _moveLocalZ;
        private float _moveLocalX;
        private float _moveTargetZ;
        private float _moveTargetX;
        private bool _moveJump;
        private float _moveUntilTime;
        private float _crouch = 0f;
        private float _manualCrouchSet = -99f;
        private float _clipSince = -99f;
        private float _lastClipFix;
        // Physics-tick throttles: the wall-proximity scan (4 rays) and the camera
        // clip probe (1 ray) run unconditionally per FixedUpdate otherwise — most
        // frames re-derive the same answer.
        private float _lastWallProbe;
        private float _lastClipCheck;
        private float _yawOffsetDeg;
        private float _pitchDeg;
        private float _yawDeg;
        // Plain monitor — hold times are nanoseconds and every site (LLM thread and
        // main thread) takes lock() on this object. Do NOT "upgrade" to
        // ReaderWriterLockSlim or mix mechanisms: EnterWriteLock guards an internal
        // RWLS lock, lock() takes the object's monitor — the two would exclude
        // nothing from each other and every lock() site would be unsynchronized.
        private readonly object _stateLock = new object();
        private float _lastBumpTime = -99f;
        // Written by wall-proximity scans on the main thread; surfaced into
        // perception/state lines that also run on the LLM thread.
        private volatile string _bumpInfo;
        private int _lastDoorTried;
        private float _lastDoorTryTime = -99f;
        // Activity tracking for dynamic think interval
        private float _lastMoveTime = -999f;
        // Perception throttling
        private int _lastFullPerceptionTick = -999;
        private object _cachedPerception;
        // Empty-reply tracking (aggregate spam, expose to history so the model self-corrects)
        private int _emptyReplies;
        private float _lastEmptyReplyLog = -99f;

        // Ambient / gaze.
        private float _lastAmbientTime = -99f;

        // ---- model tier helpers ----
        // Resolved tier: "small", "medium", "large". Auto-detection upgrades
        // from small→medium once we see the model produce valid JSON twice in a row.
        private string _resolvedTier;
        private readonly object _tierLock = new object(); // guards _resolvedTier writes
        private int _consecutiveValidJson;
        private bool IsSmallModel { get { EnsureTierResolved(); return _resolvedTier == "small"; } }
        private bool IsLargeModel { get { EnsureTierResolved(); return _resolvedTier == "large"; } }

        // ---- model family (auto-detected from the model name; no config) ----
        // Classifies the loaded model into a quirk profile (reasoning markers,
        // token headroom, payload tightness). Name source: probe → config →
        // generic. Benign race on lazy init: classification is deterministic and
        // idempotent, so double-compute across threads converges on the same
        // profile.
        private ModelFamilyProfile _familyProfile;
        private bool _familyResolved;
        internal ModelFamilyProfile FamilyProfile
        {
            get
            {
                if (!_familyResolved)
                {
                    _familyResolved = true;
                    string name = ModelProbe.DetectedModelName;
                    if (string.IsNullOrEmpty(name)) name = Val(_cfgModel);
                    _familyProfile = ModelFamilies.Classify(name);
                    Logger.LogInfo("[" + MyName() + "] model family '" + (string.IsNullOrEmpty(_familyProfile.Family) ? "generic" : _familyProfile.Family)
                        + "' from '" + (string.IsNullOrEmpty(name) ? "?" : name) + "' — " + _familyProfile.Note);
                }
                return _familyProfile;
            }
        }

        private void EnsureTierResolved()
        {
            if (_resolvedTier != null) return;
            lock (_tierLock)
            {
                if (_resolvedTier != null) return; // double-check after lock
                string raw = _cfgModelTier != null ? (_cfgModelTier.Value ?? "auto").ToLowerInvariant().Trim() : "auto";
                if (raw == "small" || raw == "medium" || raw == "large")
                {
                    _resolvedTier = raw;
                    Logger.LogInfo("[" + MyName() + "] model tier set to '" + raw + "' (manual config)");
                    return;
                }

                // auto: use probe results if available, else default to small
                if (ModelProbe.DetectedTier != null)
                {
                    _resolvedTier = ModelProbe.DetectedTier;
                    Logger.LogInfo("[" + MyName() + "] model tier resolved to '" + _resolvedTier + "' from probe ("
                        + (ModelProbe.DetectedModelName ?? "?") + ", " + ModelProbe.DetectedParameters + " params)");
                }
                else
                {
                    // No probe (server offline / name-only): trust the family
                    // classifier's sizing for known families, else default small.
                    string famTier = FamilyProfile.PreferredTier;
                    if (!string.IsNullOrEmpty(famTier))
                    {
                        _resolvedTier = famTier;
                        Logger.LogInfo("[" + MyName() + "] model tier '" + _resolvedTier + "' from family '" + FamilyProfile.Family + "'");
                    }
                    else
                    {
                        _resolvedTier = "small";
                        Logger.LogInfo("[" + MyName() + "] model tier defaulting to 'small' (no probe data — server offline?)");
                    }
                }
            }
        }

        // Call after a successfully-parsed act JSON to upgrade tier if auto.
        internal void NotifyValidActJson()
        {
            if (_resolvedTier != "small") return; // already resolved or explicitly set
            _consecutiveValidJson++;
            if (_consecutiveValidJson >= 3)
            {
                _resolvedTier = "medium";
                Logger.LogInfo("[" + MyName() + "] model tier auto-upgraded to medium (3 consecutive valid JSON responses).");
            }
        }

        internal void NotifyInvalidResponse()
        {
            // Reset the streak — the model struggled.
            _consecutiveValidJson = 0;
        }
        private float _ambientStimPrev = -1f;
        private float _gazeTimer;
        private float _gazeYaw;
        private float _gazePitch;
        private float _lastStim = -1f;
        private float _lastMoan;
        private float _lastGreetTime = -99f;
        private float _playerSeenTime = -99f;
        private float _lastStimLevel = -1f;

        // Slow-burn horniness: accumulates while the body gets no stimulation at
        // all, drains on climax, and drives 'needs.horniness' when game stim is 0.
        // Written on the main thread (FixedUpdate), read by perception on the LLM
        // thread — volatile float is 32-bit atomic.
        internal volatile float _horny = 0.08f;
        // Previous tick's stimulation, NORMALIZED 0-1 (see UpdateHorniness), and how
        // long stimulation has been sustained near max without a climax.
        private float _hornyPrevStim = 0f;
        private float _hornySaturatedFor;

        // Vision. Caption is written by the vision worker thread, read by the LLM
        // thread and state-anchor assembly.
        private string _sceneDesc = "unknown";
        private volatile string _lastVisionCaption = "";
        private volatile string _lastVisionB64;
        internal string VisionCaption { get { return _lastVisionCaption; } }
        internal string VisionFrameB64 { get { return _lastVisionB64; } }
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
        // Reusable constants to avoid magic numbers scattered across the codebase.
        internal const int MaxResponseSize = 512 * 1024;  // 512KB max HTTP response
        internal const int ColliderBufferSize = 32;
        internal const float VisionHungTimeout = 120f;    // 2 minutes
        internal const int ProbeRetries = 3;
        internal const int ProbeRetryDelayMs = 1000;

        // ------------------------------------------------------------------
        // constructor
        // ------------------------------------------------------------------
        internal NPCInstance(LLMNPCPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            Plugin = plugin;
            Logger = plugin.Logger;
            try { CreationTime = Time.unscaledTime; } catch (Exception) { CreationTime = 0f; }

            // Copy config entries so existing field-name references in partial class files just work.
            _cfgEndpoint = plugin._cfgEndpoint;
            _cfgModel = plugin._cfgModel;
            _cfgApiKey = plugin._cfgApiKey;
            _cfgSystem = plugin._cfgSystem;
            _cfgSystemPromptFile = plugin._cfgSystemPromptFile;
            _cfgThinkInterval = plugin._cfgThinkInterval;
            _cfgSendImage = plugin._cfgSendImage;
            _cfgImageEvery = plugin._cfgImageEvery;
            _cfgImageOnBump = plugin._cfgImageOnBump;
            _cfgImageOnTurn = plugin._cfgImageOnTurn;
            _cfgImageHistory = plugin._cfgImageHistory;
            _cfgMaxTokens = plugin._cfgMaxTokens;
            _cfgTemperature = plugin._cfgTemperature;
            _cfgStepDelay = plugin._cfgStepDelay;
            _cfgMaxPlan = plugin._cfgMaxPlan;
            _cfgVision = plugin._cfgVision;
            _cfgVisionEvery = plugin._cfgVisionEvery;
            _cfgVisionPrompt = plugin._cfgVisionPrompt;
            _cfgVisionDebug = plugin._cfgVisionDebug;
            _cfgVisModel = plugin._cfgVisModel;
            _cfgVisEndpoint = plugin._cfgVisEndpoint;
            _cfgVisApiKey = plugin._cfgVisApiKey;
            _cfgVisMaxTokens = plugin._cfgVisMaxTokens;
            _cfgVisTemperature = plugin._cfgVisTemperature;
            _cfgRayCount = plugin._cfgRayCount;
            _cfgRayRange = plugin._cfgRayRange;
            _cfgAutoFindRange = plugin._cfgAutoFindRange;
            _cfgDisableReagentMessages = plugin._cfgDisableReagentMessages;
            _cfgImageSize = plugin._cfgImageSize;
            _cfgImageQuality = plugin._cfgImageQuality;
            _cfgCamNearClip = plugin._cfgCamNearClip;
            _cfgCamFarClip = plugin._cfgCamFarClip;
            _cfgCamForward = plugin._cfgCamForward;
            _cfgBlockedScenes = plugin._cfgBlockedScenes;
            _cfgCommentEvery = plugin._cfgCommentEvery;
            _cfgCommentTemp = plugin._cfgCommentTemp;
            _cfgChatLogLines = plugin._cfgChatLogLines;
            _cfgStereo = plugin._cfgStereo;
            _cfgStereoIPD = plugin._cfgStereoIPD;
            _cfgTurnRate = plugin._cfgTurnRate;
            _cfgAccel = plugin._cfgAccel;
            _cfgDecel = plugin._cfgDecel;
            _cfgBrakeDist = plugin._cfgBrakeDist;
            _cfgPathEnabled = plugin._cfgPathEnabled;
            _cfgPathCell = plugin._cfgPathCell;
            _cfgPathSpan = plugin._cfgPathSpan;
            _cfgPathCap = plugin._cfgPathCap;
            _cfgPathTimeBudget = plugin._cfgPathTimeBudget;
            _cfgPathMaxExpansions = plugin._cfgPathMaxExpansions;
            _cfgRadarEnabled = plugin._cfgRadarEnabled;
            _cfgRadarSize = plugin._cfgRadarSize;
            _cfgRadarScale = plugin._cfgRadarScale;
            _cfgAIReportLog = plugin._cfgAIReportLog;
            _cfgHornyRate = plugin._cfgHornyRate;
            _cfgHornyBaseline = plugin._cfgHornyBaseline;
            _cfgModelTier = plugin._cfgModelTier;
            _cfgAutoSwitch = plugin._cfgAutoSwitch;
            _cfgNameSelection = plugin._cfgNameSelection;
            _cfgFactDecay = plugin._cfgFactDecay;
            _cfgIdentityBot = plugin._cfgIdentityBot;
            _cfgIdentityAppId = plugin._cfgIdentityAppId;
            _cfgFarmScanRadius = plugin._cfgFarmScanRadius;
            _cfgFarmScanMax = plugin._cfgFarmScanMax;
            _cfgConsoleEnabled = plugin._cfgConsoleEnabled;
            _cfgConsoleMaxRounds = plugin._cfgConsoleMaxRounds;
            _cfgConsoleMaxTokens = plugin._cfgConsoleMaxTokens;
            _cfgConsoleHistory = plugin._cfgConsoleHistory;
            _cfgConsolePromptFile = plugin._cfgConsolePromptFile;
        }

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        internal void Start()
        {
            _running = true;
            _sessionStartTime = Time.unscaledTime;
            _ctxMgr = new ContextManager(this);
            lock (_threadStartLock) StartThreadLocked();
            Logger.LogInfo("KKLLMNPC: LLM loop started for '" + (_npcName ?? "?") + "'.");
        }

        internal void Stop()
        {
            _running = false;
            try { StopIdentityBot(); } catch (Exception) { }
            try { TeardownBody(); } catch (Exception) { }
            try { CleanupVisionResources(); } catch (Exception) { }
            try
            {
                var t = _llmThread;
                if (t != null && t.IsAlive)
                {
                    if (!t.Join(3000))
                    {
                        try { t.Interrupt(); } catch (Exception) { }
                        t.Join(1000);
                    }
                }
            }
             catch (Exception) { }
            _llmThread = null;
        }

        // Retire this instance (its body was destroyed/sold/lost, or it never found
        // one). Like Stop() but WITHOUT joining the LLM thread: the reconciler calls
        // this from the main thread, and the LLM thread may be parked in a
        // RunOnMainThread call that can only complete after this returns — joining
        // it here would deadlock. The loop notices _running==false and exits on its
        // own (it's a background thread; the object is dropped afterwards).
        internal void Shutdown()
        {
            _running = false;
            try { StopIdentityBot(); } catch (Exception) { }
            try { TeardownBody(); } catch (Exception) { }
            try { CleanupVisionResources(); } catch (Exception) { }
            _llmThread = null;
        }

        internal bool IsThreadAlive => _running && _llmThread != null && _llmThread.IsAlive;

        // Does THIS live instance currently drive this body? (double-bind tripwire)
        internal bool HoldsBody(Kobold k)
        {
            if (!_running || k == null) return false;
            var b = _kobold;
            return b != null && b == k;
        }

        // One restart at a time, and NEVER a second thread while one is alive — two
        // LLM threads on one instance means two minds interleaving one conversation
        // and one body's tools (the "AI fighting itself / replying to its own past
        // lines" symptom). Both watchdog (Update) and overlay force-restart funnel
        // through this lock and re-check IsAlive under it.
        private readonly object _threadStartLock = new object();

        private bool StartThreadLocked()
        {
            if (!_running || (_llmThread != null && _llmThread.IsAlive)) return false;
            _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM-" + (_npcName ?? "??") };
            _llmThread.Start();
            return true;
        }

        // Force-restart the LLM thread (no cooldown) — used by the overlay kill-switch.
        internal void ForceRestartThread()
        {
            lock (_threadStartLock) StartThreadLocked();
        }

        internal void MaybeRestartThread()
        {
            if (!_running || (_llmThread != null && _llmThread.IsAlive)) return;
            if (Time.unscaledTime - _lastRelaunchTry <= 5f) return;
            lock (_threadStartLock)
            {
                if (!_running || (_llmThread != null && _llmThread.IsAlive)) return;
                _lastRelaunchTry = Time.unscaledTime;
                Logger.LogWarning("KKLLMNPC: LLM thread died — relaunching for '" + (_npcName ?? "?") + "'.");
                try { StartThreadLocked(); }
                catch (Exception e) { Logger.LogError("relaunch: " + e); }
            }
        }

        // ------------------------------------------------------------------
        // Vision resource cleanup (called from Stop())
        // ------------------------------------------------------------------
        private void CleanupVisionResources()
        {
            try
            {
                if (_cam != null)
                {
                    Destroy(_cam);
                    _cam = null;
                }
                if (_camR != null)
                {
                    Destroy(_camR);
                    _camR = null;
                }
                if (_rt != null)
                {
                    _rt.Release();
                    Destroy(_rt);
                    _rt = null;
                }
                if (_rtR != null)
                {
                    _rtR.Release();
                    Destroy(_rtR);
                    _rtR = null;
                }
                if (_texPoolL != null)
                {
                    Destroy(_texPoolL);
                    _texPoolL = null;
                }
                if (_texPoolR != null)
                {
                    Destroy(_texPoolR);
                    _texPoolR = null;
                }
                if (_texPoolStereo != null)
                {
                    Destroy(_texPoolStereo);
                    _texPoolStereo = null;
                }
                Logger.LogInfo("[" + MyName() + "] vision resources cleaned up.");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[" + MyName() + "] cleanup vision resources: " + e.Message);
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

        // ---- addressing (FT-02/FT-03) ----

        // Is this line addressed to ME? ("Fern, come here" / "Fern: hi" / "@Fern hey" /
        // "Fern hello"). Sets `rest` to the line with the address stripped so command
        // words inside ("follow me") still parse. Only fires at a word boundary —
        // "Ashtray, come here" doesn't address an NPC called Ash.
        internal bool IsAddressedBy(string msg, out string rest)
        {
            rest = msg;
            try
            {
                string name = MyName();
                if (string.IsNullOrEmpty(msg) || string.IsNullOrEmpty(name)) return false;
                string t = msg.TrimStart();
                if (t.Length > 0 && t[0] == '@') t = t.Substring(1).TrimStart();
                if (!t.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
                string tail = t.Substring(name.Length);
                if (tail.Length == 0) return false;
                char c0 = tail[0];
                if (c0 != ' ' && c0 != ':' && c0 != ',' && c0 != '-' && c0 != '!' && c0 != '.' && c0 != '?') return false;
                rest = tail.TrimStart(' ', ':', ',', '-');
                return true;
            }
            catch (Exception) { rest = msg; return false; }
        }

        // Cross-agent speech safety valve: another NPC's line reaches us only when
        // it addressed us AND at most twice a minute — two chatty NPCs can't duet
        // forever (each reply would be a stimulus for the next).
        private readonly List<float> _botChatDeliveries = new List<float>(4);
        internal bool BotChatAllowed()
        {
            lock (_botChatDeliveries)
            {
                float now = Time.unscaledTime;
                while (_botChatDeliveries.Count > 0 && now - _botChatDeliveries[0] > 60f) _botChatDeliveries.RemoveAt(0);
                if (_botChatDeliveries.Count >= 2) return false;
                _botChatDeliveries.Add(now);
                return true;
            }
        }

        // Direct injection of a player-chat line into THIS instance only (overlay /
        // addressed routing use it; unaddressed lines still broadcast).
        internal void InjectPlayerChat(string text)
        {
            try { HandleChat(text, "player", true); } catch (Exception) { }
        }

        internal void HandleChat(string msg, string senderName, bool isLocal)
        {
            // The game's chat can carry CRLF (Windows); trailing \r corrupts exact
            // compares (stay/leave detection) and pollutes the transcript feed.
            msg = (msg ?? "").Replace("\r", "").Trim();
            string heard = (isLocal ? "player" : (senderName ?? "someone")) + ": " + msg;
            _playerChat = heard;
            _playerChatTime = Time.unscaledTime;
            WakeFromSleep(); // being addressed ends `sleep <secs>` early
            Logger.LogInfo("heard chat: " + heard);
            RecordChatEntry(isLocal ? "player" : (senderName ?? "someone"), msg);

            // The player's current objective is ONLY ever in their chat (the game has
            // no quest/task system — verified in the game source) and the raw line
            // evaporates from context after ~30s. Persist the latest ask as a fact so
            // "can you help?" survives until it's handled.
            if (isLocal && !string.IsNullOrWhiteSpace(msg))
            {
                string t = msg.Trim();
                if (t.Length >= 4 && t.Length <= 140 && !t.StartsWith("/", StringComparison.Ordinal))
                    RememberFact("player_wants:" + t);
            }

            // Detect stay/leave commands from the player.
            if (isLocal && msg != null)
            {
                string lower = msg.ToLowerInvariant().Trim();
                if (lower == "stay" || lower == "remain" || lower == "don't leave"
                    || lower == "stay here" || lower == "stay in there" || lower == "stay in the machine"
                    || lower == "don't move" || lower == "hold position")
                {
                    _stayInStation = true;
                    Logger.LogInfo("[" + MyName() + "] player asked to stay in station.");
                }
                else if (lower == "leave" || lower == "exit" || lower == "get out"
                    || lower == "come out" || lower == "get off" || lower == "stop"
                    || lower == "leave the machine" || lower == "leave the station")
                {
                    _stayInStation = false;
                    if (IsInAnimationStation())
                    {
                        try { RunOnMainThreadAsync(() => { _photonView?.RPC("StopAnimationRPC", RpcTarget.All); }); }
                        catch (Exception e) { Logger.LogWarning("player leave: " + e.Message); }
                        _stationPurpose = null;
                        Logger.LogInfo("[" + MyName() + "] player asked to leave station.");
                    }
                }

                // FOLLOW: a state, not a tool call — the body stays near the host
                // while the model keeps thinking/talking. "follow me to the nest"
                // (the exact line from the field log) must also work.
                else if (lower.StartsWith("follow"))
                {
                    if (!_followMode)
                    {
                        _followMode = true;
                        _followLastPath = -99f;
                        Logger.LogInfo("[" + MyName() + "] player said '" + msg.Trim() + "' — follow mode ON (staying near the player)");
                    }
                }
                else if (lower.StartsWith("stop following") || lower.StartsWith("don't follow")
                    || lower.StartsWith("dont follow") || lower.StartsWith("release") || lower == "free")
                {
                    if (_followMode)
                    {
                        _followMode = false;
                        Logger.LogInfo("[" + MyName() + "] player said '" + msg.Trim() + "' — follow mode OFF");
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // camera rebuild on config change
        // ------------------------------------------------------------------
        internal void MaybeRebuildCamera()
        {
            if (_rt != null && _rt.width != _cfgImageSize.Value) SetupCamera();
            else if (_cam != null && !Mathf.Approximately(_cam.nearClipPlane, _cfgCamNearClip.Value)) SetupCamera();
            else if (_cam != null && !Mathf.Approximately(_cam.farClipPlane, _cfgCamFarClip != null ? _cfgCamFarClip.Value : 80f)) SetupCamera();
        }

        // ------------------------------------------------------------------
        // clear all memory on world reload
        // ------------------------------------------------------------------
        internal void ClearLogs() => ResetMind("world reloaded");

        // Queued when a mind wipe is requested while the LLM loop is mid-turn — the
        // loop applies it at the turn boundary (the only thread-safe place to clear
        // transcript/facts/lists it is actively reading). Volatile: main thread
        // writes, loop reads/clears.
        private volatile string _pendingMindReset;

        // Entry point for the two wipe callers (world reload, fresh-body bind) —
        // both on the main thread. Live LLM loop ⇒ defer to the turn boundary;
        // dead/no loop ⇒ apply immediately.
        internal void ResetMind(string why)
        {
            var t = _llmThread;
            if (t != null && t.IsAlive && _running)
            {
                if (string.IsNullOrEmpty(_pendingMindReset)) _pendingMindReset = why;
            }
            else ResetMindNow(why);
        }

        // Full memory/state wipe. Applied either directly (no live loop) or at the
        // LLM loop's turn boundary. A new body wakes up with NO inherited memory
        // unless the body move is an identity-reusing bodyswap: the identity dies
        // with the sold/lost body, exactly like the old body did.
        internal void ResetMindNow(string why)
        {
            lock (_stateLock) { _moveLocalZ = 0f; _moveJump = false; _moveUntilTime = 0f; _yawOffsetDeg = 0f; }
            lock (_history) { _history.Clear(); }
            lock (_thoughtHistory) { _thoughtHistory.Clear(); }
            lock (_facts) { _facts.Clear(); }
            _lastThought = "just woke up"; _lastAction = "none"; _tick = 0; _blockedInfo = null; _modelError = null;
            _playerChat = null; _lastDeliveredChat = null;
            lock (_chatEntries) { _chatEntries.Clear(); }
            _seenChatAcks.Clear();
            // Reset the console REPL conversation (fresh boot).
            _consoleMsgs = null;
            _consoleProseStreak = 0;
            _consoleEmptyStreak = 0;
            _consoleLastCmdKey = null;
            _consoleStuckCount = 0;
            if (_consoleRecentCmds != null) _consoleRecentCmds.Clear();
            _pendingShotB64 = null;
            _consoleSlept = false;
            _sleepUntilTime = 0f;
            _consoleAnswerDelivered = true;
            _pendingSayNudge = false;
            _emptyContentRetry = false;
            _lastSays.Clear();
            ResetCarrySense();
            ResetHoldSense();
            ClearRayHitCache(); // stale entity kinds/names must not survive the wipe
            BodyLost = false; _bodyLostLogged = false;
            lock (_goalLock)
            {
                _goal = null; _goalTick = -1; _goalRepeats = 0; _goalProgress = "";
                _recentlyDropped.Clear(); _lastThoughtText = null; _thoughtRepeat = 0;
            }
            _stationPurpose = null; _stationEntryTime = 0f; _stayInStation = false;
            _targetIdByInst.Clear();
            _targetRefByInst.Clear();
            _nextTargetId = 1;
            _sceneDesc = "unknown"; _lastVisionCaption = ""; _lastVisionB64 = null; _pastImages.Clear();
            _lastBellySummary = "";
            _cachedPerception = null;
            _lastFullPerceptionTick = -999;
            // World reload may mean a new room — drop the identity bot so it re-joins
            // fresh. Stop() touches Photon and can be called from the LLM loop thread
            // when applied at the turn boundary, so marshal it to the main thread.
            if (_identityBot != null)
            {
                var bot = _identityBot;
                _identityBot = null;
                RunOnMainThreadAsync(() => { try { bot.Stop(); } catch (Exception) { } });
            }
            try { Logger.LogInfo("KKLLMNPC: " + why + " — cleared NPC logs/memory."); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // NPC room identity (opt-in): lazily bring up the second Photon client so the
        // NPC's chat is attributed to the NPC, not the owner. No-op unless enabled and
        // the game is in a room.
        // ------------------------------------------------------------------
        internal void EnsureIdentityBot()
        {
            if (_cfgIdentityBot == null || !_cfgIdentityBot.Value) return;
            string roomName;
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null) return;
                // No real network = no room for a second client to join. Offline mode
                // ("offline room" — single-player before/without a Photon connection)
                // would have the bot spinning a fresh connection attempt per say,
                // forever logging "[identity-bot] starting" — the owner-attributed
                // fallback works fine offline, so just don't start the bot here.
                try
                {
                    if (PhotonNetwork.OfflineMode || !PhotonNetwork.IsConnectedAndReady) return;
                    roomName = PhotonNetwork.CurrentRoom.Name;
                    if (string.IsNullOrEmpty(roomName)) return;
                    if (string.Equals(roomName, "offline room", StringComparison.OrdinalIgnoreCase)) return;
                }
                catch (Exception) { return; }
                if (_identityBot != null)
                {
                    if (_identityBot.RoomName != roomName)
                    {
                        // Room changed (rejoin / new room) — the bot is bound to a stale room;
                        // restart it fresh instead of letting it keep chatting into the old one.
                        try { _identityBot.Stop(); } catch (Exception) { }
                        _identityBot = null;
                    }
                    else if (_identityBot.InRoom) return; // healthy for this room
                }
                if (_identityBot == null) _identityBot = new NpcIdentityBot(this);
                _identityBot.EnsureStarted(roomName, MyName());
            }
            catch (Exception e) { try { Logger.LogWarning("identity bot: " + e.Message); } catch (Exception) { } }
        }

        // Stop the identity bot (teardown). Safe to call repeatedly.
        internal void StopIdentityBot()
        {
            if (_identityBot == null) return;
            try { _identityBot.Stop(); } catch (Exception) { }
            _identityBot = null;
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

        // Internal accessors for the overlay (partial class on LLMNPCPlugin).
        internal bool IsAliveObj(UnityEngine.Object o) => IsAlive(o);
        internal string GetMyName() => MyName();
        internal string GetRecentPlayerChat() => RecentPlayerChat();

        // Field accessors for overlay debug display.
        internal bool Running => _running;
        internal void SetRunning(bool v) { _running = v; }
        internal UnityEngine.Object KoboldObj => _kobold;
        internal bool VisionBusy => _visionBusy;
        internal string BlockedInfo => _blockedInfo;
        internal string BumpInfo => _bumpInfo;
        internal float YawDeg => _yawDeg;
        internal float PitchDeg => _pitchDeg;
        internal string LastThought => _lastThought;
        internal string LastAction => _lastAction;

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
            float elapsed = Time.unscaledTime - _sessionStartTime;
            int h = (int)(elapsed / 3600f);
            int m = (int)((elapsed % 3600f) / 60f);
            int s = (int)(elapsed % 60f);
            string ts = string.Format("{0:D2}:{1:D2}:{2:D2}", h, m, s);
            string entry = ts + " " + action + (string.IsNullOrEmpty(summary) ? "" : "->" + summary);
            lock (_history)
            {
                // Deduplicate consecutive say actions to prevent echo loops.
                if (action == "say" && _history.Count > 0 && _history.Last.Value.Contains("say->"))
                    _history.RemoveLast();
                _history.AddLast(entry);
                while (_history.Count > MaxHistory) _history.RemoveFirst();
            }
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
            lock (_history)
            {
                if (_history.Count == 0) return "[]";
                // Level 3+ compaction: everything older than the dynamic recent
                // window collapses into "(earlier: Nx action)" counts (the tested
                // ContextCompaction policy) instead of just taking the newest N.
                if (_ctxMgr != null && _ctxMgr.CompactionLevel >= 3)
                {
                    var snapshot = new List<string>();
                    foreach (var h in _history) snapshot.Add(h);
                    return ContextCompaction.SummarizeHistory(snapshot, Math.Max(2, MaxHistory / 2));
                }
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
        }

        private void PushThought(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            lock (_thoughtHistory)
            {
                if (_thoughtHistory.Count == 0 || _thoughtHistory.Last.Value != t)
                    _thoughtHistory.AddLast(t);
                while (_thoughtHistory.Count > MaxThoughts) _thoughtHistory.RemoveFirst();
            }
        }

        // ------------------------------------------------------------------
        // Context compaction — dynamic limits based on model tier and compaction level.
        // Small models (≤13B) have tight context windows; large models can afford more.
        // The ContextManager escalates compaction when context pressure builds.
        // ------------------------------------------------------------------
        private int MaxHistory { get { return _ctxMgr != null ? _ctxMgr.DynamicMaxHistory(IsSmallModel ? 5 : IsLargeModel ? 15 : 10) : (IsSmallModel ? 5 : IsLargeModel ? 15 : 10); } }
        private int MaxThoughts { get { return _ctxMgr != null ? _ctxMgr.DynamicMaxThoughts(IsSmallModel ? 3 : IsLargeModel ? 8 : 6) : (IsSmallModel ? 3 : IsLargeModel ? 8 : 6); } }
        private int MaxFacts { get { return _ctxMgr != null ? _ctxMgr.DynamicMaxFacts(IsSmallModel ? 10 : IsLargeModel ? 30 : 20) : (IsSmallModel ? 10 : IsLargeModel ? 30 : 20); } }
        private int MaxChatLog { get { return IsSmallModel ? 5 : IsLargeModel ? 30 : 15; } }

        private string ThoughtHistoryJson()
        {
            lock (_thoughtHistory)
            {
                if (_thoughtHistory.Count == 0) return "[]";
                // For small models, drop oldest thoughts beyond the cap.
                int keep = MaxThoughts;
                var sb = new StringBuilder("[");
                bool first = true;
                int skip = Math.Max(0, _thoughtHistory.Count - keep);
                int i = 0;
                foreach (var t in _thoughtHistory)
                {
                    if (i++ < skip) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Json.Write(t));
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        private string FactsJson()
        {
            // Decay window in ticks (0 = off). Stale facts that the model hasn't
            // re-asserted fall out of context, so finished business stops lingering.
            int decay = _cfgFactDecay != null ? _cfgFactDecay.Value : 0;
            lock (_facts)
            {
                // Age out stale facts first (forgetting), then cap by recency.
                if (decay > 0)
                {
                    for (int i = _facts.Count - 1; i >= 0; i--)
                        if (_tick - _facts[i].Tick > decay) _facts.RemoveAt(i);
                }
                if (_facts.Count == 0) return "[]";
                int cap = MaxFacts;
                // At compaction level 3+, merge facts with the same category prefix.
                List<FactRec> source = _facts;
                if (_ctxMgr != null && _ctxMgr.CompactionLevel >= 3)
                {
                    var mergedTexts = ContextCompaction.MergeFacts(_facts.ConvertAll(f => f.Text));
                    source = new List<FactRec>();
                    for (int i = 0; i < mergedTexts.Count; i++)
                        source.Add(new FactRec { Text = mergedTexts[i], Tick = _tick });
                }
                // Most recent first, take `cap`.
                var ordered = new List<FactRec>(source);
                ordered.Sort((a, b) => b.Tick.CompareTo(a.Tick));
                if (ordered.Count > cap) ordered = ordered.GetRange(0, cap);
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var f in ordered)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Json.Write(f.Text));
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        private void RememberFact(string fact)
        {
            if (string.IsNullOrWhiteSpace(fact)) return;
            string f = Sanitize(fact.Trim());
            lock (_facts)
            {
                // Deduplicate: replace if same prefix (category) already known, and
                // refresh its age so an actively-used fact doesn't decay out.
                string prefix = f.Split(':')[0];
                for (int i = 0; i < _facts.Count; i++)
                    if (_facts[i].Text.Split(':')[0] == prefix)
                    { _facts[i].Text = f; _facts[i].Tick = _tick; return; }
                _facts.Add(new FactRec { Text = f, Tick = _tick });
                // Hard cap — evict the OLDEST (by age) so recents survive.
                while (_facts.Count > FactCap)
                {
                    int oldest = 0;
                    for (int i = 1; i < _facts.Count; i++)
                        if (_facts[i].Tick < _facts[oldest].Tick) oldest = i;
                    _facts.RemoveAt(oldest);
                }
            }
        }

        // ------------------------------------------------------------------
        // Destroy helper (NPCInstance is not a MonoBehaviour)
        // ------------------------------------------------------------------
        private static void Destroy(UnityEngine.Object obj)
        {
            try { if (obj != null) UnityEngine.Object.Destroy(obj); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // Reflection helper for reading private/protected game fields.
        // Used by DescribeNearby to detect ConstructionContract.bought, UsableMachine.constructed, etc.
        // ------------------------------------------------------------------
        internal static T GetField<T>(object obj, string name)
        {
            if (obj == null) return default(T);
            var type = obj.GetType();
            var f = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f == null) return default(T);
            try { return (T)f.GetValue(obj); } catch { return default(T); }
        }

        // MainReady exposed from plugin for LLM loop / vision checks.
        internal bool MainReady => Plugin._mainReady;
    }
}
