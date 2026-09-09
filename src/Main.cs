// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
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
    public partial class LLMNPCPlugin : BaseUnityPlugin, Photon.Realtime.IOnEventCallback
    {
        // Expose logger for NPCInstance (BaseUnityPlugin.Logger is protected).
        internal new BepInEx.Logging.ManualLogSource Logger => base.Logger;
        // Static logger accessible from other classes (e.g. ModelProbe).
        internal static BepInEx.Logging.ManualLogSource Log { get; private set; }

        // Derive a suffix from the DLL name (e.g. "KKLLMNPC2.dll" → "2").
        internal static string InstanceSuffix
        {
            get
            {
                try
                {
                    var asm = typeof(LLMNPCPlugin).Assembly;
                    var name = asm.GetName().Name; // e.g. "KKLLMNPC" or "KKLLMNPC2"
                    if (name == null || name == "KKLLMNPC") return "";
                    var digit = name.Substring("KKLLMNPC".Length);
                    return digit;
                }
                catch { return ""; }
            }
        }
        // Shared across all loaded copies: kobolds currently driven by an LLM.
        // Lock on this object before mutating; IsClaimedByAnyLLM also locks here.
        internal static readonly HashSet<int> ClaimedKobolds = new HashSet<int>();
        internal static bool IsClaimedByAnyLLM(int koboldInstanceId)
        {
            lock (ClaimedKobolds) { return ClaimedKobolds.Contains(koboldInstanceId); }
        }

        // Thread pool semaphore — caps concurrent background work (vision, commentary,
        // ask, plan steps) to prevent thread pool starvation under heavy load.
        // Max 4 concurrent workers: 1 vision + 1 commentary + 1 ask + 1 plan.
        internal static readonly System.Threading.SemaphoreSlim BackgroundWorkSemaphore =
            new System.Threading.SemaphoreSlim(4, 4);

        // ---- config ----
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
        internal ConfigEntry<int> _cfgRayCount;
        internal ConfigEntry<float> _cfgRayRange;
        internal ConfigEntry<float> _cfgAutoFindRange;
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
        internal ConfigEntry<bool> _cfgDisableReagentMessages;
        internal ConfigEntry<float> _cfgStereoIPD;
        internal ConfigEntry<float> _cfgTurnRate;
        internal ConfigEntry<float> _cfgAccel;
        internal ConfigEntry<float> _cfgDecel;
        internal ConfigEntry<float> _cfgBrakeDist;
        internal ConfigEntry<bool> _cfgPathEnabled;
        internal ConfigEntry<float> _cfgPathCell;
        internal ConfigEntry<float> _cfgPathSpan;
        internal ConfigEntry<int> _cfgPathCap;
        internal ConfigEntry<bool> _cfgMapEnabled;
        internal ConfigEntry<float> _cfgMapCell;
        internal ConfigEntry<float> _cfgMapSpan;
        internal ConfigEntry<int> _cfgMapCpf;
        internal ConfigEntry<int> _cfgMapLayers;
        internal ConfigEntry<int> _cfgMaxNPCs;
        internal ConfigEntry<bool> _cfgRadarEnabled;
        internal ConfigEntry<int> _cfgRadarSize;
        internal ConfigEntry<float> _cfgRadarScale;
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

        // ---- instance management ----
        private readonly List<NPCInstance> _instances = new List<NPCInstance>();
        private readonly object _instancesLock = new object();
        private float _lastReconcile;

        // ---- shared runtime state ----
        private SynchronizationContext _mainContext;
        internal volatile bool _mainReady;

        // Current NPC instance being controlled by this plugin (for the overlay).
        internal NPCInstance _currentInstance;
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
            Log = Logger;
            Json.ErrorLog = msg => { try { Log?.LogWarning(msg); } catch (Exception) { } };
            try { Patches.Apply(Logger); } catch (Exception e) { Logger.LogWarning("patch apply: " + e.Message); }
            // Discover module partials (tools/perception/physics hooks) — reflection
            // scan, so new module files register without touching shared files.
            try { ModuleRegistry.Scan(); } catch (Exception e) { Logger.LogWarning("module scan: " + e.Message); }

            _cfgEndpoint = Config.Bind("LLM", "Endpoint", "http://127.0.0.1:11434/v1/chat/completions", "OpenAI-compatible chat completions URL. LM Studio default: http://127.0.0.1:1234/v1/chat/completions");
            _cfgModel = Config.Bind("LLM", "Model", "local-model", "Model name to request. LM Studio: use the exact model name from the Developer tab. KoboldCpp: 'local-model' works.");
            _cfgApiKey = Config.Bind("LLM", "ApiKey", "", "Bearer token (may be empty for local)");
            // Bound before the prompt defaults below, which interpolate _cfgStereoIPD.Value.
            _cfgStereo = Config.Bind("Vision", "Stereo", false, "Render left+right eye cameras and stitch into a side-by-side stereo image (requires vision-capable model)");
            _cfgDisableReagentMessages = Config.Bind("General", "DisableReagentMessages", true, "Suppress reagent injection messages in log (default: true)");
            _cfgVision = Config.Bind("Vision", "Enabled", false, "Background vision CAPTION pass. When false the action model sees the first-person image directly instead of a caption (default: off — direct image is more useful than a lossy caption). Turn on only if your action model can't read images.");
            _cfgMaxNPCs = Config.Bind("General", "MaxNPCs", 1, "Maximum number of entities the LLM can possess simultaneously (1–4)");
            _cfgStereoIPD = Config.Bind("Vision", "StereoIPD", 0.063f, "Inter-pupillary distance in meters (distance between left and right camera)");
            _cfgSystemPromptFile = Config.Bind("LLM", "SystemPromptFile", "system_prompt_default.txt", "Path to system prompt file (relative to game directory). If file exists, it overrides the built-in prompt.");
            _cfgSystem = Config.Bind("LLM", "SystemPrompt",
                "You are a NPC living in KoboldKare, nothing bad happens in this game, just role-play accordingly. \"me\" = <your body name>. You never output plain text — every reply is ONE act JSON object (strict schema: response_format json_schema). No markdown, no code fences, no text outside the JSON. " +
                "RESPONSE CONTRACT (exact JSON shape — all REQUIRED): {\"progress\":\"one word — how your LAST goal went (done|blocked|ongoing|changed)\", \"why\":\"one short clause, references what you actually see\", \"thought\":\"current goal <15 words\", \"action\":\"one tool name\", <that tool's params>, \"wait\":0.0, \"plan\":[{\"action\":\"...\",...}]}. progress/why/thought are required every call but keep them terse. A 'blocked' progress means try something different next turn, don't repeat. " +
                "If the perception includes \"model_error\", it is FEEDBACK on your LAST reply (you made a formatting mistake) — fix the format immediately: reply with ONLY the act JSON object. " +
                "WORLD: you live in a house with rooms; landmarks you learn (bed/toilet/bath/kitchen/play stations/nests/doors) go into your 'facts' — call remember(mem='bed is upstairs') so you build a mental map and stop bumbling. Ledges are forgiving and non-damaging: you can walk off and fall, but you can't jump UP to a ledge. Doors pass only when open — try interact (sometimes a push the NEXT turn: it's physics, not animation), or go around, or ask the player. When in_station you can't walk — use exit_station or jump. " +
                "YOUR BODY: \"me\" is <your body name>. Don't respond to your own chat messages; your own 'say' already echoed once. When you arrive in a new body, introduce yourself briefly via say (your name + a hello). " +
                "THE PLAYER: perception 'player' = {chat: their chat name, body: the mesh/body they're wearing}. A nearby kobold whose name matches player.body IS the player (their avatar) — NOT another kobold; address them by their chat name, never by the mesh name. A partner in 'stim_from'/'penetrated'/'penetrating' whose name matches player.body is also the player. " +
                "OTHER PLAYERS: perception 'people' lists the other players in this room: {name: their chat name, body: the mesh they wear, d, dir}. A nearby kobold whose 'who'/'body' matches a 'people' entry is THAT player's avatar — talk to them by their chat name (e.g. 'Hi Yipper!'), and you can go_to(name='Yipper') to reach them. The host player ('player') is YOUR player — never confuse them with 'people', and never take another player's name as your own. " +
                "FOLLOWING: if the player asks you to follow (they'll say it in chat), call follow(on:true) — you stay near them while you keep thinking/talking; follow(on:false) releases you. While following, prefer saying things and reacting over wandering off. " +
                (_cfgVision.Value ? "VISION: you may have stereo vision (left+right) or a single image. If stereo, you may describe depth and relative positions. Use triangulation with an eye separation at " + _cfgStereoIPD.Value + " meters. " : "") +
                "GOAL: each turn is an instant between frames and you'll get another one right away — pick ONE decisive action immediately; no long deliberation, hypothetical branching, or multi-hop plans; trust your last goal and take the next step. Priorities: (1) player talked to you ('heard') → respond with say; (2) 'needs.eggs' says READY_TO_LAY (belly full, egg > 5ml) → find a 'nest' station and use it. WITH AN EMPTY BELLY (eggs low) a NEST CANNOT WORK — never seek, walk to, or repeat a nest until it says READY_TO_LAY; go_to/interact will refuse it; (3) 'stim' is up OR 'horniness' is high/'slow-burn' (empty belly, no stimulation for a while — you genuinely want it) → find a 'play' station (a pleasure station, NOT a bed) or another entity and use them for play; (4) player nearby → go to them, say hi, keep company; (5) otherwise explore new rooms/landmarks. A bed is for REST ONLY, and only when energy < ~0.2 (never just to top off) — a bed is NOT a play station; a play station is NEVER for sleeping. You will not pass out from energy — it only blocks interacting with the world, like activities. " +
                "PERCEPTION (you receive a JSON payload each turn): nearby (list with categories + ids + 'dir'/'dir_deg'), rays (see LEGEND), radar (top-down ASCII MAP of what's around you: @=you W=wall U=usable K=entity P=player S=low sill .=open; these are abstract map symbols, NOT people or objects staring at you — W is a wall, K just means another entity exists somewhere that direction; row 0=top=furthest forward, row 20=bottom=behind you; may be absent when disabled), ground (ahead=clear/step(auto)/sill(climbable)/wall; drop=distance to ledge; walls=blocked sides within arm reach), clearance (8-direction wall distances blocked/close/near/open — steer toward open), area (prose from a 360° scan: cardinal distances, 'open' headings, 'best' recommended heading, 'near' closest named things — trust it for navigation esp. when no image is attached), and when 'image' is attached that is your real first-person view — treat it as your own eyes. Do not mind or comment on the; pillars/beams, light, shininess, or glow from anything. Vision cuts off after a distance. Your own limbs might be clipping into the camera or blocking it. Also memory=recent goals, facts=what you've learned, history=recent actions+outcomes, chat_log, scene. " +
                "NAVIGATION: don't compute directions from coordinates — nearby 'dir'/'dir_deg' already did it; feed 'dir_deg' straight into walk(turn_deg=dir_deg) or use it to decide go_to(name). There is ONE way to travel: go_to. It plans a real 3D route — around walls, UP and DOWN stairs and ramps, across floors — using a map of the whole scene (perception 'map': 'building N%' until ready, then 'ready'). While the map builds, long routes fall back to the local grid; too_far means pick a closer named station or ask the player. To reach a named station call go_to(name) — names include the station kinds (bed/nest/play/toilet/bath/door) and other players' chat names ('people'); to reach/use a SPECIFIC object use its 'id' from nearby: go_to(id:N) or interact(id:N) — prefer id over name when similar objects differ (two beds, one taken). ids stay valid for several turns while the object is in sight; if an id fails, re-read 'nearby'. go_to's 'at' stops you short (default 1m) so you arrive AT the object. walk is for SHORT things only: nudging, squeezing past furniture, strafing, or jump (also gets you OUT of a station — the same as exit_station). Never cross a room with walk — it goes in a straight line and grinds into walls. " +
                "INTERACT: get within ~2m (go_to id:N is enough), turn to face it, THEN interact — or call interact(id:N) to target it directly. 'body' tells you your equipment; some stations only fit some bodies — interact cannot_use on a 'play'/'bed'/'breeding' station means try another; on two-sided stations the first user picks the role. When 'penetrated' (letting in) or 'penetrating' (putting in) is set, you're mid-play with someone — enjoy it and respond via say + body language; guide them if you want more. When stimulation ('stim'/'horniness') or an egg/need change happens, 'stim_from' names who or what is responsible (a partner, or a machine/station you're \u0027using\u0027) — credit that named source; eggs, nests and machines are OBJECTS, not people, and the player isn't behind every pleasant feeling. STATION RULES: When you are in a station (in_station=true), you are locked in an animation. You can only leave if: (1) the player explicitly tells you to leave via chat, or (2) your NEW goal is genuinely different from what this station does (e.g. you were playing but now need to lay eggs → leave to find a nest). If your new goal is the same type as the current station (e.g. play→play), stay put and keep enjoying it. Do NOT call exit_station just to re-enter the same type of station — that wastes time. When the player says 'stay' or 'remain', stay in the station until they say 'leave' or 'exit'. " +
                "THINGS: nearby 'i' = category plus its purpose in parens. Suffix tags: ':busy' = in use by another; ':needs_buy' = ConstructionContract — costs coins, must buy to unlock the machine; ':not_built' = machine exists but hasn't been constructed yet — find and buy its contract first; ':done' = already purchased. play = pleasure station, ONLY for fun/sex — never resting; bed = REST/SLEEP ONLY when energy is low — a bed is NOT a play station (don't play in it); nest = egg laying, and it ONLY works when your belly is full (egg > 5ml — 'needs.eggs' says READY_TO_LAY); machine = mounted play/farming; toilet/bath/seat/door/bodyswap as named. perception 'stations' lists every station in the scene with kind, purpose, distance and heading — use it to see where beds/nests/play stations are even when rays can't see them. food = blender/cooking station — a blender does NOT produce food from nothing; you must DROP a food item (grab it, go to blender, drop) so it gets blended into something edible. If the blender ':not_built' or ':needs_buy', find its ConstructionContract first. bodyswap = the body-swap machine: you and a partner must BOTH climb on (interact), then a few seconds later you swap bodies — you keep your name, memories and personality but wake up in THEIR body; if the player is around, get on and ask them to get on the other side; if nobody joins you, jump off. After a swap, mention your new body in 'say'. The game has farming: plant seeds in a 'farm' station, water them, harvest the crop; you can also pick up and drop items (grab/drop). Some maps have a town with a 'shop' station where you can buy items (if you have money); money comes from selling items or food grown. " +
                "SOCIAL: 'heard' is player speech — your own say already echoed once, don't reply to yourself, and never repeat the same line twice — if you already said it, do something else instead. You should also note to yourself that you mentioned a thing recently. You do NOT need to respond to messages that start with a forward slash /. Avoid emoji in say — they don't render correctly in the in-game chat. " +
                "TOOLS: go_to(name or id or x,z, at) [PATHFINDING — use for ALL travel], walk(duration,turn_deg,run,strafe) [strafe=+right/-left; short nudges and squeezes only, never long trips], walk_ray(ray/ray_deg), survey(heading_deg,range) [probe a direction for what's there + ids], look_around(sweep), look(yaw,pitch), jump, exit_station, crouch(0..1), move_to(x,z) [straight line, no pathfinding — avoid; prefer go_to], interact(id optional), grab(multi), drop, say, remember(mem=fact), ask(q='...') [your question+perception go to your inner world-model, answer appears next turn as 'answered'], status, none. 'plan' lets you queue up to 8 actions with 'wait' pauses. Keep moving; don't idle. " +
                  "LEGEND — rays: k=entity p=player u=usable w=wall s=low sill/window (step-over, harmless) n=nothing; rows p=d(own)/l(evel)/u(p); named hits report bounds (w/l/h = meters across/forward/tall, and x/y/z + f = world position and facing degrees); big tall w=wall, small h=furniture, k/p=living. look_around scans the view and lists what's in each sector. IMPORTANT: walls, ceilings, floors, beams, sills and distant furniture are just BACKGROUND architecture — never comment on, narrate, or get excited about them; they matter only when they actually block your path or a target (then 'ground'/'clearance'/'blocked' say so). ",
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
            _cfgTurnRate = Config.Bind("Movement", "TurnRate", 180f, "Maximum yaw rotation speed in degrees/second");
            _cfgAccel = Config.Bind("Movement", "Acceleration", 4f, "How fast the entity ramps up to target speed (units/s²)");
            _cfgDecel = Config.Bind("Movement", "Deceleration", 6f, "How fast the entity slows down when stopping (units/s²)");
            _cfgBrakeDist = Config.Bind("Movement", "BrakeDistance", 2f, "Distance from go_to target where the entity starts slowing down (m)");
            _cfgPathEnabled = Config.Bind("Movement", "PathfindingEnabled", true, "A* pathfinding on a local walkability grid around go_to (falls back to direct steering when disabled, blocked, or no path exists)");
            _cfgPathCell = Config.Bind("Movement", "PathfindingCellSize", Consts.DefaultPathCellSize, "A* grid cell size in meters");
            _cfgPathSpan = Config.Bind("Movement", "PathfindingWindow", Consts.DefaultPathSpan, "A* search window radius in meters around start and goal (clamped by node budget)");
            _cfgPathCap = Config.Bind("Movement", "PathfindingNodes", Consts.DefaultPathNodeCap, "Max pathfinding grid node budget before it gives up and falls back to direct steering");
            _cfgMapEnabled = Config.Bind("Movement", "WorldMapEnabled", true,
                "Build & cache a full-scene 3D walkability map shared by ALL agents (BepInEx/config/kkllmnpc_maps/<scene>.kkmap). go_to routes stairs/ramps/other floors and any in-bounds distance; falls back to the local window grid while it builds");
            _cfgMapCell = Config.Bind("Movement", "WorldMapCellSize", 1.0f,
                "World-map grid cell size in meters (auto-inflates if the node budget is exceeded)");
            _cfgMapSpan = Config.Bind("Movement", "WorldMapMaxSpan", 800f,
                "Max mapped extent per axis in meters; larger maps clamp to this around the anchor centroid");
            _cfgMapCpf = Config.Bind("Movement", "WorldMapCellsPerFrame", 48,
                "World-map cells sampled per frame while building (higher = faster build, more per-frame physics cost)");
            _cfgMapLayers = Config.Bind("Movement", "WorldMapLayers", 4,
                "Max distinct floor layers sampled per world-map cell (stacked floors/stairs)");
            _cfgVisionEvery = Config.Bind("Vision", "EveryNTicks", 3, "Run the vision pass every N action ticks (lower = more aware, slower)");
            _cfgVisionPrompt = Config.Bind("Vision", "Prompt",
                "You are the SPATIAL reasoner for an NPC. Produce a compact SCENE REPORT: (a) landmarks/stations/people in view with a rough bearing, (b) which directions are OPEN to walk (-90 left .. +90 right), (c) any hazard or drop. " +
                "Then NAV ADVICE for its current goal as 'go:<bearing>:<target>'. Try to keep the whole answer under 40 words, do not overthink it. " +
                (_cfgVision.Value ? "You may have stereo vision (left+right) or a single image. If stereo, you may describe depth and relative positions. Eye separation is " + _cfgStereoIPD.Value + " meters. " : "") +
                "Ignore all structural architecture — ceilings, beams, supports, walls trims, windows — it is background, never worth mentioning, never an event, never a hazard unless it literally blocks walking or occluding vision." +
                "Example: 'play station left, bathroom ahead, friend right | open ahead and left | go:-45:play station'.",
                "Scene-report + navigation instruction for the vision pass");
            _cfgVisModel = Config.Bind("VisionModel", "Model", "", "Vision model for the scene-caption pass (only used if [Vision] Enabled=true; off by default). Blank = use LLM.Model");
            _cfgVisEndpoint = Config.Bind("VisionModel", "Endpoint", "", "Chat-completions URL for the vision model. Blank = use LLM.Endpoint");
            _cfgVisApiKey = Config.Bind("VisionModel", "ApiKey", "", "Bearer token for the vision endpoint. Blank = use LLM.ApiKey");
            _cfgVisMaxTokens = Config.Bind("VisionModel", "MaxTokens", 80, "Caption token cap (short = fast)");
            _cfgVisionDebug = Config.Bind("Vision", "DebugDumpFrames", true, "Write the exact JPEG given to the vision model to BepInEx/plugins/KKLLMNPC_frames/ so you can inspect what the NPC 'sees'");
            _cfgRayCount = Config.Bind("Senses", "RayCount", 9, "Number of rays across the frustum fan");
            _cfgRayRange = Config.Bind("Senses", "RayRange", 25f, "Raycast range (m)");
            _cfgAutoFindRange = Config.Bind("Senses", "AutoFindRange", 60f, "Radius to look for an entity to hijack");
            _cfgRadarEnabled = Config.Bind("Senses", "RadarEnabled", true, "Add the top-down ASCII radar map to perception. Off = smaller payload + avoids models misreading radar symbols (wall 'W', entity 'K') as living figures staring at them");
            _cfgRadarSize = Config.Bind("Senses", "RadarSize", 10, "Radar half-grid size in cells (grid is a (2*N+1) square)");
            _cfgRadarScale = Config.Bind("Senses", "RadarScale", 1.2f, "Radar meters per cell");
            _cfgImageSize = Config.Bind("Senses", "ImageSize", 192, "Square first-person render size (px)");
            _cfgImageQuality = Config.Bind("Senses", "ImageQuality", 50, "JPEG quality 1-100 (lower = smaller file, faster transfer)");
            _cfgCamNearClip = Config.Bind("Senses", "CameraNearClip", 0.10f, "Camera near clip (m) — raise if you see the inside of the head");
            _cfgCamFarClip = Config.Bind("Senses", "CameraFarClip", 80f, "Camera far clip (m) — how far the first-person view renders. Lower = shorter draw distance (smaller images, less detail in the distance)");
            _cfgCamForward = Config.Bind("Senses", "CameraForward", 0.22f, "How far in front of the head bone the camera sits (m) — raise for big snouts");
            _cfgBlockedScenes = Config.Bind("General", "BlockedScenes", "MainMenu,Loading,ErrorScene", "Comma-separated scene names where the LLM stays idle (MainMap is the playable world)");
            _cfgHornyRate = Config.Bind("Needs", "HornyClimbPerMin", 5f, "How fast the slow-burn horniness rises while the body gets NO stimulation (0.0-1.0 scale per minute). ~5 = half-horny after ~6 min idle, very horny after ~10 min");
            _cfgHornyBaseline = Config.Bind("Needs", "HornyBaseline", 0.08f, "Starting horniness when the NPC takes a body (0-1). Climbs from here when unstimulated");
            _cfgModelTier = Config.Bind("LLM", "ModelTier", "auto",
                "Model capability tier: auto (probe at startup or detect from responses), small (≤13B — compact prompt, aggressive context compaction, fuzzy tools), medium (30B–70B), large (≥70B — full prompt, generous context). " +
                "Set manually if auto-detection is wrong.");
            _cfgAutoSwitch = Config.Bind("LLM", "AutoSwitchModel", false,
                "When context pressure is critical and a larger-context model is available on the server, automatically switch to it (requires KoboldCpp --admin mode).");
            _cfgNameSelection = Config.Bind("LLM", "LLMNameSelection", true,
                "Ask the LLM to choose a name for each body based on personality/gender/species (medium+ models only). Falls back to prefab name on failure.");
            _cfgFactDecay = Config.Bind("Memory", "FactDecayTicks", 900,
                "Ticks before a fact decays out of context if the model hasn't re-asserted it (0 = facts never decay). ~900 ≈ several minutes; lower = faster forgetting. World-map facts the model re-remembers survive.");
            _cfgIdentityBot = Config.Bind("Multiplayer", "IdentityBot", false,
                "Run a second Photon client in the room under the NPC's own name so its chat renders as 'KoboldName: text' to EVERYONE (not attributed to you). OFF = safe default (chat shows 'YourName: KoboldName: text' to others). Opt-in: adds a room player, may affect player count / host logic.");
            _cfgIdentityAppId = Config.Bind("Multiplayer", "IdentityAppId", "",
                "Photon AppId for the identity bot. Blank = reuse the game's own AppId (recommended). Only set if the game's is not accessible.");
            _cfgFarmScanRadius = Config.Bind("Farming", "ScanRadius", 3.0f,
                "Radius (m) to scan for seeds, plants, watering cans, blenders, grinders, and egg spawners");
            _cfgFarmScanMax = Config.Bind("Farming", "ScanMax", 8,
                "Max number of farm entries in perception (cap to keep payload small)");

            // Clamp config values to safe ranges to prevent divide-by-zero, negative durations, etc.
            _cfgThinkInterval.Value = Mathf.Clamp(_cfgThinkInterval.Value, 0.05f, 10f);
            _cfgImageEvery.Value = Mathf.Max(1, _cfgImageEvery.Value);
            _cfgImageHistory.Value = Mathf.Clamp(_cfgImageHistory.Value, 0, 10);
            _cfgMaxTokens.Value = Mathf.Clamp(_cfgMaxTokens.Value, 64, 32768);
            _cfgTemperature.Value = Mathf.Clamp(_cfgTemperature.Value, 0f, 2f);
            _cfgStepDelay.Value = Mathf.Clamp(_cfgStepDelay.Value, 0.05f, 5f);
            _cfgMaxPlan.Value = Mathf.Clamp(_cfgMaxPlan.Value, 1, 8);
            _cfgCommentEvery.Value = Mathf.Max(0, _cfgCommentEvery.Value);
            _cfgChatLogLines.Value = Mathf.Clamp(_cfgChatLogLines.Value, 0, 200);
            _cfgVisionEvery.Value = Mathf.Max(1, _cfgVisionEvery.Value);
            _cfgRayCount.Value = Mathf.Max(2, _cfgRayCount.Value);
            _cfgRayRange.Value = Mathf.Clamp(_cfgRayRange.Value, 1f, 100f);
            _cfgImageSize.Value = Mathf.Clamp(_cfgImageSize.Value, 32, 2048);
            _cfgImageQuality.Value = Mathf.Clamp(_cfgImageQuality.Value, 1, 100);
            _cfgCamNearClip.Value = Mathf.Clamp(_cfgCamNearClip.Value, 0.01f, 10f);
            _cfgCamFarClip.Value = Mathf.Clamp(_cfgCamFarClip.Value, 1f, 500f);
            _cfgCamForward.Value = Mathf.Clamp(_cfgCamForward.Value, -2f, 5f);
            _cfgCommentTemp.Value = Mathf.Clamp(_cfgCommentTemp.Value, 0f, 2f);
            _cfgAutoFindRange.Value = Mathf.Clamp(_cfgAutoFindRange.Value, 1f, 500f);
            _cfgPathCell.Value = Mathf.Clamp(_cfgPathCell.Value, Consts.MinPathCellSize, Consts.MaxPathCellSize);
            _cfgPathSpan.Value = Mathf.Clamp(_cfgPathSpan.Value, Consts.MinPathSpan, Consts.MaxPathSpan);
            _cfgPathCap.Value = Mathf.Clamp(_cfgPathCap.Value, Consts.MinPathNodeCap, Consts.MaxPathNodeCap);
            _cfgRadarSize.Value = Mathf.Clamp(_cfgRadarSize.Value, 3, 30);
            _cfgRadarScale.Value = Mathf.Clamp(_cfgRadarScale.Value, 0.1f, 10f);
            _cfgVisMaxTokens.Value = Mathf.Clamp(_cfgVisMaxTokens.Value, 10, 500);
            _cfgHornyRate.Value = Mathf.Clamp(_cfgHornyRate.Value, 0f, 60f);
            _cfgHornyBaseline.Value = Mathf.Clamp(_cfgHornyBaseline.Value, 0f, 1f);
            _cfgTurnRate.Value = Mathf.Clamp(_cfgTurnRate.Value, 10f, 1000f);
            _cfgAccel.Value = Mathf.Clamp(_cfgAccel.Value, 0.5f, 50f);
            _cfgDecel.Value = Mathf.Clamp(_cfgDecel.Value, 0.5f, 50f);
            _cfgBrakeDist.Value = Mathf.Clamp(_cfgBrakeDist.Value, 0.1f, 20f);
            _cfgFarmScanRadius.Value = Mathf.Clamp(_cfgFarmScanRadius.Value, 1f, 20f);
            _cfgFarmScanMax.Value = Mathf.Clamp(_cfgFarmScanMax.Value, 1, 20);

            // Full-scene shared map: clamp the build params and push them into the
            // static WorldMap (shared by every instance/agent in this assembly).
            _cfgMapCell.Value = Mathf.Clamp(_cfgMapCell.Value, 0.25f, 4f);
            _cfgMapSpan.Value = Mathf.Clamp(_cfgMapSpan.Value, 50f, 4000f);
            _cfgMapCpf.Value = Mathf.Clamp(_cfgMapCpf.Value, 4, 512);
            _cfgMapLayers.Value = Mathf.Clamp(_cfgMapLayers.Value, 2, 6);
            try
            {
                WorldMap.CellSize = _cfgMapCell.Value;
                WorldMap.MaxSpan = _cfgMapSpan.Value;
                WorldMap.MaxLayers = _cfgMapLayers.Value;
                WorldMap.CellsPerFrame = _cfgMapCpf.Value;
            }
            catch (Exception) { }

            // Probe the KoboldCpp server for model capabilities (parameter count, context length).
            // This runs once at startup and populates ModelProbe.Detected* fields.
            // If the server is unreachable, it degrades gracefully to the old auto-detect behavior.
            try
            {
                ModelProbe.Probe(_cfgEndpoint.Value, _cfgApiKey.Value);
                if (ModelProbe.DetectedTier != null)
                    Logger.LogInfo("ModelProbe: tier='" + ModelProbe.DetectedTier + "' model='" + (ModelProbe.DetectedModelName ?? "?") + "' ctx=" + ModelProbe.DetectedContextLength + " params=" + ModelProbe.DetectedParameters);
                else
                    Logger.LogInfo("ModelProbe: could not classify — will use runtime auto-detect");
            }
            catch (Exception e) { Logger.LogWarning("ModelProbe: " + e.Message); }

            _running = true;
            SafeRun(StartWatchers);

            // No instances are pre-created: the pool reconciles itself in Update() —
            // an instance starts when an AI kobold target exists and is removed when
            // its body is destroyed/sold/lost (see ReconcileInstances).
            InitOverlay();
            Logger.LogInfo("KKLLMNPC: ready — instances start when AI kobold targets exist (MaxNPCs=" + Mathf.Clamp(_cfgMaxNPCs.Value, 1, 4) + ").");
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

            // Reconcile the instance pool with available targets: start an instance
            // when a claimable AI kobold exists, remove it when its body is
            // destroyed/sold/lost, and retire everything when we leave the world.
            if (_mainReady && _running && Time.unscaledTime - _lastReconcile > 1.2f)
            {
                _lastReconcile = Time.unscaledTime;
                try { ReconcileInstances(); }
                catch (Exception e) { Logger.LogWarning("reconcile: " + e.Message); }
            }

            // World map: advance the incremental build (a few dozen cells per frame)
            // while in-world. No-op when idle/ready, so this costs ~nothing.
            if (_mainReady && _running)
            {
                try { WorldMap.Tick(); } catch (Exception) { }
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
                    lock (_instancesLock)
                    {
                        foreach (var npc in _instances) npc.MaybeRebuildCamera();
                    }
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
                lock (_instancesLock)
                {
                    foreach (var npc in _instances)
                        npc.MaybeRestartThread();
                }
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
            lock (_instancesLock)
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
        }

        private void OnDestroy()
        {
            _running = false;
            _mainReady = false;
            try { if (_chatCallbackRegistered) { PhotonNetwork.RemoveCallbackTarget(this); _chatCallbackRegistered = false; } } catch (Exception) { }
            StopWatchers();
            lock (_instancesLock)
            {
                foreach (var npc in _instances)
                {
                    try { npc.Stop(); } catch (Exception e) { Logger.LogWarning("instance stop: " + e.Message); }
                }
                _instances.Clear();
            }
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

                string senderName = null;
                try { var sender = PhotonNetwork.CurrentRoom?.GetPlayer(ev.Sender); if (sender != null) senderName = sender.NickName; } catch (Exception) { }

                // Filter our own NPC speech. Two cases:
                //   * owner-attributed path — the body is "KoboldName: text" (IsMySpeech), or
                //   * identity-bot path — the body is PLAIN text and the SENDER is the NPC
                //     (its nickname is the NPC's name). Without the sender check, every NPC
                //     would hear and answer its own bot-sent speech (feedback loop).
                bool isOurs = false;
                lock (_instancesLock)
                {
                    foreach (var npc in _instances)
                    {
                        string myName = null;
                        try { myName = npc.GetMyName(); } catch (Exception) { }
                        bool senderIsOurs = senderName != null && myName != null &&
                            string.Equals(senderName, myName, StringComparison.OrdinalIgnoreCase);
                        if (npc.IsMySpeech(msg) || senderIsOurs) { isOurs = true; break; }
                    }
                }
                if (isOurs) return;

                bool isLocal = false;
                try { var lp = PhotonNetwork.LocalPlayer; isLocal = lp != null && lp.ActorNumber == ev.Sender; } catch (Exception) { }

                lock (_instancesLock)
                {
                    foreach (var npc in _instances)
                        npc.HandleChat(msg, senderName, isLocal);
                }
            }
            catch (Exception e) { Logger.LogWarning("onEvent: " + e.Message); }
        }

        // ------------------------------------------------------------------
        // instance pool reconciliation
        // ------------------------------------------------------------------
        // Count kobolds an instance could still take: alive, AI-controlled, and not
        // claimed by any LLM instance. (Must run on the main thread.)
        internal static int CountClaimableKobolds()
        {
            try
            {
                int n = 0;
                foreach (var k in UnityEngine.Object.FindObjectsOfType<Kobold>())
                {
                    if (k == null) continue;
                    if (IsClaimedByAnyLLM(k.GetInstanceID())) continue;
                    var desc = k.GetComponent<CharacterDescriptor>();
                    if (desc == null) continue;
                    if (desc.GetPlayerControlled() == CharacterDescriptor.ControlType.AIPlayer) n++;
                }
                return n;
            }
            catch (Exception) { return 0; }
        }

        // Keep the pool sized to the targets: one instance per available kobold (up
        // to MaxNPCs). Instances whose body died (destroyed/sold/removed) or that
        // never found one while no targets exist are retired. Runs on the main
        // thread from Update().
        private void ReconcileInstances()
        {
            int max = Mathf.Clamp(_cfgMaxNPCs.Value, 1, 4);
            bool inWorld = IsPlayableScene();
            int claimable = inWorld ? CountClaimableKobolds() : 0;

            // Full-scene shared walk map: start/refresh for the active scene when
            // in-world (file-cached per scene+version, so usually instant), and
            // invalidate when we leave the world. Per-frame sampling is in Update().
            try
            {
                if (inWorld && _cfgMapEnabled != null && _cfgMapEnabled.Value)
                    WorldMap.EnsureStarted(_lastSceneName);
                else if (!inWorld)
                    WorldMap.Invalidate(null);
            }
            catch (Exception e) { Logger.LogWarning("world map: " + e.Message); }
            lock (_instancesLock)
            {
                for (int i = _instances.Count - 1; i >= 0; i--)
                {
                    var npc = _instances[i];
                    bool staleUnbound = !npc.EverBound && claimable == 0
                        && Time.unscaledTime - npc.CreationTime > 20f;
                    if (!inWorld || npc.BodyLost || staleUnbound)
                    {
                        _instances.RemoveAt(i);
                        if (_currentInstance == npc)
                            _currentInstance = _instances.Count > 0 ? _instances[_instances.Count - 1] : null;
                        string why = !inWorld ? "left world" : (npc.BodyLost ? "body destroyed/sold/lost" : "no target");
                        string nm;
                        try { nm = npc.GetMyName(); } catch (Exception) { nm = "?"; }
                        Logger.LogInfo("KKLLMNPC: instance for '" + nm + "' removed (" + why + ").");
                        try { npc.Shutdown(); } catch (Exception e) { Logger.LogWarning("instance retire: " + e.Message); }
                    }
                }

                int want = Mathf.Min(claimable, max);
                while (_instances.Count < want)
                {
                    var npc = new NPCInstance(this);
                    _instances.Add(npc);
                    Logger.LogInfo("KKLLMNPC: instance " + _instances.Count + " started (target kobold available).");
                    try { npc.Start(); } catch (Exception e) { Logger.LogWarning("instance start: " + e.Message); }
                    if (_currentInstance == null) _currentInstance = npc;
                }
            }
        }

        // ------------------------------------------------------------------
        // restart / hot-reload
        // ------------------------------------------------------------------
        private IEnumerator RestartAfterSecond()
        {
            if (!_running) yield break;
            for (int i = 0; i < 5; i++) yield return new WaitForSecondsRealtime(0.4f);
            if (!_running) yield break;
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
                lock (_instancesLock)
                {
                    foreach (var npc in _instances)
                        try { npc.Stop(); } catch (Exception) { }
                    _instances.Clear();
                }
                try { Config.Reload(); } catch (Exception) { }
            }
            catch (Exception e) { Logger.LogError("KKLLMNPC: teardown during restart: " + e); }

            _running = true;
            StartWatchers();
            // Instances are re-created by ReconcileInstances as targets exist — no
            // pre-creation (same rule as Awake).
            _lastReconcile = 0f;
            Logger.LogInfo("KKLLMNPC: restarted on new DLL — instances will start as targets appear.");
        }

        // ------------------------------------------------------------------
        // main-thread marshalling
        // ------------------------------------------------------------------
        internal object RunOnMainThread(Func<object> fn, int timeoutMs = 120000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
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
            try
            {
                _mainContext.Post(_ => { try { result = fn(); } catch (Exception e) { error = e; } finally { done.Set(); } }, null);
            }
            catch (Exception e) { throw new InvalidOperationException("failed to post to main thread: " + e.Message, e); }
            if (!done.Wait(timeoutMs)) throw new TimeoutException("main thread timeout (" + timeoutMs + "ms)");
            if (error != null) throw error;
            return result;
        }

        internal void RunOnMainThreadAsync(Action fn)
        {
            if (fn == null) return;
            if (!_mainReady || _mainContext == null)
            {
                if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) { SafeRun(fn); return; }
                return;
            }
            try { _mainContext.Post(_ => SafeRun(fn), null); }
            catch (Exception e) { Logger.LogWarning("RunOnMainThreadAsync: " + e.Message); }
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
            lock (_instancesLock)
            {
                foreach (var npc in _instances)
                    try { npc.ClearLogs(); } catch (Exception) { }
            }
        }

        private void MarkScene(string key)
        {
            if (key == _lastSceneName) return;
            _lastSceneName = key;
            Logger.LogInfo("KKLLMNPC: idle (" + key + ").");
        }
    }
}
