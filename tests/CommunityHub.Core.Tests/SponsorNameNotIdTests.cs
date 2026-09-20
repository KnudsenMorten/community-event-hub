using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1237 — A SPONSOR'S ID IS NOT A NAME: not in a mail, and above all not in the event platform.
///
/// <para>Operator 2026-09-17, on the ops mail *"Pushed to sponsor '36' — Zoho GUI fields: Company
/// name"*: <i>"this mail is useless, go through the code for gui fields in oganizer paes and emails
/// and change to display names for sponsors"</i>.</para>
///
/// <para>⚠️ <b>It was never only a mail.</b> The same string is passed to <c>SyncAsync</c> as the
/// company NAME and written to Zoho, and the callers defaulted it to <c>info.SponsorCompanyId</c>
/// before enriching it from Company Manager. With CM disabled or unreachable, CEH therefore published
/// the literal <c>"36"</c> as a sponsor's name on the public agenda — while
/// <c>SponsorInfo.CompanyName</c> held "Surveil" locally the whole time.</para>
/// </summary>
public sealed class SponsorNameNotIdTests
{
    [Theory]
    [InlineData("36", "36")]                                  // the bare id
    [InlineData("(name not synced — CM id 36)", "36")]         // the honest unknown label
    [InlineData("", "36")]
    [InlineData("   ", "36")]
    [InlineData(null, "36")]
    public void A_value_that_is_not_a_name_is_recognised(string? candidate, string companyId)
    {
        Assert.True(SponsorCompanyName.IsUnresolved(candidate, companyId));
    }

    [Theory]
    [InlineData("Surveil", "36")]
    [InlineData("2linkIT", "12")]
    [InlineData("36 Degrees", "36")]   // a real name that merely CONTAINS the id
    public void A_real_name_is_not_mistaken_for_an_id(string candidate, string companyId)
    {
        Assert.False(SponsorCompanyName.IsUnresolved(candidate, companyId));
    }

    /// <summary>
    /// 🔴 THE GUARD THAT MATTERS: the push refuses a value that is not a name, so a Company Manager
    /// outage can no longer overwrite a sponsor's name in the event platform with their id.
    /// </summary>
    [Theory]
    [InlineData("36")]
    [InlineData("(name not synced — CM id 36)")]
    public void A_non_name_is_never_pushed_as_the_company_name(string ours)
    {
        // Zoho holds the real name and "ours" differs — the pre-§1237 rule would have pushed.
        Assert.False(SponsorZohoSyncService.ShouldPushName("Surveil", ours, "36"));
    }

    [Fact]
    public void A_real_rename_is_still_pushed()
    {
        Assert.True(SponsorZohoSyncService.ShouldPushName("Old Name", "Surveil", "36"));
    }

    /// <summary>🔒 The §1140b blank guard is untouched: an unread name is UNKNOWN, never "differs".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unread_zoho_name_still_never_triggers_a_write_storm(string? inZoho)
    {
        Assert.False(SponsorZohoSyncService.ShouldPushName(inZoho, "Surveil", "36"));
    }

    /// <summary>And the old 2-argument overload keeps its meaning for callers that have no id.</summary>
    [Fact]
    public void The_two_argument_overload_still_compares_names()
    {
        Assert.True(SponsorZohoSyncService.ShouldPushName("Old Name", "Surveil"));
        Assert.False(SponsorZohoSyncService.ShouldPushName("Surveil", "Surveil"));
    }
}
