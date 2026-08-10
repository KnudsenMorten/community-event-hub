using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1035 — A TEST OR WITHDRAWN SPONSOR IS NEVER PUSHED TO ZOHO BACKSTAGE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"company 100 was a test company. it appears as i dont have a
/// IsTestSponsor so i had to offboard them as sponsor so i didnt get them synced to zoho"</i> ·
/// <i>"i didn't have a place to set the flag in the ui"</i>.</para>
///
/// <para>🔴 <b>Both halves were true, and the second made the workaround useless.</b>
/// <c>IsTestData</c> existed but was read ONLY by the social-media side; <b>nothing in the Zoho path
/// read it, and nothing read <c>Withdrawn</c> either</b>. So withdrawing a test sponsor to stop it
/// reaching Zoho did not stop it: the company stayed in scope for provisioning and sync, with a
/// <c>ZohoSponsorId</c> already there to keep updating.</para>
///
/// <para>⚠️ Measured the same day: <b>no page, handler or service anywhere wrote
/// <c>SponsorInfo.IsTestData</c></b> — the single row carrying it had been edited straight in the
/// database. The organizer control added with this section is what makes the flag real.</para>
/// </remarks>
public sealed class SponsorZohoScopeTests
{
    private static SponsorInfo Company(bool test = false, SponsorStatus status = SponsorStatus.Active) =>
        new()
        {
            EventId = 1, SponsorCompanyId = "100", CompanyName = "Test-Silver",
            IsTestData = test, Status = status,
        };

    [Fact]
    public void An_ordinary_active_sponsor_is_pushed()
    {
        Assert.True(SponsorZohoScope.MayPushToZoho(Company()));
    }

    /// <summary>🔴 The case he had to work around.</summary>
    [Fact]
    public void A_test_company_is_never_pushed()
    {
        Assert.False(SponsorZohoScope.MayPushToZoho(Company(test: true)));
        Assert.Equal("marked as test data", SponsorZohoScope.SkipReason(Company(test: true)));
    }

    /// <summary>
    /// 🔒 Withdrawn counts too — he used withdrawal AS the mechanism for "stop syncing this
    /// company", and offboarding that keeps pushing is not offboarding.
    /// </summary>
    [Fact]
    public void A_withdrawn_company_is_never_pushed()
    {
        var withdrawn = Company(status: SponsorStatus.Withdrawn);
        Assert.False(SponsorZohoScope.MayPushToZoho(withdrawn));
        Assert.Equal("withdrawn", SponsorZohoScope.SkipReason(withdrawn));
    }

    /// <summary>
    /// ⚠️ Test wins over status, so the REASON stays the honest one: a company that is both should
    /// report the property an organizer set on purpose, not the lifecycle it happens to be in.
    /// </summary>
    [Fact]
    public void A_test_company_that_is_also_withdrawn_reports_the_test_reason()
    {
        var both = Company(test: true, status: SponsorStatus.Withdrawn);
        Assert.False(SponsorZohoScope.MayPushToZoho(both));
        Assert.Equal("marked as test data", SponsorZohoScope.SkipReason(both));
    }
}
