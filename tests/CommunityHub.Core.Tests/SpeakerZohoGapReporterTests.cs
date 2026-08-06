using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §575/§586/§623 — the speaker gap report: what CEH holds that Zoho Backstage does NOT.
///
/// <para>Operator 2026-07-28: <i>"i am also missing a speaker mail, which shows the gaps between ceh
/// and zoho. for example per larsen has set country + skills"</i>. A MAIL is the only option, not a
/// fallback: the v3 speakers API is create-only — PUT, PATCH and POST to <c>/speakers/{id}</c> all
/// answer <c>404 "Please provide valid method"</c> (§584).</para>
///
/// <para><b>The rule these tests exist to protect is §582:</b> only compare what Zoho actually
/// returns. Diffing an unreadable field manufactures a PERMANENT false gap that no action can close —
/// exactly the §594 "Tags missing" mail he received for tags he had already entered. A false gap is
/// worse than no gap, because he acts on it.</para>
/// </summary>
public class SpeakerZohoGapReporterTests
{
    private static BackstageSpeaker Zoho(
        string? company = null, string? tagline = null, string? bio = null,
        string? skills = null, string? linkedIn = null, string? twitter = null,
        string? country = null) =>
        new("bs-1", "Per Larsen", tagline, bio, Country: country, linkedIn, twitter,
            Company: company, Skills: skills, Email: "per.larsen@microsoft.com");

    // ---------- the operator's own example ----------

    [Fact]
    public void Per_Larsens_missing_SKILLS_is_reported()
    {
        // His live case: CEH holds the Microsoft accreditation, Zoho's Skills box is empty.
        var ceh = new SpeakerProfile { Accreditation = "Microsoft Employee, Microsoft Expert" };

        var gaps = SpeakerZohoGapReporter.GapsFor(ceh, Zoho(skills: null));

        Assert.Contains(gaps, g => g.Contains("Skills") && g.Contains("Microsoft Employee"));
    }

    [Fact]
    public void Skills_already_in_Zoho_are_NOT_reported()
    {
        var ceh = new SpeakerProfile { Accreditation = "Microsoft Employee" };

        var gaps = SpeakerZohoGapReporter.GapsFor(ceh, Zoho(skills: "Microsoft Employee"));

        Assert.DoesNotContain(gaps, g => g.Contains("Skills"));
    }

    // ---------- 🔒 the §582 limitation ----------

    [Fact]
    public void COUNTRY_is_never_reported_as_a_detected_gap_when_CEH_has_none()
    {
        // Zoho returns NO country field at all (verified live 2026-07-29 on both the list and the
        // per-id record), so we can never know whether it is set. With nothing in CEH there is
        // nothing useful to say, and inventing a chore would be the §594 false-gap mistake.
        var gaps = SpeakerZohoGapReporter.GapsFor(new SpeakerProfile(), Zoho());

        Assert.DoesNotContain(gaps, g => g.Contains("Country"));
    }

    /// <summary>
    /// ✅ §893 — country IS readable; the §623 "not returned" finding was an artefact of a sample in
    /// which nobody had set one. Zoho OMITS an unset field, so an empty roster looked like a missing
    /// feature. Measured 2026-08-06: 9 of 25 speakers carry the key.
    /// </summary>
    [Fact]
    public void A_country_MISSING_from_Backstage_is_a_real_gap_now()
    {
        var ceh = new SpeakerProfile { Country = "DK" };

        var gap = Assert.Single(SpeakerZohoGapReporter.GapsFor(ceh, Zoho()), g => g.Contains("Country"));

        Assert.Contains("DK", gap);
        Assert.Contains("not set in Backstage", gap);
        // The old wording was an unverifiable chore he could only silence with a tick-box.
        Assert.DoesNotContain("does not report this field", gap);
    }

