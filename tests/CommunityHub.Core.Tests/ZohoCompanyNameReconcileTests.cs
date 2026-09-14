using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1153 — the company name in Zoho is reconciled on every run, on BOTH records.
///
/// <para>Operator 2026-08-29: <i>"the wrong company name appears under exhibitor in zoho. under
/// sponsors it is the correct name … it was their billing name … zoho company name comes from ceh
/// and should be the public name originally from the webshop; not billing name"</i> ·
/// <i>"it is the service that doesnt reconsile correctly, as this is due to a change"</i> ·
/// <i>"so it must check at every service run"</i>.</para>
///
/// <para>🔑 <b>The sponsor record only LOOKED right.</b> Its push always passed the name, so a
/// correction rode along whenever some other field happened to differ; the exhibitor push never
/// passed the name at all. On both records, a difference in the NAME ALONE triggered no push — so a
/// record created under a billing name stayed wrong indefinitely. Riding along is not reconciling.</para>
///
/// <para>NO real customer names — the reported case is represented as "Billing Entity A/S" vs the
/// public "Company Ten".</para>
/// </summary>
public class ZohoCompanyNameReconcileTests
{
    private static bool Push(string? inZoho, string? ours) =>
        SponsorZohoSyncService.ShouldPushName(inZoho, ours);

    /// <summary>🔴 The reported bug: Zoho holds the billing name, CEH holds the public one.</summary>
    [Fact]
    public void A_billing_name_in_Zoho_is_corrected_to_the_public_name()
    {
        Assert.True(Push("Billing Entity A/S", "Company Ten"));
    }

    [Fact]
    public void A_name_that_already_matches_is_left_alone()
    {
        // Otherwise every company takes a PUT on every run for no reason.
        Assert.False(Push("Company Ten", "Company Ten"));
    }

    [Theory]
    [InlineData("company ten")]
    [InlineData("  Company Ten  ")]
    [InlineData("COMPANY TEN")]
    public void Case_and_surrounding_space_are_not_a_difference(string inZoho)
    {
        // A trailing space or a capitalisation difference is not a rename, and treating it as one
        // would push the same value back to Zoho for ever.
        Assert.False(Push(inZoho, "Company Ten"));
    }

    /// <summary>
    /// 🔴 THE GUARD A TEST CAUGHT ON THE FIRST RUN OF THIS CHANGE.
    /// </summary>
    /// <remarks>
    /// Without it, a response that carries no name makes EVERY company look wrong and take a PUT —
    /// on every sync, for ever. That is a write storm against a live Zoho, and it is §1140's shape
    /// exactly: a field read wrong causes everything to be rewritten every run. "I could not read
    /// it" is UNKNOWN, never "it differs" (§1140b).
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_we_could_NOT_READ_is_never_treated_as_a_difference(string? inZoho)
    {
        Assert.False(Push(inZoho, "Company Ten"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void We_never_push_a_name_we_do_not_have(string? ours)
    {
        // 🔒 The other direction of the same rule: CEH failing to resolve a public name must never
        // blank the name that is already in Zoho.
        Assert.False(Push("Company Ten", ours));
    }
}
