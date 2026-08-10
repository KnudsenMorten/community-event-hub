using System;
using CommunityHub.Core.Entitlements;
using CommunityHub.Forms.Steps;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1054 — THE TRAVEL CLAIM E-MAIL WAITS UNTIL WE KNOW WHERE THE SPEAKER LIVES.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10, on a Danish speaker who received <i>"Submit travel reimbursement"</i>:
/// <i>"danish speakers get no travel reimbursement"</i>, then choosing the fix — <i>"b - withhold
/// email until country is known"</i>.</para>
///
/// <para>🔑 <b>The rule was never missing; the ORDER OF EVENTS defeated it.</b> Every gate exists and
/// works: only <c>SpeakerCategory.Community</c> is granted <c>OrderItem.TravelReimbursement</c>, the
/// travel deadline carries <c>nonDenmarkOnly: true</c>, and both the wizard step and the form call
/// <c>IsRelevantAsync</c>. But <c>SpeakerProfile.Country</c> starts BLANK — the speaker sets it
/// themselves, later, in Speaker Details — and §143 deliberately treats unknown as non-Denmark so
/// the task is offered rather than silently withheld. So the task is seeded and mailed; the speaker
/// then answers "Denmark", the seeder skips the deadline and the prune removes the task.</para>
///
/// <para>⇒ <b>The task self-heals. The e-mail does not.</b> §143's protection is kept — the task
/// still appears, nothing is silently withheld — and only the MAIL waits for the answer.</para>
/// </remarks>
public sealed class TravelClaimEmailWaitsForCountryTests
{
    /// <summary>
    /// 🔴 THE CROSS-ASSEMBLY PIN. <c>TravelReimbursementPolicy</c> lives in Core and cannot
    /// reference the web assembly, so it carries its own copy of the task key. A rename on either
    /// side unbinds the reminder gate <b>silently</b> — nothing fails, the mail simply starts going
    /// out again to speakers who have not said where they live. Same shape as §1037's system names
    /// and §767's guessed convention: a string agreed between two places, enforced in neither.
    /// </summary>
    [Fact]
    public void The_policys_task_key_matches_the_form_services_key()
    {
        Assert.Equal(TravelFormService.SubmitInvoiceTaskKey, TravelReimbursementPolicy.TaskKeyPrefix);
    }

    /// <summary>An unanswered country ⇒ no mail. This is the whole fix.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_country_must_not_be_mailed(string? country)
    {
        Assert.False(TravelReimbursementPolicy.MayEmailClaim(country));
    }

    /// <summary>
    /// 🔒 A KNOWN country ⇒ mail. The gate must not become "never mail anyone": once the country is
    /// set, Denmark is already filtered upstream by the deadline's <c>nonDenmarkOnly</c> flag, so
    /// anything reaching the mail gate WITH a country is legitimately non-Danish and is owed the
    /// reminder. A gate that blocks everything would look identical in production to one that works.
    /// </summary>
    [Theory]
    [InlineData("SE")]
    [InlineData("Norway")]
    [InlineData("DE")]
    public void A_known_country_is_still_mailed(string country)
    {
        Assert.True(TravelReimbursementPolicy.MayEmailClaim(country));
    }

    /// <summary>
    /// ⚠️ The country gate for ELIGIBILITY is unchanged and stays independent of the mail gate:
    /// blank is still treated as non-Denmark (§143), so the TASK is still offered. Pinned here
    /// because §1054 could easily be "fixed" by making blank mean Denmark — which would silently
    /// cost a real speaker their reimbursement, the exact harm §143 exists to prevent.
    /// </summary>
    [Fact]
    public void A_blank_country_is_still_ELIGIBLE_so_the_task_is_still_offered()
    {
        Assert.True(TravelReimbursementPolicy.IsEligible(null));
        Assert.True(TravelReimbursementPolicy.IsEligible(""));
        Assert.False(TravelReimbursementPolicy.IsDenmark(""));
    }

    /// <summary>Denmark is still refused outright, in every spelling the data carries.</summary>
    [Theory]
    [InlineData("DK")]
    [InlineData("dk")]
    [InlineData("Denmark")]
    [InlineData("Danmark")]
    public void Denmark_is_never_eligible(string country)
    {
        Assert.False(TravelReimbursementPolicy.IsEligible(country));
    }
}
