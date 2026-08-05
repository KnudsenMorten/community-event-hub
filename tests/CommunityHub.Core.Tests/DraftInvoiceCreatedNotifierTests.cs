using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §795.3 — *"notify info also when new invoices was created in draft"*.
/// </summary>
/// <remarks>
/// 🔑 A draft reaches nobody until a human books it. Drafts created and never booked are invisible
/// revenue — the invoice exists, the money is owed, and nobody is waiting for payment because nobody
/// knows it is there. These tests hold the two silences that keep the mail trustworthy: nothing
/// created ⇒ no mail, and a DRY RUN creates nothing so it can never announce one.
/// </remarks>
public sealed class DraftInvoiceCreatedNotifierTests
{
    private static (DraftInvoiceCreatedNotifier Notifier, CapturingEmailSender Mail) New()
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        return (new DraftInvoiceCreatedNotifier(alerts), mail);
    }

    private static CreatedDraftInvoice Draft(
        string what = "Coupon 'ARROW-DK' — 3 ticket(s)", decimal total = 10500m,
        string currency = "DKK", int number = 1042) =>
        new(what, 4242, "Arrow DK A/S", "ARROW-DK-9001", total, currency, number);

    [Fact]
    public async Task It_names_what_was_created_and_goes_to_the_actionable_mailbox()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAsync("Coupon invoicing", new[] { Draft() }));

        var sent = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.ActionableRecipient, sent.To);
        Assert.Contains("1 new draft invoice(s) created", sent.Subject);
        Assert.Contains("Coupon invoicing", sent.Subject);

        // What was created, who is billed, how much, and the number to find it by.
        Assert.Contains("ARROW-DK", sent.Html);
        Assert.Contains("Arrow DK A/S", sent.Html);
        Assert.Contains("4242", sent.Html);
        Assert.Contains("10500 DKK", sent.Html);
        Assert.Contains("draft 1042", sent.Html);
        Assert.Contains("ARROW-DK-9001", sent.Html);
    }

    /// <summary>
    /// 🔒 The §302 rule. A "nothing happened" mail every hour trains the recipient to ignore the one
    /// that matters.
    /// </summary>
    [Fact]
    public async Task Nothing_created_sends_nothing()
    {
        var (notifier, mail) = New();

        Assert.False(await notifier.NotifyAsync(
            "Coupon invoicing", Array.Empty<CreatedDraftInvoice>()));
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// ⚠️ It says these are DRAFTS and what that costs. The point of the mail is that an unbooked
    /// draft is an invoice the customer never receives.
    /// </summary>
    [Fact]
    public async Task It_says_plainly_that_a_draft_is_not_an_invoice_yet()
    {
        var (notifier, mail) = New();

        await notifier.NotifyAsync("Webshop orders", new[] { Draft() });

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("until somebody books them", html);
        Assert.Contains("never receives", html);
    }

    /// <summary>
    /// ⚠️ Totals are only summed when there is ONE currency. An added-up EUR+DKK figure would be a
    /// number that means nothing, printed with authority.
    /// </summary>
    [Fact]
    public async Task It_does_not_add_up_amounts_across_currencies()
    {
        var (notifier, mail) = New();

        await notifier.NotifyAsync("Webshop orders", new[]
        {
            Draft(total: 1000m, currency: "DKK", number: 1),
            Draft(total: 500m, currency: "EUR", number: 2),
        });

        var html = Assert.Single(mail.Messages).Html;
        Assert.DoesNotContain("1500", html);
        Assert.Contains("1000 DKK", html);
        Assert.Contains("500 EUR", html);
    }
}
