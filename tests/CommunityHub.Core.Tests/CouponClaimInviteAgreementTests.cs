using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1117 — the claim invite states the AGREEMENT it is opening.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21: *"the claim mail must include: type of ticket (prepaid, adhoc billing,
/// free). Amount, cap (if any), billing frequency"*.</para>
///
/// <para>🔑 <b>These facts were partly present and wholly unfindable.</b> The cadence lived in the
/// invoice paragraph, the quantity in the claiming paragraph, and the price and the cap nowhere at
/// all — so the mail that OPENS a paying relationship never stated its terms. They are now one
/// table, because a finance person checks them one at a time.</para>
///
/// <para>🔴 <b><c>IsPrepaid</c> could not carry the type.</b> A bool has two states and there are
/// three kinds; "not prepaid" was rendering a FREE allocation as one that gets invoiced as tickets
/// are claimed. That is the case these tests care about most.</para>
///
/// <para>FAKE values only.</para>
/// </remarks>
public sealed class CouponClaimInviteAgreementTests
{
    private static CouponClaimInviteComposer.Invite Base(
        CouponBillingType? type = null,
        bool isPrepaid = false,
        int? quantity = null,
        decimal? price = null,
        int? cap = null,
        int? share = null,
        int intervalDays = 14) =>
        new(
            EventDisplayName: "Experts Live Denmark 2027",
            CouponName: "ELDK27-Partner-pool1",
            TicketClassLabel: "2-day (Pre-day + Main Event)",
            TicketBaseUrl: "https://tickets.example.test",
            IsPrepaid: isPrepaid,
            Quantity: quantity,
            IntervalDays: intervalDays,
            BillingType: type,
            AgreedUnitPriceDkk: price,
            CapTickets: cap,
            InvoicedSharePercent: share);

    private static string Html(CouponClaimInviteComposer.Invite i)
        => CouponClaimInviteComposer.Build(i).Html;

    [Fact]
    public void A_FREE_coupon_is_never_described_as_one_that_gets_invoiced()
    {
        // ⚰️ The defect: with only a bool, "not prepaid" meant "billed as used" — so a free
        // allocation told the recipient we would invoice them for it.
        var html = Html(Base(CouponBillingType.NoInvoicing));

        Assert.Contains("Free", html, StringComparison.Ordinal);
        Assert.Contains("not invoiced", html, StringComparison.Ordinal);

        // 🔒 And no cadence: a billing frequency on a free coupon is a promise to bill nobody.
        Assert.DoesNotContain("every 2 weeks", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PREPAID_coupon_says_it_is_paid_up_front_rather_than_quoting_a_cadence()
    {
        var html = Html(Base(
            CouponBillingType.AllocatedPrepaymentByCustomer, isPrepaid: true, quantity: 30,
            price: 3000m));

        Assert.Contains("Prepaid", html, StringComparison.Ordinal);
        Assert.Contains("Tickets bought", html, StringComparison.Ordinal);
        Assert.Contains("DKK 3000", html, StringComparison.Ordinal);
        // §1108 — "invoice every 14 days" made no sense on a pool that is already paid for.
        Assert.Contains("Paid up front", html, StringComparison.Ordinal);
        Assert.DoesNotContain("every 2 weeks", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_AD_HOC_coupon_states_the_real_cadence_the_cap_and_the_split()
    {
        var html = Html(Base(
            CouponBillingType.ClaimableAdHocPaymentByCustomer,
            price: 3000m, cap: 30, share: 50, intervalDays: 7));

        Assert.Contains("Billed as used", html, StringComparison.Ordinal);
        Assert.Contains("every week", html, StringComparison.Ordinal);   // from the REAL interval
        // §1091 — a partner who agreed to 50% and reads only the unit price queries the first invoice.
        Assert.Contains("50%", html, StringComparison.Ordinal);
        // ⚠️ The cap is described as THEIRS: the number they asked for and can raise.
        Assert.Contains("Your limit", html, StringComparison.Ordinal);
        Assert.Contains("you can raise it", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_values_are_OMITTED_rather_than_printed_blank_or_guessed()
    {
        // No price, no cap, no share — a plain ad-hoc coupon.
        var html = Html(Base(CouponBillingType.ClaimableAdHocPaymentByCustomer));

        Assert.DoesNotContain("Your limit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Price", html, StringComparison.Ordinal);
        Assert.DoesNotContain("DKK ", html, StringComparison.Ordinal);
        // What IS known still shows.
        Assert.Contains("Billed as used", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_UNMAPPED_coupon_invents_no_commercial_terms_at_all()
    {
        // 🔴 There is no agreed billing yet. Saying "billed as used" would be making one up in the
        // mail that opens the relationship, so the whole block is dropped.
        var html = Html(Base(CouponBillingType.Unmapped));

        Assert.DoesNotContain("Your agreement", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Billed as used", html, StringComparison.Ordinal);
        // The mail is still a working claim invite.
        Assert.Contains("#/buyTickets?promoCode=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void From_reads_the_agreement_off_the_STORED_rule_so_no_caller_can_forget_a_term()
    {
        var rule = new CouponInvoicingSetting
        {
            CouponName = "ELDK27-Partner-adhoc",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            AgreedUnitPriceDkk = 3000m,
            ClaimCapTickets = 45,
            InvoicedSharePercent = 50,
            InvoiceIntervalDays = 7,
        };

        var invite = CouponClaimInviteComposer.From(
            rule, "Experts Live Denmark 2027", "2-day (Pre-day + Main Event)",
            "https://tickets.example.test", defaultIntervalDays: 14,
            extendDkk: 3000m, extendEur: 390m);

        Assert.Equal(CouponBillingType.ClaimableAdHocPaymentByCustomer, invite.BillingType);
        Assert.Equal(3000m, invite.AgreedUnitPriceDkk);
        Assert.Equal(45, invite.CapTickets);
        Assert.Equal(50, invite.InvoicedSharePercent);
        Assert.Equal(7, invite.IntervalDays);
    }
}
