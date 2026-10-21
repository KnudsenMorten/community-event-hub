using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1227 — the Get Started digest's opening sentence follows the wizard's scope.
/// </summary>
/// <remarks>
/// Operator 2026-09-15: <i>"the get started digest is wrong, as it is sending this to individuals …
/// it took the sponsor digest and applied to all roles, big mistake"</i>. §1081 rewrote the ONE shared
/// template for the company-scoped sponsor wizard ("the whole team's checklist — anyone can finish an
/// open step"), and every personal role received it.
/// </remarks>
public sealed class GetStartedDigestIntroByRoleTests
{
    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Attendee)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    [InlineData(ParticipantRole.Organizer)]
    public void A_personal_wizard_gets_the_individual_sentence(ParticipantRole role)
    {
        var intro = GetStartedDigestBuilder.IntroFor(role, "Experts Live Denmark 2027", "ELDK27");

        Assert.Equal(
            "You have a few steps left to complete in <strong>Get Started</strong> for "
            + "<strong>Experts Live Denmark 2027 (ELDK27)</strong>.",
            intro);
        Assert.DoesNotContain("team", intro, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("colleagues", intro, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>🔒 The sponsor wizard IS a shared company checklist, so sponsors keep the team wording.</summary>
    [Fact]
    public void A_sponsor_keeps_the_shared_checklist_sentence()
    {
        var intro = GetStartedDigestBuilder.IntroFor(ParticipantRole.Sponsor, "Experts Live Denmark 2027", "ELDK27");

        // §1227b — the shared-checklist sentence stays; §1312 (operator 2026-09-27) adds his own opening and the TIP.
        Assert.Contains("Our records show that your <strong>Get Started</strong> onboarding for "
                        + "<strong>Experts Live Denmark 2027 (ELDK27)</strong> is not complete yet.", intro);
        Assert.Contains("It's shared across your company, so a colleague can pick up any step — each one only needs "
                        + "completing once.", intro);
        Assert.Contains("<strong>TIP:</strong>", intro);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "can we optimize the weekly get started reminder mail for sponsors, so we can improve what
    /// is missing and the consequence of this". Each open sponsor step says what it costs; the mail asks to whitelist the
    /// sender, and states the real cadence.
    /// </summary>
    [Fact]
    public void A_sponsor_reminder_names_the_consequence_the_whitelist_and_the_real_cadence()
    {
        Assert.Contains("social-media sponsor announcements", GetStartedDigestBuilder.SponsorConsequence("company"));
        Assert.Contains("signage", GetStartedDigestBuilder.SponsorConsequence("logos"));
        Assert.Null(GetStartedDigestBuilder.SponsorConsequence("unknown-step"));
        Assert.Contains("please whitelist info@example.test", GetStartedDigestBuilder.SponsorPreambleHtml("info@example.test"));
        Assert.Contains("every 2 days", GetStartedDigestBuilder.SponsorOutroHtml(2));
        Assert.Contains("every week", GetStartedDigestBuilder.SponsorOutroHtml(7));
    }

    [Fact]
    public void The_event_name_is_encoded_and_a_missing_code_adds_no_empty_brackets()
    {
        var intro = GetStartedDigestBuilder.IntroFor(ParticipantRole.Speaker, "R&D <Days>", null);

        Assert.Contains("<strong>R&amp;D &lt;Days&gt;</strong>.", intro, StringComparison.Ordinal);
        Assert.DoesNotContain("()", intro, StringComparison.Ordinal);
    }

    /// <summary>🔒 The shipped template carries the per-role token, not a hard-coded sentence for one role.</summary>
    [Fact]
    public void The_shipped_template_takes_its_opening_from_the_role_token()
    {
        var html = File.ReadAllText(Path.Combine(RepoPaths.EmailTemplates(), "getstarted-digest.html"));

        Assert.Contains("{{introHtml}}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("whole team's checklist", html, StringComparison.Ordinal);
    }
}
