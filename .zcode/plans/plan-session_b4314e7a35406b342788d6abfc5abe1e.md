Two features on top of the existing decision-model branch:

A) Vore-type buttons (no clickable buttons exist today — only F7/F8 hotkeys, free chat, and generic config text-fields):
- Settings tab: a "Vore" card with discrete buttons for operator master globals — Destination (OFF/Mouth/Anal/Pussy/Dick/Nipple) and Fate (Egg/BellyEgg/Grow/Destroy), posting POST /api/config with the enum label (same path the config field already uses).
- Kobolds panel: a per-agent "my vore" Target + Fate row posting POST /api/instance {name, action:"vore_target"/"vore_mode"}; new DoInstance cases delegate to existing ToolVoreTarget/ToolVoreMode on the main thread.

B) Non-consent detection + warn + steer SFW-only:
- New pure module src/ConsentDetector.cs: consent.Analyze(reply) → {nonConsenting, reasons, score} using a tight refusal/non-consent/personalized-hurt phrase set with severity scoring (kept narrow to avoid false positives on normal gameplay refusals).
- Per-instance rolling counter in NPCInstance; flagging a model logs a one-time warning and sets _nonConsenting.
- When flagged and SFWOnly config is on, append a concise SFW directive to the next turn's prompt via the existing advisory guidance channel so non-consenting models get SFW-only roleplay instead of being driven NSFW.
- Operator UI: a standing Consent info card in settings (adult game; some models decline sex/kink and get steered SFW; you can reset) + a live consent warning area + a badge on each flagged instance card + a per-instance "reset consent flag" action.
- Config (NonConsent section): Enabled (on), SFWOnly (on), FlagThreshold.

Tests: tests/test_consent.cs + re-run SwallowLogic suite, wired into run_tests.sh; build clean. Marker-hygiene respected in the detector vocabulary.