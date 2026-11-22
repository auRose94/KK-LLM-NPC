// Unit tests for ContactCore — the honest physical-contact decision behind the
// 'touch' perception field. The regression it pins (Rose's field report
// 2026-11-21): the swallow system rides PenetrationTech ("victim as a large
// penis"), so a body that had just EATEN someone read IsPenetrated()==true and
// 'touch' reported "being_penetrated" — the model believed it was having sex
// with its meal. The decision must name vore vore, FIRST, and reserve sex
// vocabulary for non-meal penetrations.
// Run via run_tests.sh (suite "contact").
using System;
using KKLLMNPC;

internal static class TestContact
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

    private static void Has(string got, string needle, string what)
    {
        if (got == null || !got.Contains(needle))
        {
            _failed++;
            Console.WriteLine("FAIL " + what + ": '" + got + "' lacks '" + needle + "'");
        }
    }

    public static void Main()
    {
        // ---- THE regression: just ate someone (guest present, the labeled insert
        // registered in _pen) must NEVER read as sex ----
        string ate = ContactCore.ContactPhrase(false, false, false, true, true, false, false);
        Has(ate, "carrying_a_guest", "ate someone → guest, not penetrated");
        Has(ate, "VORE INSERT", "ate someone → insert named vore");
        Has(ate, "NOT sex", "ate someone → explicitly not sex");
        True(!ate.Contains("being_penetrated"), "ate someone → NO 'being_penetrated' anywhere");
        // every mention of sex in the phrase must be the disclaimer ("NOT sex")
        bool bareSex = false;
        for (int i = ate.IndexOf("sex", StringComparison.Ordinal); i >= 0 && i >= 4; i = ate.IndexOf("sex", i + 1, StringComparison.Ordinal))
            if (!ate.Substring(i - 4, 4).Equals("NOT ", StringComparison.Ordinal)) bareSex = true;
        True(!bareSex, "ate someone → every 'sex' mention is the NOT-sex disclaimer");

        // Guest WITHOUT the insert visible yet (still pulling in / text not ready)
        string guest = ContactCore.ContactPhrase(false, false, false, true, false, false, false);
        Has(guest, "carrying_a_guest", "guest alone → carrying_a_guest");
        Has(guest, "vore", "guest alone → labeled vore");
        True(!guest.Contains("being_penetrated"), "guest alone → no sex phrase");

        // The insert visible BEFORE the belly text (timing window)
        string sliding = ContactCore.ContactPhrase(false, false, false, false, true, false, false);
        Has(sliding, "vore_insert_sliding", "insert-only → vore_insert_sliding");
        Has(sliding, "NOT sex", "insert-only → explicitly not sex");
        True(!sliding.Contains("being_penetrated"), "insert-only → no sex phrase");

        // ---- real sex still reads as real sex ----
        Eq("being_penetrated",
            ContactCore.ContactPhrase(false, true, false, false, false, false, false),
            "real penetration alone → being_penetrated");
        Eq("penetrating_something",
            ContactCore.ContactPhrase(false, false, true, false, false, false, false),
            "real dick-inside alone → penetrating_something");

        // Real sex COEXISTING with a swallowed guest: the sex phrase wins (it's the
        // interactive one) but must not credit the guest as the partner.
        string both = ContactCore.ContactPhrase(false, true, false, true, true, false, false);
        Has(both, "being_penetrated", "sex + guest → still being_penetrated");
        Has(both, "NOT the partner", "sex + guest → guest disclaimed");
        string bothTop = ContactCore.ContactPhrase(false, false, true, true, false, false, false);
        Has(bothTop, "penetrating_something", "topping + guest → still penetrating_something");
        Has(bothTop, "NOT involved", "topping + guest → guest disclaimed");

        // ---- victim side ----
        Eq("inside_a_stomach",
            ContactCore.ContactPhrase(true, false, false, false, false, false, false),
            "swallowed → inside_a_stomach");
        // Being the meal outranks everything, even a (nonsense) sex flag.
        Eq("inside_a_stomach",
            ContactCore.ContactPhrase(true, true, true, true, true, false, true),
            "swallowed outranks all flags");

        // Victim-as-shaft: our body registered as the meal's penetrator inside the
        // eater (pre-Swallowed-flag window) — meal machinery, never our dick.
        string shaft = ContactCore.ContactPhrase(false, false, false, false, false, true, false);
        Has(shaft, "meal_machinery", "victim-as-shaft → meal_machinery");
        Has(shaft, "NOT sex", "victim-as-shaft → explicitly not sex");
        True(!shaft.Contains("penetrating_something"), "victim-as-shaft → no sex phrase");

        // ---- plain states unchanged ----
        Eq("mounted_in_station",
            ContactCore.ContactPhrase(false, false, false, false, false, false, true),
            "station alone → mounted_in_station");
        Eq(ContactCore.TouchNone,
            ContactCore.ContactPhrase(false, false, false, false, false, false, false),
            "nothing → none");
        Eq("none — nothing is touching you", ContactCore.TouchNone, "TouchNone wording");

        // ---- label recognizers (the two prefixes that separate meal from lover) ----
        True(ContactCore.IsVoreInsertName("vore-insert:Yara"), "insert label recognized");
        True(ContactCore.IsVoreInsertName("vore-insert:someone with spaces"), "insert label with spaces");
        True(!ContactCore.IsVoreInsertName("KDEFuta-model penis"), "real shaft model NOT an insert");
        True(!ContactCore.IsVoreInsertName(null), "null name not an insert");
        True(!ContactCore.IsVoreInsertName("vore"), "bare 'vore' is not the insert label");

        True(ContactCore.IsVorePenGoName("vore:Yara"), "virtual pen go-name recognized");
        True(ContactCore.IsVorePenGoName("vore:Bobbie"), "virtual pen go-name other meal");
        True(!ContactCore.IsVorePenGoName("vore-insert:Yara"), "insert ENTRY name is not a go-name");
        True(!ContactCore.IsVorePenGoName("Penis_A"), "ordinary go-name not a virtual pen");
        True(!ContactCore.IsVorePenGoName(null), "null go-name");

        if (_failed == 0) Console.WriteLine("test_contact: all OK");
        else Console.WriteLine("test_contact: " + _failed + " failure(s)");
        Environment.Exit(_failed);
    }
}
