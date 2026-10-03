// Unit tests for the pure Compass helpers (north = +Z, east = +X).
// Run via run_tests.sh (suite "compass").
using System;
using KKLLMNPC;

internal static class TestCompass
{
    private static int _failed;

    private static void Eq(object expect, object got, string what)
    {
        bool ok = string.Equals(Convert.ToString(expect), Convert.ToString(got), StringComparison.Ordinal);
        if (!ok)
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": expected " + expect + " got " + got);
        }
    }

    private static void IsTrue(bool ok, string what)
    {
        if (!ok) { _failed++; Console.WriteLine("FAIL " + what); }
    }

    public static void Main()
    {
        // Wrap360
        Eq(0f, Compass.Wrap360(0f), "wrap 0");
        Eq(90f, Compass.Wrap360(450f), "wrap 450");
        Eq(270f, Compass.Wrap360(-90f), "wrap -90");
        Eq(359f, Compass.Wrap360(-1f), "wrap -1");

        // 8-way names on yaw angles (0=+Z=N, 90=+X=E)
        Eq("N", Compass.NameOf(0f), "yaw 0 -> N");
        Eq("E", Compass.NameOf(90f), "yaw 90 -> E");
        Eq("S", Compass.NameOf(180f), "yaw 180 -> S");
        Eq("W", Compass.NameOf(270f), "yaw 270 -> W");
        Eq("NE", Compass.NameOf(45f), "yaw 45 -> NE");
        Eq("SW", Compass.NameOf(225f), "yaw 225 -> SW");
        Eq("NW", Compass.NameOf(315f), "yaw 315 -> NW");
        Eq("W", Compass.NameOf(-90f), "yaw -90 -> W");
        Eq("N", Compass.NameOf(359f), "yaw 359 -> N");
        Eq("N", Compass.NameOf(22f), "yaw 22 -> N");
        Eq("NE", Compass.NameOf(22.5f), "yaw 22.5 midpoint -> NE");
        Eq("N", Compass.NameOf(-22f), "yaw -22 -> N");

        // offsets (dx, dz)
        Eq("N", Compass.NameOfOffset(0f, 1f), "offset +Z -> N");
        Eq("E", Compass.NameOfOffset(1f, 0f), "offset +X -> E");
        Eq("SW", Compass.NameOfOffset(-1f, -1f), "offset -X -Z -> SW");
        Eq("here", Compass.NameOfOffset(0f, 0f), "offset zero -> here");
        Eq("N", Compass.NameOfOffset(0.001f, 2f), "offset mostly +Z -> N");
        Eq("E", Compass.NameOfOffset(3f, -0.2f), "offset mostly +X -> E");
        Eq("SE", Compass.NameOfOffset(1f, -1f), "offset X-Z diagonal -> SE");

        // glyphs
        IsTrue(Compass.GlyphOf(0f) == '^', "N glyph ^");
        IsTrue(Compass.GlyphOf(90f) == '>', "E glyph >");
        IsTrue(Compass.GlyphOf(180f) == 'v', "S glyph v");
        IsTrue(Compass.GlyphOf(270f) == '<', "W glyph <");
        IsTrue(Compass.GlyphOf(45f) == '/', "NE glyph /");
        IsTrue(Compass.GlyphOf(135f) == '\\', "SE glyph \\");
        IsTrue(Compass.GlyphOf(225f) == '/', "SW glyph /");
        IsTrue(Compass.GlyphOf(315f) == '\\', "NW glyph \\");

        // step offsets: consistent with the grid they draw (top=N -> dz=+1 is up)
        int dx, dz;
        Compass.OffsetOf(0, out dx, out dz); IsTrue(dx == 0 && dz == 1, "N step is up on grid");
        Compass.OffsetOf(2, out dx, out dz); IsTrue(dx == 1 && dz == 0, "E step is right on grid");
        Compass.OffsetOf(4, out dx, out dz); IsTrue(dx == 0 && dz == -1, "S step is down on grid");
        Compass.OffsetOf(6, out dx, out dz); IsTrue(dx == -1 && dz == 0, "W step is left on grid");
        Compass.OffsetOf(-1, out dx, out dz); IsTrue(dx == -1 && dz == 1, "NW step via negative idx");

        // precise facing text
        Eq("N (0°)", Compass.FacingText(0f), "facing 0");
        Eq("ENE (78°)", Compass.FacingText(78f), "facing 78 -> ENE");
        Eq("WSW (248°)", Compass.FacingText(248f), "facing 248 -> WSW");
        Eq("S (180°)", Compass.FacingText(-180f), "facing -180 -> S");

        Console.WriteLine(_failed == 0 ? "compass: all passed" : "compass: " + _failed + " FAILURES");
        Environment.Exit(_failed);
    }
}