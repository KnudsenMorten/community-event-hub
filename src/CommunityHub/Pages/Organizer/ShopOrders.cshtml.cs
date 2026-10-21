using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1305 items 6 + 12 — every sponsor order from BOTH shops in one list, the organizer's special conditions per CEH
/// order, and the companies registered through the CEH shop.
/// </summary>
/// <remarks>
/// Operator: <i>"Order overview must be redefined so it enums both the old orders from the woocommerce Webshop and the
/// new wehshop in ceh"</i> · <i>"organizers can add special conditions to an order inn the backend, like special
/// terms for payment when invoice should be sent – or other written terms. These information must be stored, so the
/// later ordering takes the billing terms into consideration."</i>
/// </remarks>
[Authorize]
public class ShopOrdersModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopOrderService _orders;
    private readonly ShopSigningService _signing;
    private readonly IWebshopOrderSource _oldOrders;
    private readonly IMemoryCache _cache;
    private readonly ShopCompanyService _companies;
    private readonly ShopMailer _mailer;
    private readonly ShopDraftInvoiceService _invoicer;
    private readonly CommunityHub.Core.Integrations.SponsorSwagCatalogService? _swagCatalog;
    private readonly ShopCompanyDirectoryService? _directory;

    /// <summary>True when this host only reports what it would invoice (the button then says so).</summary>
    public bool InvoiceDryRun => _invoicer.DryRun;

    public ShopOrdersModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopOrderService orders,
        ShopSigningService signing, IWebshopOrderSource oldOrders, IMemoryCache cache,
        ShopCompanyService companies, ShopMailer mailer, ShopDraftInvoiceService invoicer,
        CommunityHub.Core.Integrations.SponsorSwagCatalogService? swagCatalog = null,
        ShopCompanyDirectoryService? directory = null)
    {
        _directory = directory;
        _swagCatalog = swagCatalog;
        _invoicer = invoicer;
        _companies = companies;
        _mailer = mailer;
        _db = db;
        _participant = participant;
        _orders = orders;
        _signing = signing;
        _oldOrders = oldOrders;
        _cache = cache;
    }

    /// <summary>One row of the combined overview.</summary>
    public sealed record Row(
        string Source, string Reference, int? ShopOrderId, string Company, string Status, DateTimeOffset? Date,
        string Items, decimal? TotalEur, bool HasSpecialTerms);

    public bool AccessDenied { get; private set; }
    [BindProperty(SupportsGet = true)] public string? SourceFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int? Order { get; set; }
    [TempData] public string? Message { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public string? WebshopError { get; private set; }
    public ShopOrder? Selected { get; private set; }
    public ShopTotals? SelectedTotals { get; private set; }
    public ShopCompany? SelectedCompany { get; private set; }
    public IReadOnlyList<ShopCompany> Registered { get; private set; } = Array.Empty<ShopCompany>();

    /// <summary>The company's people, for the organizer's signer / coordinator choice on the selected order.</summary>
    public IReadOnlyList<ShopContactOption> SelectedContacts { get; private set; } = Array.Empty<ShopContactOption>();
    /// <summary>§1312 — the company's e-conomic contacts not in the hub yet: offered too, made a contact only when picked.</summary>
    public IReadOnlyList<ShopKnownContact> SelectedKnown { get; private set; } = Array.Empty<ShopKnownContact>();

    /// <summary>Unconfirmed CEH orders — carts with items, reserved, waiting for signature (operator 2026-09-26).</summary>
    public int PendingCount { get; private set; }
    public decimal PendingTotalEur { get; private set; }

    /// <summary>Every company known to the event (sponsors AND candidates), for pre-staging a cart.</summary>
    public IReadOnlyList<(string Id, string Name)> AllCompanies { get; private set; } = Array.Empty<(string, string)>();
    public IReadOnlyList<ShopProduct> PublishedProducts { get; private set; } = Array.Empty<ShopProduct>();

    /// <summary>What a sponsor pays for a swag item (their contribution for every bag).</summary>
    public decimal SwagPrice(ShopProduct p) => _orders.SwagContributionEur(p, 1);

    public static bool IsPending(ShopOrderStatus s) =>
        s is ShopOrderStatus.Cart or ShopOrderStatus.Reserved or ShopOrderStatus.AwaitingSignature;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        var eventId = me.EventId;

        var companies = await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == eventId).ToListAsync(ct);
        var nameById = companies.ToDictionary(c => c.SponsorCompanyId, c => c.OrganizerLabel);
        var infoNames = await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking().Where(s => s.EventId == eventId)
            .Select(s => new { s.SponsorCompanyId, s.CompanyName }).ToListAsync(ct);
        foreach (var i in infoNames.Where(i => i.CompanyName is not null)) nameById.TryAdd(i.SponsorCompanyId, i.CompanyName!);

        var rows = new List<Row>();
        if (SourceFilter is null or "ceh" or "pending")
        {
            var shopOrders = await _db.ShopOrders.AsNoTracking()
                .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.PrimaryCategory)
                .Where(o => o.EventId == eventId && (o.Status != ShopOrderStatus.Cart || o.Lines.Any()))
                .ToListAsync(ct);
            foreach (var o in shopOrders)
            {
                var company = companies.FirstOrDefault(c => c.SponsorCompanyId == o.SponsorCompanyId);
                decimal? total = company is null ? null : (await _orders.PriceAsync(o, company, ct)).TotalEur;
                if (IsPending(o.Status)) { PendingCount++; PendingTotalEur += total ?? 0m; }
                if (SourceFilter == "pending" && !IsPending(o.Status)) continue;
                rows.Add(new Row("CEH", o.Number ?? $"cart #{o.Id}", o.Id, nameById.GetValueOrDefault(o.SponsorCompanyId) ?? o.SponsorCompanyId,
                    o.Status.ToString(), o.SignedAt ?? o.ReservedAt ?? o.UpdatedAt ?? o.CreatedAt,
                    string.Join(", ", o.Lines.Select(l => l.Quantity > 1 ? $"{l.ProductName} × {l.Quantity}" : l.ProductName)),
                    total, !string.IsNullOrWhiteSpace(o.SpecialTerms) || o.InvoiceNotBefore is not null || o.PaymentDays is not null));
            }
        }
        if (SourceFilter is null or "webshop")
        {
            try
            {
                var woo = await _cache.GetOrCreateAsync("woo:completed-orders", async e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                    return await _oldOrders.GetOrdersAsync("completed", ct);   // §1312 — CEH's store of the old webshop's orders
                }) ?? Array.Empty<WooOrder>();
                foreach (var w in woo)
                {
                    var company = w.CompanyId is null ? w.BillingCompany : nameById.GetValueOrDefault(w.CompanyId) ?? w.BillingCompany;
                    rows.Add(new Row("Webshop", $"#{w.OrderId}", null, string.IsNullOrWhiteSpace(company) ? "(no company)" : company,
                        w.Status, w.CreatedAt,
                        string.Join(", ", w.LineItems.Select(l => l.Quantity > 1 ? $"{l.ProductName} × {l.Quantity}" : l.ProductName)),
                        w.LineItems.Sum(l => l.LineTotal), false));
                }
            }
            catch (Exception)
            {
                WebshopError = "The webshop's orders could not be read just now.";
            }
        }
        Rows = rows.OrderByDescending(r => r.Date).ToList();

        // §1312 — a WITHDRAWN company is not listed (operator 2026-09-29: "balanceme is withdawn, so … should not be shown here").
        var withdrawn = (await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EventId == eventId && s.Status == SponsorStatus.Withdrawn)
                .Select(s => s.SponsorCompanyId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        Registered = companies.Where(c => c.Source == ShopCompanySource.Registered && !withdrawn.Contains(c.SponsorCompanyId))
            .OrderByDescending(c => c.CreatedAt).ToList();
        // The pick-list shows the legal (billing) company + country, so "ARROW" x4 can be told apart.
        var labels = companies.ToDictionary(c => c.SponsorCompanyId, c => c.OrganizerLabel);
        // §1312 — nor offered in the pick-list (the duplicate Master Packager #110 is withdrawn, not deleted).
        AllCompanies = nameById.Where(kv => !withdrawn.Contains(kv.Key))
            .Select(kv => (kv.Key, labels.GetValueOrDefault(kv.Key) ?? kv.Value))
            .OrderBy(c => c.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
        PublishedProducts = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .Where(p => p.EventId == eventId && p.Status == ShopProductStatus.Published)
            .OrderBy(p => p.PrimaryCategory!.Group).ThenBy(p => p.Name).ToListAsync(ct);

        if (Order is int id)
        {
            Selected = await _orders.GetAnyAsync(eventId, id, ct);
            if (Selected is not null)
            {
                SelectedCompany = companies.FirstOrDefault(c => c.SponsorCompanyId == Selected.SponsorCompanyId);
                if (SelectedCompany is not null) SelectedTotals = await _orders.PriceAsync(Selected, SelectedCompany, ct);
                SelectedContacts = await _companies.ContactsAsync(eventId, Selected.SponsorCompanyId, ct);
                var hubEmails = SelectedContacts.Select(c => (c.Email ?? "").Trim().ToLowerInvariant()).ToHashSet();
                SelectedKnown = (await _db.ShopKnownContacts.AsNoTracking()
                        .Where(k => k.EventId == eventId && k.SponsorCompanyId == Selected.SponsorCompanyId).ToListAsync(ct))
                    .Where(k => !hubEmails.Contains(k.Email.Trim().ToLowerInvariant()))
                    .OrderByDescending(k => k.IsSigner).ThenBy(k => k.FullName).ToList();
            }
        }
    }

    [Audit("Set special conditions on a sponsor shop order", Category = AuditCategory.Admin,
        Action = "webshop.order-special-terms", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostTermsAsync(int orderId, string? terms, DateOnly? invoiceNotBefore, int? paymentDays, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        await _orders.SetSpecialTermsAsync(order, terms, invoiceNotBefore, paymentDays, ct);
        Message = $"Special conditions saved on {order.Number ?? "the cart"}. They print as \"Agreed Terms\" on the contract.";
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    [Audit("Cancelled a sponsor shop order", Category = AuditCategory.Admin, Action = "webshop.order-cancel-organizer", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostCancelAsync(int orderId, string? reason, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        var r = await _orders.CancelAsync(order, me.Email, reason, ct, byOrganizers: true);
        Message = r.Message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>
    /// Pre-stage a cart for a sponsor OR a candidate, straight from here — no act-as needed (operator
    /// 2026-09-26: "ability to prestage products into cart for potential sponsor"). The cart is the company's,
    /// so their signer and coordinator see it and get the daily reminder.
    /// </summary>
    [Audit("Pre-staged a product in a sponsor's cart", Category = AuditCategory.Admin, Action = "webshop.cart-prestage", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostPrestageAsync(string companyId, int productId, int quantity, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (string.IsNullOrWhiteSpace(companyId)) { Message = "Choose a company."; return RedirectToPage(new { SourceFilter }); }
        var company = await _companies.GetOrCreateAsync(me.EventId, companyId, ct);
        if (company is null) { Message = "That company is not known to the shop."; return RedirectToPage(new { SourceFilter }); }
        // A swag item is pre-staged the way the configurator adds it: for every attendee bag, once.
        var product = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .FirstOrDefaultAsync(p => p.Id == productId && p.EventId == me.EventId, ct);
        ShopResult r;
        if (product?.PrimaryCategory?.Group == ShopGroup.Swag && product.WooProductId is long wid && _swagCatalog is not null)
        {
            var bags = (await _swagCatalog.GetAsync(ct)).BagQuantity;
            r = await _orders.AddSwagAsync(me.EventId, companyId, wid, bags, me.ParticipantId, ct);
        }
        else
        {
            r = await _orders.AddAsync(me.EventId, companyId, productId, Math.Max(1, quantity), me.ParticipantId, ct, ShopAddMode.Organizer);
        }
        var cart = await _orders.CartAsync(me.EventId, companyId, ct);
        Message = r.Ok ? $"Added to {company.PublicName ?? company.LegalName}'s cart." : r.Message;
        return RedirectToPage(new { Order = cart?.Id, SourceFilter });
    }

    [Audit("Removed a line from a sponsor's cart", Category = AuditCategory.Admin, Action = "webshop.cart-line-remove", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostRemoveLineAsync(int orderId, int lineId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        var r = order.Status == ShopOrderStatus.Cart
            ? await _orders.SetQuantityAsync(me.EventId, order.SponsorCompanyId, lineId, 0, ct)
            : ShopResult.Fail("Only a cart can be changed; cancel a reserved order instead.");
        Message = r.Ok ? "Removed from the cart." : r.Message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>§1312 — "Manual Confirm Order": the sponsor signed offline; confirm it (optionally with their signed PDF).</summary>
    [Audit("Confirmed a sponsor order signed offline", Category = AuditCategory.Admin, Action = "webshop.order-confirm-offline", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostConfirmOfflineAsync(int orderId, IFormFile? signedPdf, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        var (bytes, error) = await CommunityHub.Pages.Sponsor.Shop.CartModel.ReadSignedPdfAsync(signedPdf, ct);
        var r = error is not null ? ShopResult.Fail(error)
            : await _signing.ConfirmOfflineAsync(order, me.FullName, bytes, signedPdf?.FileName, ct);
        Message = r.Message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>
    /// §1312 — a price agreed with this company for one line (operator 2026-09-27: "give red hat a discount … normal price
    /// 8000 euro and then a new price of 5000 euro and show the discount"). Empty = back to the normal price.
    /// </summary>
    [Audit("Set a company-specific price on a sponsor shop order line", Category = AuditCategory.Admin, Action = "webshop.order-agreed-price", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostAgreedPriceAsync(int orderId, int lineId, decimal? price, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var r = await _orders.SetAgreedPriceAsync(me.EventId, orderId, lineId, price, ct);
        Message = r.Message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>The organizer names the signer and coordinator (from the company's own people) and the PO.</summary>
    [Audit("Set the signer and coordinator of a sponsor shop order", Category = AuditCategory.Admin, Action = "webshop.order-people", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostPeopleAsync(int orderId, string? signerId, string? coordinatorId, string? purchaseOrderRef, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        // §1312 — "k:{id}" is an e-conomic contact not in the hub yet: made a contact of the company now (no mail).
        async Task<int?> Resolve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (int.TryParse(value, out var pid)) return pid;
            if (value.StartsWith("k:", StringComparison.Ordinal) && int.TryParse(value[2..], out var kid) && _directory is not null)
                return await _directory.KnownToContactAsync(me.EventId, order.SponsorCompanyId, kid, ct);
            return null;
        }
        var r = await _orders.UpdateDetailsAsync(order, purchaseOrderRef, await Resolve(signerId), await Resolve(coordinatorId), ct);
        Message = r.Ok ? "Signer, coordinator and PO saved." : r.Message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>
    /// Mail a potential sponsor an OFFER: this order's products and prices with the draft contract attached.
    /// Nothing is reserved, and the address need not belong to anyone in the hub.
    /// </summary>
    [Audit("Sent a sponsor shop offer", Category = AuditCategory.Admin, Action = "webshop.order-offer", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostOfferAsync(int orderId, string? toEmail, string? note, CancellationToken ct, string? toName = null)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        if (order.Lines.Count == 0) { Message = "The order has no products to offer."; return RedirectToPage(new { Order = orderId, SourceFilter }); }
        if (!System.Net.Mail.MailAddress.TryCreate(toEmail?.Trim(), out var address))
        {
            Message = "Enter a valid e-mail address for the offer.";
            return RedirectToPage(new { Order = orderId, SourceFilter });
        }
        var company = await _companies.GetOrCreateAsync(me.EventId, order.SponsorCompanyId, ct);
        if (company is null) return NotFound();
        var totals = await _orders.PriceAsync(order, company, ct);
        var pdf = await _signing.RenderAsync(order, ct);
        await _mailer.SendOfferAsync(order, totals, address.Address, note, pdf, ct, toName);
        // §1312 — the offer tells them to register and complete the order themselves: make that address a known person of
        // this company, so registering joins it straight away (offer in the cart) instead of waiting for a join approval.
        if (_directory is not null) await _directory.RememberOfferRecipientAsync(me.EventId, order.SponsorCompanyId, address.Address, ct);
        Message = $"Offer with the draft contract sent to {address.Address}. Nothing is reserved. They can register and complete the order themselves.";
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>Create the e-conomic draft invoice for one signed order now, instead of waiting for the hourly job.</summary>
    [Audit("Created the e-conomic invoice for a sponsor shop order", Category = AuditCategory.Admin, Action = "webshop.order-invoice", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostInvoiceAsync(int orderId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        var (_, message) = await _invoicer.InvoiceNowAsync(order, ct);
        Message = message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    /// <summary>Reserve (if still a cart) and mail the signer the contract with the signing link.</summary>
    [Audit("Sent a sponsor contract for signature", Category = AuditCategory.Admin, Action = "webshop.order-send-organizer", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostSendAsync(int orderId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        var r = await _signing.SendForSignatureAsync(order, ct);
        Message = string.IsNullOrWhiteSpace(r.Message) ? (r.Ok ? "Sent to the signer." : "That did not work.") : r.Message;
        return RedirectToPage(new { Order = orderId, SourceFilter });
    }

    public async Task<IActionResult> OnGetPdfAsync(int orderId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null) return NotFound();
        return File(await _signing.RenderAsync(order, ct), "application/pdf", $"Event-Sponsor-Contract-{order.Number ?? "draft-" + order.Id}.pdf");
    }
}
