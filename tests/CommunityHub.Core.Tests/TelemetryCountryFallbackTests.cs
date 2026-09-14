using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1147 — what the "Resident of Attendee" chart says when an attendee has no country.
///
/// <para>Operator 2026-08-28: <i>"how can country be blank"</i> → traced to a FREE order. Zoho
/// collects no billing address when there is nothing to invoice, so the country an attendee
/// inherits from their order simply does not exist. Verified on order 14880000004455013: total 0,
/// 100% discount, promo <c>ELDK27-CBS</c>, every billing field null, three tickets — matching his
/// own Backstage screenshot exactly.</para>
///
/// <para>His fix: <i>"here we need to lookup against the coupon module, where you can fetch the
/// customer details incl. country"</i> — the coupon carries an ERP customer, and that customer has
/// a country.</para>
///
/// <para>🔒 <b>DISPLAY ONLY, his decision.</b> The resolved value is the COUPON CUSTOMER's country,
/// not the attendee's own, so it is never written to <c>Attendee.Country</c> — exports and on-site
/// lists keep the honest blank. A derivation stored in a data column stops looking like a
/// derivation the moment somebody exports it.</para>
/// </summary>
public class TelemetryCountryFallbackTests
{
    private static string Label(string? own, string? viaCoupon) =>
        AttendeeTelemetryService.ResolveCountryLabel(own, viaCoupon);

    /// <summary>🔒 A country the attendee actually gave us always wins.</summary>
    [Fact]
    public void The_attendees_OWN_country_beats_the_coupon_customers()
    {
        // The stand-in must never override a value we genuinely have — otherwise a Swede who bought
        // on a Danish company's coupon block is silently relabelled Danish.
        Assert.Equal("Sweden", Label("Sweden", "Denmark"));
    }

    [Fact]
    public void A_blank_country_falls_back_to_the_coupon_customer()
    {
        // The ELDK27-CBS case: no billing address on the free order, but the coupon's ERP customer
        // is Danish.
        Assert.Equal("Denmark", Label(null, "Denmark"));
        Assert.Equal("Denmark", Label("   ", "Denmark"));
    }

    /// <summary>
    /// ⚠️ "Not given", NOT "free/coupon order".
    /// </summary>
    /// <remarks>
    /// Every blank measured on 2026-08-28 came from a coupon order, but that is an observation about
    /// this week's data, not a rule: a paid order with an incomplete address lands here too, and the
    /// coupon wording would then be a confident lie on a chart he reads at a glance.
    /// </remarks>
    [Fact]
    public void With_neither_it_says_Not_given()
    {
        Assert.Equal("Not given", Label(null, null));
        Assert.Equal("Not given", Label("", ""));
        Assert.Equal("Not given", Label("  ", null));
    }

    [Fact]
    public void Values_are_trimmed_so_one_country_is_one_slice()
    {
        // Untrimmed values would split "Denmark" and "Denmark " into two rows of the same country,
        // which is exactly the kind of quiet miscount a percentage chart hides.
        Assert.Equal("Denmark", Label(" Denmark ", null));
        Assert.Equal("Denmark", Label(null, " Denmark "));
    }

    [Fact]
    public void A_country_CODE_is_used_when_that_is_all_we_have()
    {
        // The caller passes `Country ?? CountryCode`, so a code-only attendee still gets a slice
        // rather than being lumped into "Not given".
        Assert.Equal("DK", Label("DK", null));
    }
}
