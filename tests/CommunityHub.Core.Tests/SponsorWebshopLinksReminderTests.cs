using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1127 — which sponsors get chased for a blank WEBSHOP website / LinkedIn, and which never do.
///
/// <para>Operator 2026-08-25: <i>"if weburl + linkedin are blank in webshop, sponsor contact +
/// mok@expertslive.dk must get an alert. twitter is optional"</i>.</para>
///
/// <para>🔑 This mail only became necessary because of §1125/§1126: once the webshop OWNS those
/// fields and the CEH inputs are read-only, a blank has no in-hub fix. The chaser is what stops that
/// being a dead end — so the rule about WHAT counts as missing is the part worth pinning.</para>
/// </summary>
public sealed class SponsorWebshopLinksReminderTests
{
    // ── What triggers a chase ────────────────────────────────────────────────────────────

    [Fact]
    public void A_blank_website_is_chased()
    {
        var missing = SponsorWebshopLinksReminderBuilder.MissingFor(
            websiteUrl: null, linkedInUrl: "https://linkedin.com/company/x");

        Assert.Equal(new[] { "website address" }, missing);
    }

    [Fact]
    public void A_blank_LinkedIn_is_chased()
    {
        var missing = SponsorWebshopLinksReminderBuilder.MissingFor(
            websiteUrl: "https://surveil.co", linkedInUrl: "   ");

        Assert.Equal(new[] { "LinkedIn address" }, missing);
    }

    [Fact]
    public void Both_blank_are_reported_together_in_one_mail()
    {
        var missing = SponsorWebshopLinksReminderBuilder.MissingFor(null, null);

        Assert.Equal(new[] { "website address", "LinkedIn address" }, missing);
        Assert.Equal("website address and LinkedIn address",
            SponsorWebshopLinksReminderBuilder.MissingLabel(missing));
    }

    [Fact]
    public void Nothing_missing_produces_no_chase_which_is_how_it_stops_by_itself()
    {
        // 🔑 The off switch is the absence of a reason to send — not a flag anyone has to clear.
        var missing = SponsorWebshopLinksReminderBuilder.MissingFor(
            "https://surveil.co", "https://linkedin.com/company/surveil-cloud");

        Assert.Empty(missing);
        Assert.Equal(string.Empty, SponsorWebshopLinksReminderBuilder.MissingLabel(missing));
    }

    // ── Twitter is optional, and that must stay true ─────────────────────────────────────

    [Fact]
    public void Twitter_is_never_part_of_the_missing_set()
    {
        // 🔒 "twitter is optional" (operator, verbatim). MissingFor does not even ACCEPT a Twitter
        // value — it is absent by construction rather than filtered afterwards, so there is no
        // switch anyone can flip by accident. This test documents that as a decision; if the
        // signature ever grows a third parameter, it should fail to compile and be reconsidered.
        var bothPresent = SponsorWebshopLinksReminderBuilder.MissingFor(
            "https://surveil.co", "https://linkedin.com/company/surveil-cloud");

        Assert.Empty(bothPresent);
    }

    // ── Label wording ────────────────────────────────────────────────────────────────────

    [Fact]
    public void One_missing_field_reads_naturally_in_the_subject()
    {
        Assert.Equal("website address", SponsorWebshopLinksReminderBuilder.MissingLabel(
            SponsorWebshopLinksReminderBuilder.MissingFor(null, "https://linkedin.com/company/x")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void Blank_means_blank_in_every_form(string? value)
    {
        // 🔒 Whitespace counts as missing. ⚠️ A literal "-" or "n/a" deliberately does NOT: that is
        // a human saying "none", and guessing at those would start silently exempting companies
        // that really are missing a link.
        Assert.Contains("website address",
            SponsorWebshopLinksReminderBuilder.MissingFor(value, "https://linkedin.com/company/x"));
    }
}
