using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>One row on the shared page — exactly the five fields he asked for.</summary>
/// <param name="BoughtAt">
/// ⚠️ The order's date IN ZOHO, never the local mirror's <c>CreatedAt</c>. See the remarks on
/// <see cref="AttendeeMonitorQuery"/>.
/// </param>
public sealed record MonitoredAttendee(
    DateTimeOffset? BoughtAt,
    string FirstName,
    string LastName,
    string Email,
    string OrderId,
    /// <summary>
    /// §1040 — the ATTENDEE's own company. Operator 2026-08-10: <i>"arrow invites many different
    /// partners, so we need to know names, company, email"</i> — a volume buyer hands its tickets to
    /// people from several different firms, so without this the list is names with no way to tell
    /// which partner each one came from.
    /// </summary>
    string? Company = null,
    /// <summary>Who placed the order — the volume buyer, not the attendee.</summary>
    string? BuyerName = null,
    string? BuyerEmail = null);

/// <summary>
/// §1040 — WHO HAS BOUGHT A TICKET UNDER THIS MONITOR. The one place that answers it, for both the
/// organizer's preview and the shared link.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Both surfaces call THIS.</b> An organizer previewing a monitor and an external person
/// opening the link must see the same rows — otherwise he shares a link believing it shows one thing
/// and it shows another. Two queries would drift, and the copy that drifted would be the one an
/// outsider reads (the §837 lesson, in a place with a wider audience).</para>
///
/// <para>🔑 <b>No Zoho call.</b> Everything here is already mirrored by
/// <c>AttendeeBackstageSyncJob</c>. §525: ~21 jobs each minting a token once tripped Zoho's
/// refresh-grant limit and every sync failed against a valid credential — a page an outsider can
/// reload must never be able to spend that budget.</para>
///
/// <para>⚠️ <b>ACTIVE ONLY, and it is a filter on BOTH the attendee and its order.</b> Operator:
/// <i>"cancelled tickets should not be included - only active attendees"</i>. §128's soft-cancel
/// keeps the row and flips <c>MirrorState</c>, so a cancelled ticket stays queryable for ever — it
/// disappears here only because it is excluded on purpose.</para>
/// </remarks>
public sealed class AttendeeMonitorQuery
{
    private readonly CommunityHubDbContext _db;

    public AttendeeMonitorQuery(CommunityHubDbContext db) => _db = db;

    /// <summary>The rows a monitor currently shows, newest purchase first.</summary>
    public async Task<IReadOnlyList<MonitoredAttendee>> RunAsync(
        AttendeeMonitor monitor, CancellationToken ct = default)
    {
        return monitor.Kind switch
        {
            AttendeeMonitorKind.EmailDomain => await ByDomainAsync(monitor, ct),
            AttendeeMonitorKind.CouponCode => await ByCouponAsync(monitor, ct),
            _ => Array.Empty<MonitoredAttendee>(),
        };
    }

    /// <summary>
    /// Every ACTIVE attendee whose e-mail sits at this domain.
    /// </summary>
    /// <remarks>
    /// 🔒 Matches on <c>"@" + domain</c>, not on the bare domain. Without the <c>@</c>,
    /// a monitor for <c>day.com</c> would also match <c>someone@mon<b>day.com</b></c> — a silent
    /// leak of a different company's attendee into this company's list, which is the one failure
    /// this feature must not have.
    /// </remarks>
    private async Task<IReadOnlyList<MonitoredAttendee>> ByDomainAsync(
        AttendeeMonitor monitor, CancellationToken ct)
    {
        var suffix = "@" + AttendeeMonitor.NormaliseValue(AttendeeMonitorKind.EmailDomain, monitor.Value);
        if (suffix.Length <= 1) return Array.Empty<MonitoredAttendee>();

        var rows = await _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == monitor.EventId
                        && a.MirrorState == MirrorState.Active
                        && a.Email.EndsWith(suffix))
            .Select(a => new
            {
                a.FirstName, a.LastName, a.Email, a.OrderId, a.CompanyName,
                // The BUY date and the BUYER come from the ORDER, and only from one still active.
                Order = _db.Orders
                    .Where(o => o.EventId == a.EventId
                                && o.BackstageOrderId == a.OrderId
                                && o.MirrorState == MirrorState.Active)
                    .Select(o => new { o.SourceCreatedAt, o.BuyerName, o.BuyerEmail })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new MonitoredAttendee(
                r.Order == null ? null : r.Order.SourceCreatedAt,
                r.FirstName, r.LastName, r.Email, r.OrderId ?? string.Empty,
                r.CompanyName,
                r.Order?.BuyerName, r.Order?.BuyerEmail))
            .OrderByDescending(r => r.BoughtAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.LastName)
            .ToList();
    }

