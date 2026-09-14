using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>What one coupon-invoicing pass did.</summary>
/// <param name="ClaimsSeen">Claimed coupon tickets found in the mirrored orders.</param>
/// <param name="AlreadyInvoiced">Claims whose reference already exists on a booked or draft invoice.</param>
/// <param name="Created">Drafts actually created. 🔒 Forced to 0 in a dry run.</param>
/// <param name="Skipped">Claims that could not be invoiced, each with a reason in <paramref name="Problems"/>.</param>
/// <param name="UnmappedCoupons">Coupon names claimed but with no usable billing rule — the alert set.</param>
/// <param name="CreatedDrafts">
/// §795.3 — the drafts this pass really created, described well enough to mail. 🔒 Always EMPTY in a
/// dry run: a would-create is not a draft, and announcing one would name an invoice that does not
/// exist.
/// </param>
public sealed record CouponInvoiceRunResult(
    int ClaimsSeen,
    int AlreadyInvoiced,
    int Created,
    int Skipped,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> UnmappedCoupons,
    bool DryRun = false,
    IReadOnlyList<string>? WouldCreate = null,
    IReadOnlyList<CreatedDraftInvoice>? CreatedDrafts = null)
{
    public IReadOnlyList<string> WouldCreateOrEmpty => WouldCreate ?? Array.Empty<string>();

    public IReadOnlyList<CreatedDraftInvoice> CreatedDraftsOrEmpty =>
        CreatedDrafts ?? Array.Empty<CreatedDraftInvoice>();

    public static CouponInvoiceRunResult Inactive(string why) =>
        new(0, 0, 0, 0, new[] { why }, Array.Empty<string>());
}

/// <summary>
/// §787 — creates e-conomic DRAFT invoices for claimed COUPON tickets. The C# replacement for
/// <c>Create-ERP-Invoice-Coupon-Tickets.ps1</c>.
/// </summary>
/// <remarks>
/// <para>🔑 <b>No Zoho call.</b> Claims are read from <c>Orders.RawJson</c>, which
/// <c>AttendeeBackstageSyncJob</c> already mirrors from the v3 <c>/orders</c> feed. §525: ~21 jobs
/// each minting their own token once tripped the refresh-grant limit and every sync went dark
/// against a perfectly valid credential — an hourly job that needs no token cannot do that.</para>
///
/// <para>🔒 <b>The script is EVIDENCE, not a specification (§787.5).</b> It was written ~Feb 2026 and
/// has never run, so unlike §786 there is no live behaviour to preserve. Its field assumptions were
/// re-verified against the live feed (§787.6) rather than trusted; where it is merely one person's
/// untested intention, that is flagged instead of ported.</para>
///
/// <para>⚠️ <b>It bills <c>base_price</c>, never <c>total</c>.</b> Measured on the real mirror: two
/// coupon tickets have <c>base_price</c> 3500 / 2000 and <c>total</c> <b>0</b>, because the coupon
/// covered them entirely. Billing <c>total</c> would invoice the partner 0.00 DKK for every coupon
/// ticket and nothing downstream would notice.</para>
///
/// <para>🔒 <b>An unmapped coupon is never guessed at.</b> It is skipped, recorded, and surfaced —
/// the money belongs to somebody, and inventing a customer is worse than not invoicing.</para>
/// </remarks>
public sealed class CouponDraftInvoiceService
{
    /// <summary>The idempotency marker prefix, ported from the script (§787.3).</summary>
    public const string CouponReferencePrefix = "CouponTicket-";

    private readonly CommunityHubDbContext _db;
    private readonly IEconomicInvoiceClient _invoices;
    private readonly IFxRateProvider _fx;
    private readonly EconomicErpOptions _options;
    private readonly InvoicingOptions _invoicing;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponDraftInvoiceService> _log;

    public CouponDraftInvoiceService(
        CommunityHubDbContext db,
        IEconomicInvoiceClient invoices,
        IFxRateProvider fx,
        EconomicErpOptions options,
        InvoicingOptions invoicing,
        TimeProvider clock,
        ILogger<CouponDraftInvoiceService> log)
    {
        _db = db;
        _invoices = invoices;
        _fx = fx;
        _options = options;
        _invoicing = invoicing;
        _clock = clock;
        _log = log;
    }

