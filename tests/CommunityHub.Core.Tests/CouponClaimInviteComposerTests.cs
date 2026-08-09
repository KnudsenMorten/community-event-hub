using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1016c — the partner-facing "you can start claiming" mail.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"can you also make a button which will notify the requester, that he
/// can now use the coupon code"*, with two sample mails he wrote himself. <b>His copy is the spec</b>
/// and is reproduced as written — he wrote it to partners he has a relationship with.</para>
///
/// <para>🔑 This is the FIRST outward-facing coupon mail. Every other one goes to <c>info@</c> and
/// asks an ORGANIZER to act, so a wrong word costs nothing; this one reaches a paying customer and
/// quotes a real invoice number at them.</para>
/// </remarks>
public sealed class CouponClaimInviteComposerTests
{
    private const string Base = "https://eldk27.expertslive.dk";

    private static CouponClaimInviteComposer.Invite Prepaid(
        int? qty = 20, string? invoice = "170", string? reference = "Registration fee") =>
        new("Experts Live Denmark 2027 (ELDK27)", "ELDK27-Arrow-Finland-pool1", "2-day tickets",
            Base, IsPrepaid: true, Quantity: qty, InvoiceNumber: invoice, Reference: reference);

    private static CouponClaimInviteComposer.Invite AdHoc(int intervalDays = 14) =>
        new("Experts Live Denmark 2027 (ELDK27)", "ELDK27-Arrow-Finland-pool1", "2-day tickets",
            Base, IsPrepaid: false, Reference: "Registration fee", IntervalDays: intervalDays);

    [Fact]
    public void The_prepaid_mail_says_everything_his_draft_says()
    {
        var (subject, html) = CouponClaimInviteComposer.Build(Prepaid());

        Assert.Equal("Coupon-link for Experts Live Denmark 2027 (ELDK27) Ticket claim", subject);
        Assert.Contains("Thank You again for your support", html);
        Assert.Contains("20 pre-paid", html);                       // the count they check first
        Assert.Contains("2-day tickets", html);                     // the CLASS NAME, never its id
        Assert.Contains("#/buyTickets?promoCode=", html);
        Assert.Contains("Extend with more tickets", html);
        Assert.Contains("DKK 3000/EURO390", html);                  // whole kroner, not 3000.00
        Assert.Contains("I have sent invoice #170", html);
        Assert.Contains("reference: Registration fee", html);
        Assert.Contains("Experts Live Denmark organizer-team", html);
    }

    [Fact]
    public void The_ad_hoc_mail_drops_the_quantity_and_the_extend_paragraph()
    {
        // That difference IS the two agreements: an ad-hoc partner has bought nothing yet, so there
        // is no count to quote and nothing to extend.
        var (_, html) = CouponClaimInviteComposer.Build(AdHoc());

        Assert.DoesNotContain("pre-paid", html);
        Assert.DoesNotContain("Extend with more tickets", html);
        Assert.DoesNotContain("I have sent invoice", html);
        Assert.Contains("We will invoice you bi-weekly when a ticket is claimed.", html);
        Assert.Contains("We will add the reference Registration fee", html);
    }

    /// <summary>
    /// 🔴 §1016d — the cadence sentence is DERIVED from the coupon's own billing period, so the
    /// promise and the mechanism cannot drift.
    /// </summary>
    /// <remarks>
    /// His draft said *"bi-weekly"* while the job invoiced within minutes of every claim — a promise
    /// the system actively broke. Now the same number that paces the invoicing writes the sentence,
    /// so changing one changes the other.
    /// </remarks>
    [Theory]
    [InlineData(14, "bi-weekly")]
    [InlineData(7, "weekly")]
    [InlineData(30, "monthly")]
    [InlineData(21, "every 21 days")]
    [InlineData(0, "for each claim")]
    public void The_billing_cadence_is_stated_from_the_real_interval(int days, string expected)
    {
        var (_, html) = CouponClaimInviteComposer.Build(AdHoc(days));
        Assert.Contains($"We will invoice you {expected} when a ticket is claimed.", html);
    }

    /// <summary>
    /// 🔒 A missing invoice number DROPS the sentence rather than printing a blank. "I have sent
    /// invoice # to you today" sends a partner looking for a document that does not exist.
    /// </summary>
    [Fact]
    public void No_invoice_number_means_no_invoice_sentence()
    {
        var (_, html) = CouponClaimInviteComposer.Build(Prepaid(invoice: null));

        Assert.DoesNotContain("I have sent invoice", html);
        Assert.Contains("20 pre-paid", html);       // …but the rest of the mail still stands
    }

    [Fact]
    public void The_claim_url_carries_the_promo_code_inside_the_fragment()
    {
        // ⚠️ The '#' matters: Backstage's buy flow is a fragment route, so a code placed before it
        // would be sent to the server and never reach the page that reads it.
        var url = CouponClaimInviteComposer.ClaimUrl(Base, "ELDK27-Arrow-Finland-pool1");

        Assert.Equal("https://eldk27.expertslive.dk#/buyTickets?promoCode=ELDK27-Arrow-Finland-pool1", url);
        // A trailing slash on the configured base must not produce a double slash.
        Assert.Equal(url, CouponClaimInviteComposer.ClaimUrl(Base + "/", "ELDK27-Arrow-Finland-pool1"));
    }

    [Fact]
    public void A_coupon_name_needing_escaping_still_produces_a_usable_link()
    {
        var url = CouponClaimInviteComposer.ClaimUrl(Base, "ELDK 27&pool=2");
        Assert.Contains("promoCode=ELDK%2027%26pool%3D2", url);
    }

    [Fact]
    public void The_variant_and_the_reference_come_from_the_coupon_rule()
    {
        // 🔒 `From` is the single place that decides where each value comes from, so the page and
        // the tests cannot disagree about it. Reference = the COUPON's Notes (his choice), which is
        // also the invoice sub-heading — the partner reads the same words in both places.
        var rule = new CouponInvoicingSetting
        {
            CouponName = "ARROW-FI",
            BillingType = CouponBillingType.AllocatedPrepaymentByCustomer,
            Notes = "Registration fee for Experts Live Denmark 2027 conference",
            InvoiceIntervalDays = null,
        };

        var invite = CouponClaimInviteComposer.From(
            rule, "ELDK27", "2-day ticket", Base, defaultIntervalDays: 14,
            extendDkk: 3000m, extendEur: 390m, prepaidQuantity: 20, invoiceNumber: "170");

        Assert.True(invite.IsPrepaid);
        Assert.Equal("Registration fee for Experts Live Denmark 2027 conference", invite.Reference);
        Assert.Equal(14, invite.IntervalDays);        // blank per-coupon ⇒ the edition default

        // …and a per-coupon override wins, so the sentence matches that partner's real terms.
        rule.InvoiceIntervalDays = 7;
        Assert.Equal(7, CouponClaimInviteComposer.From(
            rule, "ELDK27", "2-day ticket", Base, 14, 3000m, 390m).IntervalDays);
    }

    [Fact]
    public void The_extend_prices_come_from_config_not_from_literals()
    {
        // Next year's prices are a settings change, not a release.
        var (_, html) = CouponClaimInviteComposer.Build(
            Prepaid() with { ExtendPriceDkk = 3250m, ExtendPriceEur = 425m });

        Assert.Contains("DKK 3250/EURO425", html);
        Assert.DoesNotContain("DKK 3000", html);
    }
}
