using CommunityHub.Core.Domain;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>§990 — what one prepaid-purchase invoice attempt did.</summary>
/// <param name="DraftNumber">
/// The e-conomic DRAFT number, set only when e-conomic really accepted the POST. Null on every
/// other outcome — a number nobody can look up is worse than no number (§795.4).
/// </param>
/// <param name="Problem">The human reason nothing was created, or null on success.</param>
/// <param name="WouldCreate">
/// True when the whole invoice was composed but <c>Invoicing:DryRun</c> stopped the write. Not a
/// success and not a failure: the tickets are recorded and the invoice is still to be raised.
/// </param>
/// <param name="Created_">
/// 🔴 §1016a — the draft AS CREATED, for the "new draft invoice" ops mail. Null on every non-create
/// outcome (including a dry run, which creates nothing and must never be announced as if it had).
/// </param>
public sealed record PrepaidInvoiceResult(
    int? DraftNumber, string? Problem, bool WouldCreate = false,
    CreatedDraftInvoice? Created_ = null)
{
    public bool Created => DraftNumber is not null;

    public static PrepaidInvoiceResult Failed(string why) => new(null, why);
}

/// <summary>
/// §990 — creates the e-conomic DRAFT invoice for ONE prepaid ticket purchase, at the moment the
/// organizer records it.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This REVERSES a deliberate §795.2 decision, at the operator's explicit instruction</b>
/// (2026-08-09: *"it must create the invoice and populate the invoice number if i click - rename the
/// button to Create Invoice"*). §795.2 said *"the hub never raises this invoice"* and made
/// <c>ErpInvoiceNumber</c> a human confirmation. That is no longer true for the button path.
/// **The chase is deliberately left in place**: the number stays editable and
/// <see cref="CouponPrepaidBillingReminderService"/> still chases a purchase that has none, which is
/// what covers an invoice raised directly in e-conomic and a click where e-conomic was down.</para>
///
/// <para>🔑 <b>The price is TYPED, not derived, and that was the blocking question.</b> A claim
/// invoice prices each ticket from Zoho's <c>base_price</c> on the claim itself — but a prepaid pool
/// exists precisely BEFORE anybody claims, so there is no claim to read a price off. Deriving it from
/// past claims of the class would bill one partner at another's negotiated rate, and reading the
/// public list price would be wrong for exactly the partners who prepay. Operator chose the form
/// field (2026-08-09).</para>
///
/// <para>🔒 <b>Everything else is the §787 path, reused rather than re-derived</b> — customer lookup,
/// the DKK→customer-currency conversion with its §786.1(f) note, the layout lookup, the house heading
/// and the §798.1 requester-as-Att rule. A second, subtly different coupon invoice leaving the same
/// company is the failure this avoids.</para>
/// </remarks>
public sealed class CouponPrepaidInvoiceService
{
    /// <summary>
    /// The idempotency marker prefix. 🔒 Deliberately NOT <see cref="CouponDraftInvoiceService.CouponReferencePrefix"/>:
    /// that one marks a CLAIMED ticket, and the "already invoiced" scan must never confuse a
    /// prepayment for the tickets it later covers — or a partner gets billed twice for one seat.
    /// </summary>
    public const string PrepaidReferencePrefix = "CouponPrepaid-";

    private readonly IEconomicInvoiceClient _invoices;
    private readonly IFxRateProvider _fx;
    private readonly EconomicErpOptions _options;
    private readonly InvoicingOptions _invoicing;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponPrepaidInvoiceService> _log;

    public CouponPrepaidInvoiceService(
        IEconomicInvoiceClient invoices,
        IFxRateProvider fx,
        EconomicErpOptions options,
        InvoicingOptions invoicing,
        TimeProvider clock,
        ILogger<CouponPrepaidInvoiceService> log)
    {
        _invoices = invoices;
        _fx = fx;
        _options = options;
        _invoicing = invoicing;
        _clock = clock;
        _log = log;
    }

