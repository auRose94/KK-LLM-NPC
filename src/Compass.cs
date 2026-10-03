// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// World-fixed compass helpers. Pure C# (no Unity/BepInEx deps) so it is unit-testable:
//   mcs -target:exe -out:/tmp/t_cm.exe tests/test_compass.cs src/Compass.cs && mono /tmp/t_cm.exe
//
// The mod defines ONE compass convention used by the sonar map, station bearings and
// facing readouts: north = world +Z (Unity forward), east = world +X (Unity right).
// Bearings are WORLD angles so they never rotate when the body turns — the fix for
// agents getting disoriented by facing-relative outputs that slide around as they
// look around ("the bed was ahead, now it's behind").
using System;
using System.Collections.Generic;

namespace KKLLMNPC
{
    internal static class Compass
    {
        // Index 0..7 = N, NE, E, SE, S, SW, W, NW (angle wrapped to [0,360), /45).
        internal static readonly string[] Names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        // ASCII arrows for the sonar grid, indexed like Names. Diagonals share the
        // slash glyphs (NE/SW = '/', SE/NW = '\') — the glyph's CELL carries the
        // direction; the char just marks "this way is where you face".
        internal static readonly char[] Glyphs = { '^', '/', '>', '\\', 'v', '/', '<', '\\' };

        // Normalize any angle into [0,360).
        internal static float Wrap360(float deg)
        {
            float d = deg % 360f;
            if (d < 0f) d += 360f;
            return d;
        }

        // Compass index of a world angle (0 = north). 22.5° exact midpoints round up.
        internal static int IndexOf(float angDeg)
        {
            double v = Math.Round(Wrap360(angDeg) / 45.0, MidpointRounding.AwayFromZero);
            return ((int)v) % 8;
        }

        // Compass name ("N", "SW") of a world angle. World angles use the yaw
        // convention: 0 = +Z (north), 90 = +X (east), positive = clockwise viewed
        // from above (Unity's left-handed yaw).
        internal static string NameOf(float angDeg)
        {
            return Names[IndexOf(angDeg)];
        }

        // World yaw angle (deg, 0=+Z) of an x/z offset; 0 magnitude names "here".
        internal static float AngleOf(float dx, float dz)
        {
            if (Math.Abs(dx) < 1e-5f && Math.Abs(dz) < 1e-5f) return 0f;
            return (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        }

        // Compass name of an offset direction ("SW" for dx=-1, dz=-1).
        internal static string NameOfOffset(float dx, float dz)
        {
            if (Math.Abs(dx) < 1e-5f && Math.Abs(dz) < 1e-5f) return "here";
            return NameOf(AngleOf(dx, dz));
        }

        // ASCII arrow glyph for a facing angle ('^' for north, '>' for east ...).
        internal static char GlyphOf(float angDeg)
        {
            return Glyphs[IndexOf(angDeg)];
        }

        // One-step grid offsets (dx, dz) for a compass index — how to step one cell
        // toward that bearing on a north-up grid (top = north, right = east).
        internal static void OffsetOf(int idx, out int dx, out int dz)
        {
            switch (((idx % 8) + 8) % 8)
            {
                case 0: dx = 0; dz = 1; break;   // N
                case 1: dx = 1; dz = 1; break;   // NE
                case 2: dx = 1; dz = 0; break;   // E
                case 3: dx = 1; dz = -1; break;  // SE
                case 4: dx = 0; dz = -1; break;  // S
                case 5: dx = -1; dz = -1; break; // SW
                case 6: dx = -1; dz = 0; break;  // W
                default: dx = -1; dz = 1; break; // NW
            }
        }

        // Human-facing text: "ENE (78°)" style — name at 16-point resolution plus
        // the exact degree. 16 names so east-north-east doesn't collapse to E.
        private static readonly string[] Names16 =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
        };
        internal static string PreciseNameOf(float angDeg)
        {
            double v = Math.Round(Wrap360(angDeg) / 22.5, MidpointRounding.AwayFromZero);
            return Names16[((int)v) % 16];
        }

        // Full facing label: "ENE (78°)".
        internal static string FacingText(float angDeg)
        {
            return PreciseNameOf(angDeg) + " (" + Math.Round(Wrap360(angDeg)) + "°)";
        }
    }
}