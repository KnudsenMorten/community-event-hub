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

    // ---------- §1365 — only the Speaker role is in the mail ----------

    [Theory]
    [InlineData(ParticipantRole.Speaker, true)]
    [InlineData(ParticipantRole.Organizer, false)]
    [InlineData(ParticipantRole.Volunteer, false)]
    [InlineData(ParticipantRole.Sponsor, false)]
    public void Only_the_Speaker_role_is_reported(ParticipantRole role, bool reported)
    {
        // Operator 2026-10-21: "only take the role speaker … so we dont include organizers, volunteers, sponsors".
        Assert.Equal(reported, SpeakerZohoGapReporter.IsReported(role));
    }

    [Fact]
    public void No_role_other_than_Speaker_is_reported()
    {
        // Also covers Attendee, Media, EventPartner, and guards a role added later: it stays out of the mail unless someone decides otherwise.
        foreach (var role in Enum.GetValues<ParticipantRole>())
            Assert.Equal(role == ParticipantRole.Speaker, SpeakerZohoGapReporter.IsReported(role));
    }

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
        Assert.Equal("Country (new value in Zoho from hub) -> DK", gap);
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
    public void A_genuine_disagreement_IS_reported_with_the_CEH_value()
    {
        var ceh = new SpeakerProfile { Country = "Denmark" };

        var gap = Assert.Single(
            SpeakerZohoGapReporter.GapsFor(ceh, Zoho(country: "DE")), g => g.Contains("Country"));

        // §1364 — the value to put in Backstage only; the old Backstage value is not shown.
        Assert.Equal("Country (new value in Zoho from hub) -> Denmark", gap);
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
        Assert.Equal("Country (new value in Zoho from hub) -> DK", gap);
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

    // ---------- §1364: CEH is the master — CONTENT must match, not only exist ----------

    [Fact]
    public void A_Backstage_value_that_differs_from_CEH_is_reported_with_both_values()
    {
        // Operator 2026-10-20: "they must match, with ceh as master". Before, only a BLANK Backstage field was seen.
        var ceh = new SpeakerProfile
        {
            Tagline = "UgurLabs & OpenAdminOS | Microsoft MVP",
            CompanyName = "UgurLabs",
            Biography = "Builds admin tooling.",
            Accreditation = "Microsoft Expert, Microsoft MVP",
            Country = "DE",
        };

        var gaps = SpeakerZohoGapReporter.GapsFor(ceh, Zoho(
            tagline: "VP of AI", company: "Robopack", bio: "<p>VP of AI at a vendor.</p>",
            skills: "Microsoft MVP", country: "DK"));

        // §1364 — each line is the field and the CEH value to put in Backstage; the Backstage value is never shown.
        Assert.Contains(gaps, g => g.EndsWith("-> UgurLabs & OpenAdminOS | Microsoft MVP"));
        Assert.Contains(gaps, g => g.EndsWith("-> UgurLabs"));
        Assert.Contains(gaps, g => g.Contains("Description") && g.EndsWith("-> Builds admin tooling."));
        Assert.Contains(gaps, g => g.Contains("Skills") && g.EndsWith("-> Microsoft Expert, Microsoft MVP"));
        Assert.Contains(gaps, g => g == "Country (new value in Zoho from hub) -> DE");
        Assert.DoesNotContain(gaps, g => g.Contains("VP of AI") || g.Contains("Robopack") || g.Contains("DK"));
    }

    /// <summary>
    /// The OTHER direction: CEH is blank, Backstage has a value ⇒ "(add to CEH) &lt;- value", never "clear it" (operator:
    /// "if field is blank in ceh, but exist in zoho. tell me so i can update ceh").
    /// </summary>
    [Fact]
    public void A_value_only_Backstage_has_is_reported_as_add_to_CEH_with_the_Backstage_value()
    {
        var gaps = SpeakerZohoGapReporter.GapsFor(new SpeakerProfile(), Zoho(
            tagline: "VP of AI", company: "Byskov IT Consult", bio: "<p>Text</p>", skills: "Microsoft MVP",
            country: "DK", linkedIn: "https://linkedin.com/in/x"));

        Assert.Contains("Company Name (new value in hub from Zoho) -> Byskov IT Consult", gaps);
        Assert.Contains(gaps, g => g.EndsWith("(new value in hub from Zoho) -> VP of AI"));
        Assert.Contains(gaps, g => g.Contains("Description") && g.EndsWith("(new value in hub from Zoho) -> Text"));
        Assert.Contains(gaps, g => g.Contains("Skills") && g.EndsWith("(new value in hub from Zoho) -> Microsoft MVP"));
        Assert.Contains(gaps, g => g.Contains("Country") && g.EndsWith("(new value in hub from Zoho) -> DK"));
        Assert.Contains(gaps, g => g.EndsWith("(new value in hub from Zoho) -> https://linkedin.com/in/x"));
        Assert.DoesNotContain(gaps, g => g.Contains("clear it") || g.Contains("(new value in Zoho from hub)"));
    }

    [Theory]
    [InlineData("InSpark", "Inspark")]
    [InlineData("Twoday", "twoday")]
    public void A_company_that_differs_only_in_capitals_is_the_same(string cehCompany, string zohoCompany)
    {
        var ceh = new SpeakerProfile { CompanyName = cehCompany };
        Assert.DoesNotContain(SpeakerZohoGapReporter.GapsFor(ceh, Zoho(company: zohoCompany)), g => g.Contains("Company"));
    }

    [Theory]
    [InlineData("Microsoft Expert, Microsoft MVP", "Microsoft MVP,Microsoft Expert")]   // order and separator
    [InlineData("Microsoft MVP", "microsoft mvp")]                                      // case
    public void Skills_in_another_order_or_case_are_the_same(string cehSkills, string zohoSkills)
    {
        var ceh = new SpeakerProfile { Accreditation = cehSkills };
        Assert.DoesNotContain(SpeakerZohoGapReporter.GapsFor(ceh, Zoho(skills: zohoSkills)), g => g.Contains("Skills"));
    }

    [Fact]
    public void A_bio_that_differs_only_by_HTML_and_spacing_is_the_same()
    {
        var ceh = new SpeakerProfile { Biography = "Works on Intune &\nAutopilot." };
        var gaps = SpeakerZohoGapReporter.GapsFor(ceh, Zoho(bio: "<p>Works on Intune &amp;</p>  <p>Autopilot.</p>"));
        Assert.DoesNotContain(gaps, g => g.Contains("Description"));
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
