using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1094 — one prepaid pool as a PARTNER may see it: bought, used, left.
/// </summary>
/// <param name="Closed">The pool was closed by hand; nothing more will be claimed against it.</param>
/// <remarks>
/// 🔴 <b>DELIBERATELY NOT <see cref="CouponPoolBalance"/> itself.</b> That record also carries
/// <c>UnbilledPurchases</c> — how many of the partner's purchases we have not yet invoiced — and
/// <c>LowBalanceThreshold</c>, an internal warning level. Neither is the partner's business, and
/// the first is a statement about OUR billing that would invite a very awkward question on a page
/// we handed them ourselves. A projection is the difference between "shows their balance" and
/// "shows them our books".
/// </remarks>
public sealed record MonitoredPoolBalance(
    string CouponName,
    string TicketClassLabel,
    int Purchased,
    int Claimed,
    bool Closed)
{
    /// <summary>What is left. ⚠️ Can be NEGATIVE — see the remarks on the query.</summary>
    public int Remaining => Purchased - Claimed;

    /// <summary>More claimed than bought.</summary>
    public bool IsOversubscribed => Remaining < 0;
}

/// <summary>
/// §1094 — an AD-HOC coupon's billing status, as the partner may see it (operator 2026-08-19:
/// *"also show the status for ad-hoc billing"*).
/// </summary>
/// <param name="SharePercent">The share of each ticket THIS partner is invoiced (§1091).</param>
/// <param name="AgreedUnitPriceDkk">Their agreed price per ticket, or null when the ticket price applies.</param>
/// <param name="BilledSoFarDkk">What the claims so far add up to, on the same arithmetic the invoice uses.</param>
/// <param name="LastInvoicedAt">When we last raised an invoice for this coupon, or null for never.</param>
/// <param name="Cadence">How often they are invoiced, in the words the claim invite uses.</param>
/// <remarks>
/// 🔴 <b>An ad-hoc coupon has no pool, so "left" is meaningless — the question is "what have I run
/// up?".</b> Showing a prepaid-shaped row for it would invent a quantity nobody agreed.
///
/// <para>🔑 The amount is computed with <see cref="Erp.CouponBillableShare"/>, the SAME function the
/// invoice line uses. A second calculation here would eventually disagree with the bill, and the
/// partner would be reading the one that is wrong — on a page we gave them precisely so they could
/// check the bill.</para>
/// </remarks>
/// <param name="CapTickets">
/// §1094 — the most tickets this coupon may be claimed for, or null when it is uncapped.
/// Operator 2026-08-19: *"ad hoc can in some cases also have a cap, so both scenarios we must be
/// able to report on"*.
/// </param>
public sealed record MonitoredAdHocStatus(
    string CouponName,
    int Claimed,
    int SharePercent,
    decimal? AgreedUnitPriceDkk,
    decimal BilledSoFarDkk,
    DateTimeOffset? LastInvoicedAt,
    string Cadence,
    int? CapTickets = null)
{
    /// <summary>True when a limit was agreed — the page reports the two cases differently.</summary>
    public bool IsCapped => CapTickets is > 0;

    /// <summary>
    /// Tickets still available under the cap, or null when uncapped.
    /// ⚠️ Can be NEGATIVE, for the same reason a prepaid pool's can: the cap is an agreement, not
    /// an enforcement. <b>CEH cannot stop a claim</b> — Backstage has no coupon API (§787.14) — so
    /// the honest thing is to show that it was exceeded rather than floor it at zero and look fine.
    /// </summary>
    public int? Remaining => IsCapped ? CapTickets!.Value - Claimed : null;

    /// <summary>More has been claimed than was agreed.</summary>
    public bool IsOverCap => Remaining is < 0;
}

/// <summary>§1094 — both halves of what a monitor link can say about billing.</summary>
public sealed record MonitoredBilling(
    IReadOnlyList<MonitoredPoolBalance> Pools,
    IReadOnlyList<MonitoredAdHocStatus> AdHoc)
{
    public static readonly MonitoredBilling Empty =
        new(Array.Empty<MonitoredPoolBalance>(), Array.Empty<MonitoredAdHocStatus>());

    public bool HasAnything => Pools.Count > 0 || AdHoc.Count > 0;
}

