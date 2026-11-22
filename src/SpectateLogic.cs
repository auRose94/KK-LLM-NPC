// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// SpectateLogic — the pure (no-Unity, no-Photon) core of the spectate feature: the
// agent-eats-player gate, the follow-camera pose math, frame-rate-independent
// smoothing, and the spectator HUD line. Kept free of game types so
// tests/test_spectate.cs can compile it alone; src/Spectate.cs holds the runtime
// manager around it.
using System;

namespace KKLLMNPC
{
    internal static class SpectateLogic
    {
        // Reach of the eater's eat_player act (config-tunable, clamped 1–8 m).
        internal const float DefaultEatRange = 2.5f;

        // Machine-readable agent-eat gate. Returns null when the act may proceed,
        // else one of: disabled, no_body, no_player, self, mouth_full,
        // already_inside, too_far — each directly readable in the tool result.
        public static string AgentEatGate(bool cfgOn, bool eaterAlive, bool playerFound, bool eaterIsPlayer,
            float distSq, float maxDistSq, bool eaterBusy, bool alreadySpectated)
        {
            if (!cfgOn) return "disabled";
            if (!eaterAlive) return "no_body";
            if (!playerFound) return "no_player";
            if (eaterIsPlayer) return "self";
            if (eaterBusy) return "mouth_full";
            if (alreadySpectated) return "already_inside";
            if (maxDistSq <= 0f) return "too_far";
            if (distSq > maxDistSq) return "too_far";
            return null;
        }

        // World azimuth of a facing, in degrees: north (+Z) = 0, east (+X) = 90 —
        // the same fixed compass the sonar/addr system uses.
        public static double AzimuthDeg(double dx, double dz)
        {
            double a = Math.Atan2(dx, dz) * 180.0 / Math.PI;
            if (a < 0) a += 360.0;
            if (a >= 360.0) a -= 360.0;
            return a;
        }

        // Follow-orbit distance behind the eater, scaled by its size (the game's
        // size inflate gene; 1.0 ≈ a normal kobold). Big eaters get bigger orbits.
        public static float FollowDistance(float eaterSize)
        {
            float s = eaterSize;
            if (s < 0f) s = 0f;
            float d = 1.6f + s * 0.9f;
            if (d < 2.2f) d = 2.2f;
            if (d > 12f) d = 12f;
            return d;
        }

        // First-person eye offset in front of the eater's head bone — reuses the
        // AI vision cameras' placement convention ([Senses] CameraForward, default
        // 0.22 in front, +0.06 up, tuned for snouts), so the spectator sees the
        // same view the eaten-agent's own screenshots see. Clamped to sane ranges.
        public static float FpForwardOffset(float configured)
        {
            float v = configured;
            if (v <= 0f) v = 0.22f;
            if (v < 0.05f) v = 0.05f;
            if (v > 0.6f) v = 0.6f;
            return v;
        }

        // Third-person boom clamp: when a linecast from head to the wanted camera
        // spot hits geometry, pull the camera in to just in front of the hit. A
        // hitDist ≤ 0 (no hit) leaves the wanted distance; the result never comes
        // closer than 0.25m (that would be inside the eater).
        public static float BoomClampDist(float wantDist, float hitDist)
        {
            if (hitDist <= 0f) return wantDist;
            float d = hitDist - 0.12f;
            if (d < 0.25f) d = 0.25f;
            if (d > wantDist) d = wantDist;
            return d;
        }

        // Follow pose result: camera position (Cx..) + the point it looks at (Lx..).
        public sealed class Pose
        {
            public double Cx, Cy, Cz;
            public double Lx, Ly, Lz;
        }

        // Camera orbit behind the eater at `dist`, above the head plane so it looks
        // slightly down at them: position = head - facing*dist + up*(0.4 + 0.35*dist),
        // lookAt = head + up*0.15. Yaw is the eater's AzimuthDeg.
        public static Pose FollowPose(float hx, float hy, float hz, double yawDeg, float dist)
        {
            double rad = yawDeg * Math.PI / 180.0;
            double fx = Math.Sin(rad), fz = Math.Cos(rad);
            double cx = hx - fx * dist;
            double cz = hz - fz * dist;
            double cy = hy + 0.4 + 0.35 * dist;
            return new Pose { Cx = cx, Cy = cy, Cz = cz, Lx = hx, Ly = hy + 0.15, Lz = hz };
        }

        // Frame-rate-independent lerp weight: rate ≈ 8 settles in a few hundred ms;
        // rate ≤ 0 snaps (used for the first spectate frame so there's no slide).
        public static float SmoothFactor(float rate, float unscaledDt)
        {
            if (unscaledDt < 0f) unscaledDt = 0f;
            if (unscaledDt > 0.25f) unscaledDt = 0.25f;
            if (rate <= 0f) return 1f;
            float f = 1f - (float)Math.Exp(-rate * unscaledDt);
            if (f < 0f) f = 0f;
            if (f > 1f) f = 1f;
            return f;
        }

