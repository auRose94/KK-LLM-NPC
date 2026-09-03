// KKLLMNPC — a BepInEx plugin for KoboldKare that lets an LLM embody and play
// as an unoccupied Kobold NPC.
//
// The plugin runs inside the game process. It:
//   1. Hijacks the nearest wild (AIPlayer) Kobold, takes Photon ownership and
//      suppresses its built-in wander/look AI.
//   2. Gives the LLM two senses:
//        - a frustum fan of raycasts around the kobold's facing  (structure)
//        - a first-person camera render read back as a base64 PNG (vision)
//   3. Reports kobold stats/genes/energy + world position.
//   4. Exposes tool commands (move/turn/jump/look/interact/grab/drop/eat...)
//      by driving the same KoboldCharacterController/User/Grabber the local
//      player uses, so movement & interaction behave exactly like a player.
//   5. Talks to an OpenAI-compatible chat-completions endpoint with tool
//      calling: it pushes perceptions and executes returned tool_calls in a
//      loop on its own thread, so the LLM continuously plays the NPC.
//
// Build against BepInEx + UnityEngine + Photon + Assembly-CSharp (see build.sh).
// Drop the DLL into <game>/BepInEx/plugins/ and configure the endpoint in
// BepInEx/config/com.kk.llmnpc.cfg after first launch.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Photon.Pun;
using Photon.Realtime;

namespace KKLLMNPC
{
    [BepInPlugin("com.kk.llmnpc", "KKLLMNPC", "1.0.0")]
    public partial class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
    {
        // ---- config ----
        private ConfigEntry<string> _cfgEndpoint;

        private ConfigEntry<string> _cfgModel;

        private ConfigEntry<string> _cfgApiKey;

        private ConfigEntry<string> _cfgSystem;

        private ConfigEntry<float>  _cfgThinkInterval;

        private ConfigEntry<bool>   _cfgSendImage;      // attach first-person frame to action call sometimes

        private ConfigEntry<int>    _cfgImageEvery;

        private ConfigEntry<bool>   _cfgImageOnBump;    // attach a frame right after we bump/get blocked

        private ConfigEntry<bool>   _cfgImageOnTurn;    // attach a frame after a large look/turn

        private ConfigEntry<int>    _cfgMaxTokens;

        private ConfigEntry<float>  _cfgTemperature;

        private ConfigEntry<float>  _cfgStepDelay;   // seconds between actions in a plan

        private ConfigEntry<int>    _cfgMaxPlan;     // max chained actions per response

        private ConfigEntry<bool>   _cfgVision;      // background vision captioning

        private ConfigEntry<int>    _cfgVisionEvery; // caption every N action ticks

        private ConfigEntry<string> _cfgVisionPrompt;

        private ConfigEntry<bool>   _cfgVisionDebug;

        // Separate (vision-capable) model for captioning; blank = use main LLM cfg.
        private ConfigEntry<string> _cfgVisModel;

        private ConfigEntry<string> _cfgVisEndpoint;

        private ConfigEntry<string> _cfgVisApiKey;

        private ConfigEntry<int>    _cfgVisMaxTokens;

        private ConfigEntry<int>    _cfgRayCount;

        private ConfigEntry<float>  _cfgRayRange;

        private ConfigEntry<float>  _cfgAutoFindRange;

        private ConfigEntry<int>    _cfgImageSize;

        private ConfigEntry<float>  _cfgCamNearClip;

        private ConfigEntry<float>  _cfgCamForward;

        private ConfigEntry<string> _cfgBlockedScenes;

        private string _lastSceneName;


        // ---- runtime state (main thread) ----
        private SynchronizationContext _mainContext;

        private Thread _llmThread;

        private volatile bool _running;


        // The possessed body + its drivable parts.
        private Kobold _kobold;

        private KoboldCharacterController _controller;

        private CharacterDescriptor _descriptor;

        private CharacterControllerAnimator _charAnimator;

        private User _user;

        private Grabber _grabber;

        private Transform _head;

        private Camera _cam;

        private RenderTexture _rt;

        private PhotonView _photonView;

        private float _lastOwnershipTry;

        private float _lastRelaunchTry;


        // Current go_to destination (drives our own movement toward it).
        private Vector3? _navTarget;

        private string _navTargetName;


        // Short-term memory so the NPC doesn't forget what it was doing each tick.
        // Hot-reload support.
        private FileSystemWatcher _cfgWatcher;

        private FileSystemWatcher _dllWatcher;

        private volatile bool _cfgReloadQueued;

        private volatile bool _dllChangedQueued;


        private string _lastThought = "just woke up";

        private string _lastAction = "none";

        private int _tick;

        private string _blockedInfo;


        // Ambient creative voice: every N ticks the model is invited to comment on
        // what it sees/feels — its own words, not canned barks.
        private int _lastCommentaryTick = -999;

        private ConfigEntry<int> _cfgCommentEvery;

        private ConfigEntry<float> _cfgCommentTemp;

        private float? _ledgeDrop;   // measured drop height ahead (m), if any, for perception


        // The name this kobold calls itself, chosen once per body on possess.
        private string _npcName;


        // What the player actually said in chat recently (fed to the LLM so it can respond).
        private volatile bool _chatCallbackRegistered;

        private string _playerChat;

        private float _playerChatTime;


        // Reagent events since last perception (drank water, metabolized, sprayed...).
        private readonly Queue<string> _reagentEvents = new Queue<string>();

        private GenericReagentContainer _bellySubscribed;

        private bool _bellySnapshotPending; // skip the initial OnChange burst on possess

        private string _lastBellySummary = "";  // used to suppress "same contents" repeats


        // Short rolling history of the last few decisions so the model can see what
        // it just did and what came of it (e.g. "walk -> blocked", "interact -> used Bed").
        private readonly System.Collections.Generic.LinkedList<string> _history
            = new System.Collections.Generic.LinkedList<string>();

        private const int HistoryLen = 10;

        // Also keep the last few goals/thoughts so plans have temporal context.
        private readonly System.Collections.Generic.LinkedList<string> _thoughtHistory
            = new System.Collections.Generic.LinkedList<string>();

        private const int ThoughtHistoryLen = 6;


        // Long-term facts the model can append to ("bed is upstairs", "player is
        // friendly...") and read back every turn. Cap keeps the prompt bounded.
        private readonly List<string> _facts = new List<string>();

        private const int FactCap = 24;


        private void PushHistory(string action, string summary)
        {
            if (string.IsNullOrEmpty(action) || action == "none") return;
            _history.AddLast(action + (string.IsNullOrEmpty(summary) ? "" : "->" + summary));
            while (_history.Count > HistoryLen) _history.RemoveFirst();
        }


        private string HistoryJson()
        {
            if (_history.Count == 0) return "[]";
            var parts = new System.Text.StringBuilder("[");
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
            string f = fact.Trim();
            // Simple dedupe: if the same prefix appears, replace it (update not append).
            for (int i = 0; i < _facts.Count; i++)
                if (_facts[i].Split(':')[0] == f.Split(':')[0]) { _facts[i] = f; return; }
            _facts.Add(f);
            while (_facts.Count > FactCap) _facts.RemoveAt(0); // drop oldest when full
        }