    public async Task<CouponInvoiceRunResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        if (!_invoices.CanWrite)
        {
            return CouponInvoiceRunResult.Inactive(
                "e-conomic is not configured (base URL + both tokens), so no invoice could be "
                + "created. Nothing was attempted.");
        }

        // The claims, from CEH's own mirror.
        var raw = await _db.Orders
            .Where(o => o.EventId == eventId)
            .Select(o => o.RawJson)
            .ToListAsync(ct);

        // 🔴 §787.20 — CANCELLED TICKETS ARE NOT BILLABLE, and this filter was missing entirely.
        // Both coupon tickets in the live mirror are cancelled, so a run reported 5500 DKK of
        // tickets that no longer exist; with dry run off it would have invoiced a partner for them.
        // ⚠️ Held back only by Invoicing:DryRun being true — this was a live defect, not a gap.
        var allClaims = raw.SelectMany(j => CouponClaimExtractor.FromOrderJson(j)).ToList();
        var claims = allClaims.Where(c => !c.IsCancelled).ToList();

        var cancelled = allClaims.Count - claims.Count;
        if (cancelled > 0)
        {
            _log.LogInformation(
                "§787.20: {Cancelled} cancelled coupon ticket(s) excluded from invoicing.", cancelled);
        }
        if (claims.Count == 0)
        {
            _log.LogInformation("§787: no claimed coupon tickets in the mirrored orders.");
            return new CouponInvoiceRunResult(0, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), _invoicing.DryRun,
                Array.Empty<string>());
        }

        var settings = await _db.CouponInvoicingSettings
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);
        var byName = settings.ToDictionary(
            s => s.CouponName.Trim(), s => s, StringComparer.OrdinalIgnoreCase);

        var alreadyInvoiced = await _invoices.ListInvoicedOrderReferencesAsync(ct);

        var problems = new List<string>();
        var wouldCreate = new List<string>();
        var createdDrafts = new List<CreatedDraftInvoice>();
        var unmapped = new List<string>();
        var created = 0;
        var skippedAsInvoiced = 0;
        var skipped = 0;

        // 🔒 ONE INVOICE PER COUPON, not per ticket. The partner agreed a coupon, so that is the
        // thing they recognise on an invoice — and it is what the retired script produced. Grouping
        // also means a second claim on the same coupon does not re-bill the first.
        foreach (var group in claims.GroupBy(c => c.CouponName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var couponName = group.Key.Trim();
            var pending = new List<CouponClaim>();

            foreach (var claim in group)
            {
                if (alreadyInvoiced.Contains(claim.Reference)) skippedAsInvoiced++;
                else pending.Add(claim);
            }

            if (pending.Count == 0) continue;

            if (!byName.TryGetValue(couponName, out var rule))
            {
                // ⚠️ AUTO-CREATE the Unmapped row. The retired CSV simply had no row, and a missing
                // row cannot be shown, chased or acted on. Creating it is what puts the coupon at
                // the top of /Organizer/CouponInvoicing with a claim count and a date.
                rule = new CouponInvoicingSetting
                {
                    EventId = eventId,
                    CouponName = couponName,
                    BillingType = CouponBillingType.Unmapped,
                    FirstSeenClaimedAt = _clock.GetUtcNow(),
                    CreatedAt = _clock.GetUtcNow(),
                };
                _db.CouponInvoicingSettings.Add(rule);
                await _db.SaveChangesAsync(ct);
                byName[couponName] = rule;
            }
            else if (rule.FirstSeenClaimedAt is null)
            {
                rule.FirstSeenClaimedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
            }

            if (rule.BillingType == CouponBillingType.NoInvoicing)
            {
                // A DECISION, not a gap — never reported as a problem and never alerted on.
                continue;
            }

            // 🔴 §794 — A PREPAID COUPON IS NEVER INVOICED. The partner paid up front for an
            // allocation, so billing them per claim charges them a SECOND time for tickets they
            // already own. What they are owed is a balance, not an invoice.
            //
            // ⚠️ Not a "problem" and never alerted on: it is the agreement working. The claim
            // deducts from their pool (§794.1), which is derived from the current state of every
            // claim — so a cancellation returns the allocation by itself, and no credit note is
            // ever needed.
            if (rule.IsPrepaid)
            {
                _log.LogInformation(
                    "§794: coupon '{Coupon}' is PREPAID — {Count} claim(s) draw on the partner's "
                    + "allocation and are not invoiced.", couponName, pending.Count);
                continue;
            }

            if (!rule.IsInvoiceable)
            {
                unmapped.Add(couponName);
                skipped += pending.Count;
                problems.Add(
                    $"Coupon '{couponName}': {pending.Count} claimed ticket(s) worth "
                    + $"{pending.Sum(p => p.UnitPriceDkk):0.##} {CouponClaimExtractor.SourceCurrency}, "
                    + "but nobody has said who pays. Map it on /Organizer/CouponInvoicing.");
                continue;
            }

            // 🔴 §1016d — BATCH THE BILLING PERIOD. Operator 2026-08-09: *"we dont want to invoice
            // customer for every single claim; that creates to many invoices. therefore you must
            // batch them to every 2 weeks and remember when the last invoice was sent for this
            // coupon, so you know the 'catch-up' to invoice."*
            //
            // 🔑 The CLAIM was never the problem — the balance has always moved the instant a
            // ticket is claimed (20 → 18), and nothing here changes that. What ran too often was
            // the INVOICE: this job passes every ~10 minutes and billed whatever was new, so a
            // coupon claimed on ten different days produced ten invoices.
            //
            // 🔒 The "catch-up" needs no bookkeeping of its own. `pending` is ALREADY "every claim
            // not yet on a booked or draft invoice" (§787's per-claim reference interlock), so when
            // the window opens the batch is automatically everything since the last invoice —
            // including anything a failed earlier pass left behind. This gate decides only WHEN.
            //
            // ⚠️ Measured from FirstSeenClaimedAt when the coupon has never been invoiced, so the
            // FIRST batch accumulates too; otherwise claim #1 would get an invoice to itself.
            if (!IsBillingPeriodDue(rule, out var dueAt))
            {
                _log.LogInformation(
                    "§1016d: coupon '{Coupon}' has {Count} claim(s) waiting for its billing period "
                    + "(next invoice on or after {Due:yyyy-MM-dd}).", couponName, pending.Count, dueAt);
                // NOT a problem and NOT skipped-with-a-complaint: the claims are safe, counted and
                // will be billed together. Reporting it would alert him about the system working.
                continue;
            }

            try
            {
                var outcome = await InvoiceCouponAsync(rule, pending, wouldCreate, createdDrafts, ct);
                if (outcome is null)
                {
                    created++;
                    // 🔒 STAMPED ONLY ON A REAL CREATE. A dry run composes everything and writes
                    // nothing, so stamping it would push the next REAL invoice out by a fortnight.
                    if (!_invoicing.DryRun)
                    {
                        rule.LastInvoicedAt = _clock.GetUtcNow();
                        await _db.SaveChangesAsync(ct);
                    }
                }
                else { skipped += pending.Count; problems.Add(outcome); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad coupon must not stop the pass — the other partners are still owed their
                // invoices, and a thrown run would retry the same bad coupon for ever.
                skipped += pending.Count;
                problems.Add($"Coupon '{couponName}': {ex.Message}");
                _log.LogError(ex, "§787: failed to invoice coupon {Coupon}.", couponName);
            }
        }

        if (_invoicing.DryRun)
        {
            _log.LogInformation(
                "§787 DRY RUN — nothing was written to e-conomic. {Claims} claim(s), {Already} "
                + "already invoiced, {Would} invoice(s) WOULD have been created, {Skipped} skipped, "
                + "{Unmapped} coupon(s) unmapped. Set Invoicing:DryRun=false to create them.",
                claims.Count, skippedAsInvoiced, wouldCreate.Count, skipped, unmapped.Count);
        }
        else
        {
            _log.LogInformation(
                "§787: {Claims} claim(s), {Already} already invoiced, {Created} draft(s) created, "
                + "{Skipped} skipped, {Unmapped} coupon(s) unmapped.",
                claims.Count, skippedAsInvoiced, created, skipped, unmapped.Count);
        }

        return new CouponInvoiceRunResult(
            claims.Count, skippedAsInvoiced,
            // 🔒 Forced to 0 in a dry run — InvoiceCouponAsync returns "success" for a would-create,
            // so this would otherwise report invoices that do not exist.
            _invoicing.DryRun ? 0 : created,
            skipped, problems, unmapped, _invoicing.DryRun, wouldCreate, createdDrafts);
    }

    /// <summary>Invoices one coupon's pending claims. Null on success, else the human reason.</summary>
    /// <summary>
    /// 🔴 §1016d — is this coupon's billing period open? Batches claims into ONE invoice per
    /// <see cref="InvoicingOptions.CouponInvoiceIntervalDays"/> (default 14) instead of one per
    /// claim.
    /// </summary>
    /// <param name="dueAt">When the period next opens, for the log line.</param>
    /// <remarks>
    /// <para>🔒 <b>Each coupon runs on its OWN clock</b>, anchored to its last invoice, so two
    /// partners' billing periods do not collapse onto whichever day the job happened to start.</para>
    ///
    /// <para>⚠️ <b>An interval of 0 restores per-pass invoicing</b> — the pre-§1016d behaviour —
    /// so a period can be closed early with a setting rather than a deploy.</para>
    ///
    /// <para>⚠️ <b>A coupon with NO anchor at all invoices now.</b> Neither timestamp set means the
    /// row predates this bookkeeping; holding its claims back for a fortnight on the strength of a
    /// missing value would delay real money for a reason nobody could see. It stamps
    /// <c>LastInvoicedAt</c> on the way out, so it is anchored from then on.</para>
    /// </remarks>
    internal bool IsBillingPeriodDue(CouponInvoicingSetting rule, out DateTimeOffset dueAt)
    {
        dueAt = default;
        // §1016d — THIS coupon's own period wins over the edition default (operator 2026-08-09:
        // *"maybe the internal days could be a field that could be adjusted pr coupon"*). A billing
        // period is negotiated per partner, so one global number forces the strictest partner's
        // terms onto everybody. A NEGATIVE override is treated as unset — it is a number typed into
        // a form, and a typo must fall back to the default rather than silently change the terms.
        var days = rule.InvoiceIntervalDays is { } perCoupon && perCoupon >= 0
            ? perCoupon
            : _invoicing.CouponInvoiceIntervalDays;
        if (days <= 0) return true;                       // batching switched off

        var anchor = rule.LastInvoicedAt ?? rule.FirstSeenClaimedAt;
        if (anchor is null) return true;                  // no anchor ⇒ bill now, and anchor it

        dueAt = anchor.Value.AddDays(days);
        return _clock.GetUtcNow() >= dueAt;
    }

    private async Task<string?> InvoiceCouponAsync(
        CouponInvoicingSetting rule, IReadOnlyList<CouponClaim> claims,
        List<string> wouldCreate, List<CreatedDraftInvoice> createdDrafts, CancellationToken ct)
    {
        var customerNumber = rule.ErpCustomerNumber!.Value;
        var customer = await _invoices.GetCustomerAsync(customerNumber, ct);
        if (customer is null)
        {
            return $"Coupon '{rule.CouponName}': e-conomic customer {customerNumber} does not exist.";
        }

        var invoiceCurrency = string.IsNullOrWhiteSpace(customer.Currency)
            ? CouponClaimExtractor.SourceCurrency
            : customer.Currency.Trim().ToUpperInvariant();

        // ⚠️ The source currency here is DKK, not the webshop's EUR. Both are live commercial
        // behaviour and are deliberately NOT unified (§787.3).
        decimal? rate = null;
        if (!string.Equals(invoiceCurrency, CouponClaimExtractor.SourceCurrency, StringComparison.Ordinal))
        {
            if (!_fx.CanQuote)
            {
                return $"Coupon '{rule.CouponName}': customer '{customer.Name}' invoices in "
                       + $"{invoiceCurrency}, but no exchange-rate source is configured (FxRates), "
                       + "so the amounts cannot be converted. NOT invoiced.";
            }

            rate = await _fx.GetRateAsync(CouponClaimExtractor.SourceCurrency, invoiceCurrency, ct);
            if (rate is not > 0m)
            {
                return $"Coupon '{rule.CouponName}': no usable "
                       + $"{CouponClaimExtractor.SourceCurrency}->{invoiceCurrency} rate was "
                       + "returned, so it was NOT invoiced.";
            }
        }

        var composed = CouponInvoiceLineComposer.Compose(
            claims,
            vatZoneNumber: customer.VatZoneNumber,
            convert: dkk =>
            {
                if (rate is not { } r) return (dkk, null);
                var converted = Math.Round(dkk * r, 2, MidpointRounding.AwayFromZero);
                return (converted,
                    CouponInvoiceLineComposer.ComposeConversionNote(invoiceCurrency, dkk, converted));
            },
            // §1091 — the split. Both null on every coupon predating this, which bills the whole
            // ticket at what it actually cost: exactly the old behaviour, stated rather than implied.
            agreedUnitPriceDkk: rule.AgreedUnitPriceDkk,
            invoicedSharePercent: rule.InvoicedSharePercent);

        var layout = await _invoices.FindLayoutAsync(_options.InvoiceLayoutNameLike, ct);
        if (layout is null)
        {
            return $"Coupon '{rule.CouponName}': no e-conomic layout matching "
                   + $"'{_options.InvoiceLayoutNameLike}' was found, so the draft could not be created.";
        }

        // 🔒 The reference is the FIRST claim's own reference, which is what the script used and what
        // `ListInvoicedOrderReferencesAsync` scans for. Every claim in this invoice is listed as a
        // line, so re-running finds the reference and skips — see the grouping note above.
        var reference = claims[0].Reference;

        var invoice = new EconomicDraftInvoice(
            Customer: customer,
            Date: DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime),
            LayoutNumber: layout.Value.LayoutNumber,
            LayoutSelf: layout.Value.Self,
            Currency: invoiceCurrency,
            OtherReference: reference,
            VendorEmployeeNumber: _options.InvoiceVendorEmployeeNumber,
            // §798.1 — the REQUESTER is the invoice's Att person. Null falls back to the customer's
            // own attention contact, which is exactly what every coupon invoice did before, so a
            // coupon nobody has named a requester for is unchanged.
            AttentionContactNumber: rule.RequesterContactNumber,
            // ⚠️ "Your reference" is deliberately NOT set from the requester. He asked for the Att
            // person; §786.1(b) is a separate decision made for webshop invoices, and quietly
            // applying it here would change what prints on a partner's invoice without being asked.
            YourReferenceContactNumber: null,
            // §814 — the HOUSE heading, the same one the webshop invoices carry and the one he
            // approved on the first invoice he sent. The coupon name moves to the line below.
            Heading: _options.InvoiceHeading,
            // §990 — the coupon's own notes ride here (a PO number, or whatever the partner needs
            // on the document). Blank prints nothing.
            TextLine1: CouponInvoiceLineComposer.ComposeSubHeading(rule.CouponName, rule.Notes),
            Lines: composed.Select(l => new EconomicInvoiceLine(
                l.LineNumber, l.Description, l.Quantity, l.UnitNetPrice, l.ProductNumber)).ToList(),
            // §811(c) — the second employee reference; both must appear on the invoice.
            SalesPersonEmployeeNumber: _options.InvoiceSalesPersonEmployeeNumber);

        var total = composed.Where(l => l.UnitNetPrice is not null)
            .Sum(l => (l.Quantity ?? 0m) * (l.UnitNetPrice ?? 0m));

        // §788 — the dry run stops HERE, at the last possible moment, exactly as §786 does: the
        // customer was resolved, the currency converted and the lines composed for real. A dry run
        // that skipped that work would only prove the job starts.
        if (_invoicing.DryRun)
        {
            wouldCreate.Add(
                $"'{rule.CouponName}' ({reference}) → e-conomic customer {customer.CustomerNumber} "
                + $"'{customer.Name}', {composed.Count} ticket(s), {total:0.##} {invoiceCurrency}");

            _log.LogInformation("§788 DRY RUN: would invoice coupon {Coupon}.", rule.CouponName);
            return null;
        }

        var draftNumber = await _invoices.CreateDraftInvoiceAsync(invoice, ct);

        // §795.3 — recorded only on THIS path, after e-conomic accepted it. A draft that was really
        // created is the only thing the info@ notice may name.
        createdDrafts.Add(new CreatedDraftInvoice(
            Description: $"Coupon '{rule.CouponName}' — {composed.Count} ticket(s)",
            CustomerNumber: customer.CustomerNumber,
            CustomerName: customer.Name,
            Reference: reference,
            Total: total,
            Currency: invoiceCurrency,
            DraftNumber: draftNumber));

        return null;
    }
}
