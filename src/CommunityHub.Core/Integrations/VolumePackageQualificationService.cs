using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What one entity qualified with, and where it came from.</summary>
/// <param name="Emails">The DISTINCT attendee e-mails — the union of all three checks.</param>
public sealed record VolumePackageResult(
    int CompanyId,
    string CompanyName,
    IReadOnlySet<string> Emails,
    int FromOrders,
    int FromCoupons,
    int FromAttendeeDomains)
{
    public int Count => Emails.Count;
    public bool Qualifies => Count >= VolumePackageQualificationService.Threshold;
}

/// <summary>
/// §1077 — DOES THIS COMPANY QUALIFY FOR THE VOLUME PACKAGE? Three checks, one answer.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: ≥10 attendees ⇒ keynote mention + social-media announcement + group
/// photo.</para>
///
/// <para>🔴 <b>THE DEDUPE RULE IS THE ARCHITECTURE.</b> His requirement — <i>"an email is only
/// counted once"</i> — is not a post-processing step here. <b>Each check contributes a SET of
/// attendee e-mails and the answer is the size of the UNION.</b> A company that buys twenty tickets
/// on a coupon is found by check 1 AND check 2; with sets that is simply the same twenty people
/// twice, which is nothing. Summing the checks and reconciling afterwards would put the
/// double-count one careless edit away, for ever.</para>
///
/// <para>🔑 <b>Identity is the E-MAIL DOMAIN, never the company name.</b> The name on an order is
/// free text a buyer typed; §1045 is the standing warning (<i>"2linkIT" vs "2linkIT ApS" vs
/// "2LINKIT"</i>). Here it would decide who appears in a keynote.</para>
///
/// <para>⚠️ <b>ACTIVE attendees only</b>, via <see cref="MirrorState.Active"/> — a cancelled ticket
/// must drop the company back below the line, which is exactly what the operator asked the daily
/// re-check for.</para>
/// </remarks>
public sealed class VolumePackageQualificationService
{
    /// <summary>Ten or more. Operator: <i>"more than 10 or more tickets"</i>.</summary>
    public const int Threshold = 10;

    private readonly CommunityHubDbContext _db;
    private readonly ILogger<VolumePackageQualificationService>? _log;

