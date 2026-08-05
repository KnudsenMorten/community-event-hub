using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §813 — an order that cannot be invoiced must reach a human, not just the log.
/// </summary>
/// <remarks>
/// <para>🔴 Since the cutover (§786.4) the retired VM script is stopped and is <b>not a fallback</b>,
/// so an order CEH refuses is invoiced by nobody. Both invoice services already produce a named,
/// human reason for every skip — they were only ever written to the log. This is §795.3's argument
/// applied to the other half: a draft nobody knows about is invisible revenue, and so is an invoice
/// that was never created at all.</para>
/// </remarks>
public sealed class InvoiceProblemNotifierTests
{
    private static (InvoiceProblemNotifier Notifier, CapturingEmailSender Mail) New()
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        return (new InvoiceProblemNotifier(alerts), mail);
    }

    private const string MissingCustomer =
        "Order 10811: company 'Example A/S' has no erp_customer_number set in Company Manager, "
        + "so there is no e-conomic customer to invoice.";

    private const string NoRate =
        "Order 10726: customer 'Example A/S' invoices in DKK, but no exchange-rate source is "
        + "configured (FxRates), so the amounts cannot be converted. NOT invoiced.";

    [Fact]
    public async Task It_names_every_reason_and_says_nobody_else_will_invoice_them()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAsync("Webshop orders", new[] { MissingCustomer, NoRate }));

        var sent = Assert.Single(mail.Messages);
        // §808 — an ISSUE, so the developer mailbox rather than the actionable one.
        Assert.Equal(EngineAlertSender.Recipient, sent.To);
        Assert.Contains("2 item(s) cannot be invoiced", sent.Subject);
        Assert.Contains("Webshop orders", sent.Subject);
        Assert.Contains("erp_customer_number", sent.Html);
        Assert.Contains("exchange-rate source", sent.Html);

        // 🔴 The fact that makes it urgent: there is no second system any more.
        Assert.Contains("Nothing else is going to invoice these", sent.Html);
    }

    /// <summary>🔒 The §302 rule: a clean pass must be completely silent.</summary>
    [Fact]
    public async Task Nothing_stuck_sends_nothing()
    {
        var (notifier, mail) = New();

        Assert.False(await notifier.NotifyAsync("Webshop orders", Array.Empty<string>()));
        Assert.Empty(mail.Sent);
    }

    // ---------------------------------------------------------------------
    //  §813.1 — the throttle key is the PROBLEM SET, not the job
    // ---------------------------------------------------------------------

    /// <summary>
    /// The same stuck order must not mail every hour — an unchanged set keeps its key and stays
    /// inside the sender's quiet window.
    /// </summary>
    [Fact]
    public void The_same_problems_produce_the_same_key()
    {
        Assert.Equal(
            InvoiceProblemNotifier.ThrottleKeyFor(new[] { MissingCustomer, NoRate }),
            InvoiceProblemNotifier.ThrottleKeyFor(new[] { MissingCustomer, NoRate }));
    }

    /// <summary>...including when the sweep reports them in a different order.</summary>
    [Fact]
    public void The_order_of_the_problems_does_not_change_the_key()
    {
        Assert.Equal(
            InvoiceProblemNotifier.ThrottleKeyFor(new[] { MissingCustomer, NoRate }),
            InvoiceProblemNotifier.ThrottleKeyFor(new[] { NoRate, MissingCustomer }));
    }

    /// <summary>
    /// 🔴 THE HALF THAT MATTERS: a NEW stuck order changes the key, so it is reported on the next
    /// run instead of waiting for somebody else's quiet period to expire (§796's rule, without a
    /// table).
    /// </summary>
    [Fact]
    public void A_new_problem_changes_the_key()
    {
        var before = InvoiceProblemNotifier.ThrottleKeyFor(new[] { MissingCustomer });
        var after = InvoiceProblemNotifier.ThrottleKeyFor(new[] { MissingCustomer, NoRate });

        Assert.NotEqual(before, after);
    }

    /// <summary>And a problem that has been FIXED changes it too — the set is the identity.</summary>
    [Fact]
    public void Resolving_a_problem_changes_the_key()
    {
        var both = InvoiceProblemNotifier.ThrottleKeyFor(new[] { MissingCustomer, NoRate });
        var one = InvoiceProblemNotifier.ThrottleKeyFor(new[] { NoRate });

        Assert.NotEqual(both, one);
    }
}
