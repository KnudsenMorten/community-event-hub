using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// 🔴 §1013a — keeps each prepaid purchase's stored e-conomic invoice number CURRENT, so a draft
/// number is replaced by the booked number the moment a human books the invoice.
/// </summary>
/// <remarks>
/// <para><b>The defect this closes.</b> Operator 2026-08-09: *"you mentioned invoice 182, but the
/// actual number is 170"*, then *"i have sent invoice now and it got invoice 170"*. e-conomic
/// numbers a DRAFT from one series and RE-NUMBERS it from another when it is booked, so the number
/// CEH captured at creation was correct at that moment and became a dead reference the instant he
/// booked it. Nothing ever went back for the new one.</para>
///
/// <para>✅ Live-verified (agreement 1685551, 2026-08-09): <c>/invoices/drafts</c> held no
/// <c>CouponPrepaid-*</c> row at all, while <c>/invoices/booked</c> held <c>bookedInvoiceNumber</c>
/// 170 for <c>CouponPrepaid-1</c>. CEH held 182.</para>
///
/// <para>🔑 <b>This is wiring, not a new integration.</b>
/// <see cref="IEconomicInvoiceClient.ListInvoiceReferencesAsync"/> already pages BOTH
/// <c>/invoices/booked</c> and <c>/invoices/drafts</c> and returns
/// <c>(Reference, Number, IsBooked)</c> — it exists for the §795.4 coupon page. All that was missing
/// was somebody looking each purchase up by the marker CEH itself wrote.</para>
///
/// <para>🔒 <b>A HAND-TYPED NUMBER CAN NEVER BE OVERWRITTEN, by construction.</b> The scan matches
/// on <c>references.other == "CouponPrepaid-{purchaseId}"</c> — a marker only CEH writes, and only
/// when CEH raised the invoice. An invoice the operator raised by hand in e-conomic carries no such
/// reference, so it cannot match, so his typed value is never a candidate. That is a stronger
/// guarantee than a flag, because it cannot be got wrong by a later edit.</para>
///
/// <para>⚠️ <b>NEVER CLEARS.</b> An invoice that has vanished from both lists (deleted draft,
/// credited invoice, a read that came back short) leaves the stored number ALONE. §553's rule:
/// "not found" is not "gone", and a finance reference is exactly the wrong place to guess.</para>
/// </remarks>
public sealed class CouponPrepaidInvoiceNumberRefresher
{
    private readonly CommunityHubDbContext _db;
    private readonly IEconomicInvoiceClient _invoices;
    private readonly ILogger<CouponPrepaidInvoiceNumberRefresher>? _log;

    public CouponPrepaidInvoiceNumberRefresher(
        CommunityHubDbContext db,
        IEconomicInvoiceClient invoices,
        ILogger<CouponPrepaidInvoiceNumberRefresher>? log = null)
    {
        _db = db;
        _invoices = invoices;
        _log = log;
    }

    /// <summary>What one refresh pass changed, for the page banner and the tests.</summary>
    /// <param name="Updated">Purchases whose stored number or booked flag changed.</param>
    /// <param name="Checked">Purchases that were looked up at all.</param>
    /// <param name="Problem">Why nothing was checked, or null.</param>
    public sealed record RefreshResult(int Updated, int Checked, string? Problem = null)
    {
        public static RefreshResult Skipped(string why) => new(0, 0, why);
    }

    /// <summary>
    /// Re-read every CEH-raised prepaid invoice's CURRENT number from e-conomic and write back any
    /// that moved. Best-effort: an e-conomic outage returns a reason and changes nothing.
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(int eventId, CancellationToken ct = default)
    {
        if (!_invoices.CanWrite)
        {
            // Same configuration gate as the create path — no tokens, no lookup.
            return RefreshResult.Skipped("e-conomic is not configured on this host.");
        }

        // Only purchases CEH itself invoiced can be refreshed, and only those with a number to
        // correct. A purchase still awaiting its number is the §795.2 chase's job, not this one.
        var purchases = await _db.CouponPrepaidPurchases
            .Where(p => p.Allocation.EventId == eventId && p.ErpInvoiceNumber != null)
            .ToListAsync(ct);
        if (purchases.Count == 0) return new RefreshResult(0, 0);

        IReadOnlyCollection<EconomicInvoiceReference> live;
        try
        {
            live = await _invoices.ListInvoiceReferencesAsync(ct);
        }
        catch (Exception ex)
        {
            // 🔒 An unreadable e-conomic must never look like "these invoices are gone".
            _log?.LogWarning(ex, "§1013a: could not read e-conomic invoice references.");
            return RefreshResult.Skipped("e-conomic could not be read, so no number was refreshed.");
        }

        var byReference = new Dictionary<string, EconomicInvoiceReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in live)
        {
            if (!string.IsNullOrWhiteSpace(r.Reference)) byReference[r.Reference.Trim()] = r;
        }

        var updated = 0;
        var checkedCount = 0;
        foreach (var p in purchases)
        {
            // The marker CEH wrote when it raised the invoice. No match ⇒ this purchase's number
            // was typed in by a human (or the invoice is not in either list) ⇒ leave it alone.
            if (!byReference.TryGetValue(
                    CouponPrepaidInvoiceService.ReferenceFor(p.Id), out var found))
            {
                continue;
            }

            checkedCount++;
            var current = found.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(p.ErpInvoiceNumber, current, StringComparison.Ordinal)
                && p.ErpInvoiceIsBooked == found.IsBooked)
            {
                continue;
            }

            _log?.LogInformation(
                "§1013a: prepaid purchase {Purchase} invoice number {Old} → {New} (booked: {Booked}).",
                p.Id, p.ErpInvoiceNumber, current, found.IsBooked);
            p.ErpInvoiceNumber = current;
            p.ErpInvoiceIsBooked = found.IsBooked;
            updated++;
        }

        if (updated > 0) await _db.SaveChangesAsync(ct);
        return new RefreshResult(updated, checkedCount);
    }
}
