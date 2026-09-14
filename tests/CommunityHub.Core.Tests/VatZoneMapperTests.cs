using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1166 — country → Danish VAT zone (<i>momszone</i>).
///
/// <para>Operator 2026-09-02: <i>"It depends on the country, where the sponsor are from. For example
/// Norway and UK are Udland C but European countries are B."</i></para>
///
/// <para>🔴 <b>THE RULE IS EU MEMBERSHIP, NOT GEOGRAPHY</b> — and he named Norway and the UK for
/// exactly that reason. Both are unmistakably European and neither is in the EU, so "European
/// countries are B" implemented as "is it in Europe?" puts four countries in the wrong rubrik on a
/// real invoice. Those cases are the first tests here, not an afterthought.</para>
/// </summary>
public sealed class VatZoneMapperTests
{
    /// <summary>
    /// 🔴 The countries he named, plus the rest of non-EU Europe.
    /// </summary>
    [Theory]
    [InlineData("NO")]
    [InlineData("Norway")]
    [InlineData("GB")]
    [InlineData("United Kingdom")]
    [InlineData("England")]
    [InlineData("CH")]
    [InlineData("Switzerland")]
    [InlineData("IS")]
    [InlineData("Iceland")]
    [InlineData("LI")]
    public void European_but_not_in_the_EU_is_Udland(string country)
    {
        Assert.Equal(VatZone.Udland, VatZoneMapper.Resolve(country));
        Assert.Equal("UDLAND", VatZoneMapper.TokenFor(country));
    }

    /// <summary>
    /// ⚠️ The UK is the trap with a date on it — it WAS in the EU until 2020, so any list copied
    /// from an older source still contains it.
    /// </summary>
    [Fact]
    public void The_United_Kingdom_is_not_in_the_EU_list()
    {
        Assert.NotEqual(VatZone.Eu, VatZoneMapper.Resolve("GB"));
    }

    [Theory]
    [InlineData("SE")]
    [InlineData("Sweden")]
    [InlineData("DE")]
    [InlineData("Germany")]
    [InlineData("NL")]
    [InlineData("Netherlands")]
    [InlineData("FR")]
    [InlineData("IE")]
    [InlineData("Ireland")]
    [InlineData("PL")]
    [InlineData("ES")]
    [InlineData("IT")]
    public void An_EU_member_state_is_rubrik_B(string country)
    {
        Assert.Equal(VatZone.Eu, VatZoneMapper.Resolve(country));
        Assert.Equal("EU", VatZoneMapper.TokenFor(country));
    }

    /// <summary>
    /// 🔑 Denmark is an EU member but is DOMESTIC — checked before the EU list, or every Danish
    /// invoice would be zoned as an EU sale and go out without VAT.
    /// </summary>
    [Theory]
    [InlineData("DK")]
    [InlineData("dk")]
    [InlineData("Denmark")]
    [InlineData("Danmark")]
    public void Denmark_is_Indland_not_EU(string country)
    {
        Assert.Equal(VatZone.Indland, VatZoneMapper.Resolve(country));
        Assert.Equal("INDLAND", VatZoneMapper.TokenFor(country));
    }

    [Theory]
    [InlineData("US")]
    [InlineData("United States")]
    [InlineData("USA")]
    [InlineData("CA")]
    [InlineData("Australia")]
    [InlineData("India")]
    public void The_rest_of_the_world_is_Udland(string country)
    {
        Assert.Equal(VatZone.Udland, VatZoneMapper.Resolve(country));
    }

    /// <summary>
    /// 🔴 A zone is never guessed.
    /// </summary>
    /// <remarks>
    /// Mis-zoning is a tax error on a real invoice, and the two wrong answers are not symmetric:
    /// charging Danish VAT to a foreign customer is a refund and an apology, while omitting it
    /// wrongly is money the organiser owes.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Somewhere")]
    [InlineData("N/A")]
    public void An_unknown_country_is_Unknown_and_never_a_zone(string? country)
    {
        Assert.Equal(VatZone.Unknown, VatZoneMapper.Resolve(country));
        Assert.Equal("UNKNOWN", VatZoneMapper.TokenFor(country));
        Assert.Null(VatZoneMapper.EconomicZoneNumber(VatZone.Unknown));
    }

    /// <summary>
    /// ⚠️ An unlisted 2-letter code resolves to Udland rather than Unknown.
    /// </summary>
    /// <remarks>
    /// Deliberate: a code that is not in the EU list is, by definition, not in the EU — so Udland is
    /// the correct treatment. Reporting "unknown" instead would flag a perfectly valid country
    /// nobody had thought to list, and train the reader to ignore the flag.
    /// </remarks>
    [Theory]
    [InlineData("KE")]
    [InlineData("VN")]
    public void An_unlisted_two_letter_code_is_still_outside_the_EU(string code)
    {
        Assert.Equal(VatZone.Udland, VatZoneMapper.Resolve(code));
    }

    /// <summary>
    /// 🔒 The zone numbers are e-conomic's own, already relied on by the invoice composer.
    /// </summary>
    [Fact]
    public void The_zone_numbers_match_e_conomic()
    {
        Assert.Equal(1, VatZoneMapper.EconomicZoneNumber(VatZone.Indland));
        Assert.Equal(2, VatZoneMapper.EconomicZoneNumber(VatZone.Eu));
        Assert.Equal(3, VatZoneMapper.EconomicZoneNumber(VatZone.Udland));
        Assert.Equal(4, VatZoneMapper.EconomicZoneNumber(VatZone.IndlandUdenMoms));
    }

    /// <summary>
    /// 🔴 UNKNOWN is deliberately not a Danish word.
    /// </summary>
    /// <remarks>
    /// It must be unmistakable that no zone was determined, rather than looking like a fourth zone
    /// somebody could book against.
    /// </remarks>
    [Fact]
    public void The_unknown_token_cannot_be_mistaken_for_a_zone()
    {
        var token = VatZoneMapper.Token(VatZone.Unknown);

        Assert.Equal("UNKNOWN", token);
        Assert.NotEqual("INDLAND", token);
        Assert.NotEqual("EU", token);
        Assert.NotEqual("UDLAND", token);
    }

    [Fact]
    public void Names_and_codes_agree_with_each_other()
    {
        // The two callers are fed differently — the ERP side has a 2-letter billing country, a
        // speaker profile has a spelled-out name — so both must land on the same zone.
        Assert.Equal(VatZoneMapper.Resolve("NO"), VatZoneMapper.Resolve("Norway"));
        Assert.Equal(VatZoneMapper.Resolve("SE"), VatZoneMapper.Resolve("Sweden"));
        Assert.Equal(VatZoneMapper.Resolve("GB"), VatZoneMapper.Resolve("United Kingdom"));
    }

    [Fact]
    public void Surrounding_space_and_casing_do_not_change_the_answer()
    {
        Assert.Equal(VatZone.Udland, VatZoneMapper.Resolve("  norway  "));
        Assert.Equal(VatZone.Eu, VatZoneMapper.Resolve("  SWEDEN "));
    }
}