        private void Awake()
        {
            _mainContext = SynchronizationContext.Current;

            _cfgEndpoint     = Config.Bind("LLM", "Endpoint", "http://127.0.0.1:11434/v1/chat/completions", "OpenAI-compatible chat completions URL");
            _cfgModel        = Config.Bind("LLM", "Model", "local-model", "Model name to request");
            _cfgApiKey       = Config.Bind("LLM", "ApiKey", "", "Bearer token (may be empty for local)");
            _cfgSystem       = Config.Bind("LLM", "SystemPrompt",
                "You are a kobold NPC living in KoboldKare. You MUST respond by calling 'act' — never plain text. " +
                "You live in a house with rooms; landmarks you learn (bed/toilet/bath/kitchen/play stations/nests/doors) are in your 'facts' — remember them (" +
                "action 'remember' mem='bed is upstairs') so you build a mental map and stop bumbling. " +
                "You see yourself as 'me': <your body name>. Don't respond to your own chat messages — only the *player's* speech needs a reply; your own 'say' already echoed once. " +
                "When you arrive in a new body, introduce yourself briefly via 'say' (your name + a hello). " +
                "Every turn, pick a goal. Priorities: (1) if the player talked to you ('heard'), respond with 'say'; (2) if 'needs.eggs' says ready_to_lay, find a 'nest' station and use it; (3) when 'stim' is up, find a partner station or another kobold and play with it; (4) player nearby → walk over, say hi, play with them; (5) otherwise explore new rooms/landmarks. " +
                "Resting on a bed only when you're too tired to keep going (energy below ~0.2) — never just to top off. " +
                "Then pick ONE action toward it. 'vis_go' is the vision pass's bearing advice (it can see the image) — usually follow it unless your memory says otherwise. " +
                "nearby 'i' field categories: bed / toilet / bath / nest / play / seat / door / bodyswap / machine (':busy' = taken). " +
                "needs.eggs: egg amount in your belly and whether you're ready_to_lay; to lay, find a 'nest' station and use it — the egg comes out there. " +
                "To use one: get within ~2m, turn to face it, THEN interact — interact uses the closest thing in front of you. " +
                "'body' tells you your equipment. Some stations only fit some bodies — if interact says cannot_use on a 'play'/'bed'/'breeding' station, try another; on two-sided stations the first user picks the role. " +
                "When 'penetrated' or 'penetrating' is set, you're mid-play with someone — enjoy it and respond via 'say'+body language; guide them if you want more. " +
                "ask(q='...') to ponder the world — your question+perception go to your inner world-model, answer appears next turn as 'answered'. " +
                "Tools: walk(duration,turn_deg,run), walk_ray(ray/ray_deg), go_to(name like 'bed'/'toilet'/'nest'/'player' or x,z), look_around(sweep), look(yaw,pitch), jump, exit_station, crouch(0..1), move_to(x,z), interact, grab(multi), drop, say, remember(mem=fact), status, none. " +
                 "Rays: k=kobold p=player u=usable w=wall barrier=low sill/window n=nothing; rows p=d/l/u. " +
                 "ground: ahead=clear/step(auto)/sill(climbable)/wall; drop=distance to ledge. walls=blocked sides within arm reach. " +
                 "clearance=8-direction wall distances (blocked/close/near/open) — steer toward open. look_around scans the view and lists what's in each sector. " +
                 "history=recent actions+outcomes; memory=recent goals; facts=what you've learned. " +
                 "When in_station, you can't walk — use exit_station or jump to get off. " +
                "'plan' lets you queue up to 8 actions with 'wait' pauses. Keep moving; don't idle.",
                "System persona prompt (blank = use built-in default)");
            _cfgThinkInterval= Config.Bind("LLM", "ThinkInterval", 0.4f, "Seconds between perception/decision cycles");
            _cfgSendImage    = Config.Bind("LLM", "SendImage", true, "Attach a first-person JPEG to the action call when useful (vision model required)");
            _cfgImageEvery   = Config.Bind("LLM", "ImageEveryNTicks", 6, "Baseline: attach an image every N ticks even without a trigger");
            _cfgImageOnBump  = Config.Bind("LLM", "ImageOnBump", true, "Attach a fresh image right after blocked/bump so the model sees what stopped it");
            _cfgImageOnTurn  = Config.Bind("LLM", "ImageOnTurn", true, "Attach an image after large turns (>=30 deg) so it sees the new view");
            _cfgMaxTokens    = Config.Bind("LLM", "MaxTokens", 640, "Max response tokens (reasoning models burn tokens on thought before the action — keep this high enough to fit both)");
            _cfgTemperature  = Config.Bind("LLM", "Temperature", 0.3f, "Sampling temperature (lower = faster, more deterministic)");
            _cfgStepDelay    = Config.Bind("LLM", "PlanStepDelay", 0.35f, "Seconds between each action in a chained plan");
            _cfgMaxPlan      = Config.Bind("LLM", "PlanMaxSteps", 8, "Max actions the model may queue in one response (hard cap)");
            _cfgCommentEvery = Config.Bind("LLM", "CommentEveryNTicks", 5, "Every N ticks, invite a free 'comment' — the model voices its own take on surroundings (0 = off)");
            _cfgCommentTemp  = Config.Bind("LLM", "CommentTemp", 0.9f, "Sampling temperature for free commentary");
            _cfgVision       = Config.Bind("Vision", "Enabled", true, "Background vision pass: caption the first-person view on another thread and feed it into the next perception");
            _cfgVisionEvery  = Config.Bind("Vision", "EveryNTicks", 3, "Run the vision pass every N action ticks (lower = more aware, slower)");
            _cfgVisionPrompt = Config.Bind("Vision", "Prompt",
                "You are the SPATIAL reasoner for a kobold NPC. Produce a compact SCENE REPORT: (a) landmarks/stations/people in view with a rough bearing, (b) which directions are OPEN to walk (-90 left .. +90 right), (c) any hazard or drop. " +
                "Then NAV ADVICE for its current goal as 'go:<bearing>:<target>'. Keep the whole answer under 40 words. " +
                "Example: 'play station left, bathroom ahead, friend right | open ahead and left | go:-45:play station'." ,
                "Scene-report + navigation instruction for the vision pass");

            _cfgVisModel     = Config.Bind("VisionModel", "Model", "", "Vision-capable model name for the scene-caption pass (e.g. llava / qwen2-vl). Blank = use LLM.Model (which must then be a vision model)");
            _cfgVisEndpoint  = Config.Bind("VisionModel", "Endpoint", "", "Chat-completions URL for the vision model. Blank = use LLM.Endpoint");
            _cfgVisApiKey    = Config.Bind("VisionModel", "ApiKey", "", "Bearer token for the vision endpoint. Blank = use LLM.ApiKey");
            _cfgVisMaxTokens = Config.Bind("VisionModel", "MaxTokens", 80, "Caption token cap (short = fast)");
            _cfgVisionDebug  = Config.Bind("Vision", "DebugDumpFrames", true, "Write the exact JPEG given to the vision model to BepInEx/plugins/KKLLMNPC_frames/ so you can inspect what the NPC 'sees'");
            _cfgRayCount     = Config.Bind("Senses", "RayCount", 9, "Number of rays across the frustum fan");
            _cfgRayRange     = Config.Bind("Senses", "RayRange", 25f, "Raycast range (m)");
            _cfgAutoFindRange= Config.Bind("Senses", "AutoFindRange", 60f, "Radius to look for a kobold to hijack");
            _cfgImageSize    = Config.Bind("Senses", "ImageSize", 192, "Square first-person render size (px)");
            _cfgCamNearClip  = Config.Bind("Senses", "CameraNearClip", 0.15f, "Camera near clip (m) — raise if you see the inside of the head");
            _cfgCamForward   = Config.Bind("Senses", "CameraForward", 0.22f, "How far in front of the head bone the camera sits (m) — raise for big snouts");
                _cfgBlockedScenes= Config.Bind("General", "BlockedScenes", "MainMenu,Loading,ErrorScene", "Comma-separated scene names where the LLM stays idle (MainMap is the playable world)");

            _running = true;
            SafeRun(StartWatchers);
            try
            {
                _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM" };
                _llmThread.Start();
                Logger.LogInfo("KKLLMNPC: LLM loop started. Waiting for a kobold to possess.");
            }
            catch (Exception e)
            {
                _running = false;
                Logger.LogError("KKLLMNPC: failed to start LLM thread: " + e);
            }
        }


