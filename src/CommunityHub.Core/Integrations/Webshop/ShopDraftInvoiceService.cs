using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>What one invoicing run did.</summary>
public sealed record ShopInvoiceRunResult(int Due, int Created, int WouldCreate, IReadOnlyList<string> Problems, bool DryRun, string? Inactive = null);

/// <summary>
/// §1305 — signed CEH shop orders → e-conomic DRAFT invoices, honouring the organizer's special conditions.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: special conditions <i>"must be stored, so the later ordering takes the billing terms into
/// consideration"</i>. §1312 — the invoice is created at once, DATED <see cref="ShopOrder.InvoiceNotBefore"/> when agreed; agreed
/// <see cref="ShopOrder.PaymentDays"/> are printed on the invoice AND sent as its e-conomic payment term.</para>
///
/// <para>🔑 <b>Built from the same parts as the webshop invoicer</b> (<see cref="WebshopDraftInvoiceService"/>): the
/// line composer (VAT by the customer's e-conomic zone), the currency rule (EUR prices converted and rounded UP to a
/// whole unit, with the conversion printed), the layout, heading, employee references and the default signer as
/// Att / Your reference. So a CEH invoice reads like a webshop one.</para>
///
/// <para>🔑 <b>Every line matches the contract</b> (§1312, operator 2026-09-29 — a bookkeeper will not approve a discount
/// folded into "any product"): products at their contract line price, and each contract discount — the included booth
/// furniture (named by the webshop coupon code) and a swag credit — as its OWN line, 1 × −amount. The invoice totals
/// exactly what the contract totals. (It used to allocate the discounts into the product lines.)</para>
///
/// <para>🔒 Idempotent by its own reference, <c>CehShopOrder-{number}</c>, distinct from the webshop's
/// <c>WebshopOrderId-{id}</c>. 🔒 Creating an invoice is PROD-only (<c>ExternalWrites:ErpInvoiceCreate</c>) and
/// <c>Invoicing:DryRun</c> stops just before the write — on DEV this reports what it WOULD create.</para>
/// </remarks>
public sealed class ShopDraftInvoiceService
{
    public const string ReferencePrefix = "CehShopOrder-";

    private readonly CommunityHubDbContext _db;
    private readonly ShopOrderService _orders;
    private readonly IEconomicInvoiceClient _invoices;
    private readonly IEconomicContactAdminClient _contacts;
    private readonly IFxRateProvider _fx;
    private readonly EconomicErpOptions _options;
    private readonly InvoicingOptions _invoicing;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopDraftInvoiceService>? _log;
    private readonly Config.ShopConfigOptions? _shopOptions;
    private readonly ShopMailer? _mail;

    /// <summary><c>Shop:InvoiceDryRun</c> when set on this host, else <c>Invoicing:DryRun</c>.</summary>
    public bool DryRun => _shopOptions?.InvoiceDryRun ?? _invoicing.DryRun;

