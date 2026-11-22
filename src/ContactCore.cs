// Written by @auRose94 (https://github.com/auRose94) under MIT license. See LICENSE.txt in this repo for details.
// ContactCore — the honest physical-contact decision, pure C# (unit-tested).
//
// The 'touch' perception field must answer ONE question truthfully: is anything
// physically touching this body right now, and what? The bug it fixes (2026-11-21,
// Rose's field report): the swallow system rides the game's PenetrationTech —
// the "victim as a large penis" insert registers in _pen like any penetrator —
// so a body that had just EATEN someone read IsPenetrated()==true and ContactText
// reported "being_penetrated". The model believed it was having sex with its
// meal. The ordering here is the fix: VORE contact is named vore, first; only a
// penetration that is NOT the meal may say "being_penetrated". Same honesty for
// the victim side: a body riding the eater as the meal-shaft is meal machinery,
// never "penetrating_something".
//
// Build: mcs -target:library -out:/tmp/t_contact.dll tests/test_contact.cs src/ContactCore.cs && mono /tmp/t_contact.exe
using System;

namespace KKLLMNPC
{
    public static class ContactCore
    {
        public const string TouchNone = "none — nothing is touching you";

        // A _pen entry name carrying the swallow system's meal label ("vore-insert:Name",
        // set in Body.OnPenetrationTick) is the swallowed MEAL acting as the shaft —
        // it widens the pred's orifice via PenetrationTech and can feel sexual, but
        // it is never a partner and never sex signaling.
        public static bool IsVoreInsertName(string penName)
        {
            return penName != null && penName.StartsWith("vore-insert:", StringComparison.Ordinal);
        }

        // A penetrator GO name prefixed by the swallow system ("vore:<meal>", set in
        // Swallow.SpawnVirtualPen) is the meal-shaft itself — its listener events on
        // the victim's body are the meal being digested, not the victim's dick.
        public static bool IsVorePenGoName(string goName)
        {
            return goName != null && goName.StartsWith("vore:", StringComparison.Ordinal);
        }

        // The touch phrase. Inputs are the body's live flags (main-thread reads in
        // Body.ContactText); the ORDER is the honesty:
        //   swallowed       — you are the meal inside someone;
        //   realPenetrated  — a NON-meal penetrator is in one of your holes (real sex);
        //   realDickInside  — your dick is in a non-meal hole (real sex);
        //   bellyGuest      — a whole person is inside you (vore);
        //   voreInsertOnly  — the meal-shaft is your only "penetration" (vore);
        //   voreDickInside  — your body is riding the meal-shaft inside an eater (vore);
        //   inStation       — mounted in an animation station.
        // Real sex outranks the belly guest (a partner + a meal can coexist — the
        // phrase names both), and vore NEVER borrows sex vocabulary when alone.
        public static string ContactPhrase(bool swallowed, bool realPenetrated, bool realDickInside,
            bool bellyGuest, bool voreInsertOnly, bool voreDickInside, bool inStation)
        {
            if (swallowed) return "inside_a_stomach";
            if (realPenetrated)
                return bellyGuest
                    ? "being_penetrated (a real partner — the swallowed guest inside you is NOT the partner)"
                    : "being_penetrated";
            if (realDickInside)
                return bellyGuest
                    ? "penetrating_something (a real hole — the swallowed guest inside you is NOT involved)"
                    : "penetrating_something";
            if (bellyGuest)
                return voreInsertOnly
                    ? "carrying_a_guest — a VORE INSERT: the swallowed meal's body is widening you as it slides (vore, NOT sex)"
                    : "carrying_a_guest (vore — a whole person is inside you; NOT sex)";
            if (voreInsertOnly)
                return "vore_insert_sliding — your swallowed meal is entering you as a body-shaft (vore, NOT sex)";
            if (voreDickInside)
                return "meal_machinery — your body is acting as the meal's shaft inside an eater (vore, NOT sex)";
            if (inStation) return "mounted_in_station";
            return TouchNone;
        }
    }
}
