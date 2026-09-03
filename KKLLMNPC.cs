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
    public class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
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

        // Penetration awareness: per-penetrator depth over time — the model can tell
        // how deep it is, whether it's thrusting, and roughly which hole.
        private class PenState { public string name; public float depth; public float lastT; public float vel; public string hole; public Transform src; }
        private readonly Dictionary<object, PenState> _pen = new Dictionary<object, PenState>();
        // Fires when this kobold's penetrator enters something (and each tick).
        private void NotifyDickIn(KKPenetratorListener listener, string hole, bool entered)
        {
            try
            {
                lock (_dickIn)
                {
                    PenState st;
                    if (entered)
                    {
                        if (!_dickIn.TryGetValue(listener, out st))
                        {
                            st = new PenState { name = hole, hole = hole, depth = 0.05f, lastT = Time.unscaledTime };
                            _dickIn[listener] = st;
                        }
                        st.src = listener.lastTarget;
                        Logger.LogInfo("dick in " + hole);
                        EmitAmbient("haa~"); // their dick slid in
                    }
                    else _dickIn.Remove(listener);
                }
            }
            catch (Exception) { }
        }

        private void NotifyDickTick(KKPenetratorListener listener, string hole, float dist)
        {
            try
            {
                lock (_dickIn)
                {
                    PenState st;
                    if (!_dickIn.TryGetValue(listener, out st))
                    {
                        st = new PenState { name = hole, hole = hole, lastT = Time.unscaledTime };
                        _dickIn[listener] = st;
                    }
                    st.src = listener.lastTarget;
                    float d = Mathf.Max(0f, 0.05f - Mathf.Min(0f, dist)); // distToHole<0 => inside
                    float dt = Mathf.Max(0.001f, Time.unscaledTime - st.lastT);
                    st.vel = Mathf.Lerp(st.vel, (d - st.depth) / dt, 0.2f);
                    st.depth = d;
                    st.lastT = Time.unscaledTime;
                }
            }
            catch (Exception) { }
        }

        private string DickInInfo()
        {
            lock (_dickIn)
            {
                if (_dickIn.Count == 0) return null;
                var parts = new List<string>();
                foreach (var kv in _dickIn)
                {
                    var st = kv.Value;
                    parts.Add(st.name + " depth=" + F(st.depth) + "m thrust=" + F(st.vel));
                }
                return _dickIn.Count + " inside (" + string.Join("; ", parts.ToArray()) + ")";
            }
        }

        // Same but for when THIS kobold's own dick is inside someone/something.
        private readonly Dictionary<object, PenState> _dickIn = new Dictionary<object, PenState>();

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
        private float _lastBumpTime = -99f;   // Time.unscaledTime of last wall-contact
        private string _bumpInfo;             // human-readable "hit wall to the left"

        // Background vision pipeline.
        private string _sceneDesc = "unknown";
        private string _lastVisionCaption = "";   // previous caption — sent as context to vision model
        private volatile string _lastVisionB64;
        // Steering hint from the vision model (goal-driven "where should I go next").
        private class VisionSteer { public float deg; public string reason; }
        private volatile VisionSteer _visionSteer;
        private volatile bool _visionBusy;
        private float _visionStartTime;
        private int _lastVisionTick = -999;
        private bool _visionModelLoggedOnce;
        private int _visionShot; // last written debug frame index
        private volatile bool _needImageAfterBump;
        private float _lastBigTurnTime = -99f;

        private const float WalkProbeRange = 1.4f; // how far ahead we validate walking

        // True if the collider belongs to our own kobold (its body/limbs) — ignore it.
        private bool IsOwnCollider(Collider c)
        {
            if (c == null || _kobold == null) return false;
            return c.transform.root == _kobold.transform.root;
        }

        // Fan out left/right to find a heading with no near obstacle; return the
        // yaw delta to steer, or 0 if everything is blocked.
        private float FindClearHeading(Vector3 eye, Vector3 hitNormal)
        {
            // Try increasingly wide offsets, preferring the side of the surface normal
            // so we slide along walls instead of bouncing off them.
            float side = Vector3.Cross(hitNormal, Vector3.up).y >= 0f ? 1f : -1f;
            float[] offsets = { 20f * side, -20f * side, 40f * side, -40f * side, 65f * side, -65f * side, 90f * side, -90f * side };
            foreach (var off in offsets)
            {
                Vector3 dir = Quaternion.Euler(0, _yawDeg + off, 0) * Vector3.forward;
                if (!Physics.Raycast(eye, dir, WalkProbeRange * 1.3f, ~0, QueryTriggerInteraction.Ignore))
                    return off;
            }
            return 0f; // boxed in
        }

        // LLM-facing continuous movement command, written by LLM thread,
        // consumed in FixedUpdate on the main thread. Plain fields + Interlocked.
        private float _moveLocalZ;      // forward speed
        private bool  _moveJump;
        private bool  _moveRun;         // false => controller.inputWalking (slow); true => full speed
        private float _moveUntilTime;   // unscaled time when the current walk burst ends
        private float _crouch = 0f;     // 0..1 — applied every FixedUpdate
        private float _manualCrouchSet = -99f; // last time the model chose crouch
        private float _clipSince = -99f;       // when camera first appeared clipped
        private float _lastClipFix;            // cooldown on auto-crouch adjustments
        private float _yawOffsetDeg;    // pending yaw to apply relative to current facing
        private float _pitchDeg;        // absolute pitch for the eye/camera
        private float _yawDeg;          // absolute yaw (derived from body + offset)

        private readonly object _stateLock = new object();

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
        // Photon events: hear what players type into the chat window
        // ------------------------------------------------------------------
        public void OnEvent(ExitGames.Client.Photon.EventData ev)
        {
            try
            {
                if (ev.Code != NetworkManager.CustomChatEvent) return;
                var msg = ev.CustomData as string;
                if (string.IsNullOrEmpty(msg)) return;

                string senderName = null;
                try
                {
                    var sender = PhotonNetwork.CurrentRoom?.GetPlayer(ev.Sender);
                    if (sender != null) senderName = sender.NickName;
                }
                catch (Exception) { }

                // Ignore our own speech: our chat text is prefixed "MyName: ".
                string myPrefix = MyName() + ":";
                if (msg.StartsWith(myPrefix, StringComparison.OrdinalIgnoreCase))
                    return;

                // The local player is who we care about — but capture anyone.
                bool isLocal = false;
                try { var lp = PhotonNetwork.LocalPlayer; isLocal = lp != null && lp.ActorNumber == ev.Sender; } catch (Exception) { }

                string heard = (isLocal ? "player" : (senderName ?? "someone")) + ": " + msg;
                _playerChat = heard;
                _playerChatTime = Time.unscaledTime;
                Logger.LogInfo("heard chat: " + heard);
            }
            catch (Exception e) { Logger.LogWarning("onEvent: " + e.Message); }
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

        private bool EnsureBody()
        {
            if (_kobold != null && IsAlive(_kobold)) return true;
            if (_kobold != null) TeardownBody(); // possessed body was destroyed
            try
            {
                var playerPos = Vector3.zero;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    playerPos = pp.kobold.transform.position;

                Kobold best = null; float bestD = float.MaxValue;
                foreach (var k in FindObjectsOfType<Kobold>())
                {
                    if (k == null) continue;
                    var desc = k.GetComponent<CharacterDescriptor>();
                    if (desc == null) continue;
                    if (desc.GetPlayerControlled() != CharacterDescriptor.ControlType.AIPlayer) continue; // only unoccupied
                    float d = Vector3.Distance(k.transform.position, playerPos);
                    if (d < bestD) { bestD = d; best = k; }
                }
                // Fall back to *any* AI kobold on the map if none is within range —
                // better to possess far away than to stay inert.
                if (best == null) { Logger.LogInfo("KKLLMNPC: no AI kobold found on map."); return false; }
                if (bestD > _cfgAutoFindRange.Value)
                    Logger.LogInfo($"KKLLMNPC: nearest AI kobold is {F(bestD)}m (AutoFindRange={_cfgAutoFindRange.Value}m) — possessing anyway.");
                Possess(best);
                return true;
            }
            catch (Exception e) { Logger.LogError("EnsureBody: " + e); return false; }
        }

        private void Possess(Kobold target)
        {
            if (target == null) return;
            TeardownBody();
            if (!IsAlive(target)) return; // died between selection and possess
            _kobold = target;
            _npcName = PickName(target);   // choose a name for this body
            Logger.LogInfo("KKLLMNPC: this kobold calls itself '" + _npcName + "'");
            _yawDeg = target.transform.eulerAngles.y; // start from current facing
            _controller = target.GetComponent<KoboldCharacterController>();
            _descriptor = target.GetComponent<CharacterDescriptor>();
            _user       = target.GetComponentInChildren<User>(true);
            _grabber    = target.GetComponentInChildren<Grabber>(true);
            _charAnimator = target.GetComponentInChildren<CharacterControllerAnimator>(true);

            // Deterministic ownership: transfer to us *and* request (covers both
            // Takeover and Request Photon transfer modes). Re-assert until IsMine.
            _photonView = target.GetComponent<PhotonView>();
            try { if (_photonView != null && !_photonView.IsMine) _photonView.TransferOwnership(PhotonNetwork.LocalPlayer); } catch (Exception) { }
            try { if (_photonView != null && !_photonView.IsMine) _photonView.RequestOwnership(); } catch (Exception) { }

            if (_descriptor != null) _descriptor.SetPlayerControlled(CharacterDescriptor.ControlType.NetworkedPlayer);
            var ai = target.GetComponentInChildren<KoboldAIPossession>(true);
            if (ai != null) ai.enabled = false;
            var seeker = target.GetComponentInChildren<KoboldSeeker>(true);
            if (seeker != null) seeker.enabled = false;
            // Stop the game's LookAtHandler from driving the head/body — we manage it,
            // otherwise it fights our rigidbody steering and produces the sideways drift.
            if (_charAnimator != null) { try { _charAnimator.SetLookEnabled(false); } catch (Exception) { } }
            _navTarget = null; _navTargetName = null;

            // Reagent awareness: listen to the belly container for drink/spray/metabolize events.
            _bellySnapshotPending = true; // first OnChange after possessing = baseline, not a gift
            SubscribeBelly(target);
            // Penetration awareness: subscribe each penetrable on the body so we know
            // when something penetrates and how deep; and each penetrator so we know
            // when the kobold's own dick is inside something.
            SyncPenetrationSubscriptions(target);

            // Resolve the head bone for the eye/camera.
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    if (_kobold != target || !IsAlive(target)) return; // body changed meanwhile
                    var desc = _descriptor;
                    var anim = desc != null && IsAlive(desc) ? desc.GetDisplayAnimator() : null;
                    _head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : target.transform;
                    SetupCamera();
                    Logger.LogInfo($"KKLLMNPC: possessed '{target.name}'");
                }
                catch (Exception e) { Logger.LogError("Possess head/cam: " + e); }
            });
        }

        // ------------------------------------------------------------------
        // reagent awareness
        // ------------------------------------------------------------------
        private ScriptableReagent _eggReagent; // resolved lazily

        // How much egg reagent the kobold is carrying (needs a laying station).
        // Game rule: OvipositionSpot.CanUse = belly egg volume > 5 AND energy > 1.
        private float GetEggVolume(Kobold k)
        {
            try
            {
                if (_eggReagent == null && !Database<ScriptableReagent>.TryGetAsset("Egg", out _eggReagent)) return 0f;
                if (_eggReagent == null) return 0f;
                var c = k.bellyContainer;
                return c != null ? c.GetVolumeOf(_eggReagent) : 0f;
            }
            catch (Exception) { return 0f; }
        }

        private bool IsReadyToLayEgg(Kobold k)
        {
            try { return GetEggVolume(k) > 5f && k.GetEnergy() > 1f; } catch (Exception) { return false; }
        }

        private void SubscribeBelly(Kobold target)
        {
            UnsubscribeBelly();
            try
            {
                var c = target.bellyContainer;
                if (c != null)
                {
                    c.OnChange += OnBellyReagentsChanged;
                    _bellySubscribed = c;
                    Logger.LogInfo("KKLLMNPC: listening to belly container on '" + target.name + "'.");
                }
            }
            catch (Exception e) { Logger.LogWarning("subscribe belly: " + e.Message); }
        }

        private void UnsubscribeBelly()
        {
            try { if (_bellySubscribed != null) { _bellySubscribed.OnChange -= OnBellyReagentsChanged; _bellySubscribed = null; } }
            catch (Exception) { }
        }

        // Fired by the game when the kobold's belly container changes. We translate
        // the inject type + reagent mix into a natural-language event the LLM sees:
        //   drank/metabolized/sprayed/flooded/vacuumed "Water" (5ml) ...
        private void OnBellyReagentsChanged(ReagentContents contents, GenericReagentContainer.InjectType injectType)
        {
            try
            {
                string what = DescribeContents(contents);
                if (string.IsNullOrEmpty(what)) return;

                // The very first OnChange after possession just reports whatever the
                // belly already contained (game seeds initial contents) — don't phrase
                // that as "drank"/"filled up", or the NPC thinks it was just fed.
                if (_bellySnapshotPending)
                {
                    _bellySnapshotPending = false;
                    _lastBellySummary = what;
                    Logger.LogInfo("reagent baseline (existing belly): " + what);
                    return;
                }

                string how;
                switch (injectType)
                {
                    case GenericReagentContainer.InjectType.Metabolize: how = "metabolized"; break;
                    case GenericReagentContainer.InjectType.Spray:      how = "sprayed"; break;
                    case GenericReagentContainer.InjectType.Flood:      how = "filled up with"; break;
                    case GenericReagentContainer.InjectType.Vacuum:     how = "pumped out"; break;
                    default:                                            how = "drank"; break; // Inject = consume
                }
                string ev = how + " " + what;

                // Dedupe: if nothing materially changed, don't re-report the same mix.
                if (what == _lastBellySummary) return;
                _lastBellySummary = what;

                lock (_reagentEvents)
                {
                    _reagentEvents.Enqueue(ev);
                    while (_reagentEvents.Count > 6) _reagentEvents.Dequeue();
                }
                RememberFact(how + " " + what.Split(' ')[0]); // remember *what*, not the volume
                Logger.LogInfo("reagent: " + ev);
            }
            catch (Exception e) { Logger.LogWarning("reagent event: " + e.Message); }
        }

        // Enumerate a ReagentContents ("Water x5ml" or "Water+Cum x3ml") — falls back
        // to "something" if the database lookup misses.
        private string DescribeContents(ReagentContents contents)
        {
            try
            {
                if (contents == null || contents.Count == 0) return null;
                var parts = new List<string>();
                foreach (Reagent r in contents)
                {
                    string name = "fluid#" + r.id;
                    ScriptableReagent sr;
                    if (Database<ScriptableReagent>.TryGetAsset((short)r.id, out sr) && sr != null)
                    {
                        // Localization assemblies aren't referenced — fall back to the
                        // asset's Unity object name (that's what modders use in-game).
                        try { if (!string.IsNullOrEmpty(sr.name)) name = sr.name; } catch (Exception) { }
                    }
                    parts.Add(name + " " + F(r.volume) + "ml");
                }
                return string.Join(", ", parts.ToArray());
            }
            catch (Exception) { return null; }
        }

        // Drain queued reagent events as a list of strings, clearing them.
        private List<object> DrainReagentEvents()
        {
            lock (_reagentEvents)
            {
                var outp = new List<object>();
                while (_reagentEvents.Count > 0) outp.Add(_reagentEvents.Dequeue());
                return outp;
            }
        }

        // Compress the level row of the ray fan into per-direction clearance so the
        // model can reason "open right" instead of parsing 27 rays.
        private object FanClearance(Vector3 originForFan)
        {
            try
            {
                // Probe 8 compass directions, chest height, RayRange.
                string[] names = { "front", "front-right", "right", "back-right", "back", "back-left", "left", "front-left" };
                float[] offs = { 0f, 45f, 90f, 135f, 180f, -135f, -90f, -45f };
                var outp = new Dictionary<string, object>();
                float range = _cfgRayRange.Value;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, _yawDeg + offs[i], 0) * Vector3.forward;
                    RaycastHit hit;
                    float d = range;
                    if (Physics.Raycast(originForFan, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                        d = hit.distance;
                    // Bucket into coarse but useful distances.
                    string bucket = d < 1f ? "blocked" : d < 3f ? "close(" + F(d) + ")" : d < 8f ? "near(" + F(d) + ")" : "open";
                    outp[names[i]] = bucket;
                }
                return outp;
            }
            catch (Exception) { return new { }; }
        }

        // True when the possessed kobold is inside an animation station (bed, mount, etc.).
        // The game tracks this on CharacterControllerAnimator; used to tell the model
        // it's stuck and to gate movement (you can't walk out of a station).
        private bool IsInAnimationStation()
        {
            try { return _charAnimator != null && _charAnimator.IsAnimating(); }
            catch (Exception) { return false; }
        }

        private void TeardownBody()
        {
            if (_cam != null) { try { Destroy(_cam.gameObject); } catch (Exception) { } _cam = null; }
            if (_rt != null) { try { _rt.Release(); Destroy(_rt); } catch (Exception) { } _rt = null; }
            UnsubscribeBelly();
            UnsubscribePenetrables();
            if (_charAnimator != null) { try { _charAnimator.SetLookEnabled(true); } catch (Exception) { } }
            if (_descriptor != null)
            {
                try { _descriptor.SetPlayerControlled(CharacterDescriptor.ControlType.AIPlayer); } catch (Exception) { }
            }
            _kobold = null; _controller = null; _descriptor = null; _user = null; _grabber = null;
            _charAnimator = null; _head = null; _photonView = null;
            _navTarget = null; _navTargetName = null;
            StopMove();
        }

        private void SetupCamera()
        {
            if (_head == null) return;
            var go = new GameObject("LLMNPC_Cam");
            go.transform.SetParent(_head, false);
            // In FRONT of the muzzle so we don't see the inside of the head/eyes/teeth,
            // with a near clip that starts past them.
            go.transform.localPosition = new Vector3(0f, 0.06f, _cfgCamForward.Value);
            go.transform.localRotation = Quaternion.identity;
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = 90f;
            _cam.nearClipPlane = _cfgCamNearClip.Value; // clip past face geometry (~eyes & snout)
            _cam.farClipPlane = 80f;
            _cam.enabled = false; // we render on demand
            if (RtCreated(_rt)) { try { _rt.Release(); Destroy(_rt); } catch (Exception) { } }
            _rt = new RenderTexture(_cfgImageSize.Value, _cfgImageSize.Value, 16, RenderTextureFormat.ARGB32);
            _rt.Create();
            _cam.targetTexture = _rt;
        }

        // ------------------------------------------------------------------
        // movement plumbing: LLM thread sets fields; FixedUpdate applies them
        // ------------------------------------------------------------------
        private void SetMove(float forwardSpeed, bool jump, float turnDeg, float durationSec, bool run)
        {
            lock (_stateLock)
            {
                _moveLocalZ = Mathf.Clamp(forwardSpeed, -8f, 8f);
                _moveJump = jump;
                _moveRun = run;                          // false => use the game's walk speed
                _yawOffsetDeg = turnDeg;
                // Walk only for this long, then auto-stop — one command = one burst.
                _moveUntilTime = durationSec > 0f
                    ? Time.unscaledTime + durationSec
                    : 0f; // 0 = run until told otherwise
            }
        }
        // Pick an identity for the body: species hint from its name (e.g. "AbsolB"
        // → base name), plus a short suffix derived from its equipment so it's stable
        // per body within this session without asking the model.
        private string PickName(Kobold target)
        {
            string raw = CleanName(target.name);            // "AbsolB(Clone)" -> "AbsolB"
            if (raw.Length == 0) raw = "Kobold";
            return raw;
        }

        // What the player should call it in chat / what's in perception as "me".
        private string MyName()
        {
            return string.IsNullOrEmpty(_npcName) ? (_kobold != null ? CleanName(_kobold.name) : "NPC") : _npcName;
        }

        // Small unsolicited reactions to stimuli the LLM might miss or react to too
        // slowly. Only fires on state changes; cooldown keeps it from spamming.
        private void MaybeAmbientComment()
        {
            if (!IsAlive(_kobold)) return;
            if (Time.unscaledTime - _lastAmbientTime < 4f) return;

            // Arousal spiking while being played with — moan without waiting on the LLM.
            if (IsInAnimationStation() || IsPenetrated() || IsDickInside())
            {
                float stim = _kobold.stimulation;
                if (_ambientStimPrev >= 0f && stim > _ambientStimPrev + 0.06f)
                {
                    EmitAmbient("mmm~");
                    _ambientStimPrev = stim;
                    return;
                }
            }
            _ambientStimPrev = _kobold.stimulation;
        }

        // Say a short ambient line: bubble + console, no chat-window spam.
        private void EmitAmbient(string text)
        {
            _lastAmbientTime = Time.unscaledTime;
            Logger.LogInfo("[" + MyName() + "] " + text);
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    var chatter = _kobold != null ? _kobold.GetComponentInChildren<Chatter>(true) : null;
                    if (chatter != null) chatter.DisplayMessage(text, 2f);
                }
                catch (Exception) { }
            });
        }

        // direction of stimulation, reused by ambient.
        private float _ambientStimPrev = -1f;

        // True if the kobold was addressed by its name in the chat text.
        private bool AddressedToMe(string chat)
        {
            if (string.IsNullOrEmpty(chat)) return false;
            string me = MyName().ToLowerInvariant();
            string c = chat.ToLowerInvariant();
            return c.Contains(me) || c.StartsWith(me + ",") || c.StartsWith("hey " + me);
        }

        private void StopMove()
        {
            lock (_stateLock) { _moveLocalZ = 0f; _moveJump = false; _moveUntilTime = 0f; _crouch = 0f; }
            // Also zero the controller immediately — otherwise it keeps the last
            // inputDir applied and drifts until the next physics write.
            if (_controller != null)
            {
                try { _controller.inputDir = Vector3.zero; _controller.inputJump = false; } catch (Exception) { }
            }
        }

        private void FixedUpdate()
        {
            try { FixedUpdateSafe(); }
            catch (Exception e)
            {
                // A destroyed/invalid body must never take down the physics loop.
                if (Time.frameCount % 120 == 0) Logger.LogWarning("FixedUpdate: " + e.Message);
            }
        }

        private void FixedUpdateSafe()
        {
            if (_controller == null || _kobold == null) return;
            if (!IsPlayableScene() || !IsAlive(_kobold) || !IsAlive(_controller)) { RunOnMainThreadAsync(TeardownBody); return; }

            // Ownership watchdog: controller only applies velocity when we own the
            // PhotonView. Re-assert a few times (other mods can transfer it away).
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom
                && Time.unscaledTime - _lastOwnershipTry > 3f)
            {
                _lastOwnershipTry = Time.unscaledTime;
                Logger.LogWarning($"KKLLMNPC: not owner of '{_kobold.name}' (owner={_photonView.Owner?.NickName ?? "?"}) — re-asserting");
                try { _photonView.TransferOwnership(PhotonNetwork.LocalPlayer); } catch (Exception) { }
                try { if (!_photonView.IsMine) _photonView.RequestOwnership(); } catch (Exception) { }
            }

            float fwd, turn, crouch; bool jump, run; float until;
            lock (_stateLock) { fwd = _moveLocalZ; turn = _yawOffsetDeg; jump = _moveJump; run = _moveRun; crouch = _crouch; _yawOffsetDeg = 0f; until = _moveUntilTime; }

            // If a go_to target is active, steer toward it (a bounded "approach"
            // behavior); clear it once we're close or the walk burst expired.
            if (_navTarget.HasValue)
            {
                Vector3 toT = _navTarget.Value - _kobold.transform.position;
                toT.y = 0;
                float dist = toT.magnitude;
                if (dist < 0.8f)
                {
                    _navTarget = null;
                    StopMove();
                    _blockedInfo = "arrived" + (_navTargetName != null ? " at " + _navTargetName : "");
                }
                else
                {
                    // Keep heading fresh so the obstacle-steer fans around the target.
                    float yaw = Mathf.Atan2(toT.x, toT.z) * 57.29578f;
                    _yawDeg = Mathf.Repeat(yaw, 360f);
                }
            }

            // Burst timed out — stop before applying any more drive.
            if (until > 0f && Time.unscaledTime >= until)
            {
                lock (_stateLock) { _moveLocalZ = 0f; _moveUntilTime = 0f; }
                fwd = 0f;
            }

            // When there's no active walk command, zero the drive every frame so no
            // residual inputDir lingers between LLM calls (the "magnetize" drift).
            if (Mathf.Approximately(fwd, 0f))
            {
                try { _controller.inputDir = Vector3.zero; } catch (Exception) { }
                // And bleed off the rigidbody's leftover velocity — Friction() alone
                // is too gentle and only runs when the controller considers itself
                // in control.
                if (_controller.body != null)
                {
                    var rb = _controller.body;
                    Vector3 v = rb.velocity;
                    v.x *= 0.78f; v.z *= 0.78f; // per-physics-frame damping
                    if (Mathf.Abs(v.x) < 0.05f) v.x = 0f;
                    if (Mathf.Abs(v.z) < 0.05f) v.z = 0f;
                    try { rb.velocity = v; } catch (Exception) { }
                    // And kill any residual spin so the facing axis stops creeping.
                    try { rb.angularVelocity = Vector3.zero; } catch (Exception) { }
                }
            }

            // Turning rotates the rigidbody (whole body) via physics, not the
            // transform — so animations and colliders follow and nothing clips.
            if (Mathf.Abs(turn) > 0.001f)
                _yawDeg = Mathf.Repeat(_yawDeg + turn, 360f);

            // WALK VALIDATION: raycast where we're about to go. If blocked ahead,
            // don't walk into it — stop forward drive and auto-steer to a clear
            // heading, then report the blockage so the LLM picks a new direction.
            _blockedInfo = null;
            // Wall-proximity scan every physics frame: short rays in 4 directions
            // around the body so the model knows "wall on my left" before it hits.
            _bumpInfo = null;
            ProbeWallProximity();
            float fwdOut = Mathf.Clamp(fwd, -1f, 1f);

            // "Hit a wall": trying to move but the body barely advances (friction
            // holds us against an obstacle the forward ray already caught).
            if (fwdOut != 0f && _controller.body != null)
            {
                Vector3 hv = _controller.body.velocity; hv.y = 0;
                if (hv.magnitude < 0.15f && Time.unscaledTime - _lastBumpTime > 0.6f)
                {
                    _lastBumpTime = Time.unscaledTime;
                    _blockedInfo = "bumped a wall — turn";
                    _needImageAfterBump = true; // show the model what it hit
                }
            }
            if (fwdOut > 0.01f)
            {
                Vector3 eye = _head != null
                    ? _head.position
                    : _kobold.transform.position + Vector3.up * 0.6f;
                eye += Vector3.up * -0.15f; // chest height, not eye, for body clearance
                Vector3 dir = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward;
                RaycastHit hit;
                if (Physics.Raycast(eye, dir, out hit, WalkProbeRange, ~0, QueryTriggerInteraction.Ignore)
                    && !IsOwnCollider(hit.collider))
                {
                    // Surface in front — try to find a clear heading by fan-steering.
                    float steer = FindClearHeading(eye, hit.normal);
                    if (steer != 0f)
                    {
                        _yawDeg = Mathf.Repeat(_yawDeg + steer, 360f);
                        _blockedInfo = "blocked; steered " + (steer > 0 ? "right" : "left");
                    }
                    else
                    {
                        fwdOut = 0f;
                        _blockedInfo = "blocked, no clear way (dist " + F(hit.distance) + ")";
                    }
                }
                // LEDGE GUARD: only hard-stop on a *big* drop. Small drops (<=1.6m)
                // are fine to hop down — and if the model is jumping, it's choosing
                // to go over on purpose. Report the drop height so it can decide.
                if (fwdOut > 0.01f && _blockedInfo == null)
                {
                    Vector3 aheadDown = _kobold.transform.position + dir * 0.8f + Vector3.up * 0.5f;
                    RaycastHit lhit;
                    float drop = Physics.Raycast(aheadDown, Vector3.down, out lhit, 8f, ~0, QueryTriggerInteraction.Ignore)
                        ? lhit.distance - 0.5f : 8f;
                    _ledgeDrop = drop > 0.5f ? drop : (float?)null; // expose to perception
                    if (drop > 2.6f && !jump)   // big fall → stop
                    {
                        fwdOut = 0f;
                        _blockedInfo = "big drop ahead (" + F(drop) + "m); stopped";
                        _needImageAfterBump = true;
                    }
                    else if (drop > 0.7f && drop <= 2.6f && !jump)
                        _blockedInfo = "ledge " + F(drop) + "m — you can walk off or jump down";
                    // jump=true or small drop → let it proceed (controller handles fall).
                }
            }

            // inputDir is consumed as a world-space direction by the controller's
            // Accelerate(); walls stop it via normal rigidbody collision.
            Vector3 worldDir = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward * fwdOut;
            _controller.inputDir = new Vector3(worldDir.x, 0f, worldDir.z);
            _controller.inputJump = jump;
            // Walk vs run: inputWalking=true scales effectiveSpeed down by the
            // game's walkSpeedMultiplier; default is walk, run only when asked.
            try { _controller.inputWalking = !run; } catch (Exception) { }
            try { _controller.SetInputCrouched(crouch); } catch (Exception) { }
            if (jump) lock (_stateLock) { _moveJump = false; } // one-shot

            // Body rotation: NOT driven by us. The game's own LookAtHandler handles
            // the head; the body turns through locomotion (inputDir direction carried
            // by the controller's own FixedUpdate). When the kobold idles, its body
            // keeps whatever bearing it had — that matches real players. Rigidbody
            // MoveRotation here caused the left-direction drift.
            //
            // Head + camera: eyeRot drives absolute world yaw/pitch for the look.
            if (_charAnimator != null)
                _charAnimator.SetEyeRot(new Vector2(_yawDeg, -_pitchDeg));
            if (_cam != null)
                _cam.transform.rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);

            // CAMERA-CLIP detection + auto-crouch fix. If the head/camera is buried
            // in geometry (rays from the head hit something nearer than the camera
            // offset, or we collide with something right at the head), the first-person
            // image is uselessly full of face/wall. Ease crouch up to clear it — the
            // camera rides up as the body lowers.
            CheckCameraClip();

            // Ambient commentary: react to what's happening even when not in a station.
            MaybeAmbientComment();

            // While mounted on a station, body is driven by the animator; we still
            // steer the eyes/camera so it visibly looks around instead of staring
            // blankly, and react to rising arousal.
            if (IsInAnimationStation())
            {
                // If someone's penetrating us (or we're inside someone), lock gaze on
                // them — like watching who you're with. Otherwise wander.
                Transform partner = MostRecentPenetrationSource();
                if (partner != null)
                {
                    Vector3 to = partner.position - _head.position;
                    Vector3 flat = to; flat.y = 0;
                    float desiredYaw = Mathf.Atan2(flat.x, flat.z) * 57.29578f;
                    float desiredPitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * 57.29578f, -89f, 89f);
                    _yawDeg = MoveAngleTowards(_yawDeg, desiredYaw, 80f * Time.fixedDeltaTime);
                    _pitchDeg = Mathf.MoveTowards(_pitchDeg, desiredPitch, 80f * Time.fixedDeltaTime);
                }
                else
                {
                    _gazeTimer -= Time.fixedDeltaTime;
                    if (_gazeTimer <= 0f)
                    {
                        _gazeTimer = UnityEngine.Random.Range(0.8f, 2.2f);
                        _gazeYaw = UnityEngine.Random.Range(-35f, 35f);
                        _gazePitch = UnityEngine.Random.Range(-12f, 18f);
                    }
                    // Slowly drift the gaze toward the current target so it looks like it's scanning.
                    _yawDeg = Mathf.Repeat(_yawDeg + Mathf.Clamp(_gazeYaw, -1f, 1f) * 20f * Time.fixedDeltaTime, 360f);
                    _pitchDeg = Mathf.MoveTowards(_pitchDeg, Mathf.Clamp(_gazePitch, -89f, 89f), 20f * Time.fixedDeltaTime);
                }

                // Arousal reaction: on a noticeable stim jump, make a sound (koba's
                // chatter yowl pack) — visible to nearby players, like the real thing.
                float stim = _kobold.stimulation;
                if (_lastStim >= 0f && stim > _lastStim + 0.08f && Time.unscaledTime - _lastMoan > 3f)
                {
                    _lastMoan = Time.unscaledTime;
                    RunOnMainThreadAsync(() =>
                    {
                        try
                        {
                            var chatter = _kobold.GetComponentInChildren<Chatter>(true);
                            if (chatter != null)
                            {
                                // Use the game's yowl pack directly — one short vocal.
                                var pack = typeof(Chatter).GetField("yowls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                    ?.GetValue(chatter) as AudioPack;
                                var src = chatter.GetComponent<AudioSource>();
                                if (pack != null && src != null) { pack.PlayOneShot(src); return; }
                                // Fallback: tiny self-talk bubble so others see something.
                                chatter.DisplayMessage("~", 1.2f);
                            }
                        }
                        catch (Exception e) { Logger.LogWarning("moan: " + e.Message); }
                    });
                }
                _lastStim = stim;
            }
            else _lastStim = -1f;
        }

        // ------------------------------------------------------------------
        // senses
        // ------------------------------------------------------------------
        private object BuildPerception(bool includeImage)
        {
            try { return BuildPerceptionSafe(includeImage); }
            catch (Exception e) { return new { ok = false, reason = "perception_error", msg = e.Message }; }
        }

        private object BuildPerceptionSafe(bool includeImage)
        {
            if (!IsAlive(_kobold) || !IsAlive(_head)) return new { ok = false, reason = "no_body" };

            var rays = new List<object>();
            int n = Mathf.Max(1, _cfgRayCount.Value);
            float range = _cfgRayRange.Value;
            Vector3 origin = _head.position + _head.forward * 0.1f;
            float fov = _cam != null ? _cam.fieldOfView : 90f;
            float aspect = 1f;

            // Vertical rows: down (floor/ledges), level (obstacles), up (ceilings/high).
            // Each entry: a(ngle) d(ist) k(ind w/u/k/p/n) and pitch row p(d/l/u).
            float[] rows = { -35f, 0f, 25f };
            string[] rowNames = { "d", "l", "u" };
            for (int rI = 0; rI < rows.Length; rI++)
            {
                float pitchRow = rows[rI];
                for (int i = 0; i < n; i++)
                {
                    float t = n == 1 ? 0.5f : (float)i / (n - 1);
                    float hAngle = Mathf.Lerp(-fov * aspect * 0.5f, fov * aspect * 0.5f, t);
                    var dir = Quaternion.Euler(0, _yawDeg + hAngle, 0) * Quaternion.Euler(_pitchDeg + pitchRow, 0, 0) * Vector3.forward;
                    RaycastHit hit;
                    object r;
                    if (Physics.Raycast(origin, dir, out hit, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                    {
                        string name = "", kind = "w";
                        try
                        {
                            if (hit.collider != null)
                            {
                                var kb = hit.collider.GetComponentInParent<Kobold>();
                                var usable = hit.collider.GetComponentInParent<GenericUsable>();
                                if (kb != null) { kind = IsPlayerKobold(kb) ? "p" : "k"; name = kb.name; }
                                else if (usable != null) { kind = "u"; name = usable.name; }
                                else
                                {
                                    // Thin sill/window/ledge: low collision height means
                                    // you can see over it and probably climb it — "barrier".
                                    float topH = ProbeSurfaceTop(hit.point);
                                    if (topH < 1.35f) kind = "barrier";
                                }
                            }
                        }
                        catch (Exception) { }
                        r = name.Length == 0
                            ? (object)new { p = rowNames[rI], a = F(hAngle), d = F(hit.distance), k = kind }
                            : new { p = rowNames[rI], a = F(hAngle), d = F(hit.distance), k = kind, n = name };
                    }
                    else r = new { p = rowNames[rI], a = F(hAngle), k = "n" };
                    rays.Add(r);
                }
            }

            var pos = _kobold.transform.position;
            var ground = ProbeGround(pos);
            var clearance = FanClearance(originForFan: _head.position);

            return new {
                ok = true,
                me = MyName(),
                body = DescribeEquipment(),
                pos = new { x = F(pos.x), y = F(pos.y), z = F(pos.z) },
                yaw = F(_yawDeg),
                blocked = _blockedInfo,
                walls = _bumpInfo,
                ground = ground,
                clearance = clearance,
                vis_go = _visionSteer != null ? _visionSteer.deg.ToString("0") + "deg (" + _visionSteer.reason + ")" : null,
                needs = new {
                    energy = F(_kobold.GetEnergy()) + "/" + F(_kobold.GetMaxEnergy()),
                    horniness = F(_kobold.stimulation) + StimTrend(_kobold.stimulation)
                              + (_kobold.stimulation > 0.5f ? " very" : _kobold.stimulation > 0.25f ? "" : " low"),
                    eggs = F(GetEggVolume(_kobold)) + (IsReadyToLayEgg(_kobold) ? " ready_to_lay" : ""),
                    crouch = F(_crouch),
                },
                consumed = DrainReagentEvents(),
                in_station = IsInAnimationStation(),
                penetrated = IsPenetrated() ? PenetrationInfo() : null,
                penetrating = IsDickInside() ? DickInInfo() : null,
                heard = RecentPlayerChat(),
                asked = _pendingQuestion,
                answered = _answerBusy ? null : _lastAnswer,
                grabbed = _kobold.grabbed,
                rays = rays,
                nearby = DescribeNearby(),
            };
        }

        // The body's actual world yaw right now (rigidbody first, transform fallback).
        private float BodyYaw()
        {
            try
            {
                if (_controller != null && _controller.body != null) return _controller.body.rotation.eulerAngles.y;
                if (_kobold != null) return _kobold.transform.eulerAngles.y;
            }
            catch (Exception) { }
            return _yawDeg;
        }

        // True when k is the local human player's body (not an AI/wild kobold).
        private static bool IsPlayerKobold(Kobold k)
        {
            try
            {
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null && pp.kobold == k) return true;
                var desc = k.GetComponent<CharacterDescriptor>();
                return desc != null && desc.GetPlayerControlled() == CharacterDescriptor.ControlType.LocalPlayer;
            }
            catch (Exception) { return false; }
        }

        // What's underfoot / ahead at floor level: ground distance, whether we're
        // supported, and if a ledge or a low step is in front.
        private object ProbeGround(Vector3 pos)
        {
            try
            {
                // Down from the body.
                float groundDist = 0f; bool supported = false;
                RaycastHit ghit;
                if (Physics.Raycast(pos + Vector3.up * 0.6f, Vector3.down, out ghit, 10f, ~0, QueryTriggerInteraction.Ignore)
                    && !IsOwnCollider(ghit.collider))
                {
                    groundDist = ghit.distance - 0.6f;
                    supported = groundDist < 0.5f;
                }

                // Forward at knee vs. chest vs. head height to tell step vs window
                // vs wall.
                Vector3 fwd = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward;
                bool kneeBlocked  = CastBlocked(pos + Vector3.up * 0.3f, fwd, 1.3f);
                bool chestBlocked = CastBlocked(pos + Vector3.up * 0.9f, fwd, 1.3f);
                bool headBlocked  = CastBlocked(pos + Vector3.up * 1.6f, fwd, 1.3f);
                string ahead = "clear";
                if (kneeBlocked && !chestBlocked && !headBlocked) ahead = "step";         // auto-step
                else if (chestBlocked && !headBlocked) ahead = "sill";                    // window/counter — look over it, maybe climb
                else if (kneeBlocked || chestBlocked || headBlocked) ahead = "wall";     // solid

                // Ledge: clear at knee/chest but ground drops away just past our feet.
                bool ledge = false;
                if (!kneeBlocked && !chestBlocked)
                {
                    RaycastHit lhit;
                    Vector3 aheadDown = pos + fwd * 0.7f + Vector3.up * 0.5f;
                    if (!Physics.Raycast(aheadDown, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore) ||
                        (Physics.Raycast(aheadDown, Vector3.down, out lhit, 6f, ~0, QueryTriggerInteraction.Ignore) && lhit.distance > 3f))
                        ledge = true;
                }

                return new { dist = F(groundDist), supported, ahead, ledge, drop = _ledgeDrop.HasValue ? F(_ledgeDrop.Value) : null };
            }
            catch (Exception) { return new { dist = "0", supported = true, ahead = "clear", ledge = false }; }
        }

        // Short 4-way rays around the body (front/back/left/right at chest height)
        // recording which directions have a wall within arm's reach. Result goes
        // into the next perception as `walls`.
        private void ProbeWallProximity()
        {
            try
            {
                if (!IsAlive(_kobold)) return;
                Vector3 center = _kobold.transform.position + Vector3.up * 0.6f;
                const float R = 1.1f;
                string[] names = { "front", "right", "back", "left" };
                float[] offs = { 0f, 90f, 180f, -90f };
                var found = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, _yawDeg + offs[i], 0) * Vector3.forward;
                    if (CastBlocked(center, dir, R)) found.Add(names[i]);
                }
                if (found.Count > 0) _bumpInfo = "wall " + string.Join("+", found.ToArray());
            }
            catch (Exception) { }
        }

        private bool CastBlocked(Vector3 origin, Vector3 dir, float range)
        {
            RaycastHit h;
            return Physics.Raycast(origin, dir, out h, range, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(h.collider);
        }

        // How tall is the obstacle at the hit point? Stack CheckSphere upward from
        // the hit; the first free height is the obstacle's top. <~1.3m => sill/
        // ledge/window (barrier), >= => real wall. Works for glass since it reads
        // collision geometry, not opacity.
        private float ProbeSurfaceTop(Vector3 hitPoint)
        {
            try
            {
                float baseY = _kobold != null ? _kobold.transform.position.y : hitPoint.y - 1f;
                for (float up = 0.15f; up <= 2.0f; up += 0.15f)
                {
                    var p = new Vector3(hitPoint.x, baseY + up, hitPoint.z);
                    var colliders = Physics.OverlapSphere(p, 0.09f, ~0, QueryTriggerInteraction.Ignore);
                    bool blocked = false;
                    foreach (var c in colliders) { if (c != null && !IsOwnCollider(c)) { blocked = true; break; } }
                    if (!blocked) return up; // first height with clear air
                }
                return 2.5f; // still blocked at head height — real wall
            }
            catch (Exception) { return 2.5f; }
        }

        // Convert a world offset into a compass bearing the model can act on directly,
        // relative to where the kobold is facing: "ahead", "right", "behind-left"…
        private string RelBearing(Vector3 to)
        {
            to.y = 0;
            if (to.sqrMagnitude < 0.0001f) return "here";
            float ang = Mathf.Atan2(to.x, to.z) * 57.29578f;   // absolute yaw of target
            float rel = Mathf.DeltaAngle(_yawDeg, ang);        // signed relative to facing
            float a = Mathf.Abs(rel);
            if (a < 22.5f) return "ahead";
            if (a < 67.5f) return rel > 0 ? "front-right" : "front-left";
            if (a < 112.5f) return rel > 0 ? "right" : "left";
            if (a < 157.5f) return rel > 0 ? "back-right" : "back-left";
            return "behind";
        }

        private float _lastGreetTime = -99f;
        private float _playerSeenTime = -99f;
        private float _lastStimLevel = -1f;   // for stim trend calc
        private float _lastAmbientTime = -99f; // when we last said something unprompted

        // Direction of stimulation as a short suffix: "↑"/"↓"/"" so the model sees it changing.
        private string StimTrend(float stim)
        {
            string t;
            if (_lastStimLevel < 0f) t = "";
            else if (stim > _lastStimLevel + 0.01f) t = "↑";
            else if (stim < _lastStimLevel - 0.01f) t = "↓";
            else t = "";
            _lastStimLevel = stim;
            return t;
        }

        // Idle behavior while in a station: wandering gaze + reaction to arousal.
        private float _gazeTimer;
        private float _gazeYaw;
        private float _gazePitch;
        private float _lastStim = -1f;
        private float _lastMoan;

        // Heuristic camera-clip detector: the first-person camera sits ahead of the
        // head at _cfgCamForward; if a short forward ray from just behind the camera
        // hits geometry *closer than the camera*, the lens is inside something and
        // the image is mostly face/wall. When detected, ease crouch up (so the head
        // rises with the body as it drops) until it clears or maxes out.
        private void CheckCameraClip()
        {
            if (!IsAlive(_head) || !IsAlive(_cam)) { _clipSince = -99f; return; }
            float camFwd = _cfgCamForward.Value;
            // Ray from just behind the camera toward view direction; shorter distance
            // than the camera's forward offset means the camera pokes *into* geometry.
            Vector3 start = _cam.transform.position - _cam.transform.forward * Math.Max(0.02f, camFwd * 0.8f);
            RaycastHit hit;
            bool clipped = Physics.Raycast(start, _cam.transform.forward, out hit, camFwd, ~0, QueryTriggerInteraction.Ignore)
                           && !IsOwnCollider(hit.collider);

            if (clipped)
            {
                if (_clipSince < 0f) _clipSince = Time.unscaledTime;
                // Only interfere if the model hasn't explicitly chosen a crouch level.
                if (Time.unscaledTime - _manualCrouchSet < 5f) return;
                if (Time.unscaledTime - _clipSince > 0.4f && Time.unscaledTime - _lastClipFix > 1.5f && _crouch < 1f)
                {
                    _crouch = Mathf.Min(1f, _crouch + 0.15f);
                    _lastClipFix = Time.unscaledTime;
                    _blockedInfo = "camera clipped; crouching to clear view (crouch=" + F(_crouch) + ")";
                }
            }
            else
            {
                _clipSince = -99f;
                // Ease back out if the clip cleared — but don't stomp the model's
                // explicit crouch setting.
                if (_crouch > 0f && Time.unscaledTime - _manualCrouchSet > 10f)
                {
                    _crouch = Mathf.MoveTowards(_crouch, 0f, Time.fixedDeltaTime * 0.5f);
                }
            }
        }

        // Turn "BedStation(2) (Clone)" into "BedStation".
        internal static string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            int i = n.IndexOf("(Clone", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) n = n.Substring(0, i).TrimEnd();
            i = n.IndexOf('(');
            if (i >= 0) n = n.Substring(0, i).TrimEnd();
            return n;
        }

        // Human-readable category so the model can act on needs, not object noise.
        private static string ClassifyUsable(string name)
        {
            if (string.IsNullOrEmpty(name)) return "usable";
            string s = name.ToLowerInvariant();
            if (s.Contains("bed"))        return "bed";
            if (s.Contains("toilet") || s.Contains("potty") || s.Contains("bathroom")) return "toilet";
            if (s.Contains("tub") || s.Contains("bath") || s.Contains("shower")) return "bath";
            if (s.Contains("breeding") || s.Contains("threeway") || s.Contains("mount") || s.Contains("actionstation")) return "play";
            if (s.Contains("sex") ) return "play";
            if (s.Contains("laying") || s.Contains("ovip") || s.Contains("nest") || s.Contains("egg")) return "nest";
            if (s.Contains("kitchen") || s.Contains("stove") || s.Contains("blender") || s.Contains("food") || s.Contains("cook")) return "food";
            if (s.Contains("swap") || s.Contains("possess") || s.Contains("body")) return "bodyswap";
            if (s.Contains("door") || s.Contains("gate")) return "door";
            if (s.Contains("upgrade") || s.Contains("machine")) return "machine";
            if (s.Contains("table") || s.Contains("chair") || s.Contains("sofa") || s.Contains("couch")) return "seat";
            return "usable";
        }

        // ------------------------------------------------------------------
        // body awareness: what equipment does the possessed kobold have
        // ------------------------------------------------------------------
        private object DescribeEquipment()
        {
            try
            {
                var g = _kobold.GetGenes();
                bool penis = g.dickSize > 0.01f || (_kobold.activeDicks != null && _kobold.activeDicks.Count > 0);
                int penetrables = 0;
                try { penetrables = _kobold.penetratables != null ? _kobold.penetratables.Count : 0; } catch (Exception) { }
                string hole;
                if (penetrables >= 2) hole = "both";
                else if (penetrables == 1) hole = "one";
                else hole = "none";

                var parts = new List<string>();
                if (penis) parts.Add("penis");
                if (penetrables > 0) parts.Add(hole + " penetrable" + (penetrables > 1 ? "s" : ""));
                if (g.breastSize > 0.15f) parts.Add("breasts");
                if (parts.Count == 0) parts.Add("none");
                return new { with = string.Join("+", parts.ToArray()), penis, penetrables };
            }
            catch (Exception) { return new { with = "unknown", penis = false, penetrables = 0 }; }
        }

        private List<object> DescribeNearby()
        {
            var list = new List<object>();
            if (!IsAlive(_kobold)) return list;
            bool sawPlayer = false;
            try
            {
                var seen = new HashSet<int>();
                foreach (var c in Physics.OverlapSphere(_kobold.transform.position, 14f, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c == null) continue;
                    GenericUsable u = c.GetComponentInParent<GenericUsable>();
                    Kobold k = c.GetComponentInParent<Kobold>();
                    if (k != null && k == _kobold) continue;
                if (u == null && k == null) continue;
                    var rootComp = (Component)k ?? u;
                    if (!seen.Add(rootComp.transform.root.GetInstanceID() * 31 + rootComp.GetInstanceID())) continue;
                    Vector3 d = c.transform.position - _kobold.transform.position;
                    string label = k != null ? "kobold" : "usable";
                    string nm = k != null ? k.name : u.name;
                    if (k != null && IsPlayerKobold(k)) { label = "player"; sawPlayer = true; }
                    string hrel = d.y > 0.5f ? "above" : d.y < -0.5f ? "below" : "level";

                    // For usables: strip Unity's "(Clone)", report whether it's
                    // useable right now (bed free? station occupied?), and a guess
                    // at what it is so the model can plan toward needs.
                    string info = null;
                    if (u != null)
                    {
                        nm = CleanName(nm);
                        string kind = ClassifyUsable(nm);
                        bool canUse = true;
                        try { canUse = u.CanUse(_kobold); } catch (Exception) { }
                    info = kind + (canUse ? "" : ":busy");
                    // Landmark memory: remember where things are once seen.
                    if (canUse) RememberFact(kind + " is " + RelBearing(d) + " here");
                }
                    list.Add(new {
                        k = label,
                        n = nm,
                        d = F(d.magnitude),
                        dir = RelBearing(d),      // ahead/front-right/right/behind... relative to facing
                        h = hrel,                 // above/level/below
                        i = info,                 // e.g. "bed", "toilet", "sex:busy" for usables
                    });
                    if (list.Count >= 8) break;
                }
            }
            catch (Exception e) { Logger.LogWarning("nearby: " + e.Message); }

            // Notice the player: greet them the first time (and re-greet after a
            // cooldown), and record when we last saw them so the prompt can say so.
            if (sawPlayer)
            {
                bool firstSight = _playerSeenTime < -90f;
                _playerSeenTime = Time.unscaledTime;
                if ((firstSight && Time.unscaledTime - _lastGreetTime > 6f) || Time.unscaledTime - _lastGreetTime > 45f)
                {
                    _lastGreetTime = Time.unscaledTime;
                    string greet = firstSight ? "oh, hi!" : "hey again";
                    // Use ToolSay, which posts to the real chat window too.
                    RunOnMainThreadAsync(() => { try { ToolSay(new TextArgs(greet)); } catch (Exception e) { Logger.LogWarning("greet: " + e.Message); } });
                    Logger.LogInfo("KKLLMNPC: noticed player, greeting.");
                }
            }
            return list;
        }

        private string CaptureImageB64()
        {
            byte[] jpg = CaptureImageBytes();
            return jpg != null ? "data:image/jpeg;base64," + Convert.ToBase64String(jpg) : null;
        }

        private byte[] CaptureImageBytes()
        {
            if (_cam == null || !RtCreated(_rt)) return null;
            try
            {
                _cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                Texture2D tex = null;
                try
                {
                    tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
                    tex.Apply();
                    return UnityEngine.ImageConversion.EncodeToJPG(tex, 60);
                }
                finally
                {
                    RenderTexture.active = prev;
                    if (tex != null) Destroy(tex);
                }
            }
            catch (Exception e) { Logger.LogWarning("screenshot: " + e.Message); return null; }
        }

        // ------------------------------------------------------------------
        // background vision pass ("another process")
        // ------------------------------------------------------------------
        // Starts a caption job when due if one isn't already running. The render is
        // grabbed on the main thread (Unity must), then the HTTP+caption happens on
        // this background worker so the action loop never blocks on vision.
        private void MaybeCreativeCommentary(string perceptionJson)
        {
            if (_cfgCommentEvery == null || _cfgCommentEvery.Value <= 0) return;
            if (_tick - _lastCommentaryTick < Math.Max(1, _cfgCommentEvery.Value)) return;
            _lastCommentaryTick = _tick;
            string percep = perceptionJson; // captured for the worker
            var t = new Thread(() => CreativeCommentaryWorker(percep)) { IsBackground = true, Name = "KKLLMNPC-Comment" };
            t.Start();
        }

        // Ask the model for a short unprompted reaction to its current perception,
        // then say it out loud. No act schema — pure voice.
        private void CreativeCommentaryWorker(string percep)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] =
                            "You are a kobold NPC with a personality, inside a KoboldKare world. You have opinions about what you see and feel — comment freely, candidly, briefly. " +
                            "Output ONE short line of spoken dialogue (max 12 words) you'd actually say out loud right now given the scene. No quotes, no narration." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "perception:" + percep + "\nscene:" + _sceneDesc },
                    },
                    ["max_tokens"] = 40,
                    ["temperature"] = _cfgCommentTemp != null ? (double)_cfgCommentTemp.Value : 0.9,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);
                var req = (HttpWebRequest)WebRequest.Create(Val(_cfgEndpoint));
                req.Method = "POST"; req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(Val(_cfgApiKey))) req.Headers["Authorization"] = "Bearer " + Val(_cfgApiKey);
                req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return;
                    using (var sr = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        var root = Json.Parse(json) as Dictionary<string, object>;
                        var choices = root?.GetValueOrDefault("choices") as List<object>;
                        if (choices != null && choices.Count > 0)
                        {
                            var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                            string content = msg?.GetValueOrDefault("content") as string;
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                string line = content.Trim().Split('\n')[0].Trim().Trim('"', '"');
                                if (line.Length > 0 && line.Length < 200)
                                {
                                    Logger.LogInfo("[" + MyName() + "] muses: " + line);
                                    try { ToolSay(new TextArgs(line)); } catch (Exception) { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("commentary: " + e.Message); }
        }

        private void MaybeStartVisionPass()
        {
            if (!_cfgVision.Value || !_mainReady) return;
            if (_visionBusy)
            {
                // Reset a wedged vision flag (hung HTTP, etc.) after 2 minutes.
                if (Time.unscaledTime - _visionStartTime > 120f) _visionBusy = false; else return;
            }
            if (_tick - _lastVisionTick < Math.Max(1, _cfgVisionEvery.Value)) return;
            if (!IsAlive(_kobold) || !IsAlive(_cam)) return;
            _visionBusy = true;
            _visionStartTime = Time.unscaledTime;
            _lastVisionTick = _tick;
            var t = new Thread(VisionWorker) { IsBackground = true, Name = "KKLLMNPC-Vision" };
            t.Start();
        }

        private void VisionWorker()
        {
            try
            {
                // Render the current first-person view on the main thread.
                byte[] jpg = null;
                try { jpg = (byte[])RunOnMainThread(() => (object)CaptureImageBytes(), 8000); }
                catch (Exception) { return; }
                if (jpg == null || jpg.Length == 0) { Logger.LogWarning("vision: no frame captured"); return; }
                _lastVisionB64 = "data:image/jpeg;base64," + Convert.ToBase64String(jpg);

                string img = _lastVisionB64;

                if (_cfgVisionDebug.Value) DumpVisionFrame(jpg);

                Logger.LogInfo($"vision: sending {jpg.Length / 1024}KB frame (yaw={F(_yawDeg)})");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string cap = CaptionImage(img);
                sw.Stop();

                if (!string.IsNullOrEmpty(cap))
                {
                    if (!_visionModelLoggedOnce)
                    {
                        _visionModelLoggedOnce = true;
                        string m = !string.IsNullOrWhiteSpace(_cfgVisModel.Value) ? _cfgVisModel.Value : _cfgModel.Value;
                        Logger.LogInfo("KKLLMNPC: vision captions via model=" + m);
                    }
                    _lastVisionCaption = cap; // remembered for next pass's context

                    // Auto-remember landmarks it mentions ("bed is left", "toilet at
                    // 2m ahead") so the action model accumulates a spatial map.
                    try
                    {
                        foreach (var line in cap.Split(new[] { '.', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string l = line.Trim();
                            if (l.Length < 4 || l.Length > 60) continue;
                            string low = l.ToLowerInvariant();
                            // "X is <dir>" / "X at <dist>m <dir>" / "X left|right|ahead"
                            if (low.Contains(" is ") || low.Contains(" at ") || low.Contains(" ahead") || low.Contains(" left") || low.Contains(" right") || low.Contains(" behind"))
                                RememberFact(l);
                        }
                    }
                    catch (Exception) { }

                    // Parse steering hint "go:<deg>:<reason>" out of the caption.
                    _visionSteer = null;
                    int gi = cap.IndexOf("go:", StringComparison.OrdinalIgnoreCase);
                    if (gi >= 0)
                    {
                        string rest = cap.Substring(gi + 3);
                        int end = rest.IndexOfAny(new[] { ':', '|', ' ', '\n' });
                        if (end < 0) end = rest.Length;
                        float deg;
                        if (float.TryParse(rest.Substring(0, end), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deg))
                        {
                            deg = Mathf.Clamp(deg, -120f, 120f);
                            string why = rest.Length > end + 1 ? rest.Substring(end + 1).Trim(':',' ','|','\n') : "vision";
                            _visionSteer = new VisionSteer { deg = deg, reason = why.Length > 0 ? why : "vision" };
                        }
                    }
                    // Also keep the text part (without the go:" token) as the caption.
                    int cut = cap.IndexOf("| go:", StringComparison.OrdinalIgnoreCase);
                    string desc = gi >= 0 ? (cut >= 0 ? cap.Substring(0, cut).Trim() : cap.Substring(0, gi).Trim()) : cap;
                    _sceneDesc = string.IsNullOrEmpty(desc) ? cap : desc;
                    Logger.LogInfo($"vision ({sw.ElapsedMilliseconds}ms): " + cap);
                }
                else Logger.LogWarning($"vision: empty caption after {sw.ElapsedMilliseconds}ms (model may not be vision-capable)");
            }
            catch (Exception e) { Logger.LogWarning("vision: " + e.Message); }
            finally { _visionBusy = false; }
        }

        // Write the exact JPEG the VLM sees so you can inspect the NPC's view.
        private void DumpVisionFrame(byte[] jpg)
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath), "..", "plugins", "KKLLMNPC_frames");
                dir = Path.GetFullPath(dir);
                Directory.CreateDirectory(dir);
                int idx = ++_visionShot;
                File.WriteAllBytes(Path.Combine(dir, $"v{idx:D4}_{(int)_yawDeg}.jpg"), jpg);
                // Keep only the last 40 frames.
                var files = new DirectoryInfo(dir).GetFiles("v*.jpg").OrderBy(f => f.Name).ToList();
                while (files.Count > 40) { try { files[0].Delete(); } catch (Exception) { } files.RemoveAt(0); }
            }
            catch (Exception e) { Logger.LogWarning("vision dump: " + e.Message); }
        }

        // Send the JPEG to the vision model for a short scene caption. Uses the
        // [VisionModel] config when set; otherwise falls back to the main LLM
        // endpoint (which must then be a vision-capable model).
        private string CaptionImage(string imageB64)
        {
            try
            {
                string model    = !string.IsNullOrWhiteSpace(_cfgVisModel.Value)    ? _cfgVisModel.Value.Trim()    : Val(_cfgModel);
                string endpoint = !string.IsNullOrWhiteSpace(_cfgVisEndpoint.Value) ? _cfgVisEndpoint.Value.Trim() : Val(_cfgEndpoint);
                string apiKey   = !string.IsNullOrWhiteSpace(_cfgVisApiKey.Value)   ? _cfgVisApiKey.Value          : Val(_cfgApiKey);

                // Give the vision model its own prior caption as context so it can
                // tell *what changed* instead of describing from zero every time.
                // Also give it the NPC's current goal so its steering suggestion is
                // goal-driven, not just "most open space".
                string visionText = Val(_cfgVisionPrompt) +
                    " Current goal: \"" + _lastThought + "\"" +
                    (_blockedInfo != null ? " Note: " + _blockedInfo + "." : "");
                if (!string.IsNullOrEmpty(_lastVisionCaption))
                    visionText += " Previously: \"" + _lastVisionCaption + "\".";

                var payload = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = new object[]
                            {
                                new Dictionary<string, object> { ["type"] = "text", ["text"] = visionText },
                                new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = imageB64 } },
                            },
                        },
                    },
                    ["max_tokens"] = _cfgVisMaxTokens.Value,
                    ["temperature"] = 0.2,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);

                var req = (HttpWebRequest)WebRequest.Create(endpoint);
                req.Method = "POST";
                req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers["Authorization"] = "Bearer " + apiKey;
                req.Timeout = 60000; req.ReadWriteTimeout = 60000; // vision encode is slow
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    var ms = new MemoryStream();
                    var buf = new byte[8192]; int total = 0, nRead;
                    while ((nRead = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += nRead; if (total > 1024 * 1024) break;
                        ms.Write(buf, 0, nRead);
                    }
                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    var choices = root?.GetValueOrDefault("choices") as List<object>;
                    if (choices == null || choices.Count == 0) return null;
                    var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                    string content = msg?.GetValueOrDefault("content") as string;
                    return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
                }
            }
            catch (Exception e) { Logger.LogWarning("vision endpoint: " + e.Message); return null; }
        }

        // ------------------------------------------------------------------
        // tool commands (invoked via the LLM tool-call loop)
        // ------------------------------------------------------------------
        private object ToolMoveTo(JsonObj p)
        {
            float x = p.F("x"), y = p.F("y"), z = p.F("z");
            if (_kobold == null) return new { ok = false, reason = "no_body" };
            Vector3 target = new Vector3(x, y, z);
            Vector3 toT = target - _kobold.transform.position;
            float dist = toT.magnitude;
            toT.y = 0;
            float yaw = Mathf.Atan2(toT.x, toT.z) * 57.29578f;
            lock (_stateLock) { _yawDeg = yaw; }
            // Actually start walking there (bounded burst) so move_to isn't a no-op.
            float dur = Mathf.Clamp(dist / 2f, 0.3f, 8f);
            SetMove(1f, false, 0f, dur, p.B("run", false));
            return new { ok = true, dist = F(dist), walked_for = F(dur) };
        }

        private object ToolWalk(JsonObj p)
        {
            float speed = p.F("speed", 1f);
            bool jump = p.B("jump", false);
            float turn = p.F("turn_deg", 0f);
            // Default a 2s burst so a forgotten duration can't make it walk forever.
            float dur = Mathf.Clamp(p.F("duration", 2f), 0.1f, 8f);
            bool run = p.B("run", false); // default: walk
            if (_photonView != null && !_photonView.IsMine && PhotonNetwork.InRoom)
                Logger.LogWarning($"walk: not Photon owner (owner={_photonView.Owner?.NickName ?? "?"}) — movement won't apply");
            SetMove(speed, jump, 0f, dur, run);
            if (Mathf.Abs(turn) > 0.001f)
                lock (_stateLock) { _yawOffsetDeg += turn; }
            return new { ok = true, speed, jump, turn_deg = turn, duration = dur, run };
        }

        // Steer+walk toward one of the raycast fan directions the model can see.
        // The rays share the vision camera's yaw basis, so a ray index (or signed
        // degrees left/right of center) maps directly onto a camera-relative heading.
        private object ToolWalkRay(JsonObj p)
        {
            float speed = p.F("speed", 1f);
            float dur = Mathf.Clamp(p.F("duration", 2f), 0.1f, 8f);
            bool jump = p.B("jump", false);
            bool run = p.B("run", false); // default: walk
            float yawOut;
            lock (_stateLock)
            {
                float deltaDeg = 0f;
                float rd = p.F("ray_deg", float.NaN);
                if (!float.IsNaN(rd)) deltaDeg = Mathf.Clamp(rd, -90f, 90f);
                else
                {
                    float idx = p.F("ray", -1f);
                    int n = Mathf.Max(2, _cfgRayCount.Value);
                    float fov = _cam != null ? _cam.fieldOfView : 90f;
                    if (idx < 0f) idx = (n - 1) * 0.5f; // center
                    float t = Mathf.Clamp(idx, 0f, n - 1) / (n - 1);
                    deltaDeg = Mathf.Lerp(-fov * 0.5f, fov * 0.5f, t);
                }
                // Rays inherit camera pitch+yaw; we steer the whole body to that yaw.
                _yawDeg = Mathf.Repeat(_yawDeg + deltaDeg, 360f);
                yawOut = _yawDeg;
            }
            SetMove(speed, jump, 0f, dur, run);
            return new { ok = true, yaw = F(yawOut), dur = F(dur), run };
        }

        private object ToolLookAround(JsonObj p)
        {
            // Result goes into the NEXT perception; here we kick off a sweep and
            // immediately return what the ray fan sees in each sector.
            float sweep = Mathf.Clamp(p.F("sweep", 120f), 30f, 360f);
            lock (_stateLock) { _yawOffsetDeg += sweep; } // physically start turning
            _lastBigTurnTime = Time.unscaledTime;         // trigger an image next tick
            var seen = (Dictionary<string, object>)RunOnMainThread(() =>
            {
                // Sector scan via the 8-way fan: report what's in each direction.
                var res = new Dictionary<string, object>();
                if (!IsAlive(_kobold) || !IsAlive(_head)) return res;
                string[] names = { "front", "front-right", "right", "back-right", "back", "back-left", "left", "front-left" };
                float[] offs = { 0f, 45f, 90f, 135f, 180f, -135f, -90f, -45f };
                for (int i = 0; i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, _yawDeg + offs[i], 0) * Vector3.forward;
                    string what = "nothing";
                    RaycastHit hit;
                    if (Physics.Raycast(_head.position, dir, out hit, _cfgRayRange.Value, ~0, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider))
                    {
                        try
                        {
                            var kb = hit.collider.GetComponentInParent<Kobold>();
                            var us = hit.collider.GetComponentInParent<GenericUsable>();
                            if (kb != null) what = (IsPlayerKobold(kb) ? "player:" : "kobold:") + kb.name;
                            else if (us != null) what = "usable:" + us.name;
                            else what = "wall:" + hit.collider.gameObject.name;
                        }
                        catch (Exception) { what = "object"; }
                        what += "@" + F(hit.distance) + "m";
                    }
                    res[names[i]] = what;
                }
                return res;
            });
            return new { ok = true, turning_to_sweep = sweep, scan = seen };
        }

        private object ToolCrouch(JsonObj p)
        {
            float amount = Mathf.Clamp01(p.F("crouch", 1f));
            lock (_stateLock) { _crouch = amount; _manualCrouchSet = Time.unscaledTime; }
            return new { ok = true, crouch = amount, note = amount >= 0.05f ? "crouching" : "standing" };
        }

        // Path the body toward a world position or a named place/usable. The game's
        // NavMesh API isn't accessible from this Unity build, so this drives our own
        // movement: face the target + walk with obstacle auto-steer (from FixedUpdate).
        private object ToolGoTo(JsonObj p)
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };

            string name = p.S("name", "");
            Vector3 target;
            if (!string.IsNullOrEmpty(name))
            {
                var hit = FindPlaceByName(name);
                if (!hit.HasValue) return new { ok = false, reason = "unknown_place", tried = name };
                target = hit.Value;
                _navTargetName = name;
            }
            else { target = new Vector3(p.F("x"), 0, p.F("z")); _navTargetName = null; }

            bool run = p.B("run", false);
            Vector3 to = target - _kobold.transform.position; to.y = 0;
            float dist = to.magnitude;
            float yaw = Mathf.Atan2(to.x, to.z) * 57.29578f;
            lock (_stateLock) { _yawDeg = yaw; }
            _navTarget = new Vector3(target.x, 0, target.z);
            SetMove(1f, false, 0f, Mathf.Clamp(dist / 2f, 0.3f, 12f), run);
            return new { ok = true, to = _navTargetName ?? "position", dist = F(dist), note = "walking with obstacle steering; re-issue go_to to update heading" };
        }

        // Resolve a human-ish name ("bed", "toilet", "player", "door") to a world
        // position from what we can currently find around the map body.
        private Vector3? FindPlaceByName(string name)
        {
            if (!IsAlive(_kobold)) return null;
            string needle = name.ToLowerInvariant();
            // The player.
            if (needle.Contains("player") || needle.Contains("me") || needle.Contains("you"))
            {
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                    return pp.kobold.transform.position;
            }
            // Best matching GenericUsable (bed/toilet/tub/seat/door/swap...) in range.
            GenericUsable best = null; float bestD = float.MaxValue;
            foreach (var c in Physics.OverlapSphere(_kobold.transform.position, 60f, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null || IsOwnCollider(c)) continue;
                var u = c.GetComponentInParent<GenericUsable>();
                if (u == null) continue;
                string n = CleanName(u.name).ToLowerInvariant();
                string cls = ClassifyUsable(n).ToLowerInvariant();
                if (n.Contains(needle) || cls.Contains(needle) || needle.Contains(cls))
                {
                    float d = Vector3.Distance(u.transform.position, _kobold.transform.position);
                    if (d < bestD) { bestD = d; best = u; }
                }
            }
            return best != null ? best.transform.position : (Vector3?)null;
        }

        // Chat the player typed within the last ~30s — null otherwise, and only once
        // per distinct message so we don't keep responding to the same line.
        private string _lastDeliveredChat;
        private string RecentPlayerChat()
        {
            if (_playerChat == null || Time.unscaledTime - _playerChatTime > 30f) return null;
            if (_playerChat == _lastDeliveredChat) return null;
            _lastDeliveredChat = _playerChat;
            return _playerChat;
        }

        // Store a fact in long-term memory ("bed upstairs", "player is friendly").
        private object ToolRemember(JsonObj p)
        {
            string fact = p.S("mem", "");
            if (string.IsNullOrWhiteSpace(fact)) return new { ok = false, reason = "empty_mem" };
            RememberFact(fact);
            return new { ok = true, remembered = fact.Trim(), facts = _facts.Count };
        }

        // ------------------------------------------------------------------
        // penetration awareness
        // ------------------------------------------------------------------
        private readonly List<PenetrationTech.Penetrable> _subscribedPenetrables
            = new List<PenetrationTech.Penetrable>();
        private readonly List<KKPenetratorListener> _penListeners
            = new List<KKPenetratorListener>();

        // The penetrator-side mirror: when THIS kobold's dick is inside something.
        private class KKPenetratorListener : PenetrationTech.PenetratorListener
        {
            public LLMNPCPlugin owner;
            public PenetrationTech.Penetrator source;
            public Transform lastTarget; // the thing it's inside right now

            private string HoleName(PenetrationTech.Penetrable pen)
            {
                try
                {
                    return pen != null && pen.gameObject != null
                        ? LLMNPCPlugin.CleanName(pen.gameObject.name) : "somewhere";
                }
                catch (Exception) { return "somewhere"; }
            }

            public override void OnPenetrationStart(PenetrationTech.Penetrable penetrable)
            {
                lastTarget = penetrable != null ? penetrable.transform : null;
                owner?.NotifyDickIn(this, HoleName(penetrable), true);
            }
            public override void OnPenetrationEnd(PenetrationTech.Penetrable penetrable)
            {
                lastTarget = null;
                owner?.NotifyDickIn(this, HoleName(penetrable), false);
            }
            public override void NotifyPenetrationUpdate(PenetrationTech.Penetrator self,
                                                         PenetrationTech.Penetrable penetrable,
                                                         float distToHole)
            {
                lastTarget = penetrable != null ? penetrable.transform : lastTarget;
                owner?.NotifyDickTick(this, HoleName(penetrable), distToHole);
            }
        }

        private void SyncPenetrationSubscriptions(Kobold target)
        {
            UnsubscribePenetrables();
            try
            {
                _pen.Clear();
                foreach (var pen in target.GetComponentsInChildren<PenetrationTech.Penetrable>(true))
                {
                    if (pen == null) continue;
                    pen.penetrationNotify += OnPenetrationTick;
                    _subscribedPenetrables.Add(pen);
                }
                // And attach listeners to any penetrator appendages (dicks) so we can
                // feel penetrating others.
                _dickIn.Clear();
                foreach (var pr in target.GetComponentsInChildren<PenetrationTech.Penetrator>(true))
                {
                    try
                    {
                        if (pr == null) continue;
                        var listener = new KKPenetratorListener { owner = this, source = pr };
                        pr.listeners.Add(listener);
                        _penListeners.Add(listener);
                    }
                    catch (Exception) { }
                }
                Logger.LogInfo("KKLLMNPC: watching " + _subscribedPenetrables.Count + " penetrables + " + _penListeners.Count + " penetrators on '" + target.name + "'.");
            }
            catch (Exception e) { Logger.LogWarning("subscribe penetrables: " + e.Message); }
        }

        private void UnsubscribePenetrables()
        {
            try
            {
                foreach (var p in _subscribedPenetrables) if (p != null) p.penetrationNotify -= OnPenetrationTick;
                _subscribedPenetrables.Clear();
                foreach (var l in _penListeners) if (l != null) { try { if (l.source != null) l.source.listeners.Remove(l); } catch (Exception) { } }
                _penListeners.Clear();
                lock (_pen) _pen.Clear();
                lock (_dickIn) _dickIn.Clear();
            }
            catch (Exception) { }
        }

        // Fires per-frame while a penetrator is inside (and on exit with depth 0).
        private void OnPenetrationTick(PenetrationTech.Penetrable penetrable,
                                       PenetrationTech.Penetrator penetrator,
                                       float worldSpaceDistanceToPenetrator,
                                       PenetrationTech.Penetrable.SetClipDistanceAction clipAction)
        {
            try
            {
                if (penetrator == null) return;
                float now = Time.unscaledTime;
                string whoPen = penetrator.gameObject != null ? CleanName(penetrator.gameObject.name) : "something";

                lock (_pen)
                {
                    PenState st;
                    if (worldSpaceDistanceToPenetrator > 0.001f)
                    {
                        if (!_pen.TryGetValue(penetrator, out st))
                        {
                            st = new PenState { name = whoPen, lastT = now, depth = worldSpaceDistanceToPenetrator, vel = 0f, hole = ClassifyPenetrable(penetrable) };
                            _pen[penetrator] = st;
                            Logger.LogInfo("penetrated by " + whoPen + " (" + st.hole + ")");
                            EmitAmbient("hah~"); // being entered
                        }
                        st.src = penetrator.gameObject != null ? penetrator.transform : null;
                        float d = worldSpaceDistanceToPenetrator;
                        float dt = Mathf.Max(0.001f, now - st.lastT);
                        st.vel = Mathf.Lerp(st.vel, (d - st.depth) / dt, 0.2f);
                        st.depth = d;
                        st.lastT = now;
                    }
                    else if (_pen.TryGetValue(penetrator, out st))
                    {
                        _pen.Remove(penetrator); // pulled out
                        Logger.LogInfo(whoPen + " pulled out");
                    }
                }
            }
            catch (Exception) { }
        }

        // Rough hole classification from the penetrable's transform name/location.
        private string ClassifyPenetrable(PenetrationTech.Penetrable pen)
        {
            try
            {
                string n = (pen.gameObject != null ? pen.gameObject.name : "").ToLowerInvariant();
                if (n.Contains("mouth") || n.Contains("face") || n.Contains("head")) return "mouth";
                if (n.Contains("vag") || n.Contains("crotch")) return "vagina";
                if (n.Contains("anus") || n.Contains("ass") || n.Contains("butt") || n.Contains("rear")) return "ass";
                if (n.Contains("breast") || n.Contains("chest")) return "chest";
                return n.Length > 0 ? n : "hole";
            }
            catch (Exception) { return "hole"; }
        }

        private string PenetrationInfo()
        {
            lock (_pen)
            {
                if (_pen.Count == 0) return null;
                var parts = new List<string>();
                float anyDepth = 0f, anyVel = 0f;
                foreach (var kv in _pen)
                {
                    var st = kv.Value;
                    parts.Add(st.name + " " + st.hole + " depth=" + F(st.depth) + "m thrust=" + F(st.vel));
                    if (st.depth > anyDepth) anyDepth = st.depth;
                    anyVel += st.vel;
                }
                return _pen.Count + " inside (" + string.Join("; ", parts.ToArray()) + ")";
            }
        }

        // True if any penetrator was inside within the last ~3 seconds.
        private bool IsPenetrated()
        {
            lock (_pen)
            {
                if (_pen.Count == 0) return false;
                float now = Time.unscaledTime;
                foreach (var kv in _pen) if (now - kv.Value.lastT < 3f) return true;
                return false;
            }
        }

        // Same for the kobold's own dick being inside something.
        private bool IsDickInside()
        {
            lock (_dickIn)
            {
                if (_dickIn.Count == 0) return false;
                float now = Time.unscaledTime;
                foreach (var kv in _dickIn) if (now - kv.Value.lastT < 3f) return true;
                return false;
            }
        }

        // Ask the world-model a question ("what is that?", "is the bed taken?") with
        // the NPC's current perception as context. Async: answer lands in '_answered'
        // and gets read into perception next tick, and optionally said aloud by the model.
        private volatile string _pendingQuestion;
        private volatile string _lastAnswer;
        private volatile bool _answerBusy;

        private object ToolAsk(JsonObj p)
        {
            string q = p.S("q", "");
            if (string.IsNullOrWhiteSpace(q)) return new { ok = false, reason = "empty_question" };
            _pendingQuestion = q.Trim();
            // Fire the question on a worker thread; the answer is picked up next tick.
            if (!_answerBusy)
            {
                _answerBusy = true;
                var t = new Thread(AnswerQuestionWorker) { IsBackground = true, Name = "KKLLMNPC-Answer" };
                t.Start();
            }
            return new { ok = true, asking = _pendingQuestion, note = "answer delivered next turn as 'answered'" };
        }

        private void AnswerQuestionWorker()
        {
            try
            {
                string q = _pendingQuestion;
                // Build context from the current perception.
                string percep = null;
                try { percep = (string)RunOnMainThread(() => Json.Write(BuildPerception(false)), 8000); } catch (Exception) { }
                string ctx = "Perception now: " + percep + " Scene: " + _sceneDesc + " Remembered: " + FactsJson();
                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "You are a kobold NPC's inner world-model. Answer its question in one short sentence using the provided perception and memory. Be concrete and literal (places, distances, who, what it is). KoboldKare is an adult world; answer candidly." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = ctx + " Question: " + q },
                    },
                    ["max_tokens"] = 80,
                    ["temperature"] = 0.4,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);
                var req = (HttpWebRequest)WebRequest.Create(Val(_cfgEndpoint));
                req.Method = "POST"; req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(Val(_cfgApiKey))) req.Headers["Authorization"] = "Bearer " + Val(_cfgApiKey);
                req.Timeout = 30000; req.ReadWriteTimeout = 30000;
                byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) { _answerBusy = false; return; }
                    var ms = new MemoryStream(); var buf = new byte[8192]; int total = 0, n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > 512 * 1024) break; ms.Write(buf, 0, n); }
                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var root = Json.Parse(json) as Dictionary<string, object>;
                    var choices = root?.GetValueOrDefault("choices") as List<object>;
                    if (choices != null && choices.Count > 0)
                    {
                        var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
                        string content = msg?.GetValueOrDefault("content") as string;
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            _lastAnswer = content.Trim();
                            RememberFact("Q: " + q + " A: " + _lastAnswer);
                            Logger.LogInfo("asked: " + q + " => " + _lastAnswer);
                        }
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("ask: " + e.Message); }
            finally { _answerBusy = false; }
        }

        private object ToolStop()
        {
            StopMove();
            return new { ok = true };
        }

        // Get out of any animation station (bed/sex/mount) the kobold is locked in.
        // Identical to a player pressing Jump/Cancel: raises StopAnimationRPC.
        private object ToolExitStation()
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            bool inStation = IsInAnimationStation();
            bool sent = (bool)RunOnMainThread(() =>
            {
                try { _photonView?.RPC("StopAnimationRPC", RpcTarget.All); return true; }
                catch (Exception e) { Logger.LogWarning("exit station: " + e.Message); return false; }
            });
            StopMove(); // also release local input (controller ignores it while animating)
            return new { ok = sent, was_in_station = inStation, note = "exits any animation station — same as pressing jump" };
        }

        private object ToolLook(JsonObj p)
        {
            float yaw = p.F("yaw_deg", float.NaN);
            float pitch = p.F("pitch_deg", float.NaN);
            float dYaw = p.F("dyaw_deg", 0f);
            float dPitch = p.F("dpitch_deg", 0f);
            lock (_stateLock)
            {
                if (!float.IsNaN(yaw)) _yawDeg = Mathf.Repeat(yaw, 360f);
                if (!float.IsNaN(pitch)) _pitchDeg = Mathf.Clamp(pitch, -89f, 89f);
                _yawDeg = Mathf.Repeat(_yawDeg + dYaw, 360f);
                _pitchDeg = Mathf.Clamp(_pitchDeg + dPitch, -89f, 89f);
            }
            // Turn the whole body when looking around — so the body follows its gaze
            // like a real player would, instead of the head drifting off sideways.
            RunOnMainThreadAsync(() =>
            {
                try
                {
                    if (_controller != null && _controller.body != null)
                        _controller.body.MoveRotation(Quaternion.Euler(0, _yawDeg, 0));
                }
                catch (Exception) { }
            });
            if (Mathf.Abs(dYaw) >= 30f || Mathf.Abs((!float.IsNaN(yaw) ? yaw : _yawDeg) - _yawDeg) >= 30f)
                _lastBigTurnTime = Time.unscaledTime; // trigger a fresh image next tick
            return new { ok = true, yaw = F(_yawDeg), pitch = F(_pitchDeg) };
        }

        private object ToolJump()
        {
            // If locked in an animation station, jumping is how players get out —
            // do that first so "jump" behaves the way the model/player expects.
            if (IsInAnimationStation()) return ToolExitStation();

            lock (_stateLock) { _moveJump = true; }
            RunOnMainThreadAsync(() => { if (_controller != null) _controller.inputJump = true; });
            return new { ok = true };
        }

        // Interact with a usable machine/object. The game's User component only
        // populates closestUsable for the *local player's* tagged body, so calling
        // User.Use() on our possessed body no-ops. We instead find the nearest
        // GenericUsable ourselves, turn to face it, then drive LocalUse directly
        // (the same thing the player's Use() calls once one is in range).
        private object ToolInteract()
        {
            if (!IsAlive(_kobold)) return new { ok = false, reason = "no_body" };
            return RunOnMainThread(() =>
            {
                var target = FindBestUsable(out float dist);
                if (target == null) return new { ok = false, reason = "nothing_usable_nearby" };

                // Face it first (yaw + slight pitch down toward the object) and turn
                // the *camera* — the model's vision follows this, so it will see the
                // object it just interacted with on the next tick.
                Vector3 to = target.transform.position - _kobold.transform.position;
                Vector3 flat = to; flat.y = 0;
                float yaw = Mathf.Atan2(flat.x, flat.z) * 57.29578f;
                float pitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.2f, flat.magnitude)) * 57.29578f, -60f, 60f);
                lock (_stateLock) { _yawDeg = Mathf.Repeat(yaw, 360f); _pitchDeg = pitch; }
                try { _controller.body?.MoveRotation(Quaternion.Euler(0, _yawDeg, 0)); } catch (Exception) { }
                _lastBigTurnTime = Time.unscaledTime; // make next tick attach a fresh image

                if (!target.CanUse(_kobold)) return new { ok = false, reason = "cannot_use", name = CleanName(target.name), hint = "maybe busy/occupied or wrong state", dist = F(dist) };
                try { target.LocalUse(_kobold); }
                catch (Exception e) { Logger.LogWarning("use: " + e.Message); return new { ok = false, reason = "use_failed", name = CleanName(target.name) }; }
                return (object)new { ok = true, used = CleanName(target.name), type = ClassifyUsable(CleanName(target.name)), dist = F(dist) };
            });
        }

        // Nearest GenericUsable the kobold can actually use. Prefers what's roughly
        // in front of it, then falls back to a close 360° bubble (machines need to be
        // touched, not just seen). Range mirrors the User capsule's effective radius.
        private GenericUsable FindBestUsable(out float dist)
        {
            dist = float.MaxValue;
            if (!IsAlive(_kobold)) return null;
            Vector3 pos = _kobold.transform.position;
            Vector3 fwd = Quaternion.Euler(0, _yawDeg, 0) * Vector3.forward;

            // 1) What the forward rays are pointing at.
            GenericUsable best = null;
            if (_head != null)
            {
                Vector3 dir = Quaternion.Euler(_pitchDeg, _yawDeg, 0) * Vector3.forward;
                if (Physics.Raycast(_head.position, dir, out RaycastHit hit, InteractRange, ~0, QueryTriggerInteraction.Collide)
                    && !IsOwnCollider(hit.collider))
                {
                    var u = hit.collider.GetComponentInParent<GenericUsable>();
                    if (u != null && u.CanUse(_kobold)) { best = u; dist = hit.distance; }
                }
            }

            // 2) Otherwise nearest within the touch bubble, preferring forward-facing.
            float bestScore = best != null ? dist : float.MaxValue;
            foreach (var c in Physics.OverlapSphere(pos, InteractRange, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null || IsOwnCollider(c)) continue;
                var u2 = c.GetComponentInParent<GenericUsable>();
                if (u2 == null) continue;
                try { if (!u2.CanUse(_kobold)) continue; } catch (Exception) { continue; }
                Vector3 d = u2.transform.position - pos;
                float m = d.magnitude;
                float facing = Vector3.Dot(d.normalized, fwd);
                float score = m * (facing > -0.2f ? 1f : 2.5f);
                if (score < bestScore) { bestScore = score; dist = m; best = u2; }
            }
            return best;
        }

        private const float InteractRange = 2.6f;

        private object ToolGrab(JsonObj p)
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            bool multi = p.B("multi", false);
            bool done = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryGrab(multi); return true; } catch (Exception e) { Logger.LogWarning("grab: " + e.Message); return false; }
            });
            return new { ok = done };
        }

        private object ToolDrop()
        {
            if (_grabber == null) return new { ok = false, reason = "no_grabber" };
            bool done = (bool)RunOnMainThread(() =>
            {
                try { _grabber.TryDrop(); return true; } catch (Exception e) { Logger.LogWarning("drop: " + e.Message); return false; }
            });
            return new { ok = done };
        }

        private object ToolSay(JsonObj p)
        {
            string text = p.S("text", "");
            if (text.Length == 0) return new { ok = false, reason = "empty" };
            string who = CleanName(_kobold != null ? _kobold.name : "NPC");
            Logger.LogInfo("[NPC] " + who + ": " + text); // always visible in the console/log
            RunOnMainThreadAsync(() =>
            {
                // 1) Floating bubble above the kobold (local flavor). Force-activate
                //    the chatter hierarchy so AI kobolds' bubbles actually show.
                try
                {
                    var chatter = _kobold != null ? _kobold.GetComponentInChildren<Chatter>(true) : null;
                    if (chatter != null)
                    {
                        if (!chatter.gameObject.activeSelf) chatter.gameObject.SetActive(true);
                        var node = chatter.transform;
                        for (var par = node.parent; par != null; par = par.parent) if (!par.gameObject.activeSelf) par.gameObject.SetActive(true);
                        chatter.DisplayMessage(text, 4f);
                    }
                }
                catch (Exception e) { Logger.LogWarning("say bubble: " + e.Message); }

                // 2) The real chat window: same Photon event the ChatPanel raises,
                // so it lands in everyone's chat history. The receiver renders it as
                // "<sender nickname>: <message>" — and since we own the kobold's
                // PhotonView, the sender is YOUR username. So we prefix the kobold's
                // name in the message text itself so chat reads clearly.
                try
                {
                    string senderName = MyName();
                    string chatText = senderName + ": " + text;
                    if (PhotonNetwork.InRoom)
                    {
                        var opts = new Photon.Realtime.RaiseEventOptions {
                            CachingOption = Photon.Realtime.EventCaching.DoNotCache,
                            Receivers = Photon.Realtime.ReceiverGroup.Others, // everyone else
                        };
                        bool sent = PhotonNetwork.RaiseEvent(
                            NetworkManager.CustomChatEvent,
                            chatText.TrimEnd(),
                            opts,
                            ExitGames.Client.Photon.SendOptions.SendReliable);
                        if (!sent) Logger.LogWarning("say: RaiseEvent returned false");
                    }
                    // Local echo: we won't receive our own event, so push it into the
                    // chat log directly the same way NetworkManager.OnEvent does.
                    try { CheatsProcessor.AppendText(chatText + "\n"); } catch (Exception e) { Logger.LogWarning("say local echo: " + e.Message); }
                    if (!PhotonNetwork.InRoom) Logger.LogInfo("say (offline, not in a room): " + text);
                }
                catch (Exception e) { Logger.LogWarning("say chat: " + e.Message); }
            });
            return new { ok = true, said = text };
        }

        private object ToolStatus()
        {
            return BuildPerception(false);
        }

        // ------------------------------------------------------------------
        // LLM loop: perception -> endpoint -> tool calls -> execute
        // ------------------------------------------------------------------
        private void LLMLoop()
        {
            // Give the game a moment to load before looking for a body.
            try { Thread.Sleep(4000); } catch (Exception) { return; }
            string lastState = "";
            while (_running)
            {
                try
                {
                    // Wait until we have a main-thread context before doing anything.
                    if (!_mainReady) { Thread.Sleep(1000); continue; }

                    bool inGame = (bool)RunOnMainThread(() => IsPlayableScene(), 5000);
                    if (!inGame)
                    {
                        // Log state transitions so we can see *where* it's idle.
                        if (lastState != "no_scene") { lastState = "no_scene"; Logger.LogInfo("KKLLMNPC: waiting for a playable scene (player not spawned yet / wrong map)."); }
                        if (_kobold != null) try { RunOnMainThread(() => { TeardownBody(); return true; }, 5000); } catch (Exception) { }
                        Thread.Sleep(2000);
                        continue;
                    }

                    bool have = (bool)RunOnMainThread(() => EnsureBody(), 8000);
                    if (!have)
                    {
                        if (lastState != "no_body") { lastState = "no_body"; Logger.LogInfo("KKLLMNPC: in scene but no unoccupied AI kobold within AutoFindRange to possess."); }
                        Thread.Sleep(2000);
                        continue;
                    }

                    if (lastState != "running") { lastState = "running"; Logger.LogInfo("KKLLMNPC: loop active."); }

                    object perception = RunOnMainThread(() => BuildPerception(false), 15000);
                    string userJson = Json.Write(perception);

                    // Attach a first-person frame when it actually helps: on schedule,
                    // right after a bump/block, or after a big turn — not every tick.
                    _tick++;
                    bool imageDue = _cfgSendImage.Value && (
                        _tick % Math.Max(1, _cfgImageEvery.Value) == 0
                        || (_cfgImageOnBump.Value && _needImageAfterBump)
                        || (_cfgImageOnTurn.Value && Time.unscaledTime - _lastBigTurnTime < 0.5f));
                    string imageB64 = null;
                    if (imageDue)
                    {
                        // Prefer the frame the vision worker just captured (already
                        // encoded, costs nothing extra); otherwise render a fresh one.
                        imageB64 = _lastVisionB64 ?? (string)RunOnMainThread(() => (object)CaptureImageB64(), 8000);
                        _needImageAfterBump = false;
                    }

                    MaybeStartVisionPass();
                    MaybeCreativeCommentary(userJson);

                    string reply = QueryLLM(userJson, imageB64);
                    if (reply == null) { Thread.Sleep((int)(_cfgThinkInterval.Value * 1000)); continue; }

                    ExecuteToolCalls(reply);
                    Thread.Sleep((int)(_cfgThinkInterval.Value * 1000));
                }
                catch (InvalidOperationException) { Thread.Sleep(1000); } // main not ready
                catch (ThreadInterruptedException) { return; }
                catch (ThreadAbortException) { return; }
                catch (Exception e)
                {
                    try { Logger.LogWarning("LLM loop: " + e); } catch (Exception) { }
                    try { Thread.Sleep(2000); } catch (Exception) { return; }
                }
            }
            Logger.LogWarning("KKLLMNPC: LLMLoop exited (running=false).");
        }

        private string QueryLLM(string perceptionJson, string imageB64)
        {
            try
            {
                // Memory injected into the perception so the NPC remembers what it
                // was doing — otherwise each turn starts from zero.
                string mem = string.Format(",\"last_thought\":{0},\"memory\":{1},\"facts\":{2},\"last_action\":{3},\"scene\":{4},\"history\":{5}",
                    Json.Write(_lastThought + (_blockedInfo != null ? " (" + _blockedInfo + ")" : "")),
                    ThoughtHistoryJson(), FactsJson(),
                    Json.Write(_lastAction), Json.Write(_sceneDesc), HistoryJson());
                string percep = perceptionJson;
                if (percep.EndsWith("}")) percep = percep.Substring(0, percep.Length - 1) + mem + "}";
                string text = "perception:" + percep;

                // With a vision-capable action model, attach the frame as a proper
                // image part (not embedded in the text) so prompt tokens stay small.
                object userContent = text;
                if (imageB64 != null)
                {
                    userContent = new object[]
                    {
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = text },
                        new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> { ["url"] = imageB64 } },
                    };
                }

                // Instead of tools/tool_choice (Gemma's chat template rejects them
                // with HTTP 400 on LM Studio), constrain output with a JSON schema.
                // The model then replies *with the act-args object as content*,
                // which our parser already accepts via the content-JSON path.
                var schema = ActSchema();
                var responseFormat = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = "act",
                        ["strict"] = true,
                        ["schema"] = schema,
                    },
                };

                var payload = new Dictionary<string, object>
                {
                    ["model"] = Val(_cfgModel),
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = Val(_cfgSystem) },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = userContent },
                    },
                    ["response_format"] = responseFormat,
                    ["temperature"] = Math.Round((double)_cfgTemperature.Value, 2),
                    ["max_tokens"] = _cfgMaxTokens.Value,
                    ["stream"] = false,
                };
                string body = Json.Write(payload);

                var req = (HttpWebRequest)WebRequest.Create(_cfgEndpoint.Value);
                req.Method = "POST";
                req.ContentType = "application/json";
                if (!string.IsNullOrEmpty(_cfgApiKey.Value))
                    req.Headers["Authorization"] = "Bearer " + _cfgApiKey.Value;
                req.Timeout = 30000; req.ReadWriteTimeout = 30000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    // Cap at 4 MB so a misbehaving endpoint cannot OOM the game.
                    var ms = new MemoryStream();
                    var buf = new byte[8192]; int total = 0, nRead;
                    while ((nRead = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += nRead;
                        if (total > 4 * 1024 * 1024) { Logger.LogWarning("LLM response too large, truncating"); break; }
                        ms.Write(buf, 0, nRead);
                    }
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("LLM endpoint: " + e.Message);
                return null;
            }
        }

        private void ExecuteToolCalls(string responseJson)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(responseJson) as Dictionary<string, object>; }
            catch (Exception e) { Logger.LogWarning("parse llm json: " + e.Message); return; }
            if (root == null) { Logger.LogWarning("llm reply not an object"); return; }

            if (!root.TryGetValue("choices", out var choicesObj)) { Logger.LogWarning("llm reply: no choices"); return; }
            var choices = choicesObj as List<object>; if (choices == null || choices.Count == 0) { Logger.LogWarning("llm reply: empty choices"); return; }
            var msg = (choices[0] as Dictionary<string, object>)?.GetValueOrDefault("message") as Dictionary<string, object>;
            if (msg == null) { Logger.LogWarning("llm reply: no message"); return; }

            string content = (msg.GetValueOrDefault("content") as string)?.Trim();

            // Pull the `act` arguments wherever they live (tool_calls or content JSON).
            JsonObj args = ExtractActArgs(msg, content);

            if (args == null)
            {
                // Model ignored the tool entirely and replied with text. At minimum,
                // say it out loud so the NPC is never fully inert, and remember it.
                if (!string.IsNullOrEmpty(content))
                {
                    _lastThought = content.Length > 80 ? content.Substring(0, 80) : content;
                    _lastAction = "say";
                    Logger.LogInfo("LLM (plain text -> say): " + content);
                    try { ToolSay(new TextArgs(content)); } catch (Exception e) { Logger.LogWarning("say fallback: " + e.Message); }
                }
                else Logger.LogWarning("llm reply: no tool call and no content");
                return;
            }

            _lastThought = args.S("thought", _lastThought);
            PushThought(_lastThought);

            Logger.LogInfo($"act: thought=\"{_lastThought}\"");

            // Execute the root action, then the queued plan[] steps, with a delay
            // between each. Runs on the LLM thread — sleeping here is fine.
            ExecuteStep(args);
            var plan = args.A("plan");
            int max = Math.Max(1, _cfgMaxPlan.Value);
            int ran = 0;
            foreach (var step in plan)
            {
                if (ran >= max) { Logger.LogWarning($"plan capped at {max} steps"); break; }
                if (!_running) return;
                float wait = Mathf.Clamp(step.F("wait", _cfgStepDelay.Value), 0f, 3f);
                if (wait > 0f) Thread.Sleep((int)(wait * 1000f));
                else Thread.Sleep((int)(_cfgStepDelay.Value * 1000f));
                ExecuteStep(step);
                ran++;
            }
        }

        // One action step: optional say, then the action itself.
        private void ExecuteStep(JsonObj args)
        {
            string action = args.S("action", "none");
            string say = args.S("say", "");
            _lastAction = action;
            Logger.LogInfo($"  step action={action}" + (say.Length > 0 ? $" say=\"{say}\"" : ""));
            if (!string.IsNullOrEmpty(say))
            {
                try { RunTool("say", new TextArgs(say)); } catch (Exception e) { Logger.LogWarning("say: " + e.Message); }
            }
            if (action != "none")
            {
                object result = RunTool(action, args);
                string summary = SummarizeResult(action, result);
                PushHistory(action, summary);
                // Notable outcomes become facts the model can recall later.
                if (action == "interact" && summary != null && summary.StartsWith("used ", StringComparison.Ordinal))
                {
                    string what = summary.Substring(5);
                    RememberFact(what + " used" + (_navTargetName != null ? " near " + _navTargetName : ""));
                }
                else if (action == "go_to" && _navTargetName != null)
                    RememberFact("went to " + _navTargetName);
                Logger.LogInfo($"  {action} -> {Json.Write(result)}");
            }
            else PushHistory(action, say.Length > 0 ? "said" : null);
        }

        // Short result digest for the history buffer ("used Bed", "blocked", "ok").
        private string SummarizeResult(string action, object result)
        {
            try
            {
                var s = Json.Write(result);
                if (string.IsNullOrEmpty(s) || s == "null") return null;
                if (s.Contains("\"ok\":true"))
                {
                    if (action == "interact" && s.Contains("\"used\":\""))
                    {
                        int a = s.IndexOf("\"used\":\"", StringComparison.Ordinal) + 8;
                        int b = s.IndexOf('"', a);
                        if (b > a) return "used " + s.Substring(a, b - a);
                    }
                    if (action == "say") return "said";
                    return _blockedInfo != null ? _blockedInfo : "ok";
                }
                if (s.Contains("\"reason\":\""))
                {
                    int a = s.IndexOf("\"reason\":\"", StringComparison.Ordinal) + 10;
                    int b = s.IndexOf('"', a);
                    if (b > a) return "fail:" + s.Substring(a, b - a);
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // Finds the `act` function arguments regardless of how the model formatted them.
        private JsonObj ExtractActArgs(Dictionary<string, object> msg, string content)
        {
            // 1) Proper tool_calls array.
            if (msg.TryGetValue("tool_calls", out var tcsObj) && tcsObj is List<object> tcs)
            {
                foreach (var tcObj in tcs)
                {
                    var tc = tcObj as Dictionary<string, object>; if (tc == null) continue;
                    var fn = tc.GetValueOrDefault("function") as Dictionary<string, object>; if (fn == null) continue;
                    string name = fn.GetValueOrDefault("name") as string;
                    string argStr = fn.GetValueOrDefault("arguments") as string;
                    if (name == null) continue;
                    // Nested-JSON models double-encode arguments; unwrap once.
                    return UnwrapArgs(name, argStr);
                }
            }

            // 2) JSON in content: {"thought":...,"action":...} or {"name":"act","arguments":{...}}
            if (!string.IsNullOrEmpty(content))
            {
                var parsed = TryParseObj(content);
                if (parsed != null)
                {
                    if (parsed.ContainsKey("action")) return new JsonObj(parsed);
                    string n = (parsed.GetValueOrDefault("name") ?? parsed.GetValueOrDefault("tool")) as string;
                    if (n != null)
                    {
                        object a = parsed.GetValueOrDefault("arguments") ?? parsed.GetValueOrDefault("args");
                        return a is Dictionary<string, object> ad ? new JsonObj(ad)
                             : a is string astr ? new JsonObj(astr)
                             : null;
                    }
                    // Single action like {"walk": {"speed":1}} — treat first known key as action.
                    foreach (var k in new[] { "walk","walk_ray","go_to","stop","look","jump","exit_station","crouch","move_to","interact","grab","drop","say","status" })
                        if (parsed.ContainsKey(k))
                        {
                            var inner = new Dictionary<string, object> { ["action"] = k, ["thought"] = "implicit" };
                            if (parsed[k] is Dictionary<string, object> innerArgs)
                                foreach (var kv in innerArgs) inner[kv.Key] = kv.Value;
                            return new JsonObj(inner);
                        }
                }
            }
            return null;
        }

        private JsonObj UnwrapArgs(string name, string argStr)
        {
            if (string.IsNullOrEmpty(argStr)) return TrySalvageTruncated(argStr) ?? new JsonObj("{}");
            var parsed = TryParseObj(argStr);
            if (parsed == null)
            {
                // Truncated/unterminated JSON from a length-cut reasoning model.
                var salvaged = TrySalvageTruncated(argStr);
                if (salvaged != null) return salvaged;
                return new JsonObj("{}");
            }
            if (!parsed.ContainsKey("action") && parsed["arguments"] is string nested)
            {
                var inner = TryParseObj(nested);
                if (inner != null && inner.ContainsKey("action")) return new JsonObj(inner);
            }
            if (name != "act" && name != null) parsed["action"] = name;
            return new JsonObj(parsed);
        }

        // Given partial JSON like {"action":"wal...   or   {"action":"walk","say":"hi
        // extract whatever action/say we can with regex so the NPC still acts.
        private JsonObj TrySalvageTruncated(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var d = new Dictionary<string, object>();
            var ma = System.Text.RegularExpressions.Regex.Match(s, "\"action\"\\s*:\\s*\"([a-z_]+)");
            if (!ma.Success) return null;
            d["action"] = ma.Groups[1].Value;
            var ms = System.Text.RegularExpressions.Regex.Match(s, "\"say\"\\s*:\\s*\"([^\"]*)");
            if (ms.Success) d["say"] = ms.Groups[1].Value;
            foreach (var num in new[] { "speed","turn_deg","yaw_deg","pitch_deg","x","y","z" })
            {
                var mn = System.Text.RegularExpressions.Regex.Match(s, "\"" + num + "\"\\s*:\\s*(-?[0-9.]+)");
                if (mn.Success) d[num] = double.Parse(mn.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            Logger.LogWarning("salvaged truncated act args: action=" + d["action"]);
            return new JsonObj(d);
        }

        private static Dictionary<string, object> TryParseObj(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Strip markdown fences that chat models love to wrap JSON in.
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
                s = s.Trim();
            }
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try { return Json.Parse(s.Substring(a, b - a + 1)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        // Adapter so say can be called with a raw string.
        private class TextArgs : JsonObj
        {
            public TextArgs(string text) : base(new Dictionary<string, object> { ["text"] = text }) { }
        }

        private object RunTool(string name, JsonObj p)
        {
            try
            {
                switch (name)
                {
                    case "walk":     return ToolWalk(p);
                    case "walk_ray": return ToolWalkRay(p);
                    case "go_to":    return ToolGoTo(p);
                    case "remember": return ToolRemember(p);
                    case "ask":      return ToolAsk(p);
                    case "look_around": return ToolLookAround(p);
                    case "stop":     return ToolStop();
                    case "look":     return ToolLook(p);
                    case "jump":     return ToolJump();
                    case "exit_station": return ToolExitStation();
                    case "crouch":   return ToolCrouch(p);
                    case "move_to":  return ToolMoveTo(p);
                    case "interact": return ToolInteract();
                    case "grab":     return ToolGrab(p);
                    case "drop":     return ToolDrop();
                    case "say":      return ToolSay(p);
                    case "status":   return ToolStatus();
                    case "none":     return new { ok = true };
                    default:
                        // Model was asked for act= but produced a legacy name.
                        Logger.LogWarning("unknown action: " + name);
                        return new { ok = false, reason = "unknown_tool", tool = name };
                }
            }
            catch (Exception e) { return new { ok = false, reason = e.Message }; }
        }

        // One mega-tool. Forcing tool_choice = act means the model can never
        // "just reply with text" — every tick yields a structured action.
        // Raw JSON schema for the act-args object — sent as response_format so any
        // model (not just tool-calling ones) is constrained to emit exactly this shape.
        private Dictionary<string, object> ActSchema()
        {
            var stepProps = ActionParamProps();
            stepProps["thought"] = Str("thought", "your goal (multi-step plan summary), <20 words");
            stepProps["wait"]    = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            var planItems = new Dictionary<string, object> { ["type"] = "object", ["properties"] = stepProps, ["required"] = new object[] { "action" }, ["additionalProperties"] = false };

            var props = ActionParamProps();
            props["thought"] = Str("thought", "your goal (multi-step plan summary), <20 words");
            props["wait"]    = Num("wait", "seconds to pause after THIS action before the next plan step, 0..3");
            props["plan"]    = new Dictionary<string, object> { ["type"] = new object[] { "array", "null" }, ["description"] = "optional follow-up actions, in order", ["items"] = planItems, ["maxItems"] = 8 };

            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new object[] { "action" },
                ["additionalProperties"] = false,
            };
        }

        private Dictionary<string, object> ActionParamProps()
        {
            return new Dictionary<string, object>
            {
                // ACTION FIRST: reasoning models burn tokens on "thought" and can
                // truncate before emitting the action. Emitting action (and the
                // movement/say params) first means even a truncated call still acts.
                ["action"]   = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new object[] { "walk","walk_ray","go_to","remember","ask","look_around","stop","look","jump","exit_station","crouch","move_to","interact","grab","drop","say","status","none" } },
                ["say"]      = Str("say", "optional <10 words — posts to the real in-game chat window AND a speech bubble"),
                ["name"]     = Str("name", "go_to: place to reach — 'bed' 'toilet' 'bath' 'sex' 'seat' 'bodyswap' 'player' or any usable's name"),
                ["mem"]      = Str("mem", "optional: a fact to remember for future turns (e.g. 'bed is upstairs', 'player is friendly') — stored in your long-term memory"),
                ["q"]        = Str("q", "ask: a question about the world ('what is this place?','who is that?') — answered from your current view + perception"),
                ["crouch"]   = Num("crouch", "0..1 how much to crouch (0=stand, 1=full crouch) — also the state for the 'crouch' action"),
                ["speed"]    = Num("speed", "walk forward -1..1"),
                ["duration"] = Num("duration", "walk: seconds to keep going, 0.1..8 (default 2, then auto-stop)"),
                ["turn_deg"] = Num("turn_deg", "walk/look deg turn (+right/-left); nearby.dir tells you which way"),
                ["yaw_deg"]  = Num("yaw_deg", "look abs yaw"),
                ["pitch_deg"]= Num("pitch_deg", "look abs pitch"),
                ["ray"]      = Num("ray", "walk_ray: ray index 0..N-1 across your view, or -1=center"),
                ["ray_deg"]  = Num("ray_deg", "walk_ray: signed degrees left(-)/right(+) of camera center"),
                ["x"] = Num("x", "move_to x"), ["y"] = Num("y", "move_to y"), ["z"] = Num("z", "move_to z"),
                ["sweep"]  = Num("sweep", "look_around: degrees to sweep around (default 120)"),
            };
        }

        private Dictionary<string, object> Num(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "number", "null" }, ["description"] = d }; }
        private Dictionary<string, object> Bool(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "boolean", "null" }, ["description"] = d }; }
        private Dictionary<string, object> Str(string n, string d) { return new Dictionary<string, object> { ["type"] = new object[] { "string", "null" }, ["description"] = d }; }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        // Unity overloads == on Object to fake-null destroyed objects; `is null`
        // bypasses that. This catches both real-null and destroyed.
        private static bool IsAlive(UnityEngine.Object o) => o != null;

        // The transform of whoever is currently penetrating us / we're inside —
        // most recently active one wins. Used to look at your partner during sex.
        private Transform MostRecentPenetrationSource()
        {
            float bestT = -1f; Transform best = null;
            lock (_pen) foreach (var kv in _pen)
            {
                var st = kv.Value;
                if (st.src != null && st.lastT > bestT) { bestT = st.lastT; best = st.src; }
            }
            lock (_dickIn) foreach (var kv in _dickIn)
            {
                var st = kv.Value;
                if (st.src != null && st.lastT > bestT) { bestT = st.lastT; best = st.src; }
            }
            if (best != null && IsAlive(best.gameObject)) return best;
            return null;
        }

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

    // (NavMesh / wander suppression is done via KoboldSeeker + KoboldAIPossession.)

    // ----------------------------------------------------------------------
    // minimal JSON (no external deps), enough for the payloads we exchange.
    // ----------------------------------------------------------------------
    internal static class Json
    {
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteVal(sb, v);
            return sb.ToString();
        }
        private static void WriteVal(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteStr(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is float || v is double || v is decimal)
            {
                double d = Convert.ToDouble(v);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
            if (v is byte || v is short || v is int || v is long) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict) { if (!first) sb.Append(','); first = false; WriteStr(sb, kv.Key); sb.Append(':'); WriteVal(sb, kv.Value); }
                sb.Append('}'); return;
            }
            if (v is IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en) { if (!first) sb.Append(','); first = false; WriteVal(sb, item); }
                sb.Append(']'); return;
            }
            // Anonymous/POCO types: serialize public instance properties as an object.
            var t = v.GetType();
            if (t.IsClass && !t.FullName.StartsWith("System.", StringComparison.Ordinal))
            {
                sb.Append('{');
                bool first = true;
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    object pv;
                    try { pv = prop.GetValue(v, null); } catch (Exception) { continue; }
                    if (!first) sb.Append(','); first = false;
                    WriteStr(sb, prop.Name); sb.Append(':'); WriteVal(sb, pv);
                }
                sb.Append('}'); return;
            }
            WriteStr(sb, v.ToString());
        }
        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        // Minimal recursive-descent parser returning Dictionary/List/primitives.
        private const int MaxDepth = 64;
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0, depth = 0;
            return ParseValue(text, ref i, ref depth);
        }
        private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        private static object ParseValue(string s, ref int i, ref int depth)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            if (depth > MaxDepth) { throw new InvalidDataException("json too deep"); }
            char c = s[i];
            if (c == '{')
            {
                depth++;
                var d = new Dictionary<string, object>();
                i++; SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; depth--; return d; }
                while (i < s.Length)
                {
                    SkipWs(s, ref i); if (i >= s.Length) break;
                    string key = ParseStr(s, ref i); SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ':') i++;
                    d[key] = ParseValue(s, ref i, ref depth); SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    break;
                }
                depth--;
                return d;
            }
            if (c == '[')
            {
                depth++;
                var l = new List<object>(); i++; SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; depth--; return l; }
                while (i < s.Length)
                {
                    l.Add(ParseValue(s, ref i, ref depth)); SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    break;
                }
                depth--;
                return l;
            }
            if (c == '"') return ParseStr(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return ParseNum(s, ref i);
        }
        private static string ParseStr(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    sb.Append(e == 'n' ? '\n' : e == 'r' ? '\r' : e == 't' ? '\t' : e == '"' ? '"' : e == '\\' ? '\\' : e == '/' ? '/' : e);
                }
                else if (c != '\\') sb.Append(c);
            }
            return sb.ToString();
        }
        private static object ParseNum(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "-+.eE0123456789".IndexOf(s[i]) >= 0) i++;
            if (i == start) { i++; return null; } // not a number: skip to avoid infinite loop
            string num = s.Substring(start, i - start);
            if (num.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) { double d; if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d; }
            long l; if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return num;
        }
    }

    // ----------------------------------------------------------------------
    // accessor over a JSON object string (the LLM tool arguments).
    // ----------------------------------------------------------------------
    internal class JsonObj
    {
        private readonly Dictionary<string, object> _d;
        public JsonObj(string json)
        {
            _d = Json.Parse(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
        public JsonObj(Dictionary<string, object> d)
        {
            _d = d ?? new Dictionary<string, object>();
        }
        public float F(string k, float def = 0f)
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            if (v is double db) return (float)db;
            if (v is long l) return l;
            if (v is int i) return i;
            float f; return float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : def;
        }
        public bool B(string k, bool def = false)
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b;
            bool r; return bool.TryParse(v.ToString(), out r) ? r : def;
        }
        public string S(string k, string def = "")
        {
            if (!_d.TryGetValue(k, out var v) || v == null) return def;
            return v.ToString();
        }
        public List<JsonObj> A(string k)
        {
            var outp = new List<JsonObj>();
            if (!_d.TryGetValue(k, out var v) || v == null) return outp;
            // Accept a real array, or a single object (models often emit one).
            if (v is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object> dd) outp.Add(new JsonObj(dd));
                    else if (item is string ss) outp.Add(new JsonObj(ss));
                }
            }
            else if (v is Dictionary<string, object> single) outp.Add(new JsonObj(single));
            else if (v is string sj) { try { outp.Add(new JsonObj(sj)); } catch (Exception) { } }
            return outp;
        }
    }
}