        private void StartWatchers()
        {
            // Config hot-reload: BepInEx ConfigEntries re-read .Value from memory,
            // so we just need to call Config.Reload() when the file changes.
            try
            {
                string cfgPath = Config.ConfigFilePath;
                string dir = Path.GetDirectoryName(cfgPath), file = Path.GetFileName(cfgPath);
                _cfgWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _cfgWatcher.Changed += (s, e) => _cfgReloadQueued = true;
            }
            catch (Exception e) { Logger.LogWarning("cfg watcher: " + e.Message); }

            // Code hot-reload: build.sh copies a fresh DLL over us; watch our own
            // assembly file for that copy and restart the plugin logic in place.
            try
            {
                string dllPath = typeof(LLMNPCPlugin).Assembly.Location;
                _dllWatcher = new FileSystemWatcher(Path.GetDirectoryName(dllPath), Path.GetFileName(dllPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _dllWatcher.Changed += (s, e) => _dllChangedQueued = true;
            }
            catch (Exception e) { Logger.LogWarning("dll watcher: " + e.Message); }
        }


        private void StopWatchers()
        {
            try { if (_cfgWatcher != null) { _cfgWatcher.EnableRaisingEvents = false; _cfgWatcher.Dispose(); } } catch (Exception) { }
            try { if (_dllWatcher != null) { _dllWatcher.EnableRaisingEvents = false; _dllWatcher.Dispose(); } } catch (Exception) { }
            _cfgWatcher = null; _dllWatcher = null;
        }


        private void OnDestroy()
        {
            _running = false;
            _mainReady = false;
            try { if (_chatCallbackRegistered) { PhotonNetwork.RemoveCallbackTarget(this); _chatCallbackRegistered = false; } } catch (Exception) { }
            StopWatchers();
            try { TeardownBody(); } catch (Exception e) { Logger.LogWarning("teardown: " + e.Message); }
            try { _llmThread?.Join(1500); } catch (Exception) { }
        }


        // Reading helpers: blank/whitespace config values fall back to the entry's
        // own default so a user can set a prompt to "" and get the built-in text.
        private static string Val(ConfigEntry<string> e)
        {
            return string.IsNullOrWhiteSpace(e.Value) ? (string)e.DefaultValue : e.Value;
        }


        // ------------------------------------------------------------------
        // main-thread marshalling (same pattern as UnityMCP)
        // ------------------------------------------------------------------
        // NOTE: touching UnityEngine objects from any thread other than the
        // main thread crashes the process at the native level (no catchable
        // exception). _mainContext is captured in Awake, but during mod loading
        // SynchronizationContext.Current can be null early, so Update re-captures
        // it and the helpers below *drop* work until a context exists.
        private volatile bool _mainReady;

        private int _mainThreadId = -1;


        private void Update()
        {
            // Register the Photon chat listener once Photon is up, on the main thread.
            if (_mainReady && !_chatCallbackRegistered && PhotonNetwork.IsConnectedAndReady)
            {
                try { PhotonNetwork.AddCallbackTarget(this); _chatCallbackRegistered = true; }
                catch (Exception e) { Logger.LogWarning("chat listener: " + e.Message); }
            }

            // Drain hot-reload queues on the main thread.
            if (_cfgReloadQueued && _mainReady)
            {
                _cfgReloadQueued = false;
                try
                {
                    Config.Reload();
                    Logger.LogInfo("KKLLMNPC: config reloaded.");
                    // Rebuild/reposition the camera if its capture settings changed.
                    if (_rt != null && (_rt.width != _cfgImageSize.Value)) SetupCamera();
                    else if (_cam != null && (!Mathf.Approximately(_cam.nearClipPlane, _cfgCamNearClip.Value))) SetupCamera();
                }
                catch (Exception e) { Logger.LogWarning("cfg reload: " + e.Message); }
            }
            if (_dllChangedQueued && _mainReady)
            {
                _dllChangedQueued = false;
                // Give the copy a moment to finish writing before we do anything.
                StartCoroutine(RestartAfterSecond());
            }

            // Self-heal: if the LLM should be running but its thread is dead (any
            // exception path that killed it), relaunch once every few seconds.
            if (_mainReady && _running && (_llmThread == null || !_llmThread.IsAlive))
            {
                if (Time.unscaledTime - _lastRelaunchTry > 5f)
                {
                    _lastRelaunchTry = Time.unscaledTime;
                    Logger.LogWarning("KKLLMNPC: LLM thread died — relaunching.");
                    try
                    {
                        _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM" };
                        _llmThread.Start();
                    }
                    catch (Exception e) { Logger.LogError("relaunch: " + e); }
                }
            }

            if (_mainReady) return;
            var ctx = SynchronizationContext.Current;
            if (ctx != null)
            {
                _mainContext = ctx;
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _mainReady = true;
                Logger.LogInfo("KKLLMNPC: main thread captured.");
            }
        }


        private System.Collections.IEnumerator RestartAfterSecond()
        {
            for (int i = 0; i < 5; i++) yield return new WaitForSecondsRealtime(0.4f);
            RestartPlugin();
        }


        // Called when a new DLL is dropped in. A BepInEx plugin assembly can't be
        // unloaded from the default context, so we *gracefully reset* ourselves:
        // stop the LLM, release the body, clear state, then relaunch. On next scene
        // load (or if the chainloader re-creates us) the new code takes over fully;
        // in-session this at least stops the old loop cleanly and reloads config so
        // behavior changes apply immediately.
        private void RestartPlugin()
        {
            // Only restart when actually in a playable scene and the LLM loop is
            // the thing active — otherwise (menu/loading) we're just churning.
            if (!_mainReady || !IsPlayableScene()) { _dllChangedQueued = false; return; }

            Logger.LogWarning("KKLLMNPC: DLL changed — restarting plugin logic.");
            try
            {
                _running = false;
                StopWatchers();
                try { TeardownBody(); } catch (Exception) { }
                // Don't block the main thread — just let the old thread wind down.
                // It'll exit on its own because _running=false (IsBackground=true).

                lock (_stateLock) { _moveLocalZ = 0f; _moveJump = false; _moveUntilTime = 0f; _yawOffsetDeg = 0f; }
                _lastThought = "just woke up"; _lastAction = "none"; _tick = 0; _blockedInfo = null;
            _history.Clear();
            _thoughtHistory.Clear();
            _facts.Clear();
            _sceneDesc = "unknown"; _lastVisionCaption = ""; _lastVisionB64 = null;
                try { Config.Reload(); } catch (Exception) { }
            }
            catch (Exception e) { Logger.LogError("KKLLMNPC: teardown during restart: " + e); }

            // Always come back up, even if teardown failed.
            _running = true;
            StartWatchers();
            try
            {
                _llmThread = new Thread(LLMLoop) { IsBackground = true, Name = "KKLLMNPC-LLM" };
                _llmThread.Start();
                Logger.LogInfo("KKLLMNPC: restarted on new DLL.");
            }
            catch (Exception e) { Logger.LogError("KKLLMNPC: restart failed: " + e); }
        }


        private object RunOnMainThread(Func<object> fn, int timeoutMs = 15000)
        {
            if (!_mainReady || _mainContext == null)
            {
                if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                    return fn(); // we ARE the main thread even before capture
                // No main context yet: refuse rather than run Unity API off-thread.
                throw new InvalidOperationException("main thread not ready");
            }
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId
                || SynchronizationContext.Current == _mainContext)
                return fn();
            object result = null; Exception error = null;
            var done = new ManualResetEventSlim(false);
            _mainContext.Post(_ => { try { result = fn(); } catch (Exception e) { error = e; } finally { done.Set(); } }, null);
            if (!done.Wait(timeoutMs)) throw new TimeoutException("main thread timeout");
            if (error != null) throw error;
            return result;
        }

        private void RunOnMainThreadAsync(Action fn)
        {
            if (!_mainReady || _mainContext == null)
            {
                if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) { SafeRun(fn); return; }
                return; // drop: never run Unity API off the main thread
            }
            _mainContext.Post(_ => SafeRun(fn), null);
        }