    /// <summary>
    /// Every ACTIVE attendee on an order carrying this coupon.
    /// </summary>
    /// <remarks>
    /// <para>🔑 The promo code is read by <see cref="CouponClaimExtractor"/> (§787), NOT by a second
    /// parser written here. It already knows the shape — ticket-level <c>promo_code</c> with the
    /// order-level <c>cost.promo_code</c> as fallback — and it already handles the case that cost a
    /// bug once: a <b>blank</b> ticket-level code must fall back too, which a <c>??</c> does not do.</para>
    ///
    /// <para>⚠️ The extractor reports per TICKET, so the coupon is matched at ticket level. An order
    /// where only some tickets used the coupon therefore contributes only those tickets, which is
    /// what "attendees with coupon code xxxxx" means.</para>
    ///
    /// <para>🔒 Cross-checked against the mirrored <c>Attendees</c> rather than trusted from the
    /// JSON: the raw order is a snapshot from pull time, while <c>MirrorState</c> is maintained by
    /// every sync since. A ticket cancelled after that snapshot would otherwise still be listed.</para>
    /// </remarks>
    private async Task<IReadOnlyList<MonitoredAttendee>> ByCouponAsync(
        AttendeeMonitor monitor, CancellationToken ct)
    {
        var wanted = AttendeeMonitor.NormaliseValue(AttendeeMonitorKind.CouponCode, monitor.Value);
        if (wanted.Length == 0) return Array.Empty<MonitoredAttendee>();

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.EventId == monitor.EventId
                        && o.MirrorState == MirrorState.Active
                        && o.RawJson != null)
            .Select(o => new { o.BackstageOrderId, o.RawJson, o.SourceCreatedAt, o.BuyerName, o.BuyerEmail })
            .ToListAsync(ct);

        // Which (order, e-mail) pairs used this coupon, and the order facts to show beside them.
        var matched = new Dictionary<(string Order, string Email),
            (DateTimeOffset? Bought, string? BuyerName, string? BuyerEmail)>();
        foreach (var o in orders)
        {
            foreach (var claim in CouponClaimExtractor.FromOrderJson(o.RawJson))
            {
                if (!string.Equals(claim.CouponName, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (claim.IsCancelled) continue;
                var email = (claim.Email ?? string.Empty).Trim().ToLowerInvariant();
                if (email.Length == 0) continue;
                matched[(o.BackstageOrderId, email)] = (o.SourceCreatedAt, o.BuyerName, o.BuyerEmail);
            }
        }

        if (matched.Count == 0) return Array.Empty<MonitoredAttendee>();

        var emails = matched.Keys.Select(k => k.Email).Distinct().ToList();
        var attendees = await _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == monitor.EventId
                        && a.MirrorState == MirrorState.Active
                        && emails.Contains(a.Email))
            .Select(a => new { a.FirstName, a.LastName, a.Email, a.OrderId, a.CompanyName })
            .ToListAsync(ct);

        return attendees
            .Where(a => matched.ContainsKey((a.OrderId ?? string.Empty, a.Email)))
            .Select(a =>
            {
                var m = matched[(a.OrderId ?? string.Empty, a.Email)];
                return new MonitoredAttendee(
                    m.Bought, a.FirstName, a.LastName, a.Email, a.OrderId ?? string.Empty,
                    a.CompanyName, m.BuyerName, m.BuyerEmail);
            })
            .OrderByDescending(r => r.BoughtAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.LastName)
            .ToList();
    }
}