    public ShopDraftInvoiceService(
        CommunityHubDbContext db, ShopOrderService orders, IEconomicInvoiceClient invoices, IEconomicContactAdminClient contacts,
        IFxRateProvider fx, EconomicErpOptions options, InvoicingOptions invoicing, TimeProvider clock,
        ILogger<ShopDraftInvoiceService>? log = null, Config.ShopConfigOptions? shopOptions = null, ShopMailer? mail = null)
    {
        _shopOptions = shopOptions;
        _mail = mail;
        _db = db;
        _orders = orders;
        _invoices = invoices;
        _contacts = contacts;
        _fx = fx;
        _options = options;
        _invoicing = invoicing;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// The organizer's "Create the e-conomic draft invoice now" for ONE signed order — the same path as the hourly
    /// job, including the never-twice check. Operator 2026-09-26: <i>"you can create invoice from dev also so we can
    /// verify it works"</i>.
    /// </summary>
    public async Task<(bool Ok, string Message)> InvoiceNowAsync(ShopOrder order, CancellationToken ct = default)
    {
        if (order.Status != ShopOrderStatus.Signed) return (false, "Only a signed order can be invoiced.");
        if (!_invoices.CanWrite) return (false, "e-conomic is not configured on this host.");

        // §1312 — e-conomic decides, not the local stamp: a draft deleted there can be created again from here.
        var reference = Reference(order);
        var existing = await _invoices.ListInvoicedOrderReferencesAsync(ct);
        if (order.InvoicedAt is not null && (existing.Count == 0 || existing.Contains(reference)))
            return (false, $"Already invoiced: draft {order.ErpDraftInvoiceNumber} ({order.ErpInvoiceReference}).");
        if (existing.Contains(reference))
        {
            order.InvoicedAt = _clock.GetUtcNow();
            order.ErpInvoiceReference = reference;
            order.InvoiceProblem = null;
            await _db.SaveChangesAsync(ct);
            return (true, $"{reference} already exists in e-conomic — recorded, not created twice.");
        }
        try
        {
            var (problem, draft, dry) = await InvoiceOneAsync(order, reference, ct);
            order.InvoiceProblem = problem ?? dry;
            if (problem is null && dry is null)
            {
                order.ErpInvoiceReference = reference;
                order.ErpDraftInvoiceNumber = draft;
                order.InvoicedAt = _clock.GetUtcNow();
            }
            await _db.SaveChangesAsync(ct);
            if (problem is null && dry is null) await NotifyCreatedAsync(order, draft, ct);
            return problem is not null ? (false, problem)
                : dry is not null ? (true, dry)
                : (true, $"Draft invoice {draft} created in e-conomic ({reference}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            order.InvoiceProblem = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
            await _db.SaveChangesAsync(ct);
            _log?.LogError(ex, "§1305 invoicing of order {Number} failed.", order.Number);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 🔴 §1312 — the organizers are TOLD when a shop draft invoice is created (operator 2026-09-30: "i dont think i received
    /// email when invoice was created … we must be CC on info@expertslive.dk + mok@expertslive.dk"). This invoicer was the
    /// only one that told nobody — the webshop and coupon invoicers already announce their drafts. Fail-soft: a mail problem
    /// never undoes or blocks an invoice.
    /// </summary>
    private async Task NotifyCreatedAsync(ShopOrder order, int? draft, CancellationToken ct)
    {
        if (_mail is null) return;
        try
        {
            var company = await _db.ShopCompanies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
            string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            await _mail.NotifyOrganizersAsync(order.EventId,
                $"Draft invoice {draft} created: {company?.LegalName ?? order.SponsorCompanyId} ({order.Number})",
                $"<p>A draft invoice was created in e-conomic for the signed order <b>{E(order.Number)}</b>.</p>"
                + $"<ul><li>Company: <b>{E(company?.LegalName)}</b> (e-conomic customer {E(company?.ErpCustomerNumber)})</li>"
                + $"<li>Draft invoice: <b>{draft}</b></li><li>Reference (Øvrig ref.): <b>{E(Reference(order))}</b></li></ul>"
                + "<p>Review and book it in e-conomic. If it is wrong, delete the draft — a corrected one is created on the next run.</p>", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1312 the draft-invoice notice for order {Number} could not be sent.", order.Number);
        }
    }

    /// <summary>
    /// §1312 — an order that CANNOT be invoiced is told to the organizers, with e-conomic's reason — once per new problem, not
    /// every run (an unbilled signed order is money not asked for; it used to live only in the log).
    /// </summary>
    private async Task NotifyProblemAsync(ShopOrder order, string? problem, CancellationToken ct)
    {
        if (_mail is null || string.IsNullOrWhiteSpace(problem)) return;
        try
        {
            string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            await _mail.NotifyOrganizersAsync(order.EventId,
                $"Invoice NOT created: order {order.Number}",
                $"<p>The signed order <b>{E(order.Number)}</b> could not be invoiced in e-conomic:</p><p><b>{E(problem)}</b></p>"
                + "<p>It is tried again on every run; fix the cause above (or create the invoice from the orders page).</p>", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1312 the invoice-problem notice for order {Number} could not be sent.", order.Number);
        }
    }

    public static string Reference(ShopOrder o) => ReferencePrefix + (o.Number ?? o.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task<ShopInvoiceRunResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        if (!_invoices.CanWrite)
            return new(0, 0, 0, Array.Empty<string>(), DryRun, "e-conomic is not configured, so nothing was attempted.");

        // 🔴 §1312 — E-CONOMIC IS THE ONLY TRUTH ABOUT "INVOICED". Operator 2026-09-29: "earlier it was much better, as i
        // just looked in all invoices booked for the code and if none found, it would just add it again" · "i would assume
        // deleting a draft invoice would be better" · "this happens weekly and not ok" · "i loose money with this".
        // The local stamp (InvoicedAt) used to GATE the run, so a draft deleted in e-conomic was never rebuilt until somebody
        // cleared the stamp in the database. Now every signed order is checked against e-conomic's drafts AND booked
        // invoices on every run: its reference present ⇒ invoiced (stamped); absent ⇒ invoiced again — exactly the webshop
        // invoicer's rule. The stamp is only a record for the organizer pages.
        // §1312 — an agreed invoice date no longer holds the order back: it is invoiced now, DATED that day (operator
        // 2026-09-30: "i prefer to send the invoice now but just change the invoice date to what is agreed upon and terms").
        var signed = await _db.ShopOrders
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.PrimaryCategory)
            .Where(o => o.EventId == eventId && o.Status == ShopOrderStatus.Signed)
            .ToListAsync(ct);
        if (signed.Count == 0) return new(0, 0, 0, Array.Empty<string>(), DryRun);

        var already = await _invoices.ListInvoicedOrderReferencesAsync(ct);
        // 🔒 An EMPTY scan is not evidence that invoices were deleted — the agreement always holds invoices, so an empty list
        // is a read that failed quietly. Then a stamped order is left alone (never re-invoiced on an unread answer); only an
        // order that was never invoiced is attempted, as before.
        var scanTrusted = already.Count > 0;
        var due = signed.Where(o => o.InvoicedAt is null || (scanTrusted && !already.Contains(Reference(o)))).ToList();
        foreach (var o in signed.Where(o => o.InvoicedAt is null && already.Contains(Reference(o))))
        {
            // Created earlier (by a run whose save was lost, or by hand): record it, never invoice twice.
            o.InvoicedAt = _clock.GetUtcNow();
            o.ErpInvoiceReference = Reference(o);
            o.InvoiceProblem = null;
        }
        if (due.Count == 0) { await _db.SaveChangesAsync(ct); return new(0, 0, 0, Array.Empty<string>(), DryRun); }

        var problems = new List<string>();
        int created = 0, would = 0;

        foreach (var order in due)
        {
            var reference = Reference(order);
            if (already.Contains(reference))
            {
                order.InvoicedAt ??= _clock.GetUtcNow();
                order.ErpInvoiceReference = reference;
                order.InvoiceProblem = null;
                continue;
            }
            if (order.InvoicedAt is not null)
                _log?.LogWarning("§1312 order {Number}: draft {Draft} ({Reference}) is no longer in e-conomic — invoicing it again.",
                    order.Number, order.ErpDraftInvoiceNumber, reference);
            var previousProblem = order.InvoiceProblem;
            try
            {
                var (problem, draft, dry) = await InvoiceOneAsync(order, reference, ct);
                order.InvoiceProblem = problem ?? dry;
                if (problem is not null)
                {
                    problems.Add($"{order.Number}: {problem}");
                    if (problem != previousProblem) await NotifyProblemAsync(order, problem, ct);
                    continue;
                }
                if (dry is not null) { would++; continue; }
                order.ErpInvoiceReference = reference;
                order.ErpDraftInvoiceNumber = draft;
                order.InvoicedAt = _clock.GetUtcNow();
                order.InvoiceProblem = null;
                created++;
                await NotifyCreatedAsync(order, draft, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                order.InvoiceProblem = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                problems.Add($"{order.Number}: {ex.Message}");
                _log?.LogError(ex, "§1305 invoicing of order {Number} failed.", order.Number);
                if (order.InvoiceProblem != previousProblem) await NotifyProblemAsync(order, order.InvoiceProblem, ct);
            }
        }
        await _db.SaveChangesAsync(ct);
        _log?.LogInformation("§1305 shop invoicing (event {EventId}): {Due} due, {Created} created, {Would} would-create (dry run), {Problems} problem(s).",
            eventId, due.Count, created, would, problems.Count);
        return new(due.Count, created, would, problems, DryRun);
    }

    /// <summary>(problem, draft number, dry-run note) — exactly one of problem / draft / dry-run note is set.</summary>
    private async Task<(string? Problem, int? Draft, string? DryRun)> InvoiceOneAsync(ShopOrder order, string reference, CancellationToken ct)
    {
        var company = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        if (company is null) return ("the company has no shop record.", null, null);
        if (!int.TryParse(company.ErpCustomerNumber, out var customerNumber) || customerNumber <= 0)
            return ($"'{company.LegalName}' has no e-conomic customer number"
                    + (company.ErpError is null ? "." : $" (registration: {company.ErpError})."), null, null);

        var customer = await _invoices.GetCustomerAsync(customerNumber, ct);
        if (customer is null) return ($"e-conomic customer {customerNumber} does not exist.", null, null);

        var totals = await _orders.PriceAsync(order, company, ct);
        if (totals.Lines.Count == 0) return ("the order has no lines.", null, null);

        // 🔴 §1312 — EVERY LINE MATCHES THE CONTRACT. Operator 2026-09-29: "it doesn't match the contract. i prefer to have a
        // separate line with the coupon event discount as separate line, so each amount matches … a bookkeeper will not
        // approve the current method where you take any product and make discount" · "just make a line".
        // Products are billed at their CONTRACT line price (never reduced), and each contract discount — the included booth
        // furniture (named by the webshop coupon code) and a swag credit — is ONE negative line of its own, exactly as the
        // contract's totals show it. The invoice total is unchanged: it still equals the contract total.
        var lines = totals.Lines.Select(l =>
            new WebshopOrderLine(l.Name, l.Quantity, Math.Round(l.LineEur / Math.Max(1, l.Quantity), 2, MidpointRounding.AwayFromZero)))
            .ToList();
        if (totals.IncludedFurnitureEur > 0)
        {
            var coupon = await _orders.FurnitureCouponCodeAsync(order, ct);
            lines.Add(new WebshopOrderLine(
                "Included booth furniture" + (coupon is null ? "" : $" — coupon {coupon}"), 1, -totals.IncludedFurnitureEur));
        }
        if (totals.SwagCreditEur > 0)
            lines.Add(new WebshopOrderLine("Swag credit", 1, -totals.SwagCreditEur));

        var invoiceCurrency = string.IsNullOrWhiteSpace(customer.Currency) ? "EUR" : customer.Currency.Trim().ToUpperInvariant();
        decimal? rate = null;
        if (invoiceCurrency != "EUR")
        {
            if (!_fx.CanQuote) return ($"the customer invoices in {invoiceCurrency} and no exchange-rate source is configured.", null, null);
            rate = await _fx.GetRateAsync("EUR", invoiceCurrency, ct);
            if (rate is not > 0m) return ($"no usable EUR→{invoiceCurrency} rate was returned.", null, null);
        }

        var composed = WebshopInvoiceLineComposer.Compose(
            orderNumber: order.Number ?? order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            orderDate: order.SignedAt,
            orderLines: lines,
            vatZoneNumber: customer.VatZoneNumber,
            convert: eur =>
            {
                if (rate is not { } r) return (eur, null);
                var converted = Math.Ceiling(eur * r);
                return (converted, WebshopInvoiceLineComposer.ComposeConversionNote("EUR", invoiceCurrency, eur, converted));
            });

        var layout = await _invoices.FindLayoutAsync(_options.InvoiceLayoutNameLike, ct);
        if (layout is null) return ($"no e-conomic layout matching '{_options.InvoiceLayoutNameLike}'.", null, null);

        var signer = await SignerContactAsync(order, customerNumber, ct);
        var text = WebshopInvoiceLineComposer.ComposeHeaderTextLine(_options.InvoiceTextLine1, order.PurchaseOrderRef);
        if (order.PaymentDays is int days) text += $" · Payment: {days} days net (agreed)";

        // 🔴 §1312 — the invoice says what the signed contract says: dated the agreed invoice date, with the agreed payment
        // days as its e-conomic payment term (operator 2026-09-30: "erp invoicing must match this"). No agreed days ⇒ the
        // customer's own terms, as for every other invoice (§811a).
        var date = order.InvoiceNotBefore ?? DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        EconomicPaymentTermsChoice? terms = null;
        if (order.PaymentDays is int agreed)
        {
            terms = PickPaymentTerm(await _invoices.ListPaymentTermsAsync(ct), agreed, date);
            if (terms is null)
                return ($"the agreed {agreed} payment days match no e-conomic payment term (net {agreed} days or a due-date term) — add one in e-conomic.", null, null);
        }

        var invoice = new EconomicDraftInvoice(
            Customer: customer,
            Date: date,
            LayoutNumber: layout.Value.LayoutNumber,
            LayoutSelf: layout.Value.Self,
            Currency: invoiceCurrency,
            OtherReference: reference,
            VendorEmployeeNumber: _options.InvoiceVendorEmployeeNumber,
            AttentionContactNumber: signer,
            YourReferenceContactNumber: signer,
            Heading: _options.InvoiceHeading,
            TextLine1: text,
            Lines: composed.Select(l => new EconomicInvoiceLine(l.LineNumber, l.Description, l.Quantity, l.UnitNetPrice, l.ProductNumber)).ToList(),
            SalesPersonEmployeeNumber: _options.InvoiceSalesPersonEmployeeNumber,
            // Asks the shop's own invoice ceiling, never the §1119 one that guards webshop/coupon invoices.
            CehShopOrder: true,
            PaymentTerms: terms);

        var total = composed.Where(l => l.UnitNetPrice is not null).Sum(l => (l.Quantity ?? 0m) * (l.UnitNetPrice ?? 0m));
        if (DryRun)
            return (null, null, $"DRY RUN — would create {reference} for e-conomic customer {customer.CustomerNumber}, "
                                + $"{composed.Count - 1} line(s), {total.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {invoiceCurrency}.");

        var draft = await _invoices.CreateDraftInvoiceAsync(invoice, ct);
        return (null, draft, null);
    }

    /// <summary>
    /// §1312 — the e-conomic term for <paramref name="days"/> agreed days: a "net" term with exactly that credit (Netto 8/14/30…),
    /// else the due-date term ("Forfaldsdato") carrying invoice date + days. Null when the agreement has neither.
    /// </summary>
    internal static EconomicPaymentTermsChoice? PickPaymentTerm(IReadOnlyList<EconomicPaymentTerm> terms, int days, DateOnly invoiceDate)
    {
        var net = terms.FirstOrDefault(t => string.Equals(t.Type, "net", StringComparison.OrdinalIgnoreCase) && t.DaysOfCredit == days);
        if (net is not null) return new(net.Number, net.Self);
        var due = terms.FirstOrDefault(t => string.Equals(t.Type, "dueDate", StringComparison.OrdinalIgnoreCase));
        return due is null ? null : new(due.Number, due.Self, invoiceDate.AddDays(days));
    }

    /// <summary>The signer on the order as an e-conomic contact number (Att / Your reference), or null.</summary>
    private async Task<int?> SignerContactAsync(ShopOrder order, int customerNumber, CancellationToken ct)
    {
        var signerId = order.SignedByParticipantId ?? order.SignerParticipantId;
        if (signerId is not int id) return null;
        var link = await _db.SponsorCompanyContacts.AsNoTracking()
            .FirstOrDefaultAsync(l => l.EventId == order.EventId && l.SponsorCompanyId == order.SponsorCompanyId && l.ParticipantId == id, ct);
        if (link?.ErpContactNumber is int n) return n;
        var email = await _db.Participants.AsNoTracking().Where(p => p.Id == id).Select(p => p.Email).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email)) return null;
        var contacts = await _contacts.ListContactsAsync(customerNumber, ct);
        return contacts.FirstOrDefault(c => string.Equals(c.Email?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))?.ContactNumber;
    }
}
