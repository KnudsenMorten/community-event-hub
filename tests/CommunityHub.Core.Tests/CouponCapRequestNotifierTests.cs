using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1094/§1096 — "I need more tickets", from the partner's own page to the ops mailbox.
/// </summary>
/// <remarks>
/// Operator 2026-08-19/20: *"customer should be able to extend the cap using the status mail they
/// get … then i must get a email so i can extend it in zoho + i can invoice, if it is a prepaid
/// order"* · *"customer must see all the coupons they can for for each line, there must be a
/// Increase max button so they can extend the cap"* · *"so arrow dk will see 2 lines, with 2 buttons
/// to extend cap of, one of each"*.
/// </remarks>
public sealed class CouponCapRequestNotifierTests
{
    private static (CouponCapRequestNotifier Notifier, CapturingEmailSender Mail) New()
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        return (new CouponCapRequestNotifier(alerts), mail);
    }

    /// <summary>
    /// 🔴 <b>A PREPAID extension is a purchase — the mail must say so, because it ends in an
    /// invoice.</b> Same button for the partner, a different job for him.
    /// </summary>
    [Fact]
    public async Task A_prepaid_request_says_to_invoice_it()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAsync(
            customerLabel: "Arrow ECS Denmark", erpCustomerNumber: 1234,
            kind: CouponCapRequestNotifier.RequestKind.PrepaidTopUp,
            currentCap: 30, requestedTotal: 40, claimedSoFar: 28, note: null,
            couponName: "ARROW-DK-PREPAID"));

        var sent = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.ActionableRecipient, sent.To);
        Assert.Contains("ARROW-DK-PREPAID", sent.Subject);   // §1096 — WHICH code, in the subject
        // 🔑 §1104 — the INCREASE leads, not the total: "+10", then "30 → 40" as context. He asked
        // for this by name (*"focus on the extra / extend amount"*), because the delta is what gets
        // invoiced and what the Backstage limit moves by.
        Assert.Contains("10 more ticket(s)", sent.Subject);
        Assert.Contains("+10", sent.Html);
        Assert.Contains("Prepaid pool", sent.Html);
        Assert.Contains("40", sent.Html);
        Assert.Contains("28", sent.Html);
    }

    /// <summary>
    /// A capped AD-HOC extension raises a ceiling and bills nothing today — the mail must not send
    /// him looking for an invoice to raise.
    /// </summary>
    [Fact]
    public async Task An_ad_hoc_request_says_no_invoice_now()
    {
        var (notifier, mail) = New();

        Assert.True(await notifier.NotifyAsync(
            customerLabel: "Arrow ECS Denmark", erpCustomerNumber: 1234,
            kind: CouponCapRequestNotifier.RequestKind.AdHocCap,
            currentCap: 30, requestedTotal: 40, claimedSoFar: 29, note: "conference push",
            couponName: "ARROW-DK-ADHOC"));

        var sent = Assert.Single(mail.Messages);
        Assert.Contains("ARROW-DK-ADHOC", sent.Subject);
        Assert.Contains("raised their cap by 10", sent.Subject);
        Assert.Contains("Ad-hoc with a cap", sent.Html);
        Assert.Contains("+10", sent.Html);
        Assert.Contains("conference push", sent.Html);       // their note reaches him
    }

    /// <summary>
    /// 🔑 <b>The two agreements of ONE customer must be tellable apart at a glance.</b> Arrow Denmark
    /// holds both, so these two mails would otherwise read as duplicates — which is the exact
    /// confusion §1096's per-line buttons exist to remove.
    /// </summary>
    [Fact]
    public async Task Two_requests_from_one_customer_are_distinguishable_by_their_code()
    {
        var (notifier, mail) = New();

        await notifier.NotifyAsync(
            "Arrow ECS Denmark", 1234, CouponCapRequestNotifier.RequestKind.PrepaidTopUp,
            30, 40, 28, null, couponName: "ARROW-DK-PREPAID");
        await notifier.NotifyAsync(
            "Arrow ECS Denmark", 1234, CouponCapRequestNotifier.RequestKind.AdHocCap,
            30, 45, 29, null, couponName: "ARROW-DK-ADHOC");

        Assert.Equal(2, mail.Messages.Count);
        Assert.NotEqual(mail.Messages[0].Subject, mail.Messages[1].Subject);
        Assert.Contains("ARROW-DK-PREPAID", mail.Messages[0].Subject);
        Assert.Contains("ARROW-DK-ADHOC", mail.Messages[1].Subject);
    }

    /// <summary>Always states that CEH cannot enforce the limit — the limit is Zoho's.</summary>
    [Fact]
    public async Task The_mail_says_ceh_cannot_change_the_backstage_limit()
    {
        var (notifier, mail) = New();

        await notifier.NotifyAsync(
            "Arrow", 1234, CouponCapRequestNotifier.RequestKind.AdHocCap,
            30, 40, 29, null, couponName: "ARROW-DK-ADHOC");

        var sent = Assert.Single(mail.Messages);
        Assert.Contains("Backstage", sent.Html);
        Assert.Contains("CEH cannot change it", sent.Html);
    }

    [Fact]
    public async Task A_nonsense_total_sends_nothing()
    {
        var (notifier, mail) = New();

        Assert.False(await notifier.NotifyAsync(
            "Arrow", 1234, CouponCapRequestNotifier.RequestKind.AdHocCap,
            30, requestedTotal: 0, claimedSoFar: 29, note: null, couponName: "X"));
        Assert.Empty(mail.Messages);
    }
}