    /// <summary>
    /// 🔑 The operator's actual complaint: *"i now get a daily mail saying to check country. but
    /// that is wrong as we have new knowledge now"*. A country that IS set must produce silence.
    /// </summary>
    [Fact]
    public void A_country_that_matches_Backstage_is_SILENT()
    {
        var ceh = new SpeakerProfile { Country = "DK" };

        Assert.DoesNotContain(
            SpeakerZohoGapReporter.GapsFor(ceh, Zoho(country: "DK")), g => g.Contains("Country"));
    }

    /// <summary>
    /// ⚠️ Zoho returns an ISO-2 CODE ("DK"); CEH may hold a display name ("Denmark"). A plain string
    /// compare would mark every speaker as differing forever — §594's "Tags missing" mail again.
    /// </summary>
    [Theory]
    [InlineData("Denmark", "DK")]
    [InlineData("denmark", "dk")]
    [InlineData("Germany", "DE")]
    [InlineData("United Kingdom", "GB")]
    public void A_display_name_and_its_ISO_code_are_the_same_country(string ceh, string zoho)
        => Assert.True(SpeakerZohoGapReporter.CountryMatches(ceh, zoho));

    [Fact]
    public void A_genuine_disagreement_IS_reported_with_both_values()
    {
        var ceh = new SpeakerProfile { Country = "Denmark" };

        var gap = Assert.Single(
            SpeakerZohoGapReporter.GapsFor(ceh, Zoho(country: "DE")), g => g.Contains("Country"));

        Assert.Contains("Denmark", gap);
        Assert.Contains("DE", gap);
    }

    /// <summary>
    /// 🔒 §582 — a code we cannot map is NOT evidence of a mismatch. A false gap is worse than no
    /// gap, because he acts on it.
    /// </summary>
    [Fact]
    public void An_unmappable_code_stays_silent_rather_than_accusing()
        => Assert.True(SpeakerZohoGapReporter.CountryMatches("Faroe Islands", "FO"));

    [Fact]
    public void The_readable_flag_records_that_the_field_IS_available()
    {
        Assert.True(BackstageSpeaker.CountryIsReadable);
    }

    /// <summary>
    /// 🔒 §762 — THE MAIL MUST BE ABLE TO GO QUIET.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-01: <i>"i have just completed all the changes. so i need to approve/complete
    /// them somewhere so they dont come again - or is this mail a one-time mail"</i>. Every other
    /// field clears itself once Backstage reports it back; country never could, because Backstage
    /// never returns it — so the line repeated on every comparison for ever, and rode along on every
    /// re-send triggered by someone else's real gap. An organizer acknowledges it instead.
    /// </remarks>
    /// <summary>
    /// 🔴 §893 — THE TICK-BOX NO LONGER MASKS A REAL GAP, and that is the point of the fix.
    /// </summary>
    /// <remarks>
    /// §762's manual confirmation existed ONLY because Backstage could not be read, so the line
    /// could never clear itself. Now it can. A speaker whose country is genuinely absent in
    /// Backstage must still be reported even if someone once ticked "confirmed" — otherwise the
    /// acknowledgement silences a fact we can now verify, which is worse than the nagging it was
    /// invented to stop.
    /// </remarks>
    [Fact]
    public void A_stale_confirmation_does_NOT_hide_a_country_that_is_really_missing()
    {
        var ceh = new SpeakerProfile { Country = "DK", CountryConfirmedInBackstage = "DK" };

        var gap = Assert.Single(SpeakerZohoGapReporter.GapsFor(ceh, Zoho()), g => g.Contains("Country"));
        Assert.Contains("not set in Backstage", gap);
    }

    /// <summary>
    /// …and when Backstage really does have it, the line is silent WITHOUT needing the tick-box —
    /// which is what makes the confirmation field obsolete rather than merely ignored.
    /// </summary>
    [Fact]
    public void A_set_country_needs_no_confirmation_to_go_quiet()
    {
        var ceh = new SpeakerProfile { Country = "DK", CountryConfirmedInBackstage = null };

        Assert.DoesNotContain(
            SpeakerZohoGapReporter.GapsFor(ceh, Zoho(country: "DK")), g => g.Contains("Country"));
    }

