using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1091 — how much the LINKED COMPANY is invoiced for one claimed ad-hoc coupon ticket.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19: *"i need the possibility to include a percentage for ad-hoc coupons,
/// where we support part-payment … like 50/50 so the partner pays 50% of the ticket … and then the
/// linked company fx arrow gets a adhoc invoice of the other 50%"* · *"remember to use the agreed
/// price per coupon when calculating"* · *"prio 1: custom (agreed) price and prio 2 if prio 1 is not
/// filled out: use zoho price"*.</para>
///
/// <para>🔑 This is the only arithmetic in the coupon path that decides how much money a partner is
/// asked for, so it is tested on its own — no invoice, no HTTP client, no FX rate.</para>
/// </remarks>
public sealed class CouponBillableShareTests
{
    // ---------------------------------------------------------------------
    //  Priority: agreed price first, Zoho price second
    // ---------------------------------------------------------------------

    [Fact]
    public void The_agreed_price_wins_over_what_the_ticket_actually_sold_for()
        => Assert.Equal(3000m, CouponBillableShare.Dkk(3500m, agreedUnitPriceDkk: 3000m, invoicedSharePercent: 100));

    [Fact]
    public void Without_an_agreed_price_it_falls_back_to_the_zoho_ticket_price()
        => Assert.Equal(3500m, CouponBillableShare.Dkk(3500m, agreedUnitPriceDkk: null, invoicedSharePercent: 100));

    /// <summary>His worked example: a 50/50 on an agreed 3000 DKK ticket.</summary>
    [Fact]
    public void A_fifty_fifty_split_bills_the_company_half_of_the_agreed_price()
        => Assert.Equal(1500m, CouponBillableShare.Dkk(3500m, agreedUnitPriceDkk: 3000m, invoicedSharePercent: 50));

    [Fact]
    public void A_split_without_an_agreed_price_takes_the_share_of_the_zoho_price()
        => Assert.Equal(1750m, CouponBillableShare.Dkk(3500m, agreedUnitPriceDkk: null, invoicedSharePercent: 50));

    // ---------------------------------------------------------------------
    //  🔴 Null is never zero — the failure nobody complains about
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 <b>Every ad-hoc coupon in existence predates both fields.</b> Reading either null as 0
    /// would invoice a partner nothing and report success — the worst kind of billing bug, because
    /// an under-billed customer never rings up to correct you.
    /// </summary>
    [Fact]
    public void Both_fields_unset_bills_exactly_what_it_always_did_the_whole_ticket()
        => Assert.Equal(3500m, CouponBillableShare.Dkk(3500m, agreedUnitPriceDkk: null, invoicedSharePercent: null));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(1000)]
    public void A_nonsense_percentage_falls_back_to_the_whole_ticket_never_to_nothing(int percent)
        => Assert.Equal(3500m, CouponBillableShare.Dkk(3500m, agreedUnitPriceDkk: null, invoicedSharePercent: percent));

    /// <summary>
    /// 🔒 A zero or negative agreed price is a typo, not an agreement to bill nothing. It is ignored
    /// and the ticket price is used — the sweep must never turn a slip of the keyboard into a
    /// silently free coupon.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void A_zero_or_negative_agreed_price_is_ignored_not_honoured(decimal agreed)
        => Assert.Equal(3500m, CouponBillableShare.Dkk(3500m, agreed, invoicedSharePercent: 100));

    // ---------------------------------------------------------------------
    //  Rounding
    // ---------------------------------------------------------------------

    /// <summary>
    /// An odd price and an odd percentage: 3499.65 × 33% = 1154.8845, rounded to 2 decimals away
    /// from zero, matching what the composer does with the converted figure so the two never
    /// disagree about the last øre.
    /// </summary>
    [Fact]
    public void The_share_is_rounded_to_two_decimals()
        => Assert.Equal(1154.88m, CouponBillableShare.Dkk(3499.65m, agreedUnitPriceDkk: null, invoicedSharePercent: 33));

    [Fact]
    public void A_hundred_percent_is_returned_exactly_never_re_rounded()
        => Assert.Equal(3499.657m, CouponBillableShare.Dkk(3499.657m, null, 100));

    // ---------------------------------------------------------------------
    //  The note on the partner's invoice
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔑 A line reading "1,500.00" against a 3,000 ticket starts an e-mail thread; saying why ends
    /// it before it starts.
    /// </summary>
    [Fact]
    public void The_invoice_line_says_what_share_of_which_price_it_is()
    {
        var note = CouponBillableShare.ComposeShareNote(3500m, agreedUnitPriceDkk: 3000m, invoicedSharePercent: 50);

        Assert.NotNull(note);
        Assert.Contains("50%", note!, System.StringComparison.Ordinal);
        Assert.Contains("3000.00", note, System.StringComparison.Ordinal);
        Assert.Contains("agreed price", note, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_note_names_the_zoho_price_when_there_is_no_agreement()
    {
        var note = CouponBillableShare.ComposeShareNote(3500m, agreedUnitPriceDkk: null, invoicedSharePercent: 50);

        Assert.NotNull(note);
        Assert.Contains("ticket price", note!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3500.00", note, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 Silent in the ordinary case. Every invoice CEH has ever raised is 100% of the ticket
    /// price; printing "Invoiced: 100% of …" on all of them would be new noise on every line of
    /// every existing partner's invoice, to say nothing had changed.
    /// </summary>
    [Fact]
    public void There_is_no_note_at_a_plain_hundred_percent_of_the_ticket_price()
        => Assert.Null(CouponBillableShare.ComposeShareNote(3500m, agreedUnitPriceDkk: null, invoicedSharePercent: null));

    /// <summary>But an AGREED price is worth stating even at 100% — it is not the ticket price.</summary>
    [Fact]
    public void An_agreed_price_is_noted_even_when_the_whole_of_it_is_billed()
    {
        var note = CouponBillableShare.ComposeShareNote(3500m, agreedUnitPriceDkk: 3000m, invoicedSharePercent: null);

        Assert.NotNull(note);
        Assert.Contains("3000.00", note!, System.StringComparison.Ordinal);
        Assert.Contains("agreed price", note, System.StringComparison.OrdinalIgnoreCase);
    }
}