    /// <summary>The reference this purchase's invoice carries in <c>references.other</c>.</summary>
    public static string ReferenceFor(int purchaseId) =>
        $"{PrepaidReferencePrefix}{purchaseId}";

    /// <summary>
    /// Create the draft for one saved purchase. The purchase must already exist (its id is the
    /// invoice reference), and it is NEVER rolled back by a failure here — the partner agreed to buy
    /// the tickets whether or not the finance system was reachable a second later.
    /// </summary>
    public async Task<PrepaidInvoiceResult> CreateForPurchaseAsync(
        CouponInvoicingSetting rule,
        string ticketClassLabel,
        int purchaseId,
        int quantity,
        decimal unitPriceDkk,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!_invoices.CanWrite)
        {
            return PrepaidInvoiceResult.Failed(
                "e-conomic is not configured (base URL + both tokens), so no invoice was created. "
                + "The tickets are recorded — raise the invoice by hand and enter its number.");
        }

        if (rule.ErpCustomerNumber is not { } customerNumber)
        {
            return PrepaidInvoiceResult.Failed(
                $"'{rule.CouponName}' has no e-conomic customer, so there is nobody to invoice. "
                + "The tickets are recorded — pick the customer and create the invoice.");
        }

        if (quantity <= 0)
        {
            return PrepaidInvoiceResult.Failed("A purchase of 0 tickets cannot be invoiced.");
        }

        // 🔒 A zero or negative price is refused rather than sent. e-conomic accepts a 0.00 line
        // perfectly happily, and a partner invoice for 0.00 DKK is the §787.5 failure — wrong, silent
        // and only discovered by the person who receives it.
        if (unitPriceDkk <= 0m)
        {
            return PrepaidInvoiceResult.Failed(
                "Enter the agreed unit price before creating the invoice — the hub has no price for "
                + "a prepaid ticket class (nobody has claimed one yet), so it cannot guess it.");
        }

        var customer = await _invoices.GetCustomerAsync(customerNumber, ct);
        if (customer is null)
        {
            return PrepaidInvoiceResult.Failed(
                $"e-conomic customer {customerNumber} does not exist, so no invoice was created.");
        }

        var invoiceCurrency = string.IsNullOrWhiteSpace(customer.Currency)
            ? CouponInvoiceLineComposer.SourceCurrency
            : customer.Currency.Trim().ToUpperInvariant();

        // The same DKK source and the same conversion rule as the claim invoice (§787.3).
        decimal? rate = null;
        if (!string.Equals(invoiceCurrency, CouponInvoiceLineComposer.SourceCurrency, StringComparison.Ordinal))
        {
            if (!_fx.CanQuote)
            {
                return PrepaidInvoiceResult.Failed(
                    $"'{customer.Name}' invoices in {invoiceCurrency}, but no exchange-rate source "
                    + "is configured (FxRates), so the amount cannot be converted. NOT invoiced.");
            }

            rate = await _fx.GetRateAsync(
                CouponInvoiceLineComposer.SourceCurrency, invoiceCurrency, ct);
            if (rate is not > 0m)
            {
                return PrepaidInvoiceResult.Failed(
                    $"No usable {CouponInvoiceLineComposer.SourceCurrency}->{invoiceCurrency} rate "
                    + "was returned, so nothing was invoiced.");
            }
        }

        var unitPrice = unitPriceDkk;
        string? conversionNote = null;
        if (rate is { } r)
        {
            unitPrice = Math.Round(unitPriceDkk * r, 2, MidpointRounding.AwayFromZero);
            conversionNote = CouponInvoiceLineComposer.ComposeConversionNote(
                invoiceCurrency, unitPriceDkk, unitPrice);
        }

        var layout = await _invoices.FindLayoutAsync(_options.InvoiceLayoutNameLike, ct);
        if (layout is null)
        {
            return PrepaidInvoiceResult.Failed(
                $"No e-conomic layout matching '{_options.InvoiceLayoutNameLike}' was found, so the "
                + "draft could not be created.");
        }