        // End-of-spectate causes (kept as plain ints so the pure gate never needs an
        // enum the runtime can't share).
        public const int CauseHatch = 0;      // the egg was planted and hatched them
        public const int CauseAbort = 1;      // the swallow-abort chat command
        public const int CauseEaterLost = 2;  // the eater's body was destroyed mid-watch
        public const int CauseSceneLeave = 3; // back to menu / scene teardown (bodies die anyway)
        public const int CauseRestart = 4;    // DLL hot-reload restore (same scene)

        // The one-line status the spectating player always sees on screen (HUD).
        // hatchHint = what the planted egg promises them; null = there is NO hatch
        // (Grow/Destroy) — the line must then say so plainly and never promise one.
        // struggle01 (0..1, or negative = omit) drives the ESCAPE MINI-GAME meter
        // (2026-11-16): the chat-abort command is gone — a swallowed player MASHES
        // SPACE to struggle free, and the bar shows how close the stomach is to
        // heaving them back up.
        // The HUD's English templates (the L10n keys — see docs/L10N.md).
        public const string InsideHudTemplate = "you are inside {0} — watching";
        public const string NoHatchHudTemplate = "(no hatch coming)";
        public const string UntilHudTemplate = "until {0}";
        public const string MashHudTemplate = " — MASH SPACE to struggle free [{0}] {1}%";

        public static string HudLine(string eaterLabel, string hatchHint)
        {
            return HudLine(eaterLabel, hatchHint, -1f);
        }

        public static string HudLine(string eaterLabel, string hatchHint, float struggle01)
        {
            // English (the default translator is the identity — English text IS
            // the key, so a null t renders plain English; L10n passes the real one).
            return HudLine(eaterLabel, hatchHint, struggle01, null);
        }

        // LOCALIZED HUD (v1.13.0): t(key) resolves through the lang files (the
        // key IS the English template — {0}/{1} placeholders survive translation).
        public static string HudLine(string eaterLabel, string hatchHint, float struggle01, Func<string, string> t)
        {
            if (t == null) t = k => k;
            string who = string.IsNullOrEmpty(eaterLabel) ? "someone" : eaterLabel;
            string b = L10nCore.Fmt(t(InsideHudTemplate), who);
            if (string.IsNullOrEmpty(hatchHint)) b += " " + t(NoHatchHudTemplate);
            else b += " " + L10nCore.Fmt(t(UntilHudTemplate), hatchHint);
            if (struggle01 >= 0f)
            {
                int filled = (int)Math.Round(struggle01 * 8f);
                if (filled < 0) filled = 0;
                if (filled > 8) filled = 8;
                var bar = new System.Text.StringBuilder();
                for (int i = 0; i < 8; i++) bar.Append(i < filled ? '#' : '-');
                b += L10nCore.Fmt(t(MashHudTemplate), bar.ToString(),
                    Math.Round(struggle01 * 100f).ToString());
            }
            return b;
        }

        // POINT (v1.11.0, Rose's design): a swallowed guest clicks something in
        // the world they're watching through their pred's eyes — the click turns
        // into this line for the PRED's agent: what it is, bearing relative to
        // the pred's own facing (they can just turn), distance, and the world
        // coords (the scene gazetteer / go_to can route to it). Pure + unit-tested.
        public static string PointText(string hitName, double hx, double hy, double hz,
            double ax, double ay, double az, double yawDeg)
        {
            string who = string.IsNullOrEmpty(hitName) ? "something" : hitName;
            double dx = hx - ax, dz = hz - az, dy = hy - ay;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double rel = AzimuthDeg(dx, dz) - yawDeg;
            while (rel < -180f) rel += 360f;
            while (rel > 180f) rel -= 360f;
            string side = Math.Abs(rel) < 22.5 ? "dead ahead"
                : rel < 0 ? (rel < -67.5 ? "behind-left" : rel < -22.5 ? "to your left" : "ahead-left")
                : (rel > 67.5 ? "behind-right" : rel > 22.5 ? "to your right" : "ahead-right");
            string heightWord = Math.Abs(dy) < 1.2 ? ""
                : (dy > 0 ? " (above you)" : " (below you)");
            return "your guest is POINTING at " + who + " — " + side + ", "
                + dist.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "m"
                + heightWord
                + " (world x:" + hx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " y:" + hy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " z:" + hz.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")"
                + " — they want you to notice it";
        }
    }
}