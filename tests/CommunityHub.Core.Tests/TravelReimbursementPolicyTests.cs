using CommunityHub.Core.Entitlements;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §399 — who may claim travel reimbursement (operator 2026-07-26: <i>"when a speaker is from
/// Denmark, then the 'travel imbursement' should be disabled as the terms is that no people from
/// denmark get travel reimbursed"</i>).
///
/// <para>The rule was not new — it already governed the deadline TASK. It existed as a private
/// helper inside <c>SpeakerDeadlineSeeder</c>, so the task was correctly withheld from Danish
/// speakers while the nav entry and <c>/Forms/Travel</c> still offered them the claim. One rule
/// implemented in one place and enforced in three is the actual defect; these tests pin the shared
/// policy every caller now uses.</para>
/// </summary>
public sealed class TravelReimbursementPolicyTests
{
    [Theory]
    [InlineData("DK")]
    [InlineData("dk")]
    [InlineData("Denmark")]
    [InlineData("denmark")]
    [InlineData("Danmark")]      // the Danish spelling — the operator's own users type it
    [InlineData("  DK  ")]       // Speaker Details does not trim on the way in
    public void A_speaker_based_in_Denmark_cannot_claim(string country)
    {
        Assert.True(TravelReimbursementPolicy.IsDenmark(country));
        Assert.False(TravelReimbursementPolicy.IsEligible(country));
    }

    [Theory]
    [InlineData("SE")]
    [InlineData("Norway")]
    [InlineData("Germany")]
    [InlineData("United Kingdom")]
    public void Everyone_travelling_from_outside_Denmark_can_claim(string country)
    {
        Assert.True(TravelReimbursementPolicy.IsEligible(country));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_UNKNOWN_country_stays_eligible(string? country)
    {
        // Deliberate direction: a speaker who has not filled in their country yet must not silently
        // lose a benefit they may be entitled to. Seeing a form you turn out not to need is
        // recoverable; being quietly denied one you were owed is not — and nobody would report it,
        // because there would be nothing on screen to report.
        Assert.False(TravelReimbursementPolicy.IsDenmark(country));
        Assert.True(TravelReimbursementPolicy.IsEligible(country));
    }

    [Fact]
    public void The_explanation_says_WHY_rather_than_denying_access()
    {
        // The page shows this instead of "access denied": someone who is simply not eligible did
        // nothing wrong, and an error framing sends them hunting for a fault to fix.
        var msg = TravelReimbursementPolicy.NotEligibleMessage;
        Assert.Contains("outside Denmark", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("denied", msg, StringComparison.OrdinalIgnoreCase);
    }
}
