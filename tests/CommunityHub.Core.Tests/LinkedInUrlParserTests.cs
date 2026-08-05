using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.15 — what a LinkedIn URL can and cannot tell us about a sponsor's organization id.
/// </summary>
/// <remarks>
/// The fixtures are the REAL sponsor URLs held in PROD on 2026-08-04, not invented ones — measuring
/// them is what showed that every single sponsor carries a vanity slug and none carries an id
/// (§824.12a), which is why <c>SponsorInfo.LinkedInOrganizationId</c> had to exist at all.
/// </remarks>
public sealed class LinkedInUrlParserTests
{
    [Theory]
    // Every one of these is a live sponsor URL from PROD.
    [InlineData("https://www.linkedin.com/company/glueckkanja")]
    [InlineData("https://www.linkedin.com/company/robopack-aps")]
    [InlineData("https://www.linkedin.com/company/controlup-technology")]
    [InlineData("https://www.linkedin.com/company/system-center-dudes")]
    [InlineData("https://www.linkedin.com/company/codetwo/")]
    public void A_real_sponsor_url_yields_no_organization_id(string url)
    {
        Assert.Null(LinkedInUrlParser.TryReadOrganizationId(url));
        Assert.NotNull(LinkedInUrlParser.TryReadVanityName(url));
    }

    [Fact]
    public void A_slug_that_begins_with_a_digit_is_not_an_id()
    {
        // 🔒 THE TRAP. "2linkitnet" passes a naive "starts with a number" test — a
        // LIKE '%/company/[0-9]%' check reported exactly this false positive while measuring
        // §824.12a. Reading it as an id would build urn:li:organization:2 and mention a stranger's
        // company in a real post to 400+ followers.
        const string url = "https://www.linkedin.com/company/2linkitnet";

        Assert.Null(LinkedInUrlParser.TryReadOrganizationId(url));
        Assert.Equal("2linkitnet", LinkedInUrlParser.TryReadVanityName(url));
        Assert.Null(LinkedInUrlParser.TryBuildOrganizationUrn(null, url));
    }

    [Theory]
    [InlineData("https://www.linkedin.com/company/98360537/admin/dashboard/")]
    [InlineData("https://www.linkedin.com/company/98360537")]
    [InlineData("https://www.linkedin.com/company/98360537/?trk=x")]
    public void An_admin_style_url_does_carry_the_id(string url)
    {
        // The one case the parser earns its keep: the URL copied from a company's own admin view.
        Assert.Equal("98360537", LinkedInUrlParser.TryReadOrganizationId(url));
        Assert.Null(LinkedInUrlParser.TryReadVanityName(url));
        Assert.Equal("urn:li:organization:98360537", LinkedInUrlParser.TryBuildOrganizationUrn(null, url));
    }

    [Fact]
    public void The_stored_id_wins_over_whatever_the_url_says()
    {
        // A company can rename its vanity slug without telling us; an id somebody has seen work
        // survives that. The URL is the fallback, never the authority.
        var urn = LinkedInUrlParser.TryBuildOrganizationUrn(
            "12345", "https://www.linkedin.com/company/98360537");

        Assert.Equal("urn:li:organization:12345", urn);
    }

    [Fact]
    public void An_id_pasted_as_a_full_urn_is_accepted_without_doubling_the_prefix()
    {
        Assert.Equal(
            "urn:li:organization:98360537",
            LinkedInUrlParser.TryBuildOrganizationUrn("urn:li:organization:98360537", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/company/whatever")]
    [InlineData("https://www.linkedin.com/in/knudsenmorten")]   // a PERSON, not a company
    [InlineData("not a url at all")]
    public void Anything_that_is_not_a_company_url_yields_nothing(string? url)
    {
        Assert.Null(LinkedInUrlParser.TryReadOrganizationId(url));
        Assert.Null(LinkedInUrlParser.TryReadVanityName(url));
        Assert.Null(LinkedInUrlParser.TryBuildOrganizationUrn(null, url));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("98360537x")]
    [InlineData("urn:li:person:98360537x")]
    public void A_stored_id_that_is_not_numeric_produces_no_urn_rather_than_a_broken_one(string stored)
    {
        // An unusable mention must degrade to plain text, never to a malformed urn that LinkedIn
        // would either reject or resolve to something unintended.
        Assert.Null(LinkedInUrlParser.TryBuildOrganizationUrn(stored, null));
    }
}
