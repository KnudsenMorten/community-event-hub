using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §1093 — every billing customer with a coupon gets a monitor link, without anyone creating it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19: *"i need also you to automaitcally set up monitor for every coupons so
/// they get a secure link and can see any usage / sign-ups"* … *"it could be great that the monitor
/// is linked per billing customer, so arrow gets one link that shows any usage for any coupons they
/// have"*.</para>
///
/// <para>🔑 <b>PER CUSTOMER, NOT PER COUPON — and that is the whole design.</b> A partner thinks in
/// "my usage", not in promo codes: Arrow may hold several codes across ticket classes and campaigns,
/// so a link per code is several links, each showing a fragment and none of them answering the
/// question they have. The billing customer is also exactly who the INVOICE is addressed to, so the
/// page and the bill describe the same population — which is what makes the link usable as a check
/// on the invoice rather than just a nice extra.</para>
///
/// <para>🔒 <b>The monitor is not created per coupon, so a new coupon for an existing customer needs
/// NOTHING.</b> <c>AttendeeMonitorQuery</c> resolves the customer's coupon set at read time, so a
/// code added next month appears through the link the partner already has.</para>
///
/// <para>⚠️ <b>Idempotent, and it never resurrects a revoked link.</b> Running every few minutes
/// must not create a second monitor for a customer, and — more importantly — must not undo an
/// organizer's decision to withdraw one. A revoked monitor is a deliberate act; re-issuing it
/// automatically would make revocation impossible to keep.</para>
/// </remarks>
public sealed class CouponCustomerMonitorProvisioner
{
    private readonly CommunityHubDbContext _db;
    private readonly ILogger<CouponCustomerMonitorProvisioner>? _log;

    public CouponCustomerMonitorProvisioner(
        CommunityHubDbContext db, ILogger<CouponCustomerMonitorProvisioner>? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Ensure every e-conomic customer with at least one coupon in this edition has a live monitor.
    /// Returns how many were created.
    /// </summary>
    /// <param name="nameFor">
    /// Optional resolver for the customer's real name, so the partner sees "Arrow ECS" rather than
    /// "Customer 1234" on a page they were sent. Null ⇒ the number is used, and the organizer can
    /// rename it. ⚠️ Deliberately optional: the scheduled caller has no cheap way to look a customer
    /// up, and a monitor with a dull name is far better than no monitor at all.
    /// </param>
    public async Task<int> EnsureAsync(
        int eventId,
        Func<int, CancellationToken, Task<string?>>? nameFor = null,
        CancellationToken ct = default)
    {
        // Which customers have a coupon at all? Unmapped rows carry no customer, so they select
        // themselves out — there is nobody to send a link to.
        // 🔴 §1098 — ONLY customers with at least one coupon TICKED for a usage link. Operator
        // 2026-08-20: the one-off invoice buyers *"dont need to have a secure link as well"*, and
        // the ticks are off by default, so a link is now something he grants rather than something
        // every customer silently acquires.
        var customers = await _db.CouponInvoicingSettings
            .AsNoTracking()
            .Where(c => c.EventId == eventId
                        && c.ErpCustomerNumber != null && c.ErpCustomerNumber > 0
                        && c.IssueUsageLink)
            .Select(c => c.ErpCustomerNumber!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (customers.Count == 0) return 0;

        // 🔒 EVERY monitor for this customer, revoked ones included. The `AnyAsync` guard on the
        // organizer page filters revoked out because a human is re-creating one on purpose; here the
        // opposite is right — an automatic sweep that skips revoked rows would re-issue a link an
        // organizer had just withdrawn, on the next tick, for ever.
        var wanted = customers
            .Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = await _db.AttendeeMonitors
            .Where(m => m.EventId == eventId && m.Kind == AttendeeMonitorKind.ErpCustomer)
            .Select(m => m.Value)
            .ToListAsync(ct);

        var have = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = customers
            .Where(n => !have.Contains(n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        if (missing.Count == 0) return 0;

        foreach (var customerNumber in missing)
        {
            string? name = null;
            if (nameFor is not null)
            {
                try { name = await nameFor(customerNumber, ct); }
                catch { /* a name is a nicety; never let it stop the link existing */ }
            }

            var label = string.IsNullOrWhiteSpace(name)
                ? $"Coupon usage — customer {customerNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"Coupon usage — {name!.Trim()}";

            _db.AttendeeMonitors.Add(new AttendeeMonitor
            {
                EventId = eventId,
                Name = label,
                Kind = AttendeeMonitorKind.ErpCustomer,
                Value = customerNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Token = AttendeeMonitor.NewToken(),
                // Same horizon as every other monitor (§1040: "active until 15 feb 2027").
                ExpiresAt = AttendeeMonitor.DefaultExpiry,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByEmail = SystemAuthor,
            });
        }

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§1093: created {Count} coupon-customer monitor(s) for event {EventId}.",
            missing.Count, eventId);

        return missing.Count;
    }

    /// <summary>
    /// 🔑 Recorded as the author so the organizer page can tell an automatic link from one a person
    /// deliberately created and shared. They are revoked with different confidence.
    /// </summary>
    public const string SystemAuthor = "system (§1093 auto)";
}
