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
    string? BuyerEmail = null,
    /// <summary>
    /// §1097 — the Backstage ticket id, so a row can be matched to a specific ticket rather than
    /// only to its order. Operator 2026-08-20 asked for it by name in the export.
    /// </summary>
    /// <remarks>
    /// ⚠️ An ORDER can hold several tickets, so the order id alone cannot identify a line — which is
    /// exactly the case a volume buyer produces.
    /// </remarks>
    string? TicketId = null,
    /// <summary>§1097 — which ticket type this person holds (1-day / 2-day / …).</summary>
    string? TicketClassName = null);

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
            AttendeeMonitorKind.ErpCustomer => await ByErpCustomerAsync(monitor, ct),
            _ => Array.Empty<MonitoredAttendee>(),
        };
    }

    /// <summary>
    /// §1093 — every attendee who used ANY coupon billed to this e-conomic customer.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>The coupon set is resolved at READ time, not stored on the monitor.</b> A partner
    /// who is given a new promo code next month must see it through the link they already have —
    /// freezing the list into the row would mean every new coupon silently missing from the page
    /// the partner is using to check their invoice.</para>
    ///
    /// <para>🔒 Reuses the SAME matcher as <see cref="ByCouponAsync"/>. Two coupon matchers is how
    /// the shared link and the per-coupon link end up disagreeing about who counts, and the copy
    /// that drifts is always the one an outsider is reading.</para>
    ///
    /// <para>⚠️ No coupons for the customer ⇒ NO ROWS, never "everything". A monitor whose scope
    /// resolves to nothing must show nothing; an empty filter that falls through to an unfiltered
    /// query is the classic way a scoped page turns into a full attendee list.</para>
    /// </remarks>
    private async Task<IReadOnlyList<MonitoredAttendee>> ByErpCustomerAsync(
        AttendeeMonitor monitor, CancellationToken ct)
    {
        if (!int.TryParse(
                AttendeeMonitor.NormaliseValue(AttendeeMonitorKind.ErpCustomer, monitor.Value),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var customerNumber)
            || customerNumber <= 0)
        {
            return Array.Empty<MonitoredAttendee>();
        }

        // 🔴 §1098 — only coupons TICKED for a usage link are in scope. An unticked one (the one-off
        // invoice sale) is simply not part of any monitor, rather than the link being suppressed —
        // which would punish the partner's other agreements for the presence of a one-off.
        var codes = await _db.CouponInvoicingSettings
            .AsNoTracking()
            .Where(c => c.EventId == monitor.EventId
                        && c.ErpCustomerNumber == customerNumber
                        && c.IssueUsageLink)
            .Select(c => c.CouponName)
            .ToListAsync(ct);

        var wanted = codes
            .Select(c => AttendeeMonitor.NormaliseValue(AttendeeMonitorKind.CouponCode, c))
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return wanted.Count == 0
            ? Array.Empty<MonitoredAttendee>()
            : await ByCouponCodesAsync(monitor.EventId, wanted, ct);
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
                a.BackstageTicketId, a.TicketClassName,
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
                r.Order?.BuyerName, r.Order?.BuyerEmail,
                r.BackstageTicketId, r.TicketClassName))
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
        return wanted.Length == 0
            ? Array.Empty<MonitoredAttendee>()
            : await ByCouponCodesAsync(
                monitor.EventId,
                new HashSet<string>(new[] { wanted }, StringComparer.OrdinalIgnoreCase),
                ct);
    }

    /// <summary>
    /// §1093 — the ONE coupon matcher, over a SET of codes. A single-code monitor passes a set of
    /// one; a per-customer monitor passes every code billed to that customer.
    /// </summary>
    /// <remarks>
    /// 🔑 Extracted rather than copied. Today's §1088 and §1089 were both a duplicated thing rotting
    /// on one side only — a second copy of "which attendees used this coupon" would be the same
    /// mistake in the one place where the reader is an outsider.
    /// </remarks>
    private async Task<IReadOnlyList<MonitoredAttendee>> ByCouponCodesAsync(
        int eventId, HashSet<string> wantedCodes, CancellationToken ct)
    {
        if (wantedCodes.Count == 0) return Array.Empty<MonitoredAttendee>();

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.EventId == eventId
                        && o.MirrorState == MirrorState.Active
                        && o.RawJson != null)
            .Select(o => new { o.BackstageOrderId, o.RawJson, o.SourceCreatedAt, o.BuyerName, o.BuyerEmail })
            .ToListAsync(ct);

        // Which (order, e-mail) pairs used one of these coupons, and the order facts to show beside
        // them. §1097 — the CLAIM also carries the ticket id and class, which the attendee mirror
        // may not have for a coupon ticket, so they are captured here rather than looked up later.
        var matched = new Dictionary<(string Order, string Email),
            (DateTimeOffset? Bought, string? BuyerName, string? BuyerEmail,
             string? TicketId, string? TicketClass)>();
        foreach (var o in orders)
        {
            foreach (var claim in CouponClaimExtractor.FromOrderJson(o.RawJson))
            {
                if (!wantedCodes.Contains((claim.CouponName ?? string.Empty).Trim())) continue;
                if (claim.IsCancelled) continue;
                var email = (claim.Email ?? string.Empty).Trim().ToLowerInvariant();
                if (email.Length == 0) continue;
                matched[(o.BackstageOrderId, email)] =
                    (o.SourceCreatedAt, o.BuyerName, o.BuyerEmail,
                     claim.TicketId, claim.TicketClassName);
            }
        }

        if (matched.Count == 0) return Array.Empty<MonitoredAttendee>();

        var emails = matched.Keys.Select(k => k.Email).Distinct().ToList();
        var attendees = await _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == eventId
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
                    a.CompanyName, m.BuyerName, m.BuyerEmail,
                    m.TicketId, m.TicketClass);
            })
            .OrderByDescending(r => r.BoughtAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.LastName)
            .ToList();
    }
}
