using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §795.2 — chases a PREPAID pool that carries no e-conomic invoice number.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"we need to link an invoice number manually from erp for prepaid as
/// confirmation it was billed. otherwise reminder mails"*.</para>
///
/// <para>🔴 <b>What this chases is a prepayment nobody can prove was ever charged.</b> §794 made a
/// prepaid coupon deliberately un-invoiceable — CEH must never bill a partner a second time for
/// tickets they already own — and that left a hole: the allocation records what they BOUGHT, not
/// that anyone raised an invoice for it. Nothing else in the system would ever notice, because
/// nothing else expects an invoice for this billing type.</para>
///
/// <para>🔒 <b>It is NOT gated on <c>coupon-erp-invoicing</c>.</b> That switch governs whether CEH
/// WRITES draft invoices to e-conomic, and a prepaid pool is precisely the case CEH never writes —
/// so gating the chase behind it would mean the one billing type CEH never invoices is also the one
/// nobody is ever reminded about. The gate here is the data: a pool only exists because a human
/// created it on <c>/Organizer/CouponInvoicing</c>.</para>
///
/// <para>⚠️ <b>It must not become noise</b> (§302). Three things keep it quiet: a
/// <see cref="Grace"/> period so a pool entered minutes ago is not chased before anyone could have
/// raised the invoice, a per-row <see cref="ReAlertAfter"/> stamp rather than a blanket
/// suppression, and silence the moment a number is entered.</para>
/// </remarks>
public sealed class CouponPrepaidBillingReminderService
{
    /// <summary>
    /// How long a newly-created pool is left alone before the first reminder.
    /// </summary>
    /// <remarks>
    /// ⚠️ The invoice is raised in e-conomic BY HAND, usually not in the same minute the allocation
    /// is typed in. Chasing immediately would mail about work the same person is still doing.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    /// <summary>How long a pool stays quiet after being chased.</summary>
    /// <remarks>
    /// A week, not a day (§787's unmapped-coupon alert uses 24h). An unmapped coupon blocks an
    /// invoice CEH would otherwise raise this hour; a missing prepaid confirmation is a human errand
    /// in another system, and a daily nag about it would be trained away — which would cost more
    /// than the reminder saves.
    /// </remarks>
    public static readonly TimeSpan ReAlertAfter = TimeSpan.FromDays(7);

    private readonly CommunityHubDbContext _db;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponPrepaidBillingReminderService>? _log;

    public CouponPrepaidBillingReminderService(
        CommunityHubDbContext db,
        EngineAlertSender alerts,
        TimeProvider clock,
        ILogger<CouponPrepaidBillingReminderService>? log = null)
    {
        _db = db;
        _alerts = alerts;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Remind about prepaid pools with no invoice number. Returns how many were named in the mail;
    /// 0 when every pool is confirmed, too new, or still inside its quiet period.
    /// </summary>
    public async Task<int> RemindAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // §798.4 — the chase is per PURCHASE. A pool topped up from 50 to 75 is two agreements and
        // two invoices; chasing the POOL would have called it billed because the first 50 were.
        var purchases = await _db.CouponPrepaidPurchases
            .Include(p => p.Allocation!).ThenInclude(a => a.CouponInvoicingSetting)
            .Include(p => p.Allocation!).ThenInclude(a => a.Purchases)
            .Where(p => p.Allocation!.EventId == eventId)
            .ToListAsync(ct);

        var due = purchases
            // 🔒 Only purchases on a coupon that is STILL prepaid. A rule switched to another billing
            // type leaves its allocation behind, and chasing a hand-entered invoice number for a
            // coupon CEH now invoices itself would be asking for the same money twice.
            .Where(p => p.Allocation?.CouponInvoicingSetting?.IsPrepaid == true)
            .Where(p => !p.IsBilled)
            .Where(p => now - p.CreatedAt >= Grace)
            .Where(p => p.LastBillingReminderAt is null
                        || now - p.LastBillingReminderAt.Value >= ReAlertAfter)
            .OrderBy(p => p.CreatedAt)
            .ToList();

        if (due.Count == 0)
        {
            // ⚠️ Deliberately silent — the §302 rule. "Nothing to chase" and "the job did not run"
            // must not both produce a mail, or neither will be read.
            _log?.LogInformation(
                "§795.2: {Total} prepaid purchase(s) examined, none due a billing reminder.",
                purchases.Count);
            return 0;
        }

        var lines = string.Join("", due.Select(p =>
        {
            var a = p.Allocation!;
            var age = Math.Max(0, (int)(now - p.CreatedAt).TotalDays);
            var cls = string.IsNullOrWhiteSpace(a.TicketClassLabel) ? a.TicketClassId : a.TicketClassLabel;
            var closed = a.ClosedAt is not null ? " — <em>pool closed</em>" : string.Empty;
            // ⚠️ Named as a TOP-UP when it is one: "25 more" is a different conversation with the
            // partner, and a different invoice, than the original 50.
            var topUp = a.Purchases.Count > 1 ? " <em>(top-up)</em>" : string.Empty;
            return "<li><strong>"
                 + System.Net.WebUtility.HtmlEncode(a.CouponInvoicingSetting!.CouponName)
                 + "</strong> — " + p.Quantity + " × "
                 + System.Net.WebUtility.HtmlEncode(cls) + topUp
                 + $", bought {p.CreatedAt.UtcDateTime:dd MMM yyyy} ({age} day(s) ago)"
                 + closed + "</li>";
        }));

        var body =
            "<p><strong>These prepaid ticket purchases have no e-conomic invoice number, so nothing "
            + "in CEH says the partner was ever billed for them.</strong></p>"
            + $"<ul>{lines}</ul>"
            + "<p>A prepaid pool is <em>never</em> invoiced by CEH — the partner paid up front, and "
            + "billing them per claim would charge them twice. The invoice is raised in e-conomic by "
            + "hand, so the number has to be typed back in on "
            + "<strong>Organizer → Setup → Coupon invoicing</strong> as confirmation.</p>"
            + "<p style=\"color:#6b7280;font-size:13px;\">Entering the number stops this reminder "
            + "immediately. If the pool genuinely was not billed, that is the point of the mail: it "
            + "is a prepayment sitting uncollected, and nothing else in the system would notice.</p>";

        // 🔒 throttleKey null — the per-purchase stamp below IS the throttle. A blanket suppression
        // would hide a top-up bought today because a different one was chased an hour ago.
        // §808 — an ISSUE, so it goes to mok@ (the EngineAlertSender default) rather than the
        // actionable mailbox: an uncollected prepayment is his problem to chase, not a queue item.
        await _alerts.AlertAsync(
            $"ACTION REQUIRED: {due.Count} prepaid ticket purchase(s) with no invoice number",
            body, ct, throttleKey: null);

        foreach (var p in due) p.LastBillingReminderAt = now;
        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§795.2: chased {Count} prepaid purchase(s) with no e-conomic invoice number.", due.Count);
        return due.Count;
    }
}
