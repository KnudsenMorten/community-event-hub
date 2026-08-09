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
}
