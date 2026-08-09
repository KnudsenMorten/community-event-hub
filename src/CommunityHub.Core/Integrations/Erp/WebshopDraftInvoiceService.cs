using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>What one invoicing pass did. Every count has a matching reason in <see cref="Problems"/>
/// when it represents work that did NOT happen.</summary>
public sealed record WebshopInvoiceRunResult(
    int OrdersSeen,
    int AlreadyInvoiced,
    int Created,
    int Skipped,
    IReadOnlyList<string> Problems,
    // §788 — in a DRY RUN nothing is created; these are the invoices that WOULD have been, each
    // described well enough to be read as a decision rather than a count.
    bool DryRun = false,
    IReadOnlyList<string>? WouldCreate = null,
    // §795.3 — the drafts really created, for the info@ notice. 🔒 Empty in a dry run.
    IReadOnlyList<CreatedDraftInvoice>? CreatedDrafts = null)
{
    public IReadOnlyList<string> WouldCreateOrEmpty => WouldCreate ?? Array.Empty<string>();

    public IReadOnlyList<CreatedDraftInvoice> CreatedDraftsOrEmpty =>
        CreatedDrafts ?? Array.Empty<CreatedDraftInvoice>();

    public static WebshopInvoiceRunResult Inactive(string why) =>
        new(0, 0, 0, 0, new[] { why });
}

/// <summary>
/// §786 — creates e-conomic DRAFT invoices from completed webshop orders. The C# replacement for
/// <c>Sync-Webshop-Orders-Create-ERP-Invoice.ps1</c>, which the operator runs hourly on a VM and
/// wants retired.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The idempotency marker is a CONTRACT, not an implementation detail.</b> Every invoice
/// carries <c>references.other = "WebshopOrderId-&lt;n&gt;"</c>, and this service refuses to invoice
/// an order whose marker already exists on ANY booked or draft invoice. That is the same marker the
/// retired script writes and reads, and it is what makes the cutover safe: during the swap the two
/// implementations see each other's work, so the second one to run skips instead of double-invoicing
/// a sponsor. Do not "improve" the marker format (§786.2).</para>
///
/// <para>⚠️ <b>It never invents an exchange rate.</b> When a customer invoices in a currency other
/// than EUR and no FX provider is configured, the order is SKIPPED with a named reason — not billed
/// at its EUR figures under a foreign currency symbol, which is the one failure here that reaches a
/// customer as a wrong number rather than as a missing invoice.</para>
///
/// <para>⚠️ <b>It never invents a price.</b> A line whose unit price is 0 is refused: the hub's
/// <c>WooLineItem.UnitPrice</c> defaults to 0 for the sponsor-task callers that do not carry money,
/// so a zero genuinely means "not supplied" — and a 0.00 row on a customer's invoice would be
/// noticed by the customer before it was noticed here.</para>
/// </remarks>
public sealed class WebshopDraftInvoiceService
{
    /// <summary>The reference prefix. 🔒 Ported exactly — see the remarks.</summary>
    public const string OrderReferencePrefix = "WebshopOrderId-";

    /// <summary>The webshop's own currency. Prices arrive from WooCommerce in this.</summary>
    public const string WebshopCurrency = "EUR";

    private readonly WooCommerceClient _woo;
    private readonly CompanyManagerClient _cm;
    private readonly IEconomicInvoiceClient _invoices;
    private readonly IEconomicContactAdminClient _contacts;
    private readonly IFxRateProvider _fx;
    private readonly EconomicErpOptions _options;
    private readonly InvoicingOptions _invoicing;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebshopDraftInvoiceService> _log;

    public WebshopDraftInvoiceService(
        WooCommerceClient woo,
        CompanyManagerClient cm,
        IEconomicInvoiceClient invoices,
        IEconomicContactAdminClient contacts,
        IFxRateProvider fx,
        EconomicErpOptions options,
        InvoicingOptions invoicing,
        TimeProvider clock,
        ILogger<WebshopDraftInvoiceService> log)
    {
        _woo = woo;
        _cm = cm;
        _invoices = invoices;
        _contacts = contacts;
        _fx = fx;
        _options = options;
        _invoicing = invoicing;
        _clock = clock;
        _log = log;
    }

    /// <summary>Builds the reference this service writes and reads for one webshop order.</summary>
    public static string OrderReference(long orderId) => $"{OrderReferencePrefix}{orderId}";

