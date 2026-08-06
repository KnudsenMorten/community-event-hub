using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §894 — the country mapper. Every spelling below was taken from the REAL e-conomic customer list
/// in the 2026-08-06 reconcile mail, which is the point: the map is grounded in the data rather than
/// in an imagined atlas.
///
/// <para>🔴 The mail carried <b>sixty</b> "e-conomic country X is not a 2-letter code" lines — a
/// safety valve that became the noise it was meant to prevent. Operator: *"make a Country function
/// mapper using these names and dont throw this at me"*.</para>
/// </summary>
public sealed class CountryCodeMapperTests
{
    /// <summary>Every distinct spelling that actually appeared in that e-mail.</summary>
    [Theory]
    [InlineData("Denmark", "DK")]
    [InlineData("Danmark", "DK")]
    [InlineData("Germany", "DE")]
    [InlineData("Sweden", "SE")]
    [InlineData("Norway", "NO")]
    [InlineData("Finland", "FI")]
    [InlineData("Poland", "PL")]
    [InlineData("Netherlands", "NL")]
    [InlineData("Switzerland", "CH")]
    [InlineData("China", "CN")]
    [InlineData("Ireland", "IE")]
    [InlineData("Canada", "CA")]
    [InlineData("United Kingdom", "GB")]
    [InlineData("United States", "US")]
    [InlineData("USA", "US")]
    [InlineData("United States Of America", "US")]
    public void Every_spelling_from_the_real_customer_list_maps(string input, string expected)
        => Assert.Equal(expected, CountryCodeMapper.ToIso2(input));

    /// <summary>
    /// "United Kingdom (UK)" and "United States (US)" are both in the live data — the parenthetical
    /// is a hint for humans and noise for a lookup.
    /// </summary>
    [Theory]
    [InlineData("United Kingdom (UK)", "GB")]
    [InlineData("United States (US)", "US")]
    public void A_trailing_parenthetical_is_ignored(string input, string expected)
        => Assert.Equal(expected, CountryCodeMapper.ToIso2(input));

    [Theory]
    [InlineData("dk", "DK")]
    [InlineData("DK", "DK")]
    [InlineData("  gb  ", "GB")]
    public void An_existing_code_passes_through(string input, string expected)
        => Assert.Equal(expected, CountryCodeMapper.ToIso2(input));

    /// <summary>The ERP is Danish; half the rows use Danish spellings.</summary>
    [Theory]
    [InlineData("Tyskland", "DE")]
    [InlineData("Sverige", "SE")]
    [InlineData("Østrig", "AT")]
    [InlineData("Færøerne", "FO")]
    public void Danish_spellings_and_accents_map(string input, string expected)
        => Assert.Equal(expected, CountryCodeMapper.ToIso2(input));

    /// <summary>
    /// 🔒 §582 — unknown returns null, which every caller treats as "do nothing". Mapping is the fix
    /// for names we recognise; it is not a licence to guess a country onto a billing address.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Wakanda")]
    [InlineData("XX")]          // a 2-letter word is NOT automatically a country
    public void Unknown_returns_null_rather_than_guessing(string? input)
        => Assert.Null(CountryCodeMapper.ToIso2(input));

    /// <summary>The comparison the ERP sync and the speaker gap report both rely on.</summary>
    [Theory]
    [InlineData("Danmark", "DK")]
    [InlineData("Denmark", "dk")]
    [InlineData("United States Of America", "US")]
    [InlineData("United Kingdom (UK)", "GB")]
    public void The_same_country_written_two_ways_matches(string a, string b)
        => Assert.True(CountryCodeMapper.SameCountry(a, b));

    [Fact]
    public void A_genuine_disagreement_does_not_match()
        => Assert.False(CountryCodeMapper.SameCountry("Danmark", "DE"));

    /// <summary>Unknown on either side ⇒ silence, never a false accusation.</summary>
    [Theory]
    [InlineData("Wakanda", "DK")]
    [InlineData("Denmark", "Wakanda")]
    public void An_unmappable_value_is_never_reported_as_a_mismatch(string a, string b)
        => Assert.True(CountryCodeMapper.SameCountry(a, b));
}