/// <summary>
/// §1094 — the prepaid balances behind a monitor link, so a partner can see what they have left.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19, agreeing to the open item from §1093: for a PREPAID customer the
/// attendee list is only half the answer — the number they check first is how many of the tickets
/// they paid for are still unclaimed.</para>
///
/// <para>🔒 <b>Derived, never stored</b> — the same rule <see cref="CouponPrepaidBalance"/> already
/// follows. Purchases are summed and claims counted at read time, so the partner's page cannot
/// disagree with the organizer's.</para>
///
/// <para>⚠️ <b>A negative balance is SHOWN, not clamped.</b> Oversubscription means tickets were
/// claimed that nobody paid for, and it is precisely the state both sides need to see: hiding it
/// behind a floor of zero would let it grow quietly, and the partner is the one who can explain it.
/// This mirrors <see cref="CouponPoolBalance.IsOversubscribed"/>'s reasoning.</para>
///
/// <para>🔑 Ad-hoc coupons produce NO rows — there is no pool, nothing was bought in advance, and a
/// "0 of 0 remaining" line would be an invented fact about an agreement that has no quantity.</para>
/// </remarks>
public sealed class MonitorPrepaidBalanceQuery
{
    private readonly CommunityHubDbContext _db;

    public MonitorPrepaidBalanceQuery(CommunityHubDbContext db) => _db = db;

