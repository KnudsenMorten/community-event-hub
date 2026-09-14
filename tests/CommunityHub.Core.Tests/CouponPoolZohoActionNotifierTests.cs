using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §990 — *"i must also get an email which states to create the coupon inside zoho with the amount +
/// coupon name + ticket class, as there is no api in zoho to do this"* (operator 2026-08-09).
/// </summary>
/// <remarks>
/// 🔑 This is the gap between a partner paying and a partner being able to claim. §787.14: Backstage
/// exposes NO coupon API, so the promo code is created by hand. The hub already warns when a code is
/// WRONG (§795.1 closed pool, §796 exhausted pool); nothing told him about a code that does not exist
/// YET — so a partner could be invoiced for 20 tickets they had no way to redeem.
/// </remarks>
public sealed class CouponPoolZohoActionNotifierTests
{
    private static (CouponPoolZohoActionNotifier Notifier, CapturingEmailSender Mail) New()
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        return (new CouponPoolZohoActionNotifier(alerts), mail);
    }

    /// <summary>The three facts he asked for, by name: amount + coupon name + ticket class.</summary>
    [Fact]
    public async Task A_new_pool_asks_him_to_CREATE_the_code_with_the_three_facts()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAsync(
            couponName: "ARROW-FI", ticketClassLabel: "2-day (Pre-day + Main Event)",
            quantity: 20, totalPurchased: 20, isTopUp: false,
            invoiceNote: "e-conomic draft 5101 was created for these tickets."));

        var sent = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.ActionableRecipient, sent.To);

        Assert.Contains("create promo code", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ARROW-FI", sent.Subject);

        Assert.Contains("ARROW-FI", sent.Html);
        Assert.Contains("2-day (Pre-day + Main Event)", sent.Html);
        Assert.Contains("20", sent.Html);
        // Says WHY it is a manual step, so it does not read as the hub failing to sync.
        Assert.Contains("no coupon API", sent.Html);
        // The invoice fact rides along — "was this billed?" is read together with this or not at all.
        Assert.Contains("draft 5101", sent.Html);
    }

    /// <summary>
    /// 🔒 A TOP-UP must ask for the limit to be RAISED, and must quote the pool's NEW TOTAL. Telling
    /// him to "create" a code that exists, or quoting only the 25 just added, leaves the code capped
    /// at the old number — the §796 shape from the other direction: the partner paid, then hits a
    /// code that is used up.
    /// </summary>
    [Fact]
    public async Task A_top_up_asks_him_to_RAISE_the_limit_to_the_new_total()
    {
        var (notifier, mail) = New();

        await notifier.NotifyAsync("ARROW-FI", "2-day", quantity: 25, totalPurchased: 75,
            isTopUp: true, invoiceNote: null);

        var sent = Assert.Single(mail.Messages);
        Assert.Contains("raise", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("75", sent.Subject);
        Assert.Contains("RAISE", sent.Html);
        Assert.Contains("75", sent.Html);
        Assert.DoesNotContain("CREATE the promo code", sent.Html);
    }

    /// <summary>It is still sent when no invoice was raised — the code is needed either way.</summary>
    [Fact]
    public async Task It_is_sent_even_when_no_invoice_was_created()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAsync("ARROW-FI", "2-day", 20, 20, false, invoiceNote: null));
        Assert.Single(mail.Messages);
    }

    [Fact]
    public async Task A_blank_coupon_name_sends_nothing()
    {
        var (notifier, mail) = New();

        Assert.False(await notifier.NotifyAsync("  ", "2-day", 20, 20, false, null));
        Assert.Empty(mail.Messages);
    }

    // =====================================================================
    //  §1091 — the AD-HOC instruction (operator 2026-08-19: "rgr 2, create that")
    // =====================================================================

    /// <summary>
    /// With <b>no agreed price</b> both shares are percentages of the same ticket price, so the
    /// attendee's discount is exactly <c>100 − share</c> and the mail states it outright — that
    /// number is the entire point of the instruction.
    /// </summary>
    [Fact]
    public async Task A_fifty_fifty_split_tells_him_the_exact_discount_to_set()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAdHocAsync(
            couponName: "ARROW-2027", agreedUnitPriceDkk: null,
            invoicedSharePercent: 50, erpCustomerNumber: 1234, isNew: true));

        var sent = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.ActionableRecipient, sent.To);
        Assert.Contains("create promo code", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ARROW-2027", sent.Subject);

        Assert.Contains("50% discount", sent.Html);          // what to type into Backstage
        Assert.Contains("1234", sent.Html);                  // who gets the invoice
        Assert.Contains("no coupon API", sent.Html);         // why it is a manual step
    }

    /// <summary>
    /// 🔴 <b>THE CASE THAT MUST NOT GUESS.</b> With an agreed price the company pays
    /// <c>agreed × share</c>, which has no fixed relationship to the ticket's list price — so
    /// "100 − share" would be a plausible number that is simply wrong, printed on an instruction he
    /// would follow into Backstage.
    /// <para>🔑 Asserting an ABSENCE as well as a presence: the mail must state the DKK the company
    /// is billed and must NOT state a discount percentage.</para>
    /// </summary>
    [Fact]
    public async Task An_agreed_price_makes_the_discount_underivable_and_the_mail_refuses_to_invent_one()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAdHocAsync(
            couponName: "ARROW-2027", agreedUnitPriceDkk: 3000m,
            invoicedSharePercent: 50, erpCustomerNumber: 1234, isNew: true));

        var sent = Assert.Single(mail.Messages);

        Assert.Contains("1500.00 DKK", sent.Html);       // what the company is actually billed
        Assert.Contains("3000.00 DKK", sent.Html);       // the agreed price it came from
        Assert.Contains("yourself", sent.Html);          // explicitly his call
        // 🔒 The wrong-but-plausible number must be absent.
        Assert.DoesNotContain("50% discount", sent.Html);
    }

    /// <summary>At 100% the attendee pays nothing, so the code is a 100% discount — said plainly.</summary>
    [Fact]
    public async Task A_hundred_percent_share_means_a_hundred_percent_discount()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAdHocAsync("ARROW-2027", null, null, 1234, isNew: true));

        Assert.Contains("100% discount", Assert.Single(mail.Messages).Html);
    }

    /// <summary>
    /// A coupon with no e-conomic customer cannot be invoiced at all, so the mail says so rather
    /// than printing a blank row that reads like a rendering fault.
    /// </summary>
    [Fact]
    public async Task A_coupon_with_no_customer_says_it_cannot_be_invoiced_yet()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAdHocAsync("ARROW-2027", null, 50, null, isNew: true));

        Assert.Contains("cannot be invoiced yet", Assert.Single(mail.Messages).Html);
    }

    /// <summary>An edit to an existing coupon asks him to CHECK the code, not to create it again.</summary>
    [Fact]
    public async Task A_changed_split_asks_him_to_check_the_existing_code()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAdHocAsync("ARROW-2027", null, 30, 1234, isNew: false));

        var sent = Assert.Single(mail.Messages);
        Assert.Contains("split changed", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create promo code", sent.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("70% discount", sent.Html);
    }

    [Fact]
    public async Task A_blank_coupon_name_sends_no_ad_hoc_instruction()
    {
        var (notifier, mail) = New();

        Assert.False(await notifier.NotifyAdHocAsync("  ", null, 50, 1234, isNew: true));
        Assert.Empty(mail.Messages);
    }
}
