using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §787 — "ACTION REQUIRED: coupon missing ERP mapping". The CEH equivalent of the mail the retired
/// script sent, pointing at the page that FIXES it.
/// </summary>
/// <remarks>
/// <para>🔑 <b>This alert is why the mapping became a page rather than a config file</b> (§787.2).
/// The script mailed the same thing, but the only cure was editing a CSV on a VM — so the alert
/// arrived about something the recipient usually could not fix. Now it links to
/// <c>/Organizer/CouponInvoicing</c>, where the person reading it can act immediately. The alert and
/// the fix belong in the same place.</para>
///
/// <para>🔒 <b>It goes to the ACTIONABLE mailbox</b> (<c>info@expertslive.dk</c>, §556): somebody has
/// to open Backstage/e-conomic and decide who pays, and more than one person should be able to.</para>
///
/// <para>⚠️ <b>Re-alerts, but slowly.</b> A coupon nobody maps stays unmapped, so "alert while
/// unmapped" would mail hourly for ever — the §302 70-mail night. <see cref="ReAlertAfter"/> is the
/// compromise: silent for a day, then it speaks up again, because an unbilled ticket is not
/// something to let go quiet permanently either.</para>
/// </remarks>
public sealed class CouponMappingAlertService
{
    /// <summary>How long a coupon stays quiet after being alerted about.</summary>
    public static readonly TimeSpan ReAlertAfter = TimeSpan.FromHours(24);

    private readonly CommunityHubDbContext _db;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponMappingAlertService>? _log;

    public CouponMappingAlertService(
        CommunityHubDbContext db,
        EngineAlertSender alerts,
        TimeProvider clock,
        ILogger<CouponMappingAlertService>? log = null)
    {
        _db = db;
        _alerts = alerts;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Alert about coupons that are claimed but not billable. Returns how many were named in the
    /// mail; 0 when everything is mapped or everything is still inside its quiet period.
    /// </summary>
    public async Task<int> AlertAsync(
        int eventId, IReadOnlyCollection<string> unmappedCouponNames, CancellationToken ct = default)
    {
        if (unmappedCouponNames.Count == 0) return 0;

        var now = _clock.GetUtcNow();
        var rows = await _db.CouponInvoicingSettings
            .Where(c => c.EventId == eventId && unmappedCouponNames.Contains(c.CouponName))
            .ToListAsync(ct);

        var due = rows
            .Where(r => r.LastAlertedAt is null || now - r.LastAlertedAt.Value >= ReAlertAfter)
            .ToList();

        if (due.Count == 0)
        {
            _log?.LogInformation(
                "§787: {Count} coupon(s) still unmapped, but all are inside the {Hours}h quiet "
                + "period — no alert sent.", unmappedCouponNames.Count, ReAlertAfter.TotalHours);
            return 0;
        }

        var lines = string.Join("", due.Select(r =>
        {
            var waiting = r.FirstSeenClaimedAt is { } seen
                ? $" — first claimed {seen.UtcDateTime:dd MMM yyyy} "
                  + $"({Math.Max(0, (int)(now - seen).TotalDays)} day(s) ago)"
                : string.Empty;
            return $"<li><strong>{System.Net.WebUtility.HtmlEncode(r.CouponName)}</strong>{waiting}</li>";
        }));

        var body =
            "<p><strong>These coupons have been claimed, and CEH cannot invoice anyone for them.</strong> "
            + "Nobody has said who pays, so the tickets are sitting unbilled.</p>"
            + $"<ul>{lines}</ul>"
            + "<p>Set the billing type and the e-conomic customer on "
            + "<strong>Organizer → Setup → Coupon invoicing</strong>. The next hourly run picks them "
            + "up automatically — there is nothing to re-trigger.</p>"
            + "<p style=\"color:#6b7280;font-size:13px;\">If a coupon is genuinely free, set it to "
            + "<em>NoInvoicing</em> rather than leaving it unmapped: that records the decision and "
            + "stops this alert, where leaving it blank looks the same as nobody having looked.</p>";

        // 🔒 throttleKey null — the per-coupon LastAlertedAt below IS the throttle, and it is the
        // right one: a 6-hour blanket suppression would hide a NEWLY claimed coupon just because a
        // different one was alerted about recently.
        // 🔴 §808 — TO mok@, not the actionable mailbox. Operator 2026-08-04: *"alert mails (issues)
        // in the recent 2 invoice solutions goes to mok@expertslive.dk"*.
        //
        // ⚠️ This REVERSES §787.2's routing, and the reason it was info@ is worth keeping: this alert
        // is why the mapping became a PAGE instead of a config file — so whoever receives it can fix
        // it, and more than one person could. His rule is simpler and is his to make: an ISSUE is
        // his; a NEW PENDING INVOICE is the mailbox's (that one still goes to info@).
        await _alerts.AlertAsync(
            $"ACTION REQUIRED: {due.Count} coupon(s) missing an ERP mapping",
            body, ct, throttleKey: null);

        foreach (var r in due) r.LastAlertedAt = now;
        await _db.SaveChangesAsync(ct);

        _log?.LogInformation("§787: alerted about {Count} unmapped coupon(s).", due.Count);
        return due.Count;
    }
}
