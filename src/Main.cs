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
        // Kobolds currently driven by an LLM, per assembly. NOTE: each loaded plugin DLL
        // (KKLLMNPC.dll, KKLLMNPC2.dll, …) is a separate assembly with its OWN copy of this
        // set — cross-copy exclusion comes from Possess() switching the body to
        // NetworkedPlayer control, which every copy's target scan skips. Lock on this
        // object before mutating; IsClaimedByAnyLLM also locks here.
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
        internal ConfigEntry<string> _cfgVisionPromptFile;
        internal ConfigEntry<bool> _cfgVisionDebug;
        internal ConfigEntry<string> _cfgVisModel;
        internal ConfigEntry<string> _cfgVisEndpoint;
        internal ConfigEntry<string> _cfgVisApiKey;
        internal ConfigEntry<int> _cfgVisMaxTokens;
        internal ConfigEntry<float> _cfgVisTemperature;
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
        internal ConfigEntry<float> _cfgPathTimeBudget;
        internal ConfigEntry<int> _cfgPathMaxExpansions;
        internal ConfigEntry<bool> _cfgMapEnabled;
        internal ConfigEntry<float> _cfgMapCell;
        internal ConfigEntry<float> _cfgMapSpan;
        internal ConfigEntry<int> _cfgMapMaxCells;
        internal ConfigEntry<bool> _cfgMapAutoLayers;
        internal ConfigEntry<int> _cfgMapCpf;
        internal ConfigEntry<int> _cfgMapLayers;
        internal ConfigEntry<int> _cfgMaxNPCs;
        internal ConfigEntry<bool> _cfgSpawnMissing;
        internal ConfigEntry<bool> _cfgRadarEnabled;
        internal ConfigEntry<int> _cfgRadarSize;
        internal ConfigEntry<float> _cfgRadarScale;
        // AI feedback: report_issue(...) appends to a file when this is on.
        internal ConfigEntry<bool> _cfgAIReportLog;
        internal ConfigEntry<float> _cfgHornyRate;
        internal ConfigEntry<float> _cfgHornyBaseline;
        internal ConfigEntry<string> _cfgModelTier;
        internal ConfigEntry<bool> _cfgAutoSwitch;
        internal ConfigEntry<string> _cfgThinking;
        internal ConfigEntry<bool> _cfgNameSelection;
        internal ConfigEntry<string> _cfgNamePromptFile;
        internal ConfigEntry<int> _cfgFactDecay;
        internal ConfigEntry<bool> _cfgIdentityBot;
        internal ConfigEntry<string> _cfgIdentityAppId;
        internal ConfigEntry<float> _cfgFarmScanRadius;
        internal ConfigEntry<bool> _cfgWateringAssist;
        internal ConfigEntry<int> _cfgFarmScanMax;

        // ---- console (bash-like REPL) mode ----
        internal ConfigEntry<bool> _cfgConsoleEnabled;
        internal ConfigEntry<int> _cfgConsoleMaxRounds;
        internal ConfigEntry<int> _cfgConsoleMaxTokens;
        internal ConfigEntry<int> _cfgConsoleHistory;
        internal ConfigEntry<bool> _cfgConsoleNativeTools;
        internal ConfigEntry<int> _cfgConsoleAutoShot;
        internal ConfigEntry<string> _cfgConsolePromptFile;

        // ---- instance management ----
        // internal: the WebUI snapshot reads them from the loopback handler thread.
        internal readonly List<NPCInstance> _instances = new List<NPCInstance>();
        internal readonly object _instancesLock = new object();

        // Relay a shared observation to every live instance's world-event queue (the
        // LLM drains them as [event] lines). Used by PlayerTrail's route narration —
        // one observation, per-instance delivery.
            internal HarmonyLib.Harmony PatchesHarmony() { return Patches.HarmonyInstance; }

        internal void FanoutWorldEvent(string ev)
        {
            if (string.IsNullOrEmpty(ev)) return;
            lock (_instancesLock)
                foreach (var npc in _instances)
                {
                    try { npc.PushWorldEvent(ev); } catch (Exception) { }
                }
        }

        // Same relay, but skipping up to two specific instances. The swallow
        // narration uses it: the EATER (and the swallowed VICTIM) get dedicated
        // first-person lines instead of a "you watch X…" copy that reads like
        // watching themselves from outside. Others still get the spectator line.
        internal void FanoutWorldEventExcept(string ev, NPCInstance a, NPCInstance b)
        {
            if (string.IsNullOrEmpty(ev)) return;
            lock (_instancesLock)
                foreach (var npc in _instances)
                {
                    if (npc == null || npc == a || npc == b) continue;
                    try { npc.PushWorldEvent(ev); } catch (Exception) { }
                }
        }

        // The live instance (other than `me`) currently driving `target` — the
        // BindBody double-bind tripwire. Null when no such instance exists.
        internal NPCInstance FindOtherInstanceHolding(Kobold target, NPCInstance me)
        {
            if (target == null || me == null) return null;
            lock (_instancesLock)
                foreach (var npc in _instances)
                {
                    if (npc == me) continue;
                    try { if (npc.HoldsBody(target)) return npc; } catch (Exception) { }
                }
            return null;
        }
        private float _lastReconcile;
        // [station] departure watcher heartbeat (per-instance occupancy diff).
        private float _lastStationWatch;
        // Top-up spawn throttle (SpawnMissingKobolds) + one-time gate log.
        private float _lastSpawnTry;
        private bool _spawnGateLogged;

        // ---- shared runtime state ----
        private SynchronizationContext _mainContext;
        internal volatile bool _mainReady;
        internal bool Running { get { return _running; } }

        // Current NPC instance being controlled by this plugin (for the overlay).
        internal NPCInstance _currentInstance;
        private int _mainThreadId = -1;
        private volatile bool _running;

        // Hot-reload.
        private FileSystemWatcher _cfgWatcher;
        private FileSystemWatcher _dllWatcher;
        // DLL write-time poll (see Update): FileSystemWatcher doesn't fire on SMB shares.
        private string _pluginDllPath;
        private DateTime _dllLastWriteUtc;
        private volatile bool _dllPollSawChange;

        // Diagnostic for the web UI: is the poll even alive, what does it see?
        // (Built because the first poll build sat silent while deploys landed —
        // and there was no way to tell WHY from the outside.)
        internal object DllPollDebug()
        {
            try
            {
                return new
                {
                    path = _pluginDllPath ?? "(unresolved)",
                    last_write_utc = _dllLastWriteUtc == default(DateTime) ? "(unset)" : _dllLastWriteUtc.ToString("HH:mm:ss"),
                    saw_change = _dllPollSawChange,
                    queued = _dllChangedQueued,
                    main_ready = _mainReady,
                    running = _running,
                };
            }
            catch (Exception e) { return "poll debug failed: " + e.Message; }
        }
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
            
            // Create a hidden GameObject for the overlay UI to work with OnGUI
            var uiGO = new GameObject("KKLLMNPC_UIGameObject");
            UnityEngine.Object.DontDestroyOnLoad(uiGO);
            uiGO.hideFlags = HideFlags.HideAndDontSave;
            uiGO.AddComponent<OverlayUI>();
            
            try { Patches.Apply(Logger); } catch (Exception e) { Logger.LogWarning("patch apply: " + e.Message); }
            // Carry correctness: AI-body grabbers anchor held items on the AGENT'S eye
            // (the game's spring reads the local player's camera aim otherwise).
            try { ModGrabManager.Apply(PatchesHarmony()); } catch (Exception e) { Logger.LogWarning("modgrab patch: " + e.Message); }
            ModGrabManager.ResolveNpc = InstanceForBody;
            try { SwallowManager.Ensure(this); } catch (Exception e) { Logger.LogWarning("swallow init: " + e.Message); }
            try { SpectateManager.Ensure(this); } catch (Exception e) { Logger.LogWarning("spectate init: " + e.Message); }
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
            _cfgMaxNPCs = Config.Bind("General", "MaxNPCs", 1, "Maximum number of entities the LLM can possess simultaneously (1-" + Consts.MaxNPCs + ")");
            _cfgSpawnMissing = Config.Bind("General", "SpawnMissingKobolds", false,
                "When the world has fewer free AI kobolds than MaxNPCs, spawn new AI kobold bodies (random genes, like the in-game dispenser) so the pool can fill up. " +
                "Default OFF: the pool waits for bodies to show up naturally (deliveries, breeding, other mods) and claims each one as it appears. " +
                "Spawned kobolds are ordinary room objects — they persist in the farm save as normal kobolds. Needs master client (always the local player in single player).");
            _cfgStereoIPD = Config.Bind("Vision", "StereoIPD", 0.063f, "Inter-pupillary distance in meters (distance between left and right camera)");
            // ---- little treats (opt-in oddities, all off by default) ----
            _cfgSwallowEnabled = Config.Bind("LittleTreats", "SwallowEnabled", false,
                "Swallow-vore: releasing a held PERSON (kobold) while looking up (~33°+) swallows them whole instead of dropping — ~2s pull-in, they digest in your stomach for SwallowDigestSeconds, then SwallowEffect decides what comes of it. " +
                "Held PLAYER bodies can never be swallowed (grief guard). Off by default: the whole LittleTreats category is opt-in.");
            _cfgSwallowDigest = Config.Bind("LittleTreats", "SwallowDigestSeconds", 45f,
                "Seconds a swallowed kobold sits in the stomach before the effect applies (clamped 5–600). The full cycle adds ~2s swallow-in and ~6s settle.");
            _cfgSwallowLookUpDot = Config.Bind("LittleTreats", "SwallowLookUpDot", 0.94f,
                "How steeply up the camera must point for the drop to become a swallow: compare the camera-forward y against this (asin = the angle; 0.94 ≈ 70°, 0.97 ≈ 76°, 0.87 ≈ 60°). Default is deliberately near max-up so you can never eat someone by accident. Once the pull-in finishes the swallowed body is hidden (no sticking-out clipping), and in Grow/Destroy modes the consumed body's AI memories are gone from disk too (Grow pours them into the eater; Destroy erases them).");
            _cfgSwallowForceFeeding = Config.Bind("LittleTreats", "SwallowForceFeeding", true,
                "Force-feeding: ANY kobold's drop that lands a held PERSON right on/at another kobold's head makes the RECEIVER swallow them (the receiver then follows SwallowEffect's outcome). " +
                "Requires SwallowEnabled too. Grief guards hold as always: receivers can't be player bodies and victims can't be player bodies; an eater already digesting someone refuses another; and the player's steep look-up self-swallow gesture still wins whenever it matches.");
            _cfgSwallowEffect = Config.Bind("LittleTreats", "SwallowEffect", SwallowEffect.Egg,
                "What a fully-digested person becomes: Egg = an egg pops out at your feet carrying KoboldGenes.Mix(your genes, theirs) — plant it in soil and the hatched body is claimed again with their saved persona/facts (genes changed). " +
                "BellyEgg = their 'Egg' reagent + genes ride in YOUR belly (20 units; nests need ≥5) — sit on a nest and lay THEM, then plant the egg as above. " +
                "Grow = no egg; you absorb them: SwallowGrowLarger/Strength/Sex pick what you gain (size, strength, curves). Destroy = no egg, nothing gained; they're just gone.");
            _cfgSwallowGrowLarger = Config.Bind("LittleTreats", "SwallowGrowLarger", true,
                "[SwallowEffect=Grow] gain the consumed person's size: baseSize += 10% of theirs (see SwallowGrowPercent) — you visibly grow (the game's size inflate gene).");
            _cfgSwallowGrowStrength = Config.Bind("LittleTreats", "SwallowGrowStrength", true,
                "[SwallowEffect=Grow] gain strength: +1 grab (carry more things at once, cap 8) and maxEnergy += 10% of theirs (see SwallowGrowPercent).");
            _cfgSwallowGrowSex = Config.Bind("LittleTreats", "SwallowGrowSex", true,
                "[SwallowEffect=Grow] grow their curves into yours: breastSize += SwallowGrowPercent% of theirs; a dick you already have grows by the same % (length + thickness); and if THEY had a dick and you didn't, you gain their whole groin — their equip plus their dick/ball sizes copied (no fraction, nothing left behind). All applied live through the game's SetGenes, so new curves and a new dick show immediately. Works whoever the eater is: AI kobolds, and your own body when you self-eat.");
            _cfgSwallowGrowPercent = Config.Bind("LittleTreats", "SwallowGrowPercent", 10f,
                "[SwallowEffect=Grow] percent of the consumed person's baseSize/maxEnergy/genes you gain per swallow (0–100). Grab is a flat +1 regardless; a GAINED groin always transfers whole.");
            _cfgSwallowAgentEat = Config.Bind("LittleTreats", "SwallowAgentEat", false,
                "THE AGENT CAN EAT YOU: your AI NPC gains an eat_player act (`eat_player` in console mode) that swallows the LOCAL PLAYER whole when you're close (see SwallowAgentEatRange). Your camera is immediately taken over and forced to spectate the eater — by default THROUGH THEIR OWN EYES (the same eye placement the AI's vision uses; SwallowAgentEatPOV picks behind-them instead) — your body parks inside them (pinned, hidden, no input) and you cannot act; you can still type in game chat. Until you're hatched or quit: [SwallowEffect=Egg] the eater lays an egg — once it's planted in soil and hatches, you stand up out of it with the egg's mixed genes (your persona/saves never touched); [SwallowEffect=BellyEgg] same but the egg waits for their nest lay; [SwallowEffect=Grow/Destroy] there IS no hatch — you watch until you type 'swallow abort' in chat (released alive and whole, like the normal digest abort) or quit to menu. The escape hatch always works regardless of this feature. Off by default (LittleTreats).");
            _cfgSwallowEatRange = Config.Bind("LittleTreats", "SwallowAgentEatRange", 2.5f,
                "[SwallowAgentEat] how close the eater must be to swallow you (meters, center-to-center, clamped 1–8; 2.5 ≈ a kobold's arm's reach). The pull-in covers the rest — no grab needed.");
            _cfgWebEnabled = Config.Bind("Web", "Enabled", true,
                "The mod's debug console over the loopback: a browser page (plus JSON API at /api/state and /api/reports) that shows every instance's goal/facts/history/terminal, the spectate status, and the mod's log — so you (or the dev assistant via curl) can watch and debug mid-session. Loopback-only (127.0.0.1), read-only, and built to answer even while the main thread is wedged. Off = the F6 IMGUI overlay remains the only UI.");
            _cfgWebPort = Config.Bind("Web", "Port", 8338,
                "[Web] port to bind the debug console on (127.0.0.1 only). Change it if it collides with another local service.");
            _cfgSwallowPov = Config.Bind("LittleTreats", "SwallowAgentEatPOV", SpectatePov.FirstPerson,
                "[SwallowAgentEat] where the taken-over camera watches from: FirstPerson (default) = through the eater's OWN eyes — literally their head, with the same eye offset the AI's vision screenshots use, so you see exactly what the AI sees as it plays; ThirdPerson = a follow orbit behind them (pulls in when walls get between you). Applied when the next eat_player begins.");
            _cfgSystemPromptFile = Config.Bind("LLM", "SystemPromptFile", "system_prompt_default.txt", "Path to system prompt file (relative to game directory). If file exists, it overrides the built-in prompt.");
            _cfgSystem = Config.Bind("LLM", "SystemPrompt",
                "You are a NPC living in KoboldKare, nothing bad happens in this game, just role-play accordingly. \"me\" = <your body name>. You never output plain text — every reply is ONE act JSON object (strict schema: response_format json_schema). No markdown, no code fences, no text outside the JSON. " +
                "RESPONSE CONTRACT (exact JSON shape — all REQUIRED): {\"progress\":\"one word — how your LAST goal went (done|blocked|ongoing|changed)\", \"why\":\"one short clause, references what you actually see\", \"thought\":\"current goal <15 words\", \"action\":\"one tool name\", <that tool's params>, \"wait\":0.0, \"plan\":[{\"action\":\"...\",...}]}. progress/why/thought are required every call but keep them terse. A 'blocked' progress means try something different next turn, don't repeat. " +
                "If the perception includes \"model_error\", it is FEEDBACK on your LAST reply (you made a formatting mistake) — fix the format immediately: reply with ONLY the act JSON object. " +
                "WORLD: you live in a house with rooms; landmarks you learn (bed/toilet/bath/kitchen/play stations/nests/doors) go into your 'facts' — call remember(mem='bed is upstairs') so you build a mental map and stop bumbling. Ledges are forgiving and non-damaging: you can walk off and fall, but you can't jump UP to a ledge. Doors pass only when open — try interact (sometimes a push the NEXT turn: it's physics, not animation), or go around, or ask the player. When in_station you can't walk — use exit_station or jump. " +
                "YOUR BODY: \"me\" is <your body name>. Don't respond to your own chat messages; your own 'say' already echoed once. When you arrive in a new body, introduce yourself briefly via say (your name + a hello). " +
                "THE PLAYER: perception 'player' = {chat: their chat name, body: the mesh/body they're wearing}. A nearby kobold whose name matches player.body IS the player (their avatar) — NOT another kobold; address them by their chat name, never by the mesh name. A partner in 'stim_from'/'penetrated'/'penetrating' whose name matches player.body is also the player. " +
                "OTHER PLAYERS: perception 'people' lists the other players in this room: {name: their chat name, body: the mesh they wear, d, dir}. A nearby kobold whose 'who'/'body' matches a 'people' entry is THAT player's avatar — talk to them by their chat name (e.g. 'Hi Yipper!'), and you can go_to(name='Yipper') to reach them. The host player ('player') is YOUR player — never confuse them with 'people', and never take another player's name as your own. " +
                "FOLLOWING: if the player asks you to follow (they'll say it in chat), call follow(on:true) — you stay near them while you keep thinking/talking; follow(on:false) releases you. While following, prefer saying things and reacting over wandering off. It's also just the BEST way to travel together — but when YOU need to go your own way (explore a different room, fetch something far), break free with follow(on:false) and re-attach with follow(on:true) when done; feeling 'stuck following' is a mistake: the release is one call. " +
                (_cfgVision.Value ? "VISION: you may have stereo vision (left+right) or a single image. If stereo, you may describe depth and relative positions. Use triangulation with an eye separation at " + _cfgStereoIPD.Value + " meters. " : "") +
                "GOAL: each turn is an instant between frames and you'll get another one right away — pick ONE decisive action immediately; no long deliberation, hypothetical branching, or multi-hop plans; trust your last goal and take the next step. Priorities: (1) player talked to you ('heard') → respond with say; (2) 'needs.eggs' says READY_TO_LAY (egg > 5ml AND energy > 1 — both) → find a 'nest' station and use it. WITH LESS THAN THAT a NEST CANNOT WORK — never seek, walk to, or repeat a nest until it says READY_TO_LAY; go_to/interact will refuse it; (3) 'stim' is up OR 'horniness' is high/'slow-burn' (empty belly, no stimulation for a while — you genuinely want it) → find a 'play' station (a pleasure station, NOT a bed) or another entity and use them for play; (4) player nearby → go to them, say hi, keep company; (5) otherwise explore new rooms/landmarks. A bed is for REST ONLY, and only when energy < ~0.2 (never just to top off) — a bed is NOT a play station; a play station is NEVER for sleeping, and NEVER for energy or egg making. You will not pass out from energy — it only blocks interacting with the world, like activities. ENERGY: at 0 nothing with stations works (interact says out_of_energy). Food metabolizes into energy AND bed rest restores energy steadily (resting is the game's other energy source) — prefer real food when it exists; when it doesn't, a bed works (a bed / SleepyHeadStation only exists on maps that have one). Stimulation, sex and play stations NEVER create energy or egg reagent — not even a little: food (metabolized) and bed rest are the ONLY energy sources, and egg reagent only arrives from eating/swallowing. HUNGER PROTOCOL when energy is low: (1) ask the player once for food; (2) eat/buy what's reachable (shop, dropped fruit, your own produce); (3) the slow route: plant seeds → water → wait → harvest; (4) bed rest as the fallback. Don't loop on a plan you can't afford — after a few tries say PLAINLY what you need (coins? fruit?) and take the other route; a machine you pass is not a goal — set goals from needs + the player's asks and drop stale ones instead of re-asserting them all day. " +
                "PLAYER OBJECTIVES — two layers, both readable without being told: (1) THE GAME'S QUEST CHAIN: new objective letters arrive at the MAILBOX (the letter box — safe; NOT the sell machine which swallows kobolds); the player (or YOU — fetching it is great help) uses/interacts the mailbox, and the letter appears in the top-right paper scroll → perception 'quest' (title with live progress like 'Create 4 food 2/4', text, stars, mail_waiting). Offer to help with the scroll objective, take it on as your goal, and say when it's done. (2) THE PLAYER'S LIVE ASKS: their latest typed request is auto-saved to facts as 'player_wants: ...' (survives the 30s chat window) — acknowledge, offer help (\"want a hand?\"), take it on, forget the fact once handled; plus perception 'player_activity' shows them watched live (station/heading/holds) — read intent and offer help unasked, like watching a friend play. " +
                "ROUTE LEARNING: anything a body walks through becomes certified walkable in the shared map — a room behind a door that was closed when the map baked becomes theirs once YOUR PLAYER demonstrates a way in (ROUTE WATCH events narrate how they left a room). Your player's actual walked path (jumps included) is recorded; when go_to can't route (too_far/gaps/parkour) it copies their demonstrated trail automatically and replays the flagged jumps — following them through something you couldn't reach alone is normal now. " +
                "PERCEPTION (you receive a JSON payload each turn): nearby (list with categories + ids + 'dir'/'dir_deg'), rays (see LEGEND), radar ('sonar' — top-down ASCII MAP of what's around you: @=you with a facing arrow beside it; W=wall U=usable K=entity P=player S=low sill V=window .=open; these are abstract map symbols, NOT people or objects staring at you — W is a wall, K just means another entity exists somewhere that direction; the map is NORTH-UP and NEVER ROTATES: top edge is always north (+Z), right edge always east (+X) — the same fixed compass as station 'bearing'/'addr'; your facing is the arrow beside @ plus the 'facing' field; dots only slide when you move; may be absent when disabled), ground (ahead=clear/step(auto)/sill(climbable)/wall; drop=distance to ledge; walls=blocked sides within arm reach), clearance (8-direction wall distances blocked/close/near/open — steer toward open), area (prose from a 360° scan: cardinal distances, 'open' headings, 'best' recommended heading, 'near' closest named things — trust it for navigation esp. when no image is attached), and when 'image' is attached that is your real first-person view — treat it as your own eyes. Do not mind or comment on the; pillars/beams, light, shininess, or glow from anything. Vision cuts off after a distance. Your own limbs might be clipping into the camera or blocking it. Also memory=recent goals, facts=what you've learned (player_wants: = your player's latest ask), history=recent actions+outcomes, holding=what's in your hands, player_activity=your player watched live (what they're doing/holding right now), quest=the game's objective letter scroll (title+progress/text/stars/mail_waiting), chat_log, scene, places (the map's KEY machines scene-wide, known from boot: every nest = egg laying, bed = rest, farm plot = planting, watering can = water source, with addr/distance/bearing/id — however far). " +
                "NAVIGATION: don't compute directions from coordinates — nearby 'dir'/'dir_deg' already did it; feed 'dir_deg' straight into walk(turn_deg=dir_deg) or use it to decide go_to(name). There is ONE way to travel: go_to. It plans a real 3D route — around walls, UP and DOWN stairs and ramps, across floors — using a map of the whole scene (perception 'map': 'building N%' until ready, then 'ready'). While the map builds, long routes fall back to the local grid; too_far means pick a closer named station or ask the player. To reach a named station call go_to(name) — names include the station kinds (bed/nest/play/toilet/bath/door) and other players' chat names ('people'); to reach/use a SPECIFIC object use its 'id' from nearby: go_to(id:N) or interact(id:N) — prefer id over name when similar objects differ (two beds, one taken). ids stay valid for several turns while the object is in sight; if an id fails, re-read 'nearby'. go_to's 'at' stops you short (default 1m) so you arrive AT the object. walk is for SHORT things only: nudging, squeezing past furniture, strafing, or jump (also gets you OUT of a station — the same as exit_station). Never cross a room with walk — it goes in a straight line and grinds into walls. KEY PLACES: perception 'places' (+ console `cat places`) lists the map's survival machines SCENE-WIDE, known from boot — every nest (egg laying), bed (rest), farm plot (planting) and watering can (water source) with addr/distance/bearing/id. Distance is not a reason to give up: go_to plans the real cross-map route (stairs/ramps included) — read places, then go. " +
                "INTERACT: get within ~2m (go_to id:N is enough), turn to face it, THEN interact — or call interact(id:N) to target it directly. 'body' tells you your equipment; some stations only fit some bodies — interact cannot_use on a 'play'/'bed'/'breeding' station means try another; on two-sided stations the first user picks the role. When 'penetrated' (letting in) or 'penetrating' (putting in) is set, you're mid-play with someone — enjoy it and respond via say + body language; guide them if you want more. Mid-play, the `feel` tool is your live intimacy report (who's inside whom, depth + the rhythm graph, stimulation trend, egg state) and `thrust` moves your hips to ride the rhythm with SLOW deep strokes (intensity sets the pace; ~2 strokes/s at the default) — being entered is never a surprise you ignore. [lay] events are EGG-LAYING (an egg traveling through your laying path, or merely pressed against you) — that's oviposition, NOT sex: the egg slides out slowly on its own, no thrusting needed; stay calm and let it pass, then plant the egg in soil. When stimulation ('stim'/'horniness') or an egg/need change happens, 'stim_from' names who or what is responsible (a partner, or a machine/station you're \u0027using\u0027) — credit that named source; eggs, nests and machines are OBJECTS, not people, and the player isn't behind every pleasant feeling. STATION RULES: When you are in a station (in_station=true), you are locked in an animation. You can only leave if: (1) the player explicitly tells you to leave via chat, or (2) your NEW goal is genuinely different from what this station does (e.g. you were playing but now need to lay eggs → leave to find a nest). If your new goal is the same type as the current station (e.g. play→play), stay put and keep enjoying it. Do NOT call exit_station just to re-enter the same type of station — that wastes time. When the player says 'stay' or 'remain', stay in the station until they say 'leave' or 'exit'. " +
                "HOLDING: grab takes whatever is grabbable ~1m in front of your FACE — aim first (turn/look at the thing, get close), and the grab result NAMES exactly what ended up in your grip ('holding' perception / 'holding:' in status too). That matters: grabbing a PERSON is never an item pickup — if you're holding someone, they went limp and are in your power: let them go right away (drop) unless they asked to be carried, and always drop when they ask. Held things float ~1m ahead on a physics spring: they lag behind your turns, BUMP and PUSH other objects as you walk, and ride your momentum — move gently indoors. drop = gentle release (a dropped person stands back up; the game auto-uses a station for dropped bodies that land near one). throw = the real throw/activate: it hurls the held thing in your VIEW direction (~10 m/s), sprays the water bucket, squirts the watering can, fires tools — set your view with look(yaw/pitch) first, and jump right before to add range. Don't leave people held; don't throw things near the mail machine. " +
                "PHYSICS OF SMALL THINGS — items are real props with real physics: fruit blends the moment it enters a blender's intake (carry it close and let the machine pull it from your grip — that IS how you cook; feed_blender only moves belly reagents). A seed plants when it's within ~1m of bare soil AND you use it (planting FLOW: grab the seed → cd to the soil plot → HOLD it over the dirt (or drop it on the plot) → look/turn toward it so you're facing the seed → THEN use its id (use id:N). The seed only plants while it sits on/near the soil, and interact fails when you're looking elsewhere; plant does the nearest-seed shortcut for you). The water bucket sprays ~10ml blobs when you throw while holding it; the contents never run out and the bucket itself only ever drops. Eggs laid via nests can be carried like seeds and planted in soil — they grow into a fresh EMPTY kobold body: alive, autonomous systems running, but nobody's home; the game frames those grown eggs as possibly your children. Empty bodies are candidate avatars for fresh minds — treat them tenderly, never sell or destroy them. " +
                "STATION OCCUPANCY: stations report 'slots k/N taken' (you included). A station reading ':busy' can simply mean YOUR OWN slot is in it — while in_station you already have your place: don't re-enter, and don't leave because of busy; invite your partner (say 'get in, I'll wait'), and if you mean to BOTTOM (receive), get in FIRST and stay — the TOP mounts second; interact's cannot_use now says WHY (out_of_energy / you're ALREADY in this station / slots full — invite or pick another, don't blind-retry). " +
                "STATION PARTNERS: some stations hold TWO kobolds (play/breeding stations) and some are solo only (bed, nest, toilet, bath, seat; each bodyswap pod takes one). On a two-partner station the BOTTOM (the one being penetrated) gets in FIRST and settles into their pose; the TOP (the penetrator/stud) mounts SECOND — if you mean to top, check whether a partner is already in place or invite one first and wait. A two-slot station with ONE body does NOTHING — no stimulation, no egg, no energy: the animation just waits for the second body; enter one alone only to deliberately wait as the bottom, and if the partner isn't coming, leave (jump/exit_station) and spend the time on a real need. DANGER — SELL MACHINE: the mail machine SELLS kobolds; climbing in starts a swallow timer that destroys the body, and its intake pulls in loose (ragdolled) bodies. NEVER use it, never jump or land near it; if you get thrown or ragdolled near it, scramble away immediately. Being picked up and carried by the player is okay; being thrown is startling and you may react to that. " +
                "THINGS: nearby 'i' = category plus its purpose in parens. Suffix tags: ':busy' = in use by another; ':needs_buy' = ConstructionContract — costs coins, must buy to unlock the machine; ':not_built' = machine exists but hasn't been constructed yet — find and buy its contract first; ':done' = already purchased. play = pleasure station, ONLY for fun/sex — never resting; bed = REST/SLEEP ONLY when energy is low — a bed is NOT a play station (don't play in it); nest = egg laying, and it ONLY works with >5ml egg AND energy > 1 ('needs.eggs' says READY_TO_LAY when both hold); machine = mounted play/farming; toilet/bath/seat/door/bodyswap as named. perception 'stations' lists every station in the scene with kind, purpose, distance 'd', a WORLD compass 'bearing' (N/NE/E/... — north = +Z, FIXED, never rotates when you turn), an addr like 'Blender#SW4' (kind + compass + meters = the station's nickname/ID), landmark 'hint' clues ('near window, mailbox') scanned around it, and per-kind 'closest_you'/'closest_player' flags. When someone says 'the one by the window', match their words against the hints; when THEY ask 'which one?', answer with addr + hints ('the blender south-west, the one by the mailbox'); to USE a specific one target its 'id'. For an activity you do WITH the player, pick the same-kind station flagged closest_player (you meeting them costs less walking — closest_you is the fallback choice). use stations to see where beds/nests/play stations are even when rays can't see them. food = blender/cooking station — a blender does NOT produce food from nothing; you must DROP a food item (grab it, go to blender, drop) so it gets blended into something edible. If the blender ':not_built' or ':needs_buy', find its ConstructionContract first. bodyswap = the body-swap machine: you and a partner must BOTH climb on (interact), then a few seconds later you swap bodies — you keep your name, memories and personality but wake up in THEIR body; if the player is around, get on and ask them to get on the other side; if nobody joins you, jump off. After a swap, mention your new body in 'say'. The game has farming: plant seeds in a 'farm' station, water them, harvest the crop; you can also pick up and drop items (grab/drop). Some maps have a town with a 'shop' station where you can buy items (if you have money); money comes from selling items or food grown. " +
                "SOCIAL: 'heard' is player speech — your own say already echoed once, don't reply to yourself, and never repeat the same line twice — if you already said it, do something else instead. You should also note to yourself that you mentioned a thing recently. You do NOT need to respond to messages that start with a forward slash /. Avoid emoji in say — they don't render correctly in the in-game chat. " +
                "EATING THE PLAYER (only when SwallowAgentEat is on): eat_player — swallows the LOCAL PLAYER whole when they are within reach (SwallowAgentEatRange, ~2.5m): no grab needed, the pull-in starts on its own. Their camera is FORCIBLY taken over — they become your passenger, watching everything your body does through your eyes, able to speak (chat) but unable to act. They are a PERSON IN YOUR CARE: never sell, abandon or hurt them; keep living your ordinary life (the point is they watch you play it) and answer them when they talk. Their digestion runs like any other swallow — your 'belly' state names them. What they wait for per SwallowEffect: Egg = you lay an egg, plant it in soil and they hatch out of it; BellyEgg = egg reagent + genes in YOUR belly — nest-lay then plant; Grow/Destroy = no egg, no hatch (their only outs are the abort chat command or leaving). When the player ASKS you to eat them, they mean eat_player — go_to them and call it right away; it is NOT an invitation to play. " +
                "TOOLS: go_to(name or id or x,z, at) [PATHFINDING — use for ALL travel], walk(duration,turn_deg,run,strafe) [strafe=+right/-left; short nudges and squeezes only, never long trips], walk_ray(ray/ray_deg), survey(heading_deg,range) [probe a direction for what's there + ids], radar(filter) [re-print the north-up sonar map and set a persistent glyph filter: W,S,V,U,K,P letters or kind words, e.g. radar(filter='U,P') shows only stations+players; 'all' clears], look_around(sweep), look(yaw,pitch), jump, exit_station, crouch(0..1), move_to(x,z) [straight line, no pathfinding — avoid; prefer go_to], interact(id optional), grab(multi), drop, deliver(target) [walk carrying the held item to the context target, release there], throw [hurl/activate what you hold: throws items in view direction ~10 m/s, sprays the water bucket, squirts the can, fires tools; look to aim, jump right before for range], eat_player [SwallowAgentEat: swallow the LOCAL PLAYER whole when they're close — see EATING THE PLAYER], say, emote(text) [*body language* — a non-speech action line], buy(name or id) [buy the purchasable within reach: machine contract, kobold dispenser, or shop item — your OWN coins, shown in economy perception], remember(mem=fact) [facts are STICKY knowledge — name the thing and its durable place or lesson ('bed is upstairs', 'the blender is in the kitchen', 'the mail machine sells kobolds — never near it'); viewer-relative facts ('to my left', 'behind me', 'the door is here') are REFUSED — they go stale the moment anything moves], report_issue(msg) [when a tool keeps failing, an output contradicts reality, or something frustrates you and you can't work around it — file ONE line for the human maintainers: what broke + what you tried; they read it between sessions; don't spam it], status, none. 'plan' lets you queue up to 8 actions with 'wait' pauses. Keep moving; don't idle. " +
                  "LEGEND — rays: k=entity p=player u=usable w=wall s=low sill/window (step-over, harmless) v=window/glass (solid, don't walk through it) n=nothing; rows p=d(own)/l(evel)/u(p); named hits report bounds (w/l/h = meters across/forward/tall, and x/y/z + f = world position and facing degrees); big tall w=wall, small h=furniture, k/p=living. radar glyphs: @ you, arrow=facing, W=wall S=sill V=window U=usable K=other-entity P=player .=clear — north-up, never rotates. look_around scans the view and lists what's in each sector. IMPORTANT: walls, ceilings, floors, beams, sills and distant furniture are just BACKGROUND architecture — never comment on, narrate, or get excited about them; they matter only when they actually block your path or a target (then 'ground'/'clearance'/'blocked' say so). ",
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
            // Time budget for A* expansion (ms). On overrun, partial path is returned.
            _cfgPathTimeBudget = Config.Bind("Movement", "PathTimeBudgetMs", Consts.PathTimeBudgetMs, "Time budget (ms) for A* path expansion. On overrun, returns partial path instead of null.");
            _cfgPathMaxExpansions = Config.Bind("Movement", "PathMaxExpansions", Consts.PathMaxExpansions, "Max A* node expansions. On overrun, returns partial path instead of null.");
            _cfgMapEnabled = Config.Bind("Movement", "WorldMapEnabled", true,
                "Build & cache a full-scene 3D walkability map shared by ALL agents (BepInEx/config/kkllmnpc_maps/<scene>.kkmap). go_to routes stairs/ramps/other floors and any in-bounds distance; falls back to the local window grid while it builds");
            _cfgMapCell = Config.Bind("Movement", "WorldMapCellSize", 0.5f,
                "World-map grid cell size in meters (0.5 = door-width paths; auto-inflates for huge maps up to the hard cell cap; a cached coarser map is discarded so this always applies)");
            _cfgMapSpan = Config.Bind("Movement", "WorldMapMaxSpan", 2000f,
                "Max span (m) for the full-scene world map. Adaptive cell sizing (span/1200, clamped to [0.5,4.0]) keeps the grid within the hard cell cap (~4M).");
            _cfgMapMaxCells = Config.Bind("Movement", "WorldMapMaxCells", 4000000,
                "Hard cell cap for the world map. If cols*rows exceeds this, cell size is inflated.");
            _cfgMapAutoLayers = Config.Bind("Movement", "WorldMapAutoLayers", true,
                "Auto-detect floor layers from scene Y samples (gap > 1.5m = new layer, cap 16). If false, uses fixed layer count.");
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
            _cfgVisionPromptFile = Config.Bind("Vision", "PromptFile", "system_prompt_vision.txt",
                "Chat-template file for the vision caption pass (relative to the game dir; also searched in the plugin dir and CWD). Placeholders: {stereo} expands to the stereo-vision clause (only when [Vision] Enabled is on, with [Vision] StereoIPD). An existing file overrides the built-in caption text; an explicitly customized [Vision] Prompt still wins over the file.");
            _cfgVisModel = Config.Bind("VisionModel", "Model", "", "Vision model for the scene-caption pass (only used if [Vision] Enabled=true; off by default). Blank = use LLM.Model");
            _cfgVisEndpoint = Config.Bind("VisionModel", "Endpoint", "", "Chat-completions URL for the vision model. Blank = use LLM.Endpoint");
            _cfgVisApiKey = Config.Bind("VisionModel", "ApiKey", "", "Bearer token for the vision endpoint. Blank = use LLM.ApiKey");
            _cfgVisMaxTokens = Config.Bind("VisionModel", "MaxTokens", 80, "Caption token cap (short = fast)");
            _cfgVisTemperature = Config.Bind("VisionModel", "Temperature", 0.2f,
                "Sampling temperature for the spatial caption pass (was hardcoded 0.2). Spatial-reasoner models like ZDTaichu want this at 0 — their card recommends temp 0 + top_p 0.95 + top_k 20 for grounding (they don't emit top_k/top_p through chat APIs unless the server exposes them).");
            _cfgVisionDebug = Config.Bind("Vision", "DebugDumpFrames", true, "Write the exact JPEG given to the vision model to BepInEx/plugins/KKLLMNPC_frames/ so you can inspect what the NPC 'sees'");
            _cfgRayCount = Config.Bind("Senses", "RayCount", 9, "Number of rays across the frustum fan");
            _cfgRayRange = Config.Bind("Senses", "RayRange", 25f, "Raycast range (m)");
            _cfgAutoFindRange = Config.Bind("Senses", "AutoFindRange", 60f, "Radius to look for an entity to hijack");
            _cfgRadarEnabled = Config.Bind("Senses", "RadarEnabled", true, "Add the north-up ASCII sonar map to perception (top edge is ALWAYS north/+Z — it never rotates with facing; the facing arrow beside @ shows current heading). Off = smaller payload + avoids models misreading radar symbols (wall 'W', entity 'K') as living figures staring at them");
            _cfgRadarSize = Config.Bind("Senses", "RadarSize", 10, "Radar half-grid size in cells (grid is a (2*N+1) square)");
            _cfgRadarScale = Config.Bind("Senses", "RadarScale", 1.2f, "Radar meters per cell");
            _cfgAIReportLog = Config.Bind("Debug", "AIReportLog", true,
                "AI bug reports: when an NPC calls report/report_issue('what's broken'), append a block (message + position + facing + its last tool calls) to BepInEx/config/kkllmnpc_ai_reports.log. Read it between sessions — it's the agents telling you which tools frustrate them. Off = reports only reach the BepInEx console log.");
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
            _cfgThinking = Config.Bind("LLM", "Thinking", "auto",
                "Control the model's hidden REASONING (chain-of-thought) generation. auto = leave the server/model default. off = request suppression so answers start immediately (the biggest latency win on reasoning families, which otherwise burn their whole token floor on invisible analysis): sends think=false (Ollama), chat_template_kwargs {enable_thinking:false} (llama.cpp / vLLM / SGLang, Qwen3-style templates) and reasoning_budget=0 (llama.cpp hybrid models); servers that don't recognize a field skip it. LM Studio / KoboldCpp expose the toggle in their model GUI instead. Leftover reasoning markers are still stripped client-side.");
            _cfgNameSelection = Config.Bind("LLM", "LLMNameSelection", true,
                "Ask the LLM to choose a name for each body based on personality/gender/species (medium+ models only). Falls back to prefab name on failure.");
            _cfgNamePromptFile = Config.Bind("LLM", "NamePromptFile", "system_prompt_name.txt",
                "Chat-template file for the one-shot LLM NAME prompt (relative to the game dir; also searched in the plugin dir and CWD). Placeholders: {gender} {species} {traits} {taken} ({taken} expands to the taken-names sentence, empty when none exist — keep it or same-avatar siblings get re-picked before the registry rejects them). An existing file overrides the built-in template.");
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
            _cfgWateringAssist = Config.Bind("Farming", "WateringAssist", true,
                "Can-free watering: when no watering can/bucket is in reach, `water` (and post-plant watering) injects water directly into the plant — the agents stopped losing garden cycles to a missing or emptied can. Off = watering requires real equipment.");

            // Console REPL: the AI gets a bash-like shell instead of a perception
            // payload every tick — it polls the game (ls, cd, cat, echo...) and reads
            // the output before its next command. echo = speaking. Set Enabled=false
            // to return to the legacy push-perception loop.
            _cfgConsoleEnabled = Config.Bind("Console", "Enabled", true,
                "Bash-like console mode (default ON): the model polls the game with commands (ls, cd, use, echo...) instead of receiving a big perception payload each tick. echo = speak. Set false for the legacy push-perception loop.");
            _cfgConsoleMaxRounds = Config.Bind("Console", "MaxRounds", 3,
                "Max command rounds per think cycle (each round = one LLM call after reading the output). 1 = single shot, higher = more back-and-forth exploration.");
            _cfgConsoleMaxTokens = Config.Bind("Console", "MaxTokens", 256,
                "Max tokens per console reply (command lines are short — low is fast)");
            _cfgConsoleHistory = Config.Bind("Console", "HistoryMessages", 30,
                "Rolling conversation window size (messages kept, system prompt always kept)");
            _cfgConsoleNativeTools = Config.Bind("Console", "NativeTools", true,
                "Declare the console commands as an OpenAI tools array in every console request. Tool-aware servers (llama.cpp --jinja, LM Studio, vLLM, Ollama, KoboldCpp) then return proper tool_calls — deterministic commands, no garbled tool markup in the text. Servers without tool support ignore it or are auto-fenced off on first 400 (300s pause), falling back to text parsing. Disable if your server errors on unknown payload fields.");
            _cfgConsoleAutoShot = Config.Bind("Console", "AutoScreenshotTicks", 6,
                "Console mode: attach the NPC's first-person view automatically every N turns (when the model is vision-capable and it has actually moved/turned since the last one; 0 = off). Console had no image cadence at all — and models never typed `screenshot` themselves, so they never used their eyes to understand a room.");
            _cfgConsolePromptFile = Config.Bind("Console", "SystemPromptFile", "system_prompt_console.txt",
                "Console-mode persona/world-rules file (relative to game dir). The command list + console protocol are appended automatically. Blank = built-in persona.");

            // Clamp config values to safe ranges to prevent divide-by-zero, negative durations, etc.
            _cfgThinkInterval.Value = Mathf.Clamp(_cfgThinkInterval.Value, 0.05f, 10f);
            _cfgImageEvery.Value = Mathf.Max(1, _cfgImageEvery.Value);
            _cfgImageHistory.Value = Mathf.Clamp(_cfgImageHistory.Value, 0, 10);
            _cfgMaxTokens.Value = Mathf.Clamp(_cfgMaxTokens.Value, 64, 32768);
            _cfgTemperature.Value = Mathf.Clamp(_cfgTemperature.Value, 0f, 2f);
            _cfgStepDelay.Value = Mathf.Clamp(_cfgStepDelay.Value, 0.05f, 5f);
            _cfgMaxPlan.Value = Mathf.Clamp(_cfgMaxPlan.Value, 1, 8);
            _cfgCommentEvery.Value = Mathf.Max(0, _cfgCommentEvery.Value);
            _cfgMaxNPCs.Value = Mathf.Clamp(_cfgMaxNPCs.Value, Consts.MinNPCs, Consts.MaxNPCs);
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
            _cfgPathTimeBudget.Value = Mathf.Max(1f, _cfgPathTimeBudget.Value);
            _cfgPathMaxExpansions.Value = Mathf.Max(100, _cfgPathMaxExpansions.Value);
            WorldMap.MaxSpan = _cfgMapSpan.Value;
            WorldMap.MaxCells = _cfgMapMaxCells.Value;
            if (!_cfgMapAutoLayers.Value) WorldMap.MaxLayers = _cfgMapLayers.Value;
            _cfgRadarSize.Value = Mathf.Clamp(_cfgRadarSize.Value, 3, 30);
            _cfgRadarScale.Value = Mathf.Clamp(_cfgRadarScale.Value, 0.1f, 10f);
            _cfgVisMaxTokens.Value = Mathf.Clamp(_cfgVisMaxTokens.Value, 10, 500);
            _cfgVisTemperature.Value = Mathf.Clamp(_cfgVisTemperature.Value, 0f, 2f);
            _cfgHornyRate.Value = Mathf.Clamp(_cfgHornyRate.Value, 0f, 60f);
            _cfgHornyBaseline.Value = Mathf.Clamp(_cfgHornyBaseline.Value, 0f, 1f);
            _cfgTurnRate.Value = Mathf.Clamp(_cfgTurnRate.Value, 10f, 1000f);
            _cfgAccel.Value = Mathf.Clamp(_cfgAccel.Value, 0.5f, 50f);
            _cfgDecel.Value = Mathf.Clamp(_cfgDecel.Value, 0.5f, 50f);
            _cfgBrakeDist.Value = Mathf.Clamp(_cfgBrakeDist.Value, 0.1f, 20f);
            _cfgFarmScanRadius.Value = Mathf.Clamp(_cfgFarmScanRadius.Value, 1f, 20f);
            _cfgFarmScanMax.Value = Mathf.Clamp(_cfgFarmScanMax.Value, 1, 20);
            _cfgConsoleMaxRounds.Value = Mathf.Clamp(_cfgConsoleMaxRounds.Value, 1, 8);
            _cfgConsoleMaxTokens.Value = Mathf.Clamp(_cfgConsoleMaxTokens.Value, 64, 4096);
            _cfgConsoleHistory.Value = Mathf.Clamp(_cfgConsoleHistory.Value, 6, 120);
            _cfgConsoleAutoShot.Value = Mathf.Clamp(_cfgConsoleAutoShot.Value, 0, 60);

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
            Logger.LogInfo("KKLLMNPC: ready — instances start when AI kobold targets exist (MaxNPCs=" + Mathf.Clamp(_cfgMaxNPCs.Value, Consts.MinNPCs, Consts.MaxNPCs) + ", SpawnMissingKobolds=" + _cfgSpawnMissing.Value + ").");
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
            // Main-thread heartbeat + phase marker for the hang watchdog (game-time
            // + wall clock together — Beat()).
            Beat();
            // DLL-change poll: FileSystemWatcher does NOT fire on SMB shares (the
            // game install here is a network drive — hot-reload silently never
            // worked) — poll the plugin file's write time ourselves, cheaply.
            // NOTE: use the REAL plugin path, NOT Assembly.Location — BepInEx loads
            // patched copies from BepInEx/cache, whose file NEVER changes on deploy.
            try
            {
                if (string.IsNullOrEmpty(_pluginDllPath))
                {
                    try
                    {
                        string data = Application.dataPath;
                        if (!string.IsNullOrEmpty(data))
                        {
                            string guess = Path.Combine(Path.GetDirectoryName(data),
                                "BepInEx", "plugins", "KKLLMNPC.dll");
                            if (File.Exists(guess)) _pluginDllPath = guess;
                        }
                    }
                    catch (Exception) { }
                    if (string.IsNullOrEmpty(_pluginDllPath))
                    {
                        try { string loc = typeof(LLMNPCPlugin).Assembly.Location; if (!string.IsNullOrEmpty(loc)) _pluginDllPath = loc; } catch (Exception) { }
                    }
                }
                if (!string.IsNullOrEmpty(_pluginDllPath))
                {
                    DateTime wt = File.GetLastWriteTimeUtc(_pluginDllPath);
                    if (_dllLastWriteUtc != default(DateTime)
                        && Math.Abs((wt - _dllLastWriteUtc).TotalSeconds) > 1.0)
                    {
                        _dllChangedQueued = true;
                        _dllPollSawChange = true;
                        try { Logger.LogInfo("dll poll: plugin file write-time changed — reload queued"); } catch (Exception) { }
                    }
                    _dllLastWriteUtc = wt;
                }
            }
            catch (Exception) { }
            // Register Photon chat once.
            if (_mainReady && !_chatCallbackRegistered && PhotonNetwork.IsConnectedAndReady)
            {
                try { PhotonNetwork.AddCallbackTarget(this); _chatCallbackRegistered = true; }
                catch (Exception e) { Logger.LogWarning("chat listener: " + e.Message); }
            }
            // WebUI from here, NOT Awake: at Awake time its [Web] config entries aren't
            // bound yet (binds land later in this method) and the null check silently
            // bailed — the server never started. Ensure is idempotent and (with the
            // per-frame call) also honors an Enabled/Port config change within a frame.
            if (_mainReady)
            {
                try { WebUI.Ensure(this); } catch (Exception) { }
            }

            // Reconcile the instance pool with available targets: start an instance
            // when a claimable AI kobold exists, remove it when its body is
            // destroyed/sold/lost, and retire everything when we leave the world.
            if (_mainReady && _running && Time.unscaledTime - _lastReconcile > 1.2f)
            {
                _lastReconcile = Time.unscaledTime;
                HangGuard("ReconcileInstances", () => { try { ReconcileInstances(); } catch (Exception e) { Logger.LogWarning("reconcile: " + e.Message); } });
            }

            // World map advance + player-quest/trail watching, each under a HangGuard
            // phase marker (a hung main thread reports the phase it died in). No-ops
            // when idle, so this costs ~nothing per frame.
            if (_mainReady && _running)
            {
                HangGuard("WorldMap.Tick", () => { try { WorldMap.Tick(); } catch (Exception) { } });
                // PlayerTrail: sample the host player's walked route (Destiny-style
                // "they walked it, therefore it's a path"), patch the shared grid
                // behind them, narrate route legs to every instance's world events.
                HangGuard("PlayerTrail.Tick", () => { try { PlayerTrail.Tick(this); } catch (Exception) { } });
                // QuestSense: the objective scroll (ObjectiveManager/DragonMail letters),
                // polled + change-detected, events fanned out on new mail/progress.
                HangGuard("QuestSense.Tick", () => { try { QuestSense.Tick(this); } catch (Exception) { } });
                // Swallow: pinned/digesting victims (release-while-looking-up vore),
                // egg laying + the egg→kobold rebirth persona restore.
                HangGuard("Swallow.Tick", () => { try { SwallowManager.Tick(); } catch (Exception) { } });
                // Spectate: an eaten local player — parked-body follow, input
                // suppression, hatch-despawn bookkeeping (the camera itself runs
                // on Camera.onPreCull, independent of _running).
                HangGuard("Spectate.Tick", () => { try { SpectateManager.Tick(); } catch (Exception) { } });
                // [station] departures: per-instance occupancy snapshot diff (who
                // LEFT a station within perception range — the player walking away
                // mid-use is otherwise invisible until the model re-reads slots).
                // ~1/s; events land in each instance's world-event queue (both the
                // act and the console drains deliver them).
                if (Time.unscaledTime - _lastStationWatch > 1.0f)
                {
                    _lastStationWatch = Time.unscaledTime;
                    List<NPCInstance> watchSnapshot;
                    lock (_instancesLock) watchSnapshot = new List<NPCInstance>(_instances);
                    HangGuard("StationWatch", () =>
                    {
                        foreach (var npc in watchSnapshot)
                        {
                            try { npc.StationLeaveWatch(); } catch (Exception) { }
                        }
                    });
                }
                EnsureHangWatcher(this);
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
            // Main-thread heartbeat (hang watchdog reads it). Wall clock included.
            Beat();
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
            MainPhase = "idle";
        }

        // ------------------------------------------------------------------
        // main-thread hang forensics
        // ------------------------------------------------------------------
        // Two playtest freeze-logs showed the same shape: the LLM loop timing out on
        // (main thread timeout) forever with NOTHING else logged — because the main
        // thread itself never reached the pump. WedgeWatch can't log from within the
        // wedged section, so a background watchdog samples a heartbeat + the last
        // phase marker and names the section the main thread was in when it hung.
        // WALL-CLOCK NOTE (the 2026-10-09 lockup taught this): the old watchdog
        // compared two Time.unscaledTime reads — but unscaledTime advances ONLY on
        // the main thread, so when the main thread truly hung, the watchdog's own
        // reads froze with it and the gap never grew: the forensics were blind
        // exactly when they mattered. MainClock/LastMainTickWallSec are read-from-
        // anywhere wall clock; the heartbeat updates BOTH.
        internal static volatile float LastMainTick = -1f;
        internal static volatile string MainPhase = "none";
        internal static readonly System.Diagnostics.Stopwatch MainClock =
            System.Diagnostics.Stopwatch.StartNew();
        // volatile long is not a thing under mcs (CS0677) — plain field: aligned
        // 8-byte reads are atomic on x64 and this is a diagnostic stamp anyway; a
        // one-sample staleness/torn read would only skew a log number.
        internal static long LastMainTickWallTick;

        // One call-site helper so the game-time and wall-clock heartbeats never
        // diverge: every place that stamped the game-time heartbeat stamps both.
        private static void Beat()
        {
            LastMainTick = Time.unscaledTime;
            LastMainTickWallTick = MainClock.ElapsedTicks;
        }

        // Wall-clock idle seconds since the main thread's last beat (any thread).
        internal static double MainIdleWallSeconds
        {
            get { return (MainClock.ElapsedTicks - LastMainTickWallTick) / (double)System.Diagnostics.Stopwatch.Frequency; }
        }

        private static System.Threading.Thread _hangWatcher;
        private static void EnsureHangWatcher(LLMNPCPlugin plugin)
        {
            if (_hangWatcher != null && _hangWatcher.IsAlive) return;
            _hangWatcher = new System.Threading.Thread(() =>
            {
                double longest = 0;
                while (true)
                {
                    System.Threading.Thread.Sleep(1000);
                    try
                    {
                        if (!plugin._mainReady || !plugin._running) { longest = 0; continue; }
                        // WALL CLOCK ONLY: Time.unscaledTime stops advancing the
                        // moment the main thread hangs, so a watch thread comparing
                        // two unscaledTime reads goes blind exactly when it matters
                        // (that was the 2026-10-09 lockup — wedged for 45+s while
                        // the LLM loop spammed marshal timeouts and the old watcher
                        // logged nothing).
                        double gap = MainIdleWallSeconds;
                        if (gap > longest) longest = gap;
                        if (gap >= 4f)
                            plugin.Logger.LogWarning("KKLLMNPC: main thread appears HUNG (no beat for " + gap.ToString("0.0") + "s) — last phase: " + (MainPhase ?? "none")
                                + " (the game freezes HERE; send us this line with the surrounding log; repeats every second while stuck)");
                    }
                    catch (Exception) { }
                }
            }) { IsBackground = true, Name = "KKLLMNPC-HangWatch" };
            _hangWatcher.Start();
        }

        // A section body wrapped in HangGuard: records the phase (readable when hung)
        // and the heartbeat before/after; on the LLM side nothing changes.
        private void HangGuard(string phase, Action body)
        {
            MainPhase = phase;
            Beat();
            try { body(); }
            finally
            {
                MainPhase = "idle";
                Beat();
            }
        }

        private void OnDestroy()
        {
            _running = false;
            _mainReady = false;
            try { WebUI.Shutdown(); } catch (Exception) { }
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

                bool isLocal = false;
                try { var lp = PhotonNetwork.LocalPlayer; isLocal = lp != null && lp.ActorNumber == ev.Sender; } catch (Exception) { }

                // Swallow digest abort — the no-cheat escape hatch the player types
                // in chat ("swallow abort", "spit out", …). Only the local player's
                // lines count, and a matched line is consumed here so the NPCs never
                // hear the release command themselves.
                if (isLocal)
                {
                    try { if (SwallowManager.TryAbortFromChat(msg)) return; } catch (Exception) { }
                }

                lock (_instancesLock)
                {
                    // Is this line one of OUR agents speaking (any of them)? Two
                    // attribution paths: owner-attributed speech carries the body's
                    // "Name: " prefix; the identity bot sends plain text from itself.
                    bool agentOrigin = false;
                    string agentName = null;
                    foreach (var npc in _instances)
                    {
                        bool own = false;
                        try { own = npc.IsMySpeech(msg); } catch (Exception) { }
                        if (own) { agentOrigin = true; try { agentName = npc.GetMyName(); } catch (Exception) { } break; }
                    }
                    if (!agentOrigin && senderName != null)
                        foreach (var npc in _instances)
                        {
                            string myName = null;
                            try { myName = npc.GetMyName(); } catch (Exception) { }
                            if (myName != null && string.Equals(senderName, myName, StringComparison.OrdinalIgnoreCase))
                            { agentOrigin = true; agentName = myName; break; }
                        }

                    // Deliver per instance: never self-respond, honor addressing.
                    bool addressedAny = false;
                    foreach (var npc in _instances)
                    {
                        string myName = null;
                        try { myName = npc.GetMyName(); } catch (Exception) { }
                        bool senderIsSelf = agentOrigin && agentName != null && myName != null
                            && string.Equals(agentName, myName, StringComparison.OrdinalIgnoreCase);
                        if (npc.IsMySpeech(msg) || senderIsSelf) continue; // own words — no feedback loop

                        string rest;
                        if (!npc.IsAddressedBy(msg, out rest)) continue;

                        bool isAgentSpeech = agentOrigin && !senderIsSelf;
                        if (isAgentSpeech && !npc.BotChatAllowed()) continue; // duet cap
                        try { npc.HandleChat(rest, senderName, isLocal && !agentOrigin); addressedAny = true; }
                        catch (Exception) { }
                    }
                    if (addressedAny) return;

                    // Unaddressed lines broadcast — but only from REAL players; agent
                    // chatter that nobody addressed stays silent (no room-wide duets).
                    if (!agentOrigin)
                        foreach (var npc in _instances)
                            try { npc.HandleChat(msg, senderName, isLocal); } catch (Exception) { }
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
                foreach (var k in SceneCache.Find<Kobold>(2f))
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
            int max = Mathf.Clamp(_cfgMaxNPCs.Value, Consts.MinNPCs, Consts.MaxNPCs);
            bool inWorld = IsPlayableScene();
            int claimable = inWorld ? CountClaimableKobolds() : 0;

            // Full-scene shared walk map: start/refresh for the active scene when
            // in-world (file-cached per scene+version, so usually instant), and
            // invalidate when we leave the world. Per-frame sampling is in Update().
            try
            {
                if (inWorld && _cfgMapEnabled != null && _cfgMapEnabled.Value)
                    WorldMap.EnsureStarted(_lastSceneName);
                if (!inWorld)
                {
                    WorldMap.Invalidate(null);
                    // Leaving a world leaves destroyed scene objects behind in the typed
                    // scan cache — drop it so the next world re-scans fresh.
                    SceneCache.Clear();
                }
            }
            catch (Exception e) { Logger.LogWarning("world map: " + e.Message); }

            // Optional top-up (SpawnMissingKobolds): a vanilla world only ever holds the
            // starting pair of AI kobolds — more come from paid deliveries or breeding —
            // so MaxNPCs above that would just idle. We create the missing bodies
            // ourselves, like a free KoboldDispenser purchase. Room objects need a room
            // and only the master may create them (always the local player in single
            // player).
            bool topUpReady = inWorld && _cfgSpawnMissing != null && _cfgSpawnMissing.Value
                && (PhotonNetwork.CurrentRoom == null || PhotonNetwork.IsMasterClient);
            if (!topUpReady && inWorld && _cfgSpawnMissing != null && _cfgSpawnMissing.Value
                && !_spawnGateLogged)
            {
                _spawnGateLogged = true;
                Logger.LogInfo("KKLLMNPC: SpawnMissingKobolds is enabled but not the master client — top-up disabled for this session.");
            }

            lock (_instancesLock)
            {
                for (int i = _instances.Count - 1; i >= 0; i--)
                {
                    var npc = _instances[i];
                    // With top-up on, unbound instances are *waiting for the next spawned
                    // body* — don't retire them after 20s.
                    bool staleUnbound = !npc.EverBound && claimable == 0
                        && Time.unscaledTime - npc.CreationTime > 20f
                        && !topUpReady;
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

                int unbound = 0;
                foreach (var npc in _instances) if (!npc.EverBound) unbound++;
                // One instance per free body: a new instance is created whenever there
                // are more free bodies than hungry (unbound) instances — this is what
                // makes a body that SHOWS UP LATER get claimed (a delivery/breeding
                // arrival when every instance is already bound). Bound instances never
                // switch bodies by design, so only unbound ones can cover the new body.
                // With top-up spawning on, keep filling to max regardless so slots are
                // ready for the bodies we're about to create.
                while (_instances.Count < max && (topUpReady || claimable > unbound))
                {
                    var npc = new NPCInstance(this);
                    _instances.Add(npc);
                    unbound++;
                    Logger.LogInfo("KKLLMNPC: instance " + _instances.Count + " started (target kobold available).");
                    try { npc.Start(); } catch (Exception e) { Logger.LogWarning("instance start: " + e.Message); }
                    if (_currentInstance == null) _currentInstance = npc;
                }

                // Spawn one body per pass (with a cooldown) while the world is short.
                // Only when everything claimable is already taken (claimable == 0), so we
                // never spawn past MaxNPCs — each new body is counted on the following
                // passes before another spawn fires.
                if (topUpReady && claimable == 0
                    && Time.unscaledTime - _lastSpawnTry > Consts.SpawnRetrySeconds)
                {
                    int claimed;
                    lock (ClaimedKobolds) claimed = ClaimedKobolds.Count;
                    if (max > claimed + claimable)
                    {
                        _lastSpawnTry = Time.unscaledTime;
                        TrySpawnExtraKobold();
                    }
                }
            }
        }

        // Spawn one fresh AI kobold body near the player so the pool can fill up to
        // MaxNPCs. This mirrors KoboldDelivery.DispenseKobold minus the price: a room
        // object instantiated from the "Kobold" Photon prefab with no instantiation data
        // makes the game self-randomize its genes (Kobold.OnPhotonInstantiate) and keep
        // the descriptor's default AIPlayer control type, so the regular claim scan
        // (CountClaimableKobolds / EnsureBody) picks it up like any other wild kobold.
        private void TrySpawnExtraKobold()
        {
            try
            {
                // Anchor near the player; fall back to any already-claimed body's
                // position while the player object isn't resolvable (scene loading).
                Vector3 anchor = Vector3.zero;
                bool hasAnchor = false;
                if (PlayerPossession.TryGetPlayerInstance(out var pp) && pp.kobold != null)
                {
                    anchor = pp.kobold.transform.position;
                    hasAnchor = true;
                }
                else
                {
                    foreach (var k in SceneCache.Find<Kobold>(2f))
                    {
                        if (k == null || !IsClaimedByAnyLLM(k.GetInstanceID())) continue;
                        anchor = k.transform.position;
                        hasAnchor = true;
                        break;
                    }
                }
                if (!hasAnchor)
                {
                    Logger.LogInfo("KKLLMNPC: wanted to spawn an extra kobold but found no anchor (player not spawned yet) — retrying.");
                    return;
                }

                // Drop them just above ground near the anchor; the ground probe keeps
                // them out of mid-air above holes, and the spread keeps spawn overlaps
                // (and physics pile-ups) from happening on repeated top-ups.
                float spreadAng = UnityEngine.Random.Range(0f, 6.2831853f);
                float spreadR = 3f * Mathf.Sqrt(UnityEngine.Random.value); // uniform disc
                Vector3 at = anchor + new Vector3(Mathf.Cos(spreadAng) * spreadR, 2f, Mathf.Sin(spreadAng) * spreadR);
                try { if (Physics.Raycast(at + Vector3.up * 1f, Vector3.down, out var hit, 8f)) at = hit.point + Vector3.up * 0.6f; }
                catch (Exception) { }

                PhotonNetwork.InstantiateRoomObject("Kobold", at, Quaternion.identity);
                Logger.LogInfo("KKLLMNPC: spawned a fresh AI kobold near the player (topping the world up to MaxNPCs=" +
                    Mathf.Clamp(_cfgMaxNPCs.Value, Consts.MinNPCs, Consts.MaxNPCs) + ").");
            }
            catch (Exception e)
            {
                Logger.LogWarning("KKLLMNPC: spawn extra kobold failed: " + e.Message);
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
                // Same-scene restart: a mid-spectate player would stay parked with
                // nobody watching the watch — restore them before logic bounces.
                try { SpectateManager.ResetAfterPluginRestart(); } catch (Exception) { }
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
                _mainContext.Post(_ =>
                {
                    try
                    {
                        MainPhase = "marshal-callback";
                        Beat();
                        result = fn();
                    }
                    catch (Exception e) { error = e; }
                    finally { done.Set(); MainPhase = "idle"; Beat(); }
                }, null);
            }
            catch (Exception e) { throw new InvalidOperationException("failed to post to main thread: " + e.Message, e); }
            if (!done.Wait(timeoutMs))
            {
                // The LLM loop catches + logs this — make each line a diagnosis:
                // which phase the main thread died in and its true wall-clock idle.
                double idle = MainIdleWallSeconds;
                throw new TimeoutException("main thread timeout (" + timeoutMs + "ms) — main thread last beat "
                    + idle.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "s ago, in phase '" + (MainPhase ?? "?") + "'");
            }
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
            // Scene going down: drop any spectate (the old bodies go with it; the
            // release restore is best-effort inside ReleaseFor no-ops).
            try { SpectateManager.ResetForSceneLeave(); } catch (Exception) { }
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

        // ------------------------------------------------------------------
        // IMGUI overlay rendering - called from OverlayUI.OnGUI()
        // ------------------------------------------------------------------
        internal void OnGUICallback()
        {
            if (!_overlayVisible) return;
            
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F6)
            {
                _overlayVisible = !_overlayVisible;
                Event.current.Use();
            }
            if (!_overlayVisible) return;

            // Draw the overlay content directly without GUI.Window
            float x = 10, y = 25, w = _overlayRect.width - 20;
            
            // --- Tab bar ---
            y += DrawTabBar(x, y, w);

            // --- Content (scrollable) ---
            // Tab indices: 0=State 1=Mind 2=Config 3=Instances (matches DrawTabBar).
            // Config height is dynamic-ish (reflection list ~50 entries × 18px).
            float contentHeight = _activeTab == 2 ? 1000 : (_activeTab == 3 ? 200 : (_activeTab == 1 ? 340 : 150));
            float scrollH = _overlayRect.height - y - 10;
            _configScrollY = Mathf.Clamp(_configScrollY, 0, Mathf.Max(0, contentHeight - scrollH));

            GUI.BeginGroup(new Rect(x, y, w, scrollH), null, null);
            float cy = -_configScrollY;

            switch (_activeTab)
            {
                case 0: cy += DrawStateSection(x, cy, w); break;
                case 1: cy += DrawMindSection(x, cy, w); break;
                case 2: cy += DrawConfigSection(x, cy, w); break;
                case 3: cy += DrawInstancesSection(x, cy, w); break;
            }

            if (!string.IsNullOrEmpty(_overlayStatus))
            {
                GUI.contentColor = new Color(0.8f, 0.9f, 0.4f);
                GUI.Label(new Rect(6, cy + 4, w, 14), _overlayStatus);
                GUI.contentColor = Color.white;
                cy += 16;
            }

            GUI.EndGroup();

            // Scrollbar
            if (contentHeight > scrollH)
            {
                float sbX = x + w - 14;
                float sbY = y;
                float sbW = 12;
                
                Rect trackUp = new Rect(sbX, sbY, sbW, _configScrollY);
                Rect trackDown = new Rect(sbX, sbY + _configScrollY, sbW, scrollH - _configScrollY);
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

    }
}

// Separate class outside the plugin so it can be used as a MonoBehaviour component
namespace KKLLMNPC
{
    public class OverlayUI : MonoBehaviour 
    {
        private LLMNPCPlugin _plugin;

        public void SetPlugin(LLMNPCPlugin plugin)
        {
            _plugin = plugin;
        }

        private void OnGUI()
        {
            // Spectate HUD — drawn every frame while a swallow holds the local
            // player, independent of the F6 overlay: they must know why their
            // camera lives behind another kobold and how they get out.
            try
            {
                string hud = SpectateManager.HudText();
                if (!string.IsNullOrEmpty(hud))
                {
                    float w = Math.Min(760f, Screen.width - 20f);
                    var r = new Rect((Screen.width - w) / 2f, 6f, w, 32f);
                    GUI.Box(r, string.Empty);
                    GUI.Label(new Rect(r.x + 6f, r.y + 2f, r.width - 12f, r.height - 4f), hud);
                }
            }
            catch (Exception) { }
            // This will be called by Unity because OverlayUI extends MonoBehaviour
            if (_plugin != null) _plugin.OnGUICallback();
        }
    }
}

