// Written by @auRose94 (https://github.com/auRose94) under MIT license.
// Named constants for magic numbers scattered throughout the codebase.
//
using System;

namespace KKLLMNPC
{
    /// <summary>
    /// Central constants for magic numbers used across the plugin.
    /// Makes tuning easier and documents the purpose of each value.
    /// </summary>
    internal static class Consts
    {
        // ---- HTTP / LLM ----
        public const int DefaultHttpTimeoutMs = 120000;      // 120s for LLM calls
        public const int HttpTimeoutMsVision = 120000;        // 120s for vision model
        public const int HttpTimeoutMsShort = 15000;          // 15s for short calls (naming)
        public const int HttpTimeoutMsProbe = 5000;           // 5s for server probes
        public const int MaxResponseSizeBytes = 1048576;      // 1MB response cap
        public const int DefaultRetries = 3;
        public const int ProbeRetryDelayMs = 1000;
        public const int ProbeMaxRetries = 3;

        // ---- Perception ----
        public const float DefaultRayRange = 25f;
        public const float DefaultRayCount = 9f;
        public const float NearbyRadius = 14f;
        public const float CameraFieldOfView = 90f;
        public const float DefaultImageSize = 192f;
        public const float DefaultImageQuality = 50f;
        public const float DefaultCamNearClip = 0.10f;
        public const float DefaultCamFarClip = 80f;
        public const float DefaultCamForward = 0.22f;
        public const float DefaultStereoIPD = 0.063f;
        public const float DefaultHornyBaseline = 0.08f;
        public const float DefaultHornyRate = 5f;

        // ---- Movement ----
        public const float DefaultTurnRate = 180f;
        public const float DefaultAccel = 4f;
        public const float DefaultDecel = 6f;
        public const float DefaultBrakeDist = 2f;
        public const float WalkProbeRange = 10f;
        public const float WallBrakeDist = 3.0f;
        public const float LedgeDropHardStop = 2.6f;
        public const float LedgeDropSoftWarning = 0.7f;
        public const float LedgeDropSoftMax = 2.6f;
        public const float CameraClipMinOffset = 0.02f;
        public const float CameraClipOffsetFactor = 0.8f;
        public const float CameraClipEraseTime = 0.4f;
        public const float CameraClipFixCooldown = 1.5f;
        public const float CameraClipCrouchRate = 0.15f;
        public const float CameraClipMaxCrouch = 1f;
        public const float CameraClipCrouchEaseRate = 0.5f;

        // ---- Pathfinding ----
        public const float DefaultPathCellSize = 0.5f;
        public const float DefaultPathSpan = 20f;
        public const int DefaultPathNodeCap = 9000;
        public const int MinPathNodeCap = 200;
        public const int MaxPathNodeCap = 50000;
        public const float MinPathCellSize = 0.1f;
        public const float MaxPathCellSize = 5f;
        public const float MinPathSpan = 2f;
        public const float MaxPathSpan = 100f;
        public const float PathMinSpan = 4f;
        // Max layers for per-query local grid (PathGridState). WorldMap auto-detects.
        public const int PathMaxLayers = 4;
        public const float PathClimbStep = 0.5f;
        public const int PathDoorCrossCost = 550;
        public const int PathNodeBudgetMin = 2048;
        public const int PathWaypointLimit = 100000;
        public const float PathRayHeightOffset = 0.75f;
        // Time budget for A* expansion (ms). On overrun, partial path is returned.
        public const float PathTimeBudgetMs = 8f;
        // Max A* node expansions (default). On overrun, partial path is returned.
        public const int PathMaxExpansions = 20000;

        // ---- Radar ----
        public const int DefaultRadarSize = 10;
        public const float DefaultRadarScale = 1.2f;
        public const int MinRadarSize = 3;
        public const int MaxRadarSize = 30;
        public const float MinRadarScale = 0.1f;
        public const float MaxRadarScale = 10f;