    [Fact]
    public void CHANGING_the_country_re_asks_because_a_new_value_is_a_new_fact()
    {
        // 🔒 The confirmation stores the VALUE, not a boolean. A flag would keep suppressing the
        // line after CEH's country changed — silently hiding a Backstage record that is now wrong,
        // which is worse than the nagging it was meant to stop.
        var ceh = new SpeakerProfile { Country = "SE", CountryConfirmedInBackstage = "DK" };

        var gap = Assert.Single(SpeakerZohoGapReporter.GapsFor(ceh, Zoho()), g => g.Contains("Country"));
        Assert.Contains("SE", gap);
    }

    [Fact]
    public void A_confirmation_with_no_country_in_CEH_confirms_nothing()
    {
        var ceh = new SpeakerProfile { Country = null, CountryConfirmedInBackstage = "DK" };

        Assert.False(SpeakerZohoGapReporter.CountryIsConfirmed(ceh));
    }

    // ---------- readable fields ----------

    [Fact]
    public void Company_tagline_bio_and_socials_are_reported_when_blank_in_Zoho()
    {
        var ceh = new SpeakerProfile
        {
            CompanyName = "Microsoft",
            Tagline = "Senior Product Manager",
            Biography = "Works on Intune.",
            LinkedIn = "https://linkedin.com/in/perlarsen1975",
            Twitter = "https://x.com/perlarsen",
        };

        var gaps = SpeakerZohoGapReporter.GapsFor(ceh, Zoho());

        Assert.Contains(gaps, g => g.Contains("Microsoft"));
        Assert.Contains(gaps, g => g.Contains("Senior Product Manager"));
        Assert.Contains(gaps, g => g.Contains("Description"));
        Assert.Contains(gaps, g => g.Contains("linkedin.com/in/perlarsen1975"));
        Assert.Contains(gaps, g => g.Contains("x.com/perlarsen"));
    }

    [Fact]
    public void A_fully_populated_Zoho_record_produces_NO_gaps()
    {
        // The property that keeps the mail trustworthy: no gaps ⇒ no mail. A reporter that always
        // finds something is one he learns to ignore.
        var ceh = new SpeakerProfile
        {
            CompanyName = "Microsoft",
            Tagline = "Senior Product Manager",
            Biography = "Works on Intune.",
            Accreditation = "Microsoft Employee",
            LinkedIn = "https://linkedin.com/in/x",
            Twitter = "https://x.com/x",
        };

        var gaps = SpeakerZohoGapReporter.GapsFor(ceh, Zoho(
            company: "Microsoft", tagline: "Senior Product Manager", bio: "<p>Works on Intune.</p>",
            skills: "Microsoft Employee", linkedIn: "https://linkedin.com/in/x",
            twitter: "https://x.com/x"));

        Assert.Empty(gaps);
    }

    [Fact]
    public void A_value_CEH_does_not_have_is_never_reported()
    {
        // We report what CEH could supply. An empty CEH field is not a gap — it is simply unknown,
        // and chasing the organizers for something we cannot provide wastes their time.
        var gaps = SpeakerZohoGapReporter.GapsFor(new SpeakerProfile(), Zoho());

        Assert.Empty(gaps);
    }

    [Fact]
    public void The_None_accreditation_placeholder_never_becomes_a_skill()
    {
        // §306: "None" is the mandatory-field placeholder for a non-accredited speaker. Reporting it
        // as a missing skill would ask the organizers to type "None" into Zoho.
        var ceh = new SpeakerProfile { Accreditation = "None" };

        Assert.DoesNotContain(SpeakerZohoGapReporter.GapsFor(ceh, Zoho()), g => g.Contains("Skills"));
    }
}
