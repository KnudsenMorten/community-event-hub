using CommunityHub.Pages.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1359 — Organizer → Sponsors hides test, withdrawn and inactive companies by default. Operator 2026-10-19: *"it should
/// exclude test data/inactive/data by default, with option to include"*.
/// </summary>
public sealed class SponsorsHiddenByDefaultTests
{
    private static SponsorsModel.CompanyRow Row(bool withdrawn = false, bool test = false, params bool[] contactsActive) =>
        new("19", "Patch My PC",
            contactsActive.Select((a, i) => new SponsorsModel.Contact(i + 1, $"P{i}", $"p{i}@example.test", a)).ToList(),
            0, 0, 0, 0, 0, null, withdrawn, test);

    [Fact]
    public void A_real_active_sponsor_is_shown() =>
        Assert.False(SponsorsModel.HiddenByDefault(Row(contactsActive: new[] { true, false })));

    [Fact]
    public void Test_withdrawn_and_inactive_companies_are_hidden()
    {
        Assert.True(SponsorsModel.HiddenByDefault(Row(test: true, contactsActive: new[] { true })));
        Assert.True(SponsorsModel.HiddenByDefault(Row(withdrawn: true, contactsActive: new[] { true })));
        Assert.True(SponsorsModel.HiddenByDefault(Row(contactsActive: new[] { false, false })));   // every contact deactivated
    }

    [Fact]
    public void A_company_with_no_contacts_yet_is_mid_onboarding_and_stays_visible() =>
        Assert.False(SponsorsModel.HiddenByDefault(Row()));
}
