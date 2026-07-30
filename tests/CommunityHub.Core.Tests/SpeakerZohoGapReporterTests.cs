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
        string? skills = null, string? linkedIn = null, string? twitter = null) =>
        new("bs-1", "Per Larsen", tagline, bio, Country: null, linkedIn, twitter,
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

    [Fact]
    public void COUNTRY_is_reported_as_UNVERIFIABLE_and_carries_the_answer()
    {
        // When CEH HAS a country the line is worth sending — but it must be honest that this is a
        // "please confirm", not a detected deviation, and it must carry the value so the fix is one
        // paste rather than a lookup.
        var ceh = new SpeakerProfile { Country = "DK" };

        var gap = Assert.Single(SpeakerZohoGapReporter.GapsFor(ceh, Zoho()), g => g.Contains("Country"));

        Assert.Contains("DK", gap);
        Assert.Contains("does not report this field", gap);
    }

    [Fact]
    public void The_unreadable_flag_is_explicit_so_nobody_reintroduces_the_diff()
    {
        Assert.False(BackstageSpeaker.CountryIsReadable);
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
