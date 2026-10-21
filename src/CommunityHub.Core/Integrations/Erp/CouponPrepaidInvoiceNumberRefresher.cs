using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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

    private readonly CouponPrepaidInvoiceService? _create;

    public CouponPrepaidInvoiceNumberRefresher(
        CommunityHubDbContext db,
        IEconomicInvoiceClient invoices,
        ILogger<CouponPrepaidInvoiceNumberRefresher>? log = null,
        CouponPrepaidInvoiceService? create = null)
    {
        _db = db;
        _invoices = invoices;
        _log = log;
        _create = create;
    }

    /// <summary>What one refresh pass changed, for the page banner and the tests.</summary>
    /// <param name="Updated">Purchases whose stored number or booked flag changed.</param>
    /// <param name="Checked">Purchases that were looked up at all.</param>
    /// <param name="Problem">Why nothing was checked, or null.</param>
    public sealed record RefreshResult(int Updated, int Checked, string? Problem = null)
    {
        public static RefreshResult Skipped(string why) => new(0, 0, why);

        /// <summary>§1312 — drafts created AGAIN because CEH's own invoice had disappeared from e-conomic.</summary>
        public IReadOnlyList<CreatedDraftInvoice> Recreated { get; init; } = Array.Empty<CreatedDraftInvoice>();

        /// <summary>§1312 — purchases whose invoice is gone but could not be recreated, with the reason.</summary>
        public IReadOnlyList<string> RecreateProblems { get; init; } = Array.Empty<string>();
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

        // §1312 — EVERY purchase, not only numbered ones: a partner's self-service purchase had its draft created but its
        // number never stored, so it was never refreshed. A purchase is matched only by CEH's own reference, so a number an
        // organizer typed (an invoice CEH did not make) is still left exactly as it is.
        var purchases = await _db.CouponPrepaidPurchases
            .Include(p => p.Allocation)
            .Where(p => p.Allocation.EventId == eventId)
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
            // 🔴 §1362 — a credit note carries the marker of the invoice it credits; taking it wrote credit note 203 over
            // invoice 199 on the purchase it credits. The purchase's number is its INVOICE's, never the credit note's.
            if (r.IsCreditNote) continue;
            if (!string.IsNullOrWhiteSpace(r.Reference)) byReference[r.Reference.Trim()] = r;
        }

        var updated = 0;
        var checkedCount = 0;
        var recreated = new List<CreatedDraftInvoice>();
        var recreateProblems = new List<string>();
        var marked = false;
        foreach (var p in purchases)
        {
            // The marker CEH wrote when it raised the invoice.
            if (!byReference.TryGetValue(
                    CouponPrepaidInvoiceService.ReferenceFor(p.Id), out var found))
            {
                // 🔴 §1312 — CEH's OWN invoice is gone from e-conomic (drafts AND booked): the draft was deleted, so it
                // is raised again (operator 2026-09-29: "make the same fix for the coupon prepaid invoice"). 🔒 Only a
                // purchase CEH itself invoiced, and never on an EMPTY answer — the agreement always holds invoices, so an
                // empty list is a read that failed quietly, not "everything was deleted". A typed number: left alone.
                if (p.ErpInvoiceRaisedByCeh && live.Count > 0 && _create is not null)
                    await RecreateAsync(p, recreated, recreateProblems, ct);
                continue;
            }

            checkedCount++;
            var current = found.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Its reference is CEH's ⇒ CEH raised it: marks every invoice made before the flag existed. Bookkeeping, not a
            // number change, so it is saved without counting as "updated".
            if (!p.ErpInvoiceRaisedByCeh) { p.ErpInvoiceRaisedByCeh = true; marked = true; }
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

        if (updated > 0 || marked || recreated.Count > 0 || recreateProblems.Count > 0) await _db.SaveChangesAsync(ct);
        return new RefreshResult(updated, checkedCount) { Recreated = recreated, RecreateProblems = recreateProblems };
    }

    /// <summary>§1312 — raise a CEH prepaid invoice again, from what the purchase recorded.</summary>
    private async Task RecreateAsync(
        CouponPrepaidPurchase p, List<CreatedDraftInvoice> recreated, List<string> problems, CancellationToken ct)
    {
        // Loaded here, not Included in the scan: an Include of the (required) coupon setting is an INNER join, and a
        // purchase whose setting row is missing would silently drop out of the whole refresh.
        var rule = p.Allocation is null ? null : await _db.CouponInvoicingSettings
            .FirstOrDefaultAsync(s => s.Id == p.Allocation.CouponInvoicingSettingId, ct);
        if (rule is null || p.UnitPriceDkk is not { } price || price <= 0m || p.Quantity <= 0)
        {
            problems.Add($"Prepaid purchase {p.Id}: its invoice is gone from e-conomic but the purchase has no coupon or price to raise it again.");
            return;
        }
        var label = p.Allocation!.TicketClassLabel ?? p.Allocation.TicketClassId;
        var result = await _create!.CreateForPurchaseAsync(rule, label, p.Id, p.Quantity, price, ct);
        if (result.DraftNumber is { } draft)
        {
            _log?.LogWarning("§1312 prepaid purchase {Purchase}: invoice {Old} is no longer in e-conomic — raised again as draft {New}.",
                p.Id, p.ErpInvoiceNumber, draft);
            p.ErpInvoiceNumber = draft.ToString(System.Globalization.CultureInfo.InvariantCulture);
            p.ErpInvoiceIsBooked = false;
            if (result.Created_ is { } created) recreated.Add(created);
        }
        else if (result.Problem is { } why)
        {
            problems.Add($"Prepaid purchase {p.Id} ('{rule.CouponName}'): the invoice is gone from e-conomic and could not be raised again — {why}");
        }
    }
}