        // ---- Config clamping ----
        public const float MinThinkInterval = 0.05f;
        public const float MaxThinkInterval = 10f;
        public const int MinImageEvery = 1;
        public const int MaxImageHistory = 10;
        public const int MinMaxTokens = 64;
        public const int MaxMaxTokens = 32768;
        public const float MinTemperature = 0f;
        public const float MaxTemperature = 2f;
        public const float MinStepDelay = 0.05f;
        public const float MaxStepDelay = 5f;
        public const int MinMaxPlan = 1;
        public const int MaxMaxPlan = 8;
        public const int MinChatLogLines = 0;
        public const int MaxChatLogLines = 200;
        public const int MinVisionEvery = 1;
        public const int MinRayCount = 2;
        public const float MinRayRange = 1f;
        public const float MaxRayRange = 100f;
        public const int MinImageSize = 32;
        public const int MaxImageSize = 2048;
        public const int MinImageQuality = 1;
        public const int MaxImageQuality = 100;
        public const float MinCamNearClip = 0.01f;
        public const float MaxCamNearClip = 10f;
        public const float MinCamFarClip = 1f;
        public const float MaxCamFarClip = 500f;
        public const float MinCamForward = -2f;
        public const float MaxCamForward = 5f;
        public const float MinCommentTemp = 0f;
        public const float MaxCommentTemp = 2f;
        public const float MinAutoFindRange = 1f;
        public const float MaxAutoFindRange = 500f;
        public const float MinTurnRate = 10f;
        public const float MaxTurnRate = 1000f;
        public const float MinAccel = 0.5f;
        public const float MaxAccel = 50f;
        public const float MinDecel = 0.5f;
        public const float MaxDecel = 50f;
        public const float MinBrakeDist = 0.1f;
        public const float MaxBrakeDist = 20f;
        public const float MinHornyRate = 0f;
        public const float MaxHornyRate = 60f;
        public const float MinHornyBaseline = 0f;
        public const float MaxHornyBaseline = 1f;
        public const int MaxNPCs = 4;
        public const int MinNPCs = 1;
        public const int MaxVisMaxTokens = 500;
        public const int MinVisMaxTokens = 10;

        // ---- Vision ----
        public const float VisionHungTimeout = 120f;        // 2 min hung timeout
        public const int VisionMaxCaptionTokens = 80;
        public const int VisionFrameCleanupMax = 40;

        // ---- Horniness ----
        public const float HornyClimbThreshold = 0.01f;
        public const float HornyStimDriven = 0.15f;
        public const float HornyStimHigh = 0.8f;
        public const float HornyStimLow = 0.3f;
        public const float HornyPostClimax = 0.05f;

        // ---- Camera ----
        public const float CameraClipCheckOffset = 0.15f;
        public const float CameraClipManualCrouchWindow = 5f;
        public const float CameraClipManualCrouchWindowEase = 10f;
        public const float CameraClipCrouchStep = 0.15f;

        // ---- Perception ----
        public const float ProbeSurfaceTopStep = 0.15f;
        public const float ProbeSurfaceTopMax = 2.0f;
        public const float ProbeSurfaceTopResult = 2.5f;
        public const float ProbeSurfaceTopRadius = 0.09f;
        public const float ProbeSurfaceTopBaseOffset = 1f;
        public const float ProbeGroundRayY = 0.6f;
        public const float ProbeGroundRayDist = 10f;
        public const float ProbeGroundMaxDist = 0.5f;
        public const float ProbeKneeHeight = 0.3f;
        public const float ProbeKneeRange = 1.3f;
        public const float ProbeChestHeight = 0.9f;
        public const float ProbeHeadHeight = 1.6f;
        public const float ProbeLedgeAheadDist = 0.7f;
        public const float ProbeLedgeDownStart = 0.5f;
        public const float ProbeLedgeDownRange = 6f;
        public const float ProbeLedgeMinDrop = 0.5f;
        public const float ProbeWallProximityRange = 1.1f;
        public const float ProbeWallProximitySteps = 128f;
        public const float SpatialLayoutRange = 15f;
        public const int SpatialLayoutSteps = 16;
        public const float SpatialLayoutStepDeg = 22.5f;
        public const float SpatialLayoutOpenThreshold = 8f;
        public const int SpatialLayoutNearPasses = 3;
        public const float SpatialLayoutNearDist1 = 4f;
        public const float SpatialLayoutNearDist2 = 7f;
        public const float SpatialLayoutNearDist3 = 10f;
        public const int SpatialLayoutMaxNear = 3;
        public const int SpatialLayoutMaxChars = 320;