        private void SafeRun(Action fn)
        {
            try { fn(); } catch (Exception e) { Logger.LogError(e); }
        }


        // ------------------------------------------------------------------
        // body acquisition / teardown
        // ------------------------------------------------------------------
        // Runs on the main thread. True once a scene other than the configured
        // blocked ones (menu/loading) is active AND the local player exists.
        private bool IsPlayableScene()
        {
            try
            {
                // The game's own definition of "in a level, not the menu".
                if (!GameManager.InLevel()) { MarkScene("menu-or-error"); return false; }

                var scene = SceneManager.GetActiveScene();
                if (!scene.isLoaded) return false;
                string name = scene.name ?? "";
                var blocked = (_cfgBlockedScenes?.Value ?? "MainMenu,Loading,ErrorScene")
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var b in blocked)
                    if (string.Equals(name, b.Trim(), StringComparison.OrdinalIgnoreCase))
                    { MarkScene(name); return false; }

                // And require the local player to actually exist — otherwise we
                // possess menu-dressing kobolds and burn LLM calls.
                bool hasPlayer = PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null;
                if (!hasPlayer) { MarkScene("no-player:" + name); return false; }

                if (name != _lastSceneName) { _lastSceneName = name; Logger.LogInfo("KKLLMNPC: active scene = '" + name + "'."); }
                return true;
            }
            catch (Exception) { return false; }
        }


        private void MarkScene(string key)
        {
            if (key == _lastSceneName) return;
            _lastSceneName = key;
            Logger.LogInfo("KKLLMNPC: idle (" + key + ").");
        }


        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        // Unity overloads == on Object to fake-null destroyed objects; `is null`
        // bypasses that. This catches both real-null and destroyed.
        private static bool IsAlive(UnityEngine.Object o) => o != null;


        // Mathf.MoveTowardsAngle doesn't exist in this Unity build's Mathf shim — hand-roll.
        private static float MoveAngleTowards(float current, float target, float maxDelta)
        {
            float d = Mathf.Repeat(target - current + 180f, 360f) - 180f;
            if (Mathf.Abs(d) <= maxDelta) return target;
            return Mathf.Repeat(current + Mathf.Sign(d) * maxDelta, 360f);
        }


        // RenderTexture.IsCreated() is not available in this Unity build — use
        // width/height as a proxy for a successfully- Create()d texture.
        private static bool RtCreated(RenderTexture rt)
        {
            try { return rt != null && rt.width > 0 && rt.height > 0; }
            catch (Exception) { return false; }
        }


        private string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