    public VolumePackageQualificationService(
        CommunityHubDbContext db, ILogger<VolumePackageQualificationService>? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Compute every entity's answer for one edition.</summary>
    public async Task<IReadOnlyList<VolumePackageResult>> ComputeAllAsync(
        int eventId, CancellationToken ct = default)
    {
        var companies = await _db.VolumePackageCompanies
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);

        if (companies.Count == 0) return Array.Empty<VolumePackageResult>();

        // ---- read the live data ONCE, then answer every company from memory ------------------
        // 🔒 One pass, not one query per company: this runs daily over the whole attendee base and
        // an N+1 here would be a query per company per day for no benefit.
        var attendees = await _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active
                        && a.Email != null && a.Email != "")
            .Select(a => new { a.Email, a.OrderId })
            .ToListAsync(ct);

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.EventId == eventId && o.MirrorState == MirrorState.Active)
            .Select(o => new { o.BackstageOrderId, o.BuyerEmail })
            .ToListAsync(ct);

        // order id -> buyer domain, for check 1.
        var orderBuyerDomain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in orders)
        {
            if (string.IsNullOrWhiteSpace(o.BackstageOrderId)) continue;
            var d = DomainOf(o.BuyerEmail);
            if (d.Length > 0) orderBuyerDomain[o.BackstageOrderId] = d;
        }

        // ---- check 2's mapping: coupon -> entity, via the ERP customer already curated for
        // invoicing (operator: "you are allowed to fetch data in erp"). Deriving beats re-typing:
        // the mapping exists, is maintained, and would otherwise be entered a second time and drift.
        var couponToErp = await _db.CouponInvoicingSettings
            .AsNoTracking()
            .Where(c => c.EventId == eventId && c.ErpCustomerNumber != null)
            .Select(c => new { c.CouponName, ErpCustomerNumber = c.ErpCustomerNumber!.Value })
            .ToListAsync(ct);

        var erpToCoupons = new Dictionary<int, HashSet<string>>();
        foreach (var c in couponToErp)
        {
            if (string.IsNullOrWhiteSpace(c.CouponName)) continue;
            if (!erpToCoupons.TryGetValue(c.ErpCustomerNumber, out var set))
                erpToCoupons[c.ErpCustomerNumber] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(c.CouponName.Trim());
        }

        // Which attendee used which coupon — the §787 extractor, not a second parser.
        var couponClaims = await CouponEmailsByCodeAsync(eventId, ct);

        var results = new List<VolumePackageResult>(companies.Count);
        foreach (var company in companies)
        {
            var domains = company.DomainList
                .Select(VolumePackageCompany.NormaliseDomain)
                .Where(d => d.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Coupon codes: the entity's own overrides PLUS everything its ERP customers own.
            var codes = company.CouponCodeList.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var erp in company.ErpCustomerNumberList)
                if (erpToCoupons.TryGetValue(erp, out var owned))
                    foreach (var code in owned) codes.Add(code);

            var linked = company.LinkedEmailList
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ---- CHECK 1 — every active attendee on an order whose BUYER is ours ---------------
            // 🔑 "no matter the attendees email addresses used in the order" (his words): a company
            // buying for guests still counts them.
            var fromOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in attendees)
            {
                if (string.IsNullOrWhiteSpace(a.OrderId)) continue;
                if (orderBuyerDomain.TryGetValue(a.OrderId, out var buyerDomain)
                    && domains.Contains(buyerDomain))
                {
                    fromOrders.Add(a.Email.Trim());
                }
            }

            // ---- CHECK 2 — attendees who claimed one of this entity's coupons ------------------
            var fromCoupons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in codes)
                if (couponClaims.TryGetValue(code, out var emails))
                    foreach (var e in emails) fromCoupons.Add(e);

            // ---- CHECK 3 — attendees whose OWN address belongs to the company ------------------
            // Domain match, or an explicitly linked address (the freelancer on a private e-mail).
            var fromDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in attendees)
            {
                var email = a.Email.Trim();
                if (domains.Contains(DomainOf(email)) || linked.Contains(email))
                    fromDomains.Add(email);
            }

            // 🔴 THE UNION. Not a sum — see the class remarks.
            var all = new HashSet<string>(fromOrders, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(fromCoupons);
            all.UnionWith(fromDomains);

            results.Add(new VolumePackageResult(
                company.Id,
                string.IsNullOrWhiteSpace(company.CustomName) ? $"Company {company.Id}" : company.CustomName,
                all,
                fromOrders.Count, fromCoupons.Count, fromDomains.Count));
        }

        _log?.LogInformation(
            "Volume package: {Companies} entit(ies) evaluated, {Qualified} at or above {Threshold}.",
            results.Count, results.Count(r => r.Qualifies), Threshold);

        return results;
    }

    /// <summary>
    /// Coupon code → the ACTIVE attendees who claimed it.
    /// </summary>
    /// <remarks>
    /// 🔒 Uses <see cref="CouponClaimExtractor"/> (§787) rather than a second parser: it already
    /// knows the shape — ticket-level <c>promo_code</c> with the order-level code as fallback — and
    /// it already handles the case that cost a bug once, a BLANK ticket-level code needing to fall
    /// back (which <c>??</c> does not do).
    /// ⚠️ Cross-checked against the live mirror, so a ticket cancelled after the order snapshot is
    /// not counted.
    /// </remarks>
    private async Task<Dictionary<string, HashSet<string>>> CouponEmailsByCodeAsync(
        int eventId, CancellationToken ct)
    {
        var byCode = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var activeEmails = (await _db.Attendees
                .AsNoTracking()
                .Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active
                            && a.Email != null && a.Email != "")
                .Select(a => a.Email)
                .ToListAsync(ct))
            .Select(e => e.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.EventId == eventId && o.MirrorState == MirrorState.Active && o.RawJson != null)
            .Select(o => o.RawJson!)
            .ToListAsync(ct);

        foreach (var raw in orders)
        {
            foreach (var claim in CouponClaimExtractor.FromOrderJson(raw))
            {
                if (claim.IsCancelled) continue;   // §787 — a cancelled ticket is not a claim
                if (string.IsNullOrWhiteSpace(claim.CouponName) || string.IsNullOrWhiteSpace(claim.Email))
                    continue;

                var email = claim.Email.Trim();
                if (!activeEmails.Contains(email)) continue;   // cancelled since the snapshot

                var code = claim.CouponName.Trim();
                if (!byCode.TryGetValue(code, out var set))
                    byCode[code] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(email);
            }
        }

        return byCode;
    }

    /// <summary>The domain part of an address, lower-cased. Empty when there is no <c>@</c>.</summary>
    public static string DomainOf(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var at = email.LastIndexOf('@');
        return at < 0 || at == email.Length - 1
            ? string.Empty
            : email[(at + 1)..].Trim().ToLowerInvariant();
    }
}
