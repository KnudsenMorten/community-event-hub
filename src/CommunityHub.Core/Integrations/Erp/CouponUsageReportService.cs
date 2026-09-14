using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §1094 — the fortnightly usage report a partner receives by e-mail.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19: *"they must get status per mail every 2 weeks of usage, cap
/// (remaining) if set"*.</para>
///
/// <para>🔑 <b>Per BILLING CUSTOMER, on the monitor row's stamp.</b> Same grain as the link it
/// carries: a partner with three codes gets ONE report covering all of them, not three fragments.
/// </para>
///
/// <para>🔴 <b>ONLY to partners we have already written to.</b> The recipient is the address their
/// claim invite went to (<c>ClaimInviteSentToEmail</c>). A partner who has never been invited is
/// skipped — starting an unsolicited fortnightly mail to an e-conomic contact who has heard nothing
/// from us is not a status report, it is cold mail, and it would be CEH's only such send.</para>
///
/// <para>🔒 The mail is a SUMMARY plus the link. It deliberately does not list attendees: the page
/// does that, behind a token, where access can be withdrawn — an e-mail cannot be unsent, and it
/// gets forwarded.</para>
/// </remarks>
public sealed class CouponUsageReportService
{
    /// <summary>Every 14 days, per his instruction.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(14);

    private readonly CommunityHubDbContext _db;
    private readonly MonitorPrepaidBalanceQuery _billing;
    private readonly AttendeeMonitorQuery _attendees;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _ctx;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponUsageReportService>? _log;

    public CouponUsageReportService(
        CommunityHubDbContext db,
        MonitorPrepaidBalanceQuery billing,
        AttendeeMonitorQuery attendees,
        IEmailSender email,
        IEmailContextAccessor ctx,
        TimeProvider clock,
        ILogger<CouponUsageReportService>? log = null)
    {
        _db = db;
        _billing = billing;
        _attendees = attendees;
        _email = email;
        _ctx = ctx;
        _clock = clock;
        _log = log;
    }

    /// <summary>Send any reports that are due. Returns how many went out.</summary>
    public async Task<int> SendDueAsync(
        int eventId, string baseUrl, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var monitors = await _db.AttendeeMonitors
            .Where(m => m.EventId == eventId
                        && m.Kind == AttendeeMonitorKind.ErpCustomer
                        && m.RevokedAt == null)
            .ToListAsync(ct);

        if (monitors.Count == 0) return 0;

        var eventName = await _db.Events
            .Where(e => e.Id == eventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct)
            ?? "the event";

        var sent = 0;
        foreach (var monitor in monitors)
        {
            // Not yet due. A first report goes out on the next run after the link is created, which
            // is what makes this useful immediately rather than in a fortnight's time.
            if (monitor.UsageReportSentAt is { } last && now - last < Interval) continue;
            if (monitor.IsExpired(now)) continue;

            if (!int.TryParse(monitor.Value, out var customerNumber) || customerNumber <= 0) continue;

            // 🔴 §1098 — the status mail is TICKED ON per coupon, and off by default. Operator
            // 2026-08-20: the one-off invoice buyers *"typically make one order and then they dont
            // buy more. we will let it run, but they dont get a status."* No ticked coupon for this
            // customer ⇒ no report, even though they have a link for something else.
            var wanted = await _db.CouponInvoicingSettings
                .AsNoTracking()
                .AnyAsync(c => c.EventId == eventId
                               && c.ErpCustomerNumber == customerNumber
                               && c.SendUsageStatusMail, ct);
            if (!wanted) continue;

            var to = await RecipientForAsync(eventId, customerNumber, ct);
            if (string.IsNullOrWhiteSpace(to)) continue;   // never invited ⇒ never cold-mailed

            var billing = await _billing.RunAsync(monitor, ct);
            var rows = await _attendees.RunAsync(monitor, ct);

            // 🔒 Nothing to say ⇒ nothing sent. A fortnightly "0 tickets, no change" is the §302
            // shape: mail that arrives on a timer regardless of content stops being read, and takes
            // the useful ones with it.
            if (!billing.HasAnything && rows.Count == 0) continue;

            var (subject, html) = CouponUsageReportComposer.Build(
                eventName, monitor.Name, rows.Count, billing,
                $"{baseUrl.TrimEnd('/')}/monitor/{monitor.Token}");

            try
            {
                // §707.2 — the mail carries its own identity so it lands in the ledger like any
                // other send. 🔴 RingExempt for the SAME reason the §1016c claim invite is: the
                // recipient is an e-conomic CONTACT at a partner company, not a participant of the
                // edition, so the ring gate cannot resolve them and would fail closed — "ring-gated"
                // would mean "a report that never sends".
                using (_ctx.Set(new EmailContext(
                           "coupon-usage-report", eventId, null, to, RingExempt: true)))
                {
                    // §1118 — the organizer team is copied on everything a paying customer receives.
                    await _email.SendAsync(to!, subject, html, CouponMailAudience.CcFor(to), ct);
                }
            }
            catch (Exception ex)
            {
                // Fail-soft per partner: one bad address must not stop everybody else's report.
                _log?.LogWarning(ex, "§1094: usage report to {To} failed.", to);
                continue;
            }

            // Stamped only after the send, so a failure retries on the next pass.
            monitor.UsageReportSentAt = now;
            sent++;
        }

        if (sent > 0) await _db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>
    /// The address this customer's claim invite was sent to, or null if they have never had one.
    /// </summary>
    /// <remarks>
    /// 🔑 The newest invite wins when a customer holds several coupons — it is the most recent
    /// address we know actually reached them.
    /// </remarks>
    private async Task<string?> RecipientForAsync(int eventId, int customerNumber, CancellationToken ct) =>
        await _db.CouponInvoicingSettings
            .AsNoTracking()
            .Where(c => c.EventId == eventId
                        && c.ErpCustomerNumber == customerNumber
                        && c.ClaimInviteSentToEmail != null
                        && c.ClaimInviteSentAt != null)
            .OrderByDescending(c => c.ClaimInviteSentAt)
            .Select(c => c.ClaimInviteSentToEmail)
            .FirstOrDefaultAsync(ct);
}