        var reference = ReferenceFor(purchaseId);

        // 🔑 ONE line for the whole purchase, quantity = the tickets bought. A claim invoice is one
        // line per attendee because each names a person; a prepayment names nobody yet, so N lines
        // would be N identical blocks.
        var line = new EconomicInvoiceLine(
            1,
            CouponInvoiceLineComposer.ComposePrepaidDescription(
                ticketClassLabel, rule.CouponName, quantity, conversionNote),
            quantity,
            unitPrice,
            WebshopInvoiceLineComposer.ResolveProductNumber(customer.VatZoneNumber));

        var invoice = new EconomicDraftInvoice(
            Customer: customer,
            Date: DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime),
            LayoutNumber: layout.Value.LayoutNumber,
            LayoutSelf: layout.Value.Self,
            Currency: invoiceCurrency,
            OtherReference: reference,
            VendorEmployeeNumber: _options.InvoiceVendorEmployeeNumber,
            AttentionContactNumber: rule.RequesterContactNumber,   // §798.1
            YourReferenceContactNumber: null,
            Heading: _options.InvoiceHeading,                      // §814 house heading
            TextLine1: CouponInvoiceLineComposer.ComposeSubHeading(rule.CouponName, rule.Notes),
            Lines: new[] { line },
            SalesPersonEmployeeNumber: _options.InvoiceSalesPersonEmployeeNumber);

        // §788 — the dry run stops at the last possible moment, having composed everything for real.
        if (_invoicing.DryRun)
        {
            _log.LogInformation(
                "§990 DRY RUN: would invoice {Qty} prepaid ticket(s) on coupon {Coupon} to "
                + "customer {Customer}.", quantity, rule.CouponName, customer.CustomerNumber);

            return new PrepaidInvoiceResult(null,
                $"Dry run is ON (Invoicing:DryRun), so NO invoice was created in e-conomic. The "
                + $"{quantity} ticket(s) are recorded; the draft would have gone to "
                + $"'{customer.Name}' for {quantity * unitPrice:0.##} {invoiceCurrency}.",
                WouldCreate: true);
        }

        try
        {
            var draftNumber = await _invoices.CreateDraftInvoiceAsync(invoice, ct);

            _log.LogInformation(
                "§990: created e-conomic draft {Draft} for prepaid purchase {Purchase} "
                + "({Qty} × {Class} on coupon {Coupon}).",
                draftNumber, purchaseId, quantity, ticketClassLabel, rule.CouponName);

            // 🔴 §1016a — hand the created draft back so the caller can ANNOUNCE it. Operator
            // 2026-08-09: *"i did not get any emails about a new invoice was created"*, and he was
            // right: DraftInvoiceCreatedNotifier had exactly two callers, both background jobs, so
            // the one invoice path a human triggers by clicking a button called "Create Invoice"
            // was the only one that announced nothing.
            return new PrepaidInvoiceResult(draftNumber, null, Created_: new CreatedDraftInvoice(
                Description: CouponInvoiceLineComposer.ComposePrepaidDescription(
                    ticketClassLabel, rule.CouponName, quantity, conversionNote: null),
                CustomerNumber: customer.CustomerNumber,
                CustomerName: customer.Name,
                Reference: reference,
                Total: quantity * unitPrice,
                Currency: invoiceCurrency,
                DraftNumber: draftNumber));
        }
        catch (EconomicApiException ex)
        {
            // 🔒 Fail SOFT and say so. The purchase row stays — the agreement happened — and the
            // §795.2 chase will keep asking for a number, which is exactly the right outcome.
            _log.LogError(ex,
                "§990: e-conomic refused the prepaid draft for purchase {Purchase}.", purchaseId);

            return PrepaidInvoiceResult.Failed(
                $"e-conomic refused the invoice ({ex.Message}). The {quantity} ticket(s) are "
                + "recorded — raise the invoice by hand and enter its number.");
        }
    }
}
