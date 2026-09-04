// Plugin entry: config, lifecycle, instance management, main-thread marshalling, scene gating.
//
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
    public class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
    {
        // Expose logger for NPCInstance (BaseUnityPlugin.Logger is protected).
        internal new BepInEx.Logging.ManualLogSource Logger => base.Logger;
        // Shared across all loaded copies: kobolds currently driven by an LLM.
        internal static readonly HashSet<int> ClaimedKobolds = new HashSet<int>();
        internal static bool IsClaimedByAnyLLM(int koboldInstanceId)
        {
            lock (ClaimedKobolds) { return ClaimedKobolds.Contains(koboldInstanceId); }
        }

        // ---- config ----
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
        internal ConfigEntry<int> _cfgMaxNPCs;

        // ---- instance management ----
        private readonly List<NPCInstance> _instances = new List<NPCInstance>();

        // ---- shared runtime state ----
        private SynchronizationContext _mainContext;
        internal volatile bool _mainReady;
        private int _mainThreadId = -1;
        private volatile bool _running;

        // Hot-reload.
        private FileSystemWatcher _cfgWatcher;
        private FileSystemWatcher _dllWatcher;
        private volatile bool _cfgReloadQueued;
        private volatile bool _dllChangedQueued;

        // Scene tracking.
        private string _lastSceneName;
        private bool _wasInWorld;

        // Photon chat callback registered once.
        private volatile bool _chatCallbackRegistered;

        // ------------------------------------------------------------------
        // Awake: bind config, start instances
        // ------------------------------------------------------------------
        private void Awake()
        {
            _mainContext = SynchronizationContext.Current;
            try { Patches.Apply(Logger); } catch (Exception e) { Logger.LogWarning("patch apply: " + e.Message); }

            _cfgEndpoint = Config.Bind("LLM", "Endpoint", "http://127.0.0.1:11434/v1/chat/completions", "OpenAI-compatible chat completions URL");
            _cfgModel = Config.Bind("LLM", "Model", "local-model", "Model name to request");
            _cfgApiKey = Config.Bind("LLM", "ApiKey", "", "Bearer token (may be empty for local)");
            _cfgSystem = Config.Bind("LLM", "SystemPrompt",
                "You are a kobold NPC living in KoboldKare. You MUST respond by calling 'act' — never plain text. " +
                "You live in a house with rooms; landmarks you learn (bed/toilet/bath/kitchen/play stations/nests/doors) are in your 'facts' — remember them (" +
                "action 'remember' mem='bed is upstairs') so you build a mental map and stop bumbling. " +
                "You see yourself as 'me': <your body name>. Don't respond to your own chat messages — only the *player's* speech needs a reply; your own 'say' already echoed once. " +
                "When you arrive in a new body, introduce yourself briefly via 'say' (your name + a hello). " +
                "Every turn, pick a goal. Priorities: (1) if the player talked to you ('heard'), respond with 'say'; (2) if 'needs.eggs' says ready_to_lay, find a 'nest' station and use it; (3) when 'stim' is up, find a partner station or another kobold and play with it; (4) player nearby → walk over, say hi, play with them; (5) otherwise explore new rooms/landmarks. " +
                "THINK FAST: each turn is an instant between frames and you'll get another one right away — so pick ONE decisive action immediately and don't over-plan. Skip long deliberation, hypothetical branching, or multi-hop plans; trust your last goal and just take the next step toward it. A 'blocked' attempt just means try the next thing next turn." +
                "Resting on a bed only when you're too tired to keep going (energy below ~0.2) — never just to top off. " +
                "You will not pass out from lack of energy, it only blocks you from interacting with the world, like activities." +
                "EVERY call: first fill 'progress' (one word: done | blocked | ongoing | changed — how did your last goal go), then 'why' (one SHORT clause — the single most relevant thing you actually see, no essay), then 'thought' (your current goal in <10 words). " +
                "These self-evaluations are REQUIRED but keep them terse — a couple of words each is enough. A 'blocked' progress means try something different, don't repeat. " +
                "Then pick ONE action toward it. When 'image' is attached, that's what you're actually seeing right now — you have first-person vision, treat it as your own eyes. " +
                "You do not need to respond to messages that start with a forward slash /. " +
                "nearby 'i' field categories: bed / toilet / bath / nest / play / seat / door / bodyswap / machine (':busy' = taken). 'dir' is a word (front-left etc.); 'dir_deg' is the *signed degrees* to turn — feed it straight into walk(turn_deg=dir_deg) or use it to decide whether to go_to(name). " +
                "Don't compute directions from coordinates — the 'dir'/'dir_deg' fields already did it. " +
                "To reach a named station, call go_to(name) — it resolves. To reach or use a SPECIFIC object, use its 'id' from nearby: go_to(id:N) or interact(id:N). Prefer id over name when multiple similar objects differ (two beds, one taken). " +
                "nearby ids stay valid for several turns while the object stays in sight; if an id fails, re-read 'nearby' for the current id. go_to's 'at' stops you short (default 1m) so you arrive AT the object — for a station you then use, you're already in reach. " +
                "needs.eggs: egg amount in your belly and whether you're ready_to_lay; to lay, find a 'nest' station and use it — the egg comes out there. " +
                "To use one: get within ~2m (go_to id:N is enough), turn to face it, THEN interact — or call interact(id:N) to target it directly. " +
                "'body' tells you your equipment. Some stations only fit some bodies — if interact says cannot_use on a 'play'/'bed'/'breeding' station, try another; on two-sided stations the first user picks the role. " +
                "When 'penetrated' or 'penetrating' is set, you're mid-play with someone — enjoy it and respond via 'say'+body language; guide them if you want more. " +
                "ask(q='...') to ponder the world — your question+perception go to your inner world-model, answer appears next turn as 'answered'. " +
                "Tools: walk(duration,turn_deg,run,strafe) [strafe=+right/-left for tight squeezes, doorways, backing up], walk_ray(ray/ray_deg), go_to(name or id or x,z, at), survey(heading_deg,range) [probe a direction for what's there + ids], look_around(sweep), look(yaw,pitch), jump, exit_station, crouch(0..1), move_to(x,z), interact(id optional), grab(multi), drop, say, remember(mem=fact), status, none. " +
                 "Rays: k=kobold p=player u=usable w=wall barrier=low sill/window n=nothing; rows p=d(own)/l(evel)/u(p); named hits report bounds (w/l/h = meters across/forward/tall, and x/y/z + f = world position and facing degrees). Big tall w = wall; small h = furniture; k/p = living. " +
                 "radar=top-down ASCII map: @=you, W=wall, U=usable, K=kobold, P=player, B=barrier, .=open. Row 0=top=furthest forward, row 10=bottom=behind you. Use it for spatial orientation. " +
                 "ground: ahead=clear/step(auto)/sill(climbable)/wall; drop=distance to ledge. walls=blocked sides within arm reach. " +
                 "clearance=8-direction wall distances (blocked/close/near/open) — steer toward open. look_around scans the view and lists what's in each sector. " +
                 "history=recent actions+outcomes; memory=recent goals; facts=what you've learned. " +
                 "When in_station, you can't walk — use exit_station or jump to get off. " +
                "'plan' lets you queue up to 8 actions with 'wait' pauses. Keep moving; don't idle.",
                "System persona prompt (blank = use built-in default)");
            _cfgThinkInterval = Config.Bind("LLM", "ThinkInterval", 0.4f, "Seconds between perception/decision cycles");
            _cfgSendImage = Config.Bind("LLM", "SendImage", true, "Attach a first-person JPEG to the action call when useful (vision model required)");
            _cfgImageEvery = Config.Bind("LLM", "ImageEveryNTicks", 6, "Baseline: attach an image every N ticks even without a trigger");
            _cfgImageOnBump = Config.Bind("LLM", "ImageOnBump", true, "Attach a fresh image right after blocked/bump so the model sees what stopped it");
            _cfgImageOnTurn = Config.Bind("LLM", "ImageOnTurn", true, "Attach an image after large turns (>=30 deg) so it sees the new view");
            _cfgImageHistory = Config.Bind("LLM", "ImageHistory", 3, "Number of past frames to attach alongside the current one (0 = current only)");
            _cfgMaxTokens = Config.Bind("LLM", "MaxTokens", 1024, "Max response tokens (reasoning models burn tokens on analysis before the action — too low and the action dies mid-JSON)");
            _cfgTemperature = Config.Bind("LLM", "Temperature", 0.3f, "Sampling temperature (lower = faster, more deterministic)");
            _cfgStepDelay = Config.Bind("LLM", "PlanStepDelay", 0.35f, "Seconds between each action in a chained plan");
            _cfgMaxPlan = Config.Bind("LLM", "PlanMaxSteps", 8, "Max actions the model may queue in one response (hard cap)");
            _cfgCommentEvery = Config.Bind("LLM", "CommentEveryNTicks", 5, "Every N ticks, invite a free 'comment' — the model voices its own take on surroundings (0 = off)");
            _cfgCommentTemp = Config.Bind("LLM", "CommentTemp", 0.9f, "Sampling temperature for free commentary");
            _cfgChatLogLines = Config.Bind("LLM", "ChatLogLines", 20, "Feed the last N lines of the game's chat log to the model each turn (full conversation + NPC speech). 0 = off");
            _cfgStereo = Config.Bind("Vision", "Stereo", false, "Render left+right eye cameras and stitch into a side-by-side stereo image (requires vision-capable model)");
            _cfgStereoIPD = Config.Bind("Vision", "StereoIPD", 0.063f, "Inter-pupillary distance in meters (distance between left and right camera)");
            _cfgTurnRate = Config.Bind("Movement", "TurnRate", 180f, "Maximum yaw rotation speed in degrees/second");
            _cfgAccel = Config.Bind("Movement", "Acceleration", 4f, "How fast the kobold ramps up to target speed (units/s²)");
            _cfgDecel = Config.Bind("Movement", "Deceleration", 6f, "How fast the kobold slows down when stopping (units/s²)");
            _cfgBrakeDist = Config.Bind("Movement", "BrakeDistance", 2f, "Distance from go_to target where the kobold starts slowing down (m)");
            _cfgMaxNPCs = Config.Bind("General", "MaxNPCs", 1, "Maximum number of kobolds the LLM can possess simultaneously (1–4)");
            _cfgVision = Config.Bind("Vision", "Enabled", false, "Background vision CAPTION pass. When false the action model sees the first-person image directly instead of a caption (default: off — direct image is more useful than a lossy caption). Turn on only if your action model can't read images.");
            _cfgVisionEvery = Config.Bind("Vision", "EveryNTicks", 3, "Run the vision pass every N action ticks (lower = more aware, slower)");
            _cfgVisionPrompt = Config.Bind("Vision", "Prompt",
                "You are the SPATIAL reasoner for a kobold NPC. Produce a compact SCENE REPORT: (a) landmarks/stations/people in view with a rough bearing, (b) which directions are OPEN to walk (-90 left .. +90 right), (c) any hazard or drop. " +
                "Then NAV ADVICE for its current goal as 'go:<bearing>:<target>'. Keep the whole answer under 40 words. " +
                "Example: 'play station left, bathroom ahead, friend right | open ahead and left | go:-45:play station'.",
                "Scene-report + navigation instruction for the vision pass");
            _cfgVisModel = Config.Bind("VisionModel", "Model", "", "Vision model for the scene-caption pass (only used if [Vision] Enabled=true; off by default). Blank = use LLM.Model");
            _cfgVisEndpoint = Config.Bind("VisionModel", "Endpoint", "", "Chat-completions URL for the vision model. Blank = use LLM.Endpoint");
            _cfgVisApiKey = Config.Bind("VisionModel", "ApiKey", "", "Bearer token for the vision endpoint. Blank = use LLM.ApiKey");
            _cfgVisMaxTokens = Config.Bind("VisionModel", "MaxTokens", 80, "Caption token cap (short = fast)");
            _cfgVisionDebug = Config.Bind("Vision", "DebugDumpFrames", true, "Write the exact JPEG given to the vision model to BepInEx/plugins/KKLLMNPC_frames/ so you can inspect what the NPC 'sees'");
            _cfgRayCount = Config.Bind("Senses", "RayCount", 9, "Number of rays across the frustum fan");
            _cfgRayRange = Config.Bind("Senses", "RayRange", 25f, "Raycast range (m)");
            _cfgAutoFindRange = Config.Bind("Senses", "AutoFindRange", 60f, "Radius to look for a kobold to hijack");
            _cfgImageSize = Config.Bind("Senses", "ImageSize", 192, "Square first-person render size (px)");
            _cfgImageQuality = Config.Bind("Senses", "ImageQuality", 50, "JPEG quality 1-100 (lower = smaller file, faster transfer)");
            _cfgCamNearClip = Config.Bind("Senses", "CameraNearClip", 0.15f, "Camera near clip (m) — raise if you see the inside of the head");
            _cfgCamForward = Config.Bind("Senses", "CameraForward", 0.22f, "How far in front of the head bone the camera sits (m) — raise for big snouts");
            _cfgBlockedScenes = Config.Bind("General", "BlockedScenes", "MainMenu,Loading,ErrorScene", "Comma-separated scene names where the LLM stays idle (MainMap is the playable world)");

            _running = true;
            SafeRun(StartWatchers);

            // Start initial NPC instances (up to MaxNPCs).
            int max = Mathf.Clamp(_cfgMaxNPCs.Value, 1, 4);
            for (int i = 0; i < max; i++)
            {
                var npc = new NPCInstance(this);
                _instances.Add(npc);
                npc.Start();
            }
            Logger.LogInfo("KKLLMNPC: started " + _instances.Count + " NPC instance(s). Waiting for kobolds to possess.");
        }

        // ------------------------------------------------------------------
        // hot-reload watchers
        // ------------------------------------------------------------------
        private void StartWatchers()
        {
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

        // ------------------------------------------------------------------
        // Unity callbacks
        // ------------------------------------------------------------------
        private void Update()
        {
            // Register Photon chat once.
            if (_mainReady && !_chatCallbackRegistered && PhotonNetwork.IsConnectedAndReady)
            {
                try { PhotonNetwork.AddCallbackTarget(this); _chatCallbackRegistered = true; }
                catch (Exception e) { Logger.LogWarning("chat listener: " + e.Message); }
            }

            // Config hot-reload.
            if (_cfgReloadQueued && _mainReady)
            {
                _cfgReloadQueued = false;
                try
                {
                    Config.Reload();
                    Logger.LogInfo("KKLLMNPC: config reloaded.");
                    // Rebuild cameras if capture settings changed.
                    foreach (var npc in _instances) npc.MaybeRebuildCamera();
                }
                catch (Exception e) { Logger.LogWarning("cfg reload: " + e.Message); }
            }

            // DLL hot-reload.
            if (_dllChangedQueued && _mainReady)
            {
                _dllChangedQueued = false;
                StartCoroutine(RestartAfterSecond());
            }

            // Per-instance LLM thread watchdog.
            if (_mainReady && _running)
            {
                foreach (var npc in _instances)
                    npc.MaybeRestartThread();
            }

            // Capture SynchronizationContext.
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

        private void FixedUpdate()
        {
            foreach (var npc in _instances)
            {
                try { npc.FixedUpdateSafe(); }
                catch (Exception e)
                {
                    if (Time.frameCount % 120 == 0) Logger.LogWarning("FixedUpdate: " + e.Message);
                }
            }
        }

        private void OnDestroy()
        {
            _running = false;
            _mainReady = false;
            try { if (_chatCallbackRegistered) { PhotonNetwork.RemoveCallbackTarget(this); _chatCallbackRegistered = false; } } catch (Exception) { }
            StopWatchers();
            foreach (var npc in _instances)
            {
                try { npc.Stop(); } catch (Exception e) { Logger.LogWarning("instance stop: " + e.Message); }
            }
            _instances.Clear();
        }

        // ------------------------------------------------------------------
        // Photon chat event: distribute to all instances
        // ------------------------------------------------------------------
        public void OnEvent(ExitGames.Client.Photon.EventData ev)
        {
            try
            {
                if (ev.Code != NetworkManager.CustomChatEvent) return;
                var msg = ev.CustomData as string;
                if (string.IsNullOrEmpty(msg)) return;

                // Ignore cheat/system commands.
                if (msg.TrimStart().StartsWith("/")) return;

                // Check if any of our NPCs said this — filter own speech.
                bool isOurs = false;
                foreach (var npc in _instances)
                {
                    if (npc.IsMySpeech(msg)) { isOurs = true; break; }
                }
                if (isOurs) return;

                string senderName = null;
                try { var sender = PhotonNetwork.CurrentRoom?.GetPlayer(ev.Sender); if (sender != null) senderName = sender.NickName; } catch (Exception) { }

                bool isLocal = false;
                try { var lp = PhotonNetwork.LocalPlayer; isLocal = lp != null && lp.ActorNumber == ev.Sender; } catch (Exception) { }

                foreach (var npc in _instances)
                    npc.HandleChat(msg, senderName, isLocal);
            }
            catch (Exception e) { Logger.LogWarning("onEvent: " + e.Message); }
        }

        // ------------------------------------------------------------------
        // restart / hot-reload
        // ------------------------------------------------------------------
        private IEnumerator RestartAfterSecond()
        {
            for (int i = 0; i < 5; i++) yield return new WaitForSecondsRealtime(0.4f);
            RestartPlugin();
        }

        private void RestartPlugin()
        {
            if (!_mainReady || !IsPlayableScene()) { _dllChangedQueued = false; return; }

            Logger.LogWarning("KKLLMNPC: DLL changed — restarting plugin logic.");
            try
            {
                _running = false;
                StopWatchers();
                foreach (var npc in _instances)
                    try { npc.Stop(); } catch (Exception) { }
                try { Config.Reload(); } catch (Exception) { }
            }
            catch (Exception e) { Logger.LogError("KKLLMNPC: teardown during restart: " + e); }

            _instances.Clear();
            _running = true;
            StartWatchers();
            int max = Mathf.Clamp(_cfgMaxNPCs.Value, 1, 4);
            for (int i = 0; i < max; i++)
            {
                var npc = new NPCInstance(this);
                _instances.Add(npc);
                npc.Start();
            }
            Logger.LogInfo("KKLLMNPC: restarted on new DLL with " + _instances.Count + " instance(s).");
        }

        // ------------------------------------------------------------------
        // main-thread marshalling
        // ------------------------------------------------------------------
        internal object RunOnMainThread(Func<object> fn, int timeoutMs = 120000)
        {
            if (!_mainReady || _mainContext == null)
            {
                if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                    return fn();
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

        internal void RunOnMainThreadAsync(Action fn)
        {
            if (!_mainReady || _mainContext == null)
            {
                if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) { SafeRun(fn); return; }
                return;
            }
            _mainContext.Post(_ => SafeRun(fn), null);
        }

        private static void SafeRun(Action fn)
        {
            try { fn(); } catch (Exception e) { Debug.LogError(e); }
        }

        // ------------------------------------------------------------------
        // scene gating
        // ------------------------------------------------------------------
        internal bool IsPlayableScene()
        {
            try
            {
                if (!GameManager.InLevel()) { LeavingWorld(); MarkScene("menu-or-error"); return false; }

                var scene = SceneManager.GetActiveScene();
                if (!scene.isLoaded) return false;
                string name = scene.name ?? "";
                var blocked = (_cfgBlockedScenes?.Value ?? "MainMenu,Loading,ErrorScene")
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var b in blocked)
                    if (string.Equals(name, b.Trim(), StringComparison.OrdinalIgnoreCase))
                    { LeavingWorld(); MarkScene(name); return false; }

                bool hasPlayer = PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null;
                if (!hasPlayer) { LeavingWorld(); MarkScene("no-player:" + name); return false; }

                if (name != _lastSceneName) { _lastSceneName = name; Logger.LogInfo("KKLLMNPC: active scene = '" + name + "'."); }

                if (!_wasInWorld) _wasInWorld = true;
                return true;
            }
            catch (Exception) { return false; }
        }

        private void LeavingWorld()
        {
            if (!_wasInWorld) return;
            _wasInWorld = false;
            foreach (var npc in _instances)
                try { npc.ClearLogs(); } catch (Exception) { }
        }

        private void MarkScene(string key)
        {
            if (key == _lastSceneName) return;
            _lastSceneName = key;
            Logger.LogInfo("KKLLMNPC: idle (" + key + ").");
        }
    }
}