        // ---- Movement ----
        public const float MoveSpeedClamp = 8f;
        public const float MoveSpeedMin = -8f;
        public const float WalkBurstMinDur = 0.3f;
        public const float WalkBurstMaxDur = 12f;
        public const float PathReuseDistThreshold = 1.5f;
        public const float PathReuseTimeThreshold = 6f;
        public const float PathWalkLenMin = 1f;
        public const float PathWalkLenMax = 30f;
        public const float PathWalkLenDivisor = 1.5f;
        public const float NavTargetArriveDist = 0.8f;
        public const float GoToMaxReachBase = 30f;
        public const float GoToAtDefault = 1f;
        public const float GoToAtMax = 8f;
        public const float GoToAtMin = 0f;
        public const float GoToDistMin = 0.1f;
        public const float SurveyRangeMin = 2f;
        public const float SurveyRangeMax = 40f;
        public const float LookSweepMin = 30f;
        public const float LookSweepMax = 360f;
        public const float LookPitchClamp = 89f;
        public const float LookPitchMin = -89f;
        public const float LookPitchMax = 60f;
        public const float BigTurnTriggerDeg = 30f;
        public const float CrouchClamp = 1f;
        public const float InteractRange = 2.6f;
        public const float FindUsableRange = 60f;
        public const float AutoFindRangeDefault = 60f;
        public const float PossessAutoFindRangeDefault = 60f;

        // ---- Timing ----
        public const float LlmSleepMs = 4000;
        public const float LlmNoSceneSleepMs = 2000;
        public const float LlmNoBodySleepMs = 2000;
        public const float LlmMainReadySleepMs = 1000;
        public const float MainReadyTimeoutMs = 120000;
        public const float MainReadyTimeoutGoToMs = 8000;
        public const float MainReadyTimeoutPathMs = 8000;
        public const float MainReadyTimeoutPerceptionMs = 15000;
        public const float MainReadyTimeoutVisionMs = 8000;
        public const float MainReadyTimeoutEnsureBodyMs = 8000;
        public const float MainReadyTimeoutAskMs = 8000;
        public const float RestartDelayTotal = 2f;
        public const float RestartDelaySteps = 0.4f;
        public const float RestartDelayStepsCount = 5;
        public const float ConfigReloadLogCooldown = 20f;
        public const float GreetFirstCooldown = 6f;
        public const float GreetRepeatCooldown = 45f;
        public const float PlayerChatTimeout = 30f;
        public const float AmbientCooldown = 4f;
        public const float BumpCooldown = 0.6f;
        public const float DoorCooldown = 1.5f;
        public const float OwnershipTryCooldown = 3f;
        public const float ModelErrorCooldown = 20f;
        public const float ManualCrouchWindow = 5f;
        public const float ManualCrouchWindowEase = 10f;
        public const float ClipCrouchWindow = 5f;
        public const float ClipCrouchEaseWindow = 10f;

        // ---- Memory ----
        public const int MaxFacts = 24;
        public const int MaxHistory = 24;
        public const int MaxThoughtHistory = 24;
        public const int MaxChatLog = 200;
        public const int MaxPastImages = 10;
        public const int MaxReagentEvents = 6;
        public const int MaxNearby = 8;
        public const int MaxPlanSteps = 8;
        public const int MaxPlanStepsSmall = 4;
        public const int ModelErrorMaxTokens = 80;
        public const int MaxResponseSize = 1048576;
        public const int VisionMaxTokens = 80;

        // ---- Classification ----
        public const float ProbeSurfaceTopHeightThreshold = 1.35f;
        public const float ProbeSurfaceTopHeightMax = 2.5f;
        public const float ProbeGroundStepThreshold = 0.5f;
        public const float ProbeGroundSillThreshold = 1.35f;

        // ---- Model tier thresholds ----
        public const long ModelTierSmallMax = 3_000_000_000L;
        public const long ModelTierMediumMax = 15_000_000_000L;

        // ---- Pathfinding A* ----
        public const int AstarNodeBudgetMin = 2048;
        public const int AstarWaypointLimit = 100000;
        public const float AstarDoorCrossCost = 550f;
        public const float AstarClimbCostMultiplier = 20f;
        public const float AstarDiagCost = 1.41421356f;
        public const float AstarScale = 100f;

        // ---- Levenshtein ----
        public const int LevenshteinMaxDist = 2;

        // ---- ContextManager ----
        public const int ContextManagerMaxCompactionLevel = 5;
        public const float ContextManagerHighFillThreshold = 0.9f;
        public const float ContextManagerLowFillThreshold = 0.5f;
        public const float ContextManagerSwitchCooldown = 120f;
        public const int ContextManagerConsecutiveHighThreshold = 3;

        // ---- Token estimation ----
        public const int TokenEstimateSystemPrompt = 300;
        public const int TokenEstimatePerNearby = 200;
        public const int TokenEstimatePerFact = 50;
        public const int TokenEstimatePerHistory = 30;
        public const int TokenEstimatePerThought = 20;
        public const int TokenEstimatePerChatLine = 15;

        // ---- Player detection ----
        public const float PlayerSeenTimeInitial = -90f;

        // ---- Camera stereo ----
        public const float StereoHalfIPDMin = 0.001f;
    }
}
