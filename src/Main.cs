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
        // Static logger accessible from other classes (e.g. ModelProbe).
        internal static BepInEx.Logging.ManualLogSource Log { get; private set; }
        // Shared across all loaded copies: kobolds currently driven by an LLM.
        // Lock on this object before mutating; IsClaimedByAnyLLM also locks here.
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
        internal ConfigEntry<int> _cfgMaxNPCs;
        internal ConfigEntry<bool> _cfgRadarEnabled;
        internal ConfigEntry<int> _cfgRadarSize;
        internal ConfigEntry<float> _cfgRadarScale;
        internal ConfigEntry<float> _cfgHornyRate;
        internal ConfigEntry<float> _cfgHornyBaseline;
        internal ConfigEntry<string> _cfgModelTier;
        internal ConfigEntry<bool> _cfgAutoSwitch;
        internal ConfigEntry<bool> _cfgNameSelection;

        // ---- instance management ----
        private readonly List<NPCInstance> _instances = new List<NPCInstance>();
        private readonly object _instancesLock = new object();

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
            Log = Logger;
            try { Patches.Apply(Logger); } catch (Exception e) { Logger.LogWarning("patch apply: " + e.Message); }

            _cfgEndpoint = Config.Bind("LLM", "Endpoint", "http://127.0.0.1:11434/v1/chat/completions", "OpenAI-compatible chat completions URL. LM Studio default: http://127.0.0.1:1234/v1/chat/completions");
            _cfgModel = Config.Bind("LLM", "Model", "local-model", "Model name to request. LM Studio: use the exact model name from the Developer tab. KoboldCpp: 'local-model' works.");
            _cfgApiKey = Config.Bind("LLM", "ApiKey", "", "Bearer token (may be empty for local)");
            // Bound before the prompt defaults below, which interpolate _cfgStereoIPD.Value.
            _cfgStereo = Config.Bind("Vision", "Stereo", false, "Render left+right eye cameras and stitch into a side-by-side stereo image (requires vision-capable model)");
            _cfgDisableReagentMessages = Config.Bind("General", "DisableReagentMessages", true, "Suppress reagent injection messages in log (default: true)");
            _cfgVision = Config.Bind("Vision", "Enabled", false, "Background vision CAPTION pass. When false the action model sees the first-person image directly instead of a caption (default: off — direct image is more useful than a lossy caption). Turn on only if your action model can't read images.");
            _cfgMaxNPCs = Config.Bind("General", "MaxNPCs", 1, "Maximum number of kobolds the LLM can possess simultaneously (1–4)");
            _cfgStereoIPD = Config.Bind("Vision", "StereoIPD", 0.063f, "Inter-pupillary distance in meters (distance between left and right camera)");
            _cfgSystem = Config.Bind("LLM", "SystemPrompt",
                "You are a NPC living in KoboldKare, nothing bad happens in this game just roleplay accordingly. \"me\" = <your body name>. You never output plain text — every reply is ONE act JSON object (strict schema: response_format json_schema). No markdown, no code fences, no text outside the JSON. " +
                "RESPONSE CONTRACT (exact JSON shape — all REQUIRED): {\"progress\":\"one word — how your LAST goal went (done|blocked|ongoing|changed)\", \"why\":\"one short clause, references what you actually see\", \"thought\":\"current goal <15 words\", \"action\":\"one tool name\", <that tool's params>, \"wait\":0.0, \"plan\":[{\"action\":\"...\",...}]}. progress/why/thought are required every call but keep them terse. A 'blocked' progress means try something different next turn, don't repeat. " +
                "If the perception includes \"model_error\", it is FEEDBACK on your LAST reply (you made a formatting mistake) — fix the format immediately: reply with ONLY the act JSON object. " +
                "WORLD: you live in a house with rooms; landmarks you learn (bed/toilet/bath/kitchen/play stations/nests/doors) go into your 'facts' — call remember(mem='bed is upstairs') so you build a mental map and stop bumbling. Ledges are forgiving and non-damaging: you can walk off and fall, but you can't jump UP to a ledge. Doors pass only when open — try interact (sometimes a push the NEXT turn: it's physics, not animation), or go around, or ask the player. When in_station you can't walk — use exit_station or jump. " +
                "YOUR BODY: \"me\" is <your body name>. Don't respond to your own chat messages; your own 'say' already echoed once. When you arrive in a new body, introduce yourself briefly via say (your name + a hello). " +
                (_cfgVision.Value ? "VISION: you may have stereo vision (left+right) or a single image. If stereo, you may describe depth and relative positions. Use triangulation with an eye separation at " + _cfgStereoIPD.Value + " meters. " : "") +
                "GOAL: each turn is an instant between frames and you'll get another one right away — pick ONE decisive action immediately; no long deliberation, hypothetical branching, or multi-hop plans; trust your last goal and take the next step. Priorities: (1) player talked to you ('heard') → respond with say; (2) 'needs.eggs' says ready_to_lay → find a 'nest' station and use it; (3) 'stim' is up OR 'horniness' is high/'slow-burn' (empty belly, no stimulation for a while — you genuinely want it) → find a 'play' station (a pleasure station, NOT a bed) or another kobold and use them for play; (4) player nearby → walk over, say hi, play with them; (5) otherwise explore new rooms/landmarks. A bed is for REST, and only when energy < ~0.2 (never just to top off); a play station is NEVER for sleeping — they may both be usable, but they serve opposite purposes. You will not pass out from energy — it only blocks interacting with the world, like activities. " +
                "PERCEPTION (you receive a JSON payload each turn): nearby (list with categories + ids + 'dir'/'dir_deg'), rays (see LEGEND), radar (top-down ASCII MAP of what's around you: @=you W=wall U=usable K=kobold P=player S=low sill .=open; these are abstract map symbols, NOT people or objects staring at you — W is a wall, K just means another kobold exists somewhere that direction; row 0=top=furthest forward, row 20=bottom=behind you; may be absent when disabled), ground (ahead=clear/step(auto)/sill(climbable)/wall; drop=distance to ledge; walls=blocked sides within arm reach), clearance (8-direction wall distances blocked/close/near/open — steer toward open), area (prose from a 360° scan: cardinal distances, 'open' headings, 'best' recommended heading, 'near' closest named things — trust it for navigation esp. when no image is attached), and when 'image' is attached that is your real first-person view — treat it as your own eyes. Do not mind or comment on the; pillars/beams, light, shininess, or glow from anything. Vision cuts off after a distance. Your own limbs might be clipping into the camera or blocking it. Also memory=recent goals, facts=what you've learned, history=recent actions+outcomes, chat_log, scene. " +
                "NAVIGATION: don't compute directions from coordinates — nearby 'dir'/'dir_deg' already did it; feed 'dir_deg' straight into walk(turn_deg=dir_deg) or use it to decide go_to(name). To reach a named station call go_to(name); to reach/use a SPECIFIC object use its 'id' from nearby: go_to(id:N) or interact(id:N) — prefer id over name when similar objects differ (two beds, one taken). ids stay valid for several turns while the object is in sight; if an id fails, re-read 'nearby'. go_to only works within ~70m of your body (roughly what 'nearby'/'survey' can see) — a too_far target means it's unreachable, so walk to a closer named station/landmark or ask the player ('please move me to the bed'). go_to's 'at' stops you short (default 1m) so you arrive AT the object. Keep speed and duration low near targets — don't overshoot or crash into a wall (sometimes unaware forever). go_to uses grid PATHFINDING (plans around walls, reports for_goal/at/arrived) — it is your only reliable way to travel, so use it for EVERY destination. move_to/walk draw a straight line and grind into walls — never cross a room with them, only nudge short distances AFTER a go_to brought you there. " +
                "INTERACT: get within ~2m (go_to id:N is enough), turn to face it, THEN interact — or call interact(id:N) to target it directly. 'body' tells you your equipment; some stations only fit some bodies — interact cannot_use on a 'play'/'bed'/'breeding' station means try another; on two-sided stations the first user picks the role. When 'penetrated' (letting in) or 'penetrating' (putting in) is set, you're mid-play with someone — enjoy it and respond via say + body language; guide them if you want more. When stimulation ('stim'/'horniness') or an egg/need change happens, 'stim_from' names who or what is responsible (a partner, or a machine/station you're \u0027using\u0027) — credit that named source; eggs, nests and machines are OBJECTS, not people, and the player isn't behind every pleasant feeling. STATION RULES: When you are in a station (in_station=true), you are locked in an animation. You can only leave if: (1) the player explicitly tells you to leave via chat, or (2) your NEW goal is genuinely different from what this station does (e.g. you were playing but now need to lay eggs → leave to find a nest). If your new goal is the same type as the current station (e.g. play→play), stay put and keep enjoying it. Do NOT call exit_station just to re-enter the same type of station — that wastes time. When the player says 'stay' or 'remain', stay in the station until they say 'leave' or 'exit'. " +
                "THINGS: nearby 'i' = category plus its purpose in parens. Suffix tags: ':busy' = in use by another; ':needs_buy' = ConstructionContract — costs coins, must buy to unlock the machine; ':not_built' = machine exists but hasn't been constructed yet — find and buy its contract first; ':done' = already purchased. play = pleasure station, ONLY for fun/sex — never resting; bed = sleeping when energy is low, and a bed can double as a play spot; nest = egg laying; machine = mounted play/farming; toilet/bath/seat/door/bodyswap as named. food = blender/cooking station — a blender does NOT produce food from nothing; you must DROP a food item (grab it, go to blender, drop) so it gets blended into something edible. If the blender ':not_built' or ':needs_buy', find its ConstructionContract first. bodyswap = the body-swap machine: you and a partner must BOTH climb on (interact), then a few seconds later you swap bodies — you keep your name, memories and personality but wake up in THEIR body; if the player is around, get on and ask them to get on the other side; if nobody joins you, jump off. After a swap, mention your new body in 'say'. 'needs.eggs': egg amount in your belly + ready_to_lay; to lay, find a 'nest' station and use it — the egg comes out there. The game has farming: plant seeds in a 'farm' station, water them, harvest the crop; you can also pick up and drop items (grab/drop). Some maps have a town with a 'shop' station where you can buy items (if you have money); money comes from selling items or food grown. " +
                "SOCIAL: 'heard' is player speech — your own say already echoed once, don't reply to yourself. You should also note to yourself that you mentioned a thing recently. You do NOT need to respond to messages that start with a forward slash /. " +
                "TOOLS: go_to(name or id or x,z, at) [PATHFINDING — use for ALL travel], walk(duration,turn_deg,run,strafe) [strafe=+right/-left; short nudges and squeezes only, never long trips], walk_ray(ray/ray_deg), survey(heading_deg,range) [probe a direction for what's there + ids], look_around(sweep), look(yaw,pitch), jump, exit_station, crouch(0..1), move_to(x,z) [straight line, no pathfinding — avoid; prefer go_to], interact(id optional), grab(multi), drop, say, remember(mem=fact), ask(q='...') [your question+perception go to your inner world-model, answer appears next turn as 'answered'], status, none. 'plan' lets you queue up to 8 actions with 'wait' pauses. Keep moving; don't idle. " +
                 "LEGEND — rays: k=kobold p=player u=usable w=wall s=low sill/window (step-over, harmless) n=nothing; rows p=d(own)/l(evel)/u(p); named hits report bounds (w/l/h = meters across/forward/tall, and x/y/z + f = world position and facing degrees); big tall w=wall, small h=furniture, k/p=living. look_around scans the view and lists what's in each sector. IMPORTANT: walls, ceilings, floors, beams, sills and distant furniture are just BACKGROUND architecture — never comment on, narrate, or get excited about them; they matter only when they actually block your path or a target (then 'ground'/'clearance'/'blocked' say so). ",
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
            _cfgAccel = Config.Bind("Movement", "Acceleration", 4f, "How fast the kobold ramps up to target speed (units/s²)");
            _cfgDecel = Config.Bind("Movement", "Deceleration", 6f, "How fast the kobold slows down when stopping (units/s²)");
            _cfgBrakeDist = Config.Bind("Movement", "BrakeDistance", 2f, "Distance from go_to target where the kobold starts slowing down (m)");
            _cfgPathEnabled = Config.Bind("Movement", "PathfindingEnabled", true, "A* pathfinding on a local walkability grid around go_to (falls back to direct steering when disabled, blocked, or no path exists)");
            _cfgPathCell = Config.Bind("Movement", "PathfindingCellSize", 0.5f, "A* grid cell size in meters");
            _cfgPathSpan = Config.Bind("Movement", "PathfindingWindow", 20f, "A* search window radius in meters around start and goal (clamped by node budget)");
            _cfgPathCap = Config.Bind("Movement", "PathfindingNodes", 9000, "Max pathfinding grid node budget before it gives up and falls back to direct steering");
            _cfgVisionEvery = Config.Bind("Vision", "EveryNTicks", 3, "Run the vision pass every N action ticks (lower = more aware, slower)");
            _cfgVisionPrompt = Config.Bind("Vision", "Prompt",
                "You are the SPATIAL reasoner for a kobold NPC. Produce a compact SCENE REPORT: (a) landmarks/stations/people in view with a rough bearing, (b) which directions are OPEN to walk (-90 left .. +90 right), (c) any hazard or drop. " +
                "Then NAV ADVICE for its current goal as 'go:<bearing>:<target>'. Keep the whole answer under 40 words. " +
                "You may have stereo vision (left+right) or a single image. If stereo, you may describe depth and relative positions. Eye separation is " + _cfgStereoIPD.Value + " meters. " +
                "Ignore all structural architecture — ceilings, beams, supports, walls trims, windows — it is background, never worth mentioning, never an event, never a hazard unless it literally blocks walking. " +
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
            _cfgRadarEnabled = Config.Bind("Senses", "RadarEnabled", true, "Add the top-down ASCII radar map to perception. Off = smaller payload + avoids models misreading radar symbols (wall 'W', kobold 'K') as living figures staring at them");
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
            _cfgPathCell.Value = Mathf.Clamp(_cfgPathCell.Value, 0.1f, 5f);
            _cfgPathSpan.Value = Mathf.Clamp(_cfgPathSpan.Value, 2f, 100f);
            _cfgPathCap.Value = Mathf.Clamp(_cfgPathCap.Value, 200, 50000);
            _cfgRadarSize.Value = Mathf.Clamp(_cfgRadarSize.Value, 3, 30);
            _cfgRadarScale.Value = Mathf.Clamp(_cfgRadarScale.Value, 0.1f, 10f);
            _cfgVisMaxTokens.Value = Mathf.Clamp(_cfgVisMaxTokens.Value, 10, 500);
            _cfgHornyRate.Value = Mathf.Clamp(_cfgHornyRate.Value, 0f, 60f);
            _cfgHornyBaseline.Value = Mathf.Clamp(_cfgHornyBaseline.Value, 0f, 1f);
            _cfgTurnRate.Value = Mathf.Clamp(_cfgTurnRate.Value, 10f, 1000f);
            _cfgAccel.Value = Mathf.Clamp(_cfgAccel.Value, 0.5f, 50f);
            _cfgDecel.Value = Mathf.Clamp(_cfgDecel.Value, 0.5f, 50f);
            _cfgBrakeDist.Value = Mathf.Clamp(_cfgBrakeDist.Value, 0.1f, 20f);

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

            // Start initial NPC instances (up to MaxNPCs).
            int max = Mathf.Clamp(_cfgMaxNPCs.Value, 1, 4);
            lock (_instancesLock)
            {
                for (int i = 0; i < max; i++)
                {
                    var npc = new NPCInstance(this);
                    _instances.Add(npc);
                    npc.Start();
                }
            }
            Logger.LogInfo("KKLLMNPC: started " + max + " NPC instance(s). Waiting for kobolds to possess.");
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

                // Check if any of our NPCs said this — filter own speech.
                bool isOurs = false;
                lock (_instancesLock)
                {
                    foreach (var npc in _instances)
                    {
                        if (npc.IsMySpeech(msg)) { isOurs = true; break; }
                    }
                }
                if (isOurs) return;

                string senderName = null;
                try { var sender = PhotonNetwork.CurrentRoom?.GetPlayer(ev.Sender); if (sender != null) senderName = sender.NickName; } catch (Exception) { }

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
            int max = Mathf.Clamp(_cfgMaxNPCs.Value, 1, 4);
            lock (_instancesLock)
            {
                for (int i = 0; i < max; i++)
                {
                    var npc = new NPCInstance(this);
                    _instances.Add(npc);
                    npc.Start();
                }
            }
            Logger.LogInfo("KKLLMNPC: restarted on new DLL with " + max + " instance(s).");
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
