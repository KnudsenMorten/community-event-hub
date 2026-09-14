using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Forms.Steps;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1166 — the travel claim tells finance which VAT zone it belongs to.
///
/// <para>Operator 2026-09-02: <i>"I need the subject to include [MOMSZONE=UDLAND] or [MOMSZONE=EU]
/// dependent of the country of the speaker. This must be included as postfix in the subject of the
/// email sent to ELDK finance."</i></para>
///
/// <para>🔑 <b>The subject is a contract, not a label.</b> Finance filters and files on it, so the
/// exact shape is asserted here rather than left to a method that needs a database, an e-mail sender
/// and a participant before it will produce a string.</para>
///
/// <para>NO real names.</para>
/// </summary>
public sealed class TravelClaimVatZoneTests
{
    [Fact]
    public void The_zone_is_a_postfix_and_the_existing_subject_is_untouched()
    {
        var subject = TravelFormService.ClaimSubject("Ada Lovelace", "UDLAND");

        // ⚠️ Postfix, not prefix: the front of the subject is what finance already recognises, and
        // moving it would break every rule they have.
        Assert.StartsWith("Travel Rebursement - Ada Lovelace - ELDK27", subject);
        Assert.EndsWith("[MOMSZONE=UDLAND]", subject);
    }

    /// <summary>
    /// 🔴 The two he named, end to end from a country.
    /// </summary>
    [Theory]
    [InlineData("Norway", "UDLAND")]
    [InlineData("NO", "UDLAND")]
    [InlineData("United Kingdom", "UDLAND")]
    [InlineData("GB", "UDLAND")]
    [InlineData("Sweden", "EU")]
    [InlineData("SE", "EU")]
    [InlineData("Germany", "EU")]
    public void A_speakers_country_decides_the_token(string country, string expected)
    {
        var subject = TravelFormService.ClaimSubject("Grace Hopper", VatZoneMapper.TokenFor(country));

        Assert.EndsWith($"[MOMSZONE={expected}]", subject);
    }

    /// <summary>
    /// 🔒 An unknown country is marked as such and never guessed into a zone.
    /// </summary>
    /// <remarks>
    /// <para>§1054 already withholds the claim REMINDER until the speaker has said where they live,
    /// so this should be rare — but a submission mail is a different path from a reminder, and a
    /// zone invented to fill the gap would be a tax error that looks like data.</para>
    ///
    /// <para>UNKNOWN is deliberately not a Danish word, so nobody can mistake it for a fourth zone
    /// to book against.</para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Somewhere")]
    public void An_unknown_country_says_UNKNOWN(string? country)
    {
        var subject = TravelFormService.ClaimSubject("Katherine Johnson", VatZoneMapper.TokenFor(country));

        Assert.EndsWith("[MOMSZONE=UNKNOWN]", subject);
    }

    /// <summary>
    /// 🔑 A Danish speaker resolves to INDLAND rather than to nothing.
    /// </summary>
    /// <remarks>
    /// He named only UDLAND and EU, because §1054 filters Danish speakers out of travel
    /// reimbursement upstream — they are not owed one. Completing the set matters anyway: a missing
    /// token would be ambiguous with a mail sent before this existed, and an absent field is the
    /// hardest kind of wrong to notice.
    /// </remarks>
    [Fact]
    public void A_danish_speaker_is_INDLAND_not_a_missing_token()
    {
        var subject = TravelFormService.ClaimSubject("Ada Lovelace", VatZoneMapper.TokenFor("Denmark"));

        Assert.EndsWith("[MOMSZONE=INDLAND]", subject);
    }
}