    /// <summary>The billing picture behind this link — prepaid pools and ad-hoc coupons.</summary>
    public async Task<MonitoredBilling> RunAsync(
        AttendeeMonitor monitor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        // Which coupons does this link cover? Resolved the same way the attendee list resolves them,
        // so the two halves of the page can never describe different coupons.
        var coupons = await CouponNamesForAsync(monitor, ct);
        if (coupons.Count == 0) return MonitoredBilling.Empty;

        var settings = await _db.CouponInvoicingSettings
            .AsNoTracking()
            .Where(c => c.EventId == monitor.EventId)
            .ToListAsync(ct);

        settings = settings.Where(c => coupons.Contains(c.CouponName.Trim())).ToList();

        var allocations = await _db.CouponPrepaidAllocations
            .AsNoTracking()
            .Include(a => a.Purchases)
            .Where(a => a.EventId == monitor.EventId)
            .ToListAsync(ct);

        // 🔑 A pool hangs off the SETTING by id, not off a coupon name — so the scope is carried
        // through the settings we already filtered, never re-matched on a string.
        var couponNameBySettingId = settings.ToDictionary(s => s.Id, s => s.CouponName);
        allocations = allocations
            .Where(a => couponNameBySettingId.ContainsKey(a.CouponInvoicingSettingId))
            .ToList();

        if (allocations.Count == 0 && settings.Count == 0) return MonitoredBilling.Empty;

        // Claims come from the order mirror, exactly as everywhere else — never from Zoho (§525).
        var raw = await _db.Orders
            .AsNoTracking()
            .Where(o => o.EventId == monitor.EventId
                        && o.MirrorState == MirrorState.Active
                        && o.RawJson != null)
            .Select(o => o.RawJson)
            .ToListAsync(ct);

        var claims = raw.SelectMany(CouponClaimExtractor.FromOrderJson).ToList();

        List<CouponClaim> ClaimsFor(string couponName) => claims
            .Where(c => string.Equals(c.CouponName, couponName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // --- prepaid pools -------------------------------------------------------------
        var pools = new List<MonitoredPoolBalance>();
        foreach (var allocation in allocations)
        {
            var couponName = couponNameBySettingId[allocation.CouponInvoicingSettingId];
            var balance = CouponPrepaidBalance.For(allocation, ClaimsFor(couponName));

            pools.Add(new MonitoredPoolBalance(
                CouponName: couponName,
                TicketClassLabel: string.IsNullOrWhiteSpace(balance.Label)
                    ? "Tickets"
                    : balance.Label!,
                Purchased: balance.Purchased,
                Claimed: balance.Claimed,
                Closed: balance.ClosedByHand));
        }

        // --- ad-hoc coupons ------------------------------------------------------------
        var adHoc = new List<MonitoredAdHocStatus>();
        foreach (var rule in settings.Where(s =>
                     s.BillingType == CouponBillingType.ClaimableAdHocPaymentByCustomer))
        {
            var mine = ClaimsFor(rule.CouponName).Where(c => !c.IsCancelled).ToList();

            // 🔑 The SAME arithmetic the invoice line uses (§1091). A second calculation here would
            // eventually disagree with the bill, and the partner would be reading the wrong one — on
            // a page we gave them so they could check the bill.
            var billed = mine.Sum(c => CouponBillableShare.Dkk(
                c.UnitPriceDkk, rule.AgreedUnitPriceDkk, rule.InvoicedSharePercent));

            adHoc.Add(new MonitoredAdHocStatus(
                CouponName: rule.CouponName,
                Claimed: mine.Count,
                SharePercent: CouponBillableShare.Percent(rule.InvoicedSharePercent),
                AgreedUnitPriceDkk: rule.AgreedUnitPriceDkk,
                BilledSoFarDkk: decimal.Round(billed, 2, MidpointRounding.AwayFromZero),
                LastInvoicedAt: rule.LastInvoicedAt,
                Cadence: CouponClaimInviteComposer.Cadence(
                    rule.InvoiceIntervalDays is { } d && d >= 0 ? d : DefaultIntervalDays),
                CapTickets: rule.ClaimCapTickets));
        }

        return new MonitoredBilling(
            // Trouble first, then alphabetically — the same order the organizer's page uses.
            pools.OrderBy(r => r.Remaining)
                 .ThenBy(r => r.CouponName, StringComparer.OrdinalIgnoreCase).ToList(),
            adHoc.OrderBy(r => r.Remaining ?? int.MaxValue)
                 .ThenBy(r => r.CouponName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// The edition default billing period, mirroring <c>InvoicingOptions.CouponInvoiceIntervalDays</c>.
    /// ⚠️ A constant here rather than an injected option: this query runs on an ANONYMOUS page, and
    /// the cadence sentence is cosmetic — it must not be able to fail the page if config is absent.
    /// </summary>
    private const int DefaultIntervalDays = 14;

    /// <summary>
    /// The coupon codes a monitor covers. 🔑 Mirrors <c>AttendeeMonitorQuery</c>'s scoping exactly:
    /// a customer monitor resolves its coupon set at READ time, so a code added later is included
    /// here too.
    /// </summary>
    private async Task<HashSet<string>> CouponNamesForAsync(
        AttendeeMonitor monitor, CancellationToken ct)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        switch (monitor.Kind)
        {
            case AttendeeMonitorKind.CouponCode:
            {
                var one = AttendeeMonitor.NormaliseValue(AttendeeMonitorKind.CouponCode, monitor.Value);
                if (one.Length > 0) empty.Add(one);
                return empty;
            }

            case AttendeeMonitorKind.ErpCustomer:
            {
                if (!int.TryParse(
                        AttendeeMonitor.NormaliseValue(AttendeeMonitorKind.ErpCustomer, monitor.Value),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var customerNumber)
                    || customerNumber <= 0)
                {
                    return empty;
                }

                // §1098 — the SAME tick filter as AttendeeMonitorQuery. The two halves of the page
                // must describe the same coupons, or the balances would count a coupon whose
                // sign-ups are not listed below them.
                var names = await _db.CouponInvoicingSettings
                    .AsNoTracking()
                    .Where(c => c.EventId == monitor.EventId
                                && c.ErpCustomerNumber == customerNumber
                                && c.IssueUsageLink)
                    .Select(c => c.CouponName)
                    .ToListAsync(ct);

                foreach (var n in names)
                {
                    var v = (n ?? string.Empty).Trim();
                    if (v.Length > 0) empty.Add(v);
                }
                return empty;
            }

            // An e-mail-domain monitor has no coupon and therefore no pool.
            default:
                return empty;
        }
    }
}