    public async Task<WebshopInvoiceRunResult> RunAsync(CancellationToken ct = default)
    {
        if (!_invoices.CanWrite)
        {
            return WebshopInvoiceRunResult.Inactive(
                "e-conomic is not configured (base URL + both tokens), so no invoice could be "
                + "created. Nothing was attempted.");
        }

        var orders = await _woo.GetOrdersAsync("completed", ct, enrichCategories: false);

        // Legacy orders predate the Company Manager design and carry no company id; the retired
        // script skipped them and so does this. They are not a problem to report — they are simply
        // not in scope, and reporting them every hour would bury the ones that are.
        var inScope = orders.Where(o => !string.IsNullOrWhiteSpace(o.CompanyId)).ToList();

        var alreadyInvoiced = await _invoices.ListInvoicedOrderReferencesAsync(ct);

        var problems = new List<string>();
        var wouldCreate = new List<string>();
        var createdDrafts = new List<CreatedDraftInvoice>();
        var created = 0;
        var skippedAsInvoiced = 0;
        var skipped = 0;

        foreach (var order in inScope)
        {
            ct.ThrowIfCancellationRequested();

            var reference = OrderReference(order.OrderId);

            if (alreadyInvoiced.Contains(reference))
            {
                skippedAsInvoiced++;
                continue;
            }

            try
            {
                var outcome = await InvoiceOneAsync(order, reference, wouldCreate, createdDrafts, ct);
                if (outcome is null) created++;
                else { skipped++; problems.Add(outcome); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad order must never stop the pass — the remaining sponsors are still owed
                // their invoices, and a thrown run would retry the same bad order forever.
                skipped++;
                problems.Add($"Order {order.OrderId}: {ex.Message}");
                _log.LogError(ex, "§786: failed to invoice webshop order {Order}.", order.OrderId);
            }
        }

        if (_invoicing.DryRun)
        {
            _log.LogInformation(
                "§788 DRY RUN — nothing was written to e-conomic. {Seen} order(s) in scope, "
                + "{Already} already invoiced, {Would} WOULD have been created, {Skipped} skipped. "
                + "Set Invoicing:DryRun=false to create them for real.",
                inScope.Count, skippedAsInvoiced, wouldCreate.Count, skipped);
        }
        else
        {
            _log.LogInformation(
                "§786: {Seen} completed order(s) in scope, {Already} already invoiced, {Created} "
                + "draft(s) created, {Skipped} skipped.",
                inScope.Count, skippedAsInvoiced, created, skipped);
        }

        return new WebshopInvoiceRunResult(
            inScope.Count, skippedAsInvoiced,
            // 🔒 Created is FORCED to 0 in a dry run. InvoiceOneAsync returns "success" for a
            // would-create, so the counter would otherwise report invoices that do not exist — the
            // one number an operator would take at face value.
            _invoicing.DryRun ? 0 : created,
            skipped, problems, _invoicing.DryRun, wouldCreate, createdDrafts);
    }

    /// <summary>Invoices one order. Returns null on success, or the human reason it was skipped.</summary>
    private async Task<string?> InvoiceOneAsync(
        WooOrder order, string reference, List<string> wouldCreate,
        List<CreatedDraftInvoice> createdDrafts, CancellationToken ct)
    {
        if (!int.TryParse(order.CompanyId, out var companyId))
        {
            return $"Order {order.OrderId}: company id '{order.CompanyId}' is not a number.";
        }

        var company = await _cm.GetCompanyAsync(companyId, ct);
        if (company is null)
        {
            return $"Order {order.OrderId}: Company Manager company {companyId} could not be read.";
        }

        if (!int.TryParse(company.ErpCustomerNumber, out var erpCustomerNumber) || erpCustomerNumber <= 0)
        {
            // The script reported this too. It is the single most common reason an order does not
            // become an invoice, and it is fixable only in Company Manager — so it is named.
            return $"Order {order.OrderId}: company '{company.Name}' has no erp_customer_number set "
                   + "in Company Manager, so there is no e-conomic customer to invoice.";
        }

        var customer = await _invoices.GetCustomerAsync(erpCustomerNumber, ct);
        if (customer is null)
        {
            return $"Order {order.OrderId}: e-conomic customer {erpCustomerNumber} "
                   + $"(from company '{company.Name}') does not exist.";
        }

        // §1017 — carry the pre/post-coupon line totals and the order's coupon codes through, so a
        // discounted line SAYS it was discounted. ✅ The AMOUNT needs no change: WooCommerce's
        // `price` is already the post-discount unit price (live-verified on order 10841 —
        // subtotal 25000, total 24400, price 24400), so CEH has been billing the right figure all
        // along. What the sponsor never got was the reason for it.
        var lines = order.LineItems
            .Select(li => new WebshopOrderLine(
                li.ProductName, li.Quantity, li.UnitPrice,
                li.LineSubtotal, li.LineTotal, order.CouponCodes))
            .ToList();

        if (lines.Count == 0)
        {
            return $"Order {order.OrderId}: no line items, so there is nothing to invoice.";
        }

        // ⚰️ §807.1 — THE ZERO-PRICE REFUSAL IS GONE, AND IT WAS THE OPERATOR'S CALL TO REMOVE IT.
        //
        // This used to return *"no unit price on line(s) […], so it was NOT invoiced. Invoicing a
        // 0.00 row would reach the customer as a wrong invoice."* Operator 2026-08-04:
        //
        //   *"even though product prices are 0, we need an invoice of 0. this is for documentation
        //    and is relevant to show sponsors if they receive a product as discount"*
        //
        // 🔑 A zero-priced product is a DISCOUNT THAT HAS TO BE DOCUMENTED — the sponsor received
        // something, and the 0.00 invoice is the record that says so. Refusing it left the
        // transaction with no trace anywhere, which is worse than the wrong number the old rule was
        // guarding against: he is the one who fields the question, and he wants the document.
        //
        // ⚠️ What the guard was for, and why it is safe to drop: `WooLineItem.UnitPrice` defaults to
        // 0 for sponsor-task callers that carry no money, so a 0 could mean "not supplied" rather
        // than "free". The ORDER is the evidence the sale happened — a line CEH was handed is a line
        // the webshop recorded — so the ambiguity belongs upstream, not at the invoice.
        //
        // 🔒 A line with a NEGATIVE price is still refused below: that is not a discount, it is a
        // credit note wearing an invoice's clothes, and nothing here knows how to raise one.
        if (lines.Any(l => l.UnitPriceEur < 0m))
        {
            var names = string.Join(", ", lines.Where(l => l.UnitPriceEur < 0m).Select(l => l.ProductName));
            return $"Order {order.OrderId}: NEGATIVE unit price on line(s) [{names}], so it was NOT "
                   + "invoiced. A negative line is a credit note, and CEH does not raise those.";
        }

        var invoiceCurrency = string.IsNullOrWhiteSpace(customer.Currency)
            ? WebshopCurrency
            : customer.Currency.Trim().ToUpperInvariant();

        // ---- the conversion, or a named refusal -------------------------------------------------
        decimal? rate = null;
        if (!string.Equals(invoiceCurrency, WebshopCurrency, StringComparison.Ordinal))
        {
            if (!_fx.CanQuote)
            {
                return $"Order {order.OrderId}: customer '{customer.Name}' invoices in "
                       + $"{invoiceCurrency}, but no exchange-rate source is configured (FxRates), so "
                       + "the amounts cannot be converted. NOT invoiced — CEH will not bill EUR "
                       + "figures under a foreign currency.";
            }

            rate = await _fx.GetRateAsync(WebshopCurrency, invoiceCurrency, ct);
            if (rate is not > 0m)
            {
                return $"Order {order.OrderId}: no usable {WebshopCurrency}->{invoiceCurrency} rate "
                       + "was returned, so it was NOT invoiced.";
            }
        }

        var composed = WebshopInvoiceLineComposer.Compose(
            orderNumber: order.OrderId.ToString(),
            orderDate: order.CreatedAt,
            orderLines: lines,
            vatZoneNumber: customer.VatZoneNumber,
            convert: eur =>
            {
                if (rate is not { } r) return (eur, null);

                // 🔒 Ceiling, as the retired script did. Rounding UP to a whole unit is the
                // operator's existing commercial behaviour; switching to banker's rounding here
                // would quietly change every foreign-currency invoice he issues.
                var converted = Math.Ceiling(eur * r);
                return (converted, WebshopInvoiceLineComposer.ComposeConversionNote(
                    WebshopCurrency, invoiceCurrency, eur, converted));
            });

        var layout = await _invoices.FindLayoutAsync(_options.InvoiceLayoutNameLike, ct);
        if (layout is null)
        {
            return $"Order {order.OrderId}: no e-conomic layout matching "
                   + $"'{_options.InvoiceLayoutNameLike}' was found, so the draft could not be created.";
        }

        // §786.1(a)+(b) — Att person AND Your reference are both the company's DEFAULT SIGNER.
        var signerContact = await ResolveDefaultSignerContactAsync(company, erpCustomerNumber, ct);

        var invoice = new EconomicDraftInvoice(
            Customer: customer,
            Date: DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime),
            LayoutNumber: layout.Value.LayoutNumber,
            LayoutSelf: layout.Value.Self,
            Currency: invoiceCurrency,
            OtherReference: reference,
            VendorEmployeeNumber: _options.InvoiceVendorEmployeeNumber,
            AttentionContactNumber: signerContact,
            YourReferenceContactNumber: signerContact,
            Heading: _options.InvoiceHeading,
            // §811(b) — the script prints "Sponsorship" under the heading; the two together are the
            // phrase he quoted. Sending an empty string here is what produced a bare "Webshop Order".
            // §821 — plus the customer's PURCHASE-ORDER reference from Company Manager, which the
            // retired script read but never put on the invoice. Absent reference ⇒ unchanged text.
            TextLine1: WebshopInvoiceLineComposer.ComposeHeaderTextLine(
                _options.InvoiceTextLine1, company.BillingReference),
            Lines: composed.Select(l => new EconomicInvoiceLine(
                l.LineNumber, l.Description, l.Quantity, l.UnitNetPrice, l.ProductNumber)).ToList(),
            // §811(c) — the second employee reference; both must appear on the invoice.
            SalesPersonEmployeeNumber: _options.InvoiceSalesPersonEmployeeNumber);

        // §788 — DRY RUN stops HERE, at the last possible moment. Everything above has already run
        // for real: the customer was resolved, the signer looked up, the currency converted and the
        // lines composed. That is deliberate — a dry run that skipped the work would only prove the
        // job starts, and every one of those steps is a place this can fail.
        var total = composed.Where(l => l.UnitNetPrice is not null)
            .Sum(l => (l.Quantity ?? 0m) * (l.UnitNetPrice ?? 0m));

        if (_invoicing.DryRun)
        {
            wouldCreate.Add(
                $"{reference} → e-conomic customer {customer.CustomerNumber} '{customer.Name}', "
                + $"{composed.Count - 1} line(s), {total:0.##} {invoiceCurrency}"
                + (signerContact is { } sc ? $", att/your-ref contact {sc}" : ", att/your-ref: customer default"));

            _log.LogInformation("§788 DRY RUN: would create {Reference}.", reference);
            return null;
        }

        var draftNumber = await _invoices.CreateDraftInvoiceAsync(invoice, ct);

        // §795.3 — only after e-conomic accepted it. The info@ notice must never name a draft that
        // was merely composed.
        createdDrafts.Add(new CreatedDraftInvoice(
            Description: $"Webshop order {order.OrderId}",
            CustomerNumber: customer.CustomerNumber,
            CustomerName: customer.Name,
            Reference: reference,
            Total: total,
            Currency: invoiceCurrency,
            DraftNumber: draftNumber));

        return null;
    }

    /// <summary>
    /// §786.1(a)+(b) — resolve the company's DEFAULT SIGNER to an e-conomic customer-contact number.
    /// </summary>
    /// <remarks>
    /// <para>The chain is: Company Manager company → <c>default_signer_id</c> (a CM user) → that
    /// user's e-mail → the e-conomic contact on this customer with the same e-mail → its
    /// <c>customerContactNumber</c>. E-mail is the join because it is the only field both systems
    /// hold for the same person; <c>ErpWebshopContactSyncService</c> already keeps the two sides in
    /// step, which is what makes the match reliable.</para>
    ///
    /// <para>⚠️ Returns null rather than throwing when any hop is missing (no signer set, no e-mail,
    /// no matching contact). The caller then falls back to whatever the CUSTOMER record already
    /// points at — the pre-§786 behaviour. 🔒 That is deliberate: an unset default signer is a data
    /// gap in Company Manager, and refusing to invoice over it would hold up a sponsor's billing for
    /// a field they cannot see. The fallback is logged so the gap is visible.</para>
    /// </remarks>
    private async Task<int?> ResolveDefaultSignerContactAsync(
        CompanyManagerCompany company, int erpCustomerNumber, CancellationToken ct)
    {
        if (company.DefaultSignerUserId <= 0)
        {
            _log.LogInformation(
                "§786.1: company '{Company}' has no default signer set in Company Manager; the "
                + "invoice keeps the e-conomic customer's own attention contact.", company.Name);
            return null;
        }

        var signer = await _cm.GetUserAsync(company.DefaultSignerUserId, ct);
        if (signer is null || string.IsNullOrWhiteSpace(signer.Email))
        {
            _log.LogWarning(
                "§786.1: default signer {UserId} for '{Company}' has no readable e-mail, so it could "
                + "not be matched to an e-conomic contact.", company.DefaultSignerUserId, company.Name);
            return null;
        }

        var contacts = await _contacts.ListContactsAsync(erpCustomerNumber, ct);
        var match = contacts.FirstOrDefault(c =>
            string.Equals(c.Email?.Trim(), signer.Email.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _log.LogWarning(
                "§786.1: default signer '{Email}' for '{Company}' is not a contact on e-conomic "
                + "customer {Customer}, so Att person / Your reference fall back to the customer "
                + "default.", signer.Email, company.Name, erpCustomerNumber);
            return null;
        }

        return match.ContactNumber;
    }
}
