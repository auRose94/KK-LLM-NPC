// Unit tests for the spectate feature's pure core (SpectateLogic): the
// agent-eats-player gate, the follow-camera pose math behind the eater, the
// frame-rate-independent smoothing factor, and the spectator HUD line.
// Run via run_tests.sh (suite "spectate").
using System;
using KKLLMNPC;

internal static class TestSpectate
{
    private static int _failed;

    private static void Eq(object expect, object got, string what)
    {
        if (!string.Equals(Convert.ToString(expect), Convert.ToString(got), StringComparison.Ordinal))
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected '" + expect + "' got '" + got + "'");
        }
    }

    private static void True(bool b, string what)
    {
        if (!b) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static void Near(double expect, double got, string what)
    {
        if (Math.Abs(expect - got) > 1e-3)
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected '" + expect + "' got '" + got + "'");
        }
    }

    public static int Main()
    {
        // ---- the agent-eat gate: every reason is reachable, ok = null ----
        Eq("disabled", SpectateLogic.AgentEatGate(false, true, true, false, 1f, 4f, false, false), "gate cfg off");
        Eq("no_body", SpectateLogic.AgentEatGate(true, false, true, false, 1f, 4f, false, false), "gate eater dead");
        Eq("no_player", SpectateLogic.AgentEatGate(true, true, false, false, 1f, 4f, false, false), "gate no player");
        Eq("self", SpectateLogic.AgentEatGate(true, true, true, true, 0f, 4f, false, false), "gate eater is player");
        Eq("mouth_full", SpectateLogic.AgentEatGate(true, true, true, false, 1f, 4f, true, false), "gate eater busy");
        Eq("already_inside", SpectateLogic.AgentEatGate(true, true, true, false, 1f, 4f, false, true), "gate already spectating");
        Eq("too_far", SpectateLogic.AgentEatGate(true, true, true, false, 5f, 4f, false, false), "gate too far");
        Eq(null, SpectateLogic.AgentEatGate(true, true, true, false, 4f, 4f, false, false), "gate ok at max dist");
        Eq(null, SpectateLogic.AgentEatGate(true, true, true, false, 0.09f, 6.25f, false, false), "gate ok close");
        // a bogus range guard: no reach configured → never allowed
        Eq("too_far", SpectateLogic.AgentEatGate(true, true, true, false, 1f, 0f, false, false), "gate zero range");

        // ---- azimuth: same fixed compass as sonar/addrs (N=0, E=90, S=180, W=270) ----
        Near(0, SpectateLogic.AzimuthDeg(0, 10), "azimuth north");
        Near(90, SpectateLogic.AzimuthDeg(10, 0), "azimuth east");
        Near(180, SpectateLogic.AzimuthDeg(0, -10), "azimuth south");
        Near(270, SpectateLogic.AzimuthDeg(-10, 0), "azimuth west");
        Near(45, SpectateLogic.AzimuthDeg(5, 5), "azimuth NE");
        Near(315, SpectateLogic.AzimuthDeg(-5, 5), "azimuth NW (wraps 0..360)");

        // ---- follow distance: size-scaled, clamped ----
        Near(2.2, SpectateLogic.FollowDistance(0f), "follow dist size 0 clamps to min");
        Near(2.5, SpectateLogic.FollowDistance(1f), "follow dist normal size");
        Near(2.2, SpectateLogic.FollowDistance(-1f), "follow dist negative size clamped");
        Near(12, SpectateLogic.FollowDistance(9999f), "follow dist huge clamps to max");
        True(SpectateLogic.FollowDistance(3f) > SpectateLogic.FollowDistance(1f), "follow dist monotonic in size");

        // ---- follow pose: behind the eater, above head plane, looking at them ----
        // facing north (yaw 0): the camera sits strictly BEHIND on -Z, eye level
        // raised; the look point is the head nudged up.
        var p = SpectateLogic.FollowPose(10f, 2f, 30f, 0.0, 2.5f);
        Near(10, p.Cx, "pose yaw0 cx");
        Near(30 - 2.5, p.Cz, "pose yaw0 cz behind");
        Near(2 + 0.4 + 0.35 * 2.5, p.Cy, "pose yaw0 cy over head");
        Near(10, p.Lx, "pose yaw0 look x");
        Near(2 + 0.15, p.Ly, "pose yaw0 look y");
        Near(30, p.Lz, "pose yaw0 look z");
        // facing east (yaw 90): behind = straight west.
        p = SpectateLogic.FollowPose(5f, 1f, 5f, 90.0, 3f);
        Near(5 - 3, p.Cx, "pose yaw90 cx west of head");
        Near(5, p.Cz, "pose yaw90 cz");
        // facing south (yaw 180): behind = north side.
        p = SpectateLogic.FollowPose(5f, 1f, 5f, 180.0, 3f);
        Near(5 + 3, p.Cz, "pose yaw180 cz north of head");
        // yaw 45: camera at head - dist*(sin45, cos45).
        p = SpectateLogic.FollowPose(0f, 0f, 0f, 45.0, 4f);
        Near(-4 * Math.Sin(Math.PI / 4), p.Cx, "pose yaw45 cx");
        Near(-4 * Math.Cos(Math.PI / 4), p.Cz, "pose yaw45 cz");

        // ---- smoothing: snap on rate<=0, exponential in dt, clamped ----
        Eq(1.0, (double)SpectateLogic.SmoothFactor(0f, 0.016f), "smooth rate 0 snaps");
        Near(0.0, SpectateLogic.SmoothFactor(8f, -1f), "smooth negative dt → 0");
        Near(1.0 - Math.Exp(-8.0 * 0.25), SpectateLogic.SmoothFactor(8f, 5f), "smooth dt clamps at 0.25");
        Near(1.0 - Math.Exp(-8.0 * 0.016), SpectateLogic.SmoothFactor(8f, 0.016f), "smooth exponential at 60fps");
        True(SpectateLogic.SmoothFactor(8f, 0.5f) >= SpectateLogic.SmoothFactor(8f, 0.01f), "smooth monotonic in dt");
        True(SpectateLogic.SmoothFactor(16f, 0.016f) > SpectateLogic.SmoothFactor(4f, 0.016f), "smooth monotonic in rate");

        // ---- HUD line: the spectating player's always-on status ----
        string hud = SpectateLogic.HudLine("Sylvie", "the egg is planted in soil");
        True(hud.Contains("Sylvie"), "hud names the eater");
        True(hud.Contains("planted in soil"), "hud carries the hatch promise");
        True(hud.Contains("swallow abort"), "hud names the escape command");
        hud = SpectateLogic.HudLine("Sylvie", null);
        True(hud.Contains("no hatch coming"), "hud explains Grow/Destroy have no hatch");
        hud = SpectateLogic.HudLine(null, "x");
        True(hud.Contains("someone"), "hud falls back to 'someone'");
        // the HUD never leaks a hatch promise when the effect has none
        string hudNoHatch = SpectateLogic.HudLine("Sylvie", null);
        True(!hudNoHatch.Contains("until"), "hud 'no hatch' variant promises nothing");

        // ---- first-person eye offset: the AI-vision convention, clamped ----
        Near(0.22, SpectateLogic.FpForwardOffset(0.22f), "fp offset passthrough");
        Near(0.22, SpectateLogic.FpForwardOffset(0f), "fp offset unset defaults");
        Near(0.22, SpectateLogic.FpForwardOffset(-1f), "fp offset unset (negative) defaults");
        Near(0.05, SpectateLogic.FpForwardOffset(0.01f), "fp offset tiny clamps to floor");
        Near(0.6, SpectateLogic.FpForwardOffset(9f), "fp offset ceiling");

        // ---- third-person boom clamp: walls pull the camera in ----
        Near(3.0, SpectateLogic.BoomClampDist(3f, 0f), "boom no hit keeps distance");
        Near(1.38, SpectateLogic.BoomClampDist(3f, 1.5f), "boom pulls to hit-0.12");
        Near(0.25, SpectateLogic.BoomClampDist(3f, 0.1f), "boom never inside the eater");
        Near(2.5, SpectateLogic.BoomClampDist(2.5f, 99f), "boom beyond distance clamps to want");

        // ---- causes are distinct (release paths log them) ----
        True(SpectateLogic.CauseHatch != SpectateLogic.CauseAbort
            && SpectateLogic.CauseAbort != SpectateLogic.CauseEaterLost
            && SpectateLogic.CauseEaterLost != SpectateLogic.CauseSceneLeave
            && SpectateLogic.CauseSceneLeave != SpectateLogic.CauseRestart,
            "cause codes distinct");

        if (_failed > 0)
        {
            Console.WriteLine("TestSpectate: " + _failed + " FAILED");
            return 1;
        }
        Console.WriteLine("TestSpectate: all passed");
        return 0;
    }
}