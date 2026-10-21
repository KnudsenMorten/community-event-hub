using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CommunityHub.Pages.Sponsor.Shop;

/// <summary>
/// §1305 items 3, 4, 7 — the company view and the cart: lines, totals (VAT, late surcharge, included
/// furniture), the order's PO / signer / event coordinator, the company's web + social details, and the two
/// contract buttons.
/// </summary>
/// <remarks>
/// Operator: <i>"both a event coordinator and a signer can login and see what is in the cart for them and
/// modify. It should be easy to modify the values like purchase order, signer/event coordinator (dropdown) …
/// change purchase order id, linkedin, twitter"</i> · <i>"2 buttons 'Sign Contract' and 'Build Contract
/// (offline PDF)'"</i>.
/// </remarks>
[Authorize]
public class CartModel : ShopPageModel
{
    private readonly ShopOrderService _orders;
    private readonly ShopSigningService _signing;
    private readonly ShopSettings _settings;
    private readonly ShopReservationService? _reservations;

    public CartModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, ShopOrderService orders, ShopSigningService signing, ShopSettings settings,
        ShopReservationService? reservations = null)
        : base(participant, gate, acting, companies)
    {
        _reservations = reservations;
        _orders = orders;
        _signing = signing;
        _settings = settings;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    public ShopOrder? Cart { get; private set; }
    public ShopTotals? Totals { get; private set; }
    public IReadOnlyList<ShopContactOption> Contacts { get; private set; } = Array.Empty<ShopContactOption>();
    public IReadOnlyList<(ShopOrder Order, ShopTotals Totals)> OpenOrders { get; private set; } = Array.Empty<(ShopOrder, ShopTotals)>();
    public bool ContractConfigured { get; private set; }

    /// <summary>
    /// §1305 — may the signed-in person sign this cart's contract? Only the order's signer (or, before one is chosen, a
    /// person holding the company's signer role) — and never while acting as someone. Operator 2026-09-26: "if the
    /// person logged in doesnot hold the signer role, then hide the Sign Contract button".
    /// </summary>
    public bool ViewerMaySign { get; private set; }

    /// <summary>Cart line id → the swag split sentence (sponsor share, full price for every bag, ELDK's part).</summary>
    public Dictionary<int, string> SwagNotes { get; } = new();

    /// <summary>The same rule for any order on the page — the cart or one waiting for signature.</summary>
    public bool MaySignOrder(ShopOrder order)
    {
        var me = Participant.Current;
        return me is { IsActingAs: false }
            && (order.SignerParticipantId is int signer
                ? signer == me.ParticipantId
                : Contacts.Any(c => c.ParticipantId == me.ParticipantId && c.IsSigner));
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;
        ContractConfigured = _settings.IsConfigured;
        Contacts = await Companies.ContactsAsync(EventId, CompanyId!, ct);
        Cart = await _orders.CartAsync(EventId, CompanyId!, ct);
        ViewerMaySign = Cart is null || MaySignOrder(Cart);
        // The swag split under each swag line (operator 2026-09-26: "show the split in payment").
        if (Cart is not null && _settings.IsConfigured)
        {
            var rule = _settings.Shop().SwagPricing;
            foreach (var l in Cart.Lines.Where(l => l.Product?.PrimaryCategory?.Group == ShopGroup.Swag))
                SwagNotes[l.Id] = ShopPricing.SwagSplitText(ShopPricing.SwagSplitFor(l.Product!, rule, 1));
        }
        if (Cart is not null) Totals = await _orders.PriceAsync(Cart, Company!, ct);

        var open = new List<(ShopOrder, ShopTotals)>();
        foreach (var o in (await _orders.OrdersAsync(EventId, CompanyId!, ct))
                 .Where(o => o.Status is ShopOrderStatus.Reserved or ShopOrderStatus.AwaitingSignature))
            open.Add((o, await _orders.PriceAsync(o, Company!, ct)));
        OpenOrders = open;
    }

    public async Task<IActionResult> OnPostQuantityAsync(int lineId, int quantity, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        return Done(await _orders.SetQuantityAsync(EventId, CompanyId!, lineId, quantity, ct));
    }

    public async Task<IActionResult> OnPostRemoveAsync(int lineId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        return Done(await _orders.SetQuantityAsync(EventId, CompanyId!, lineId, 0, ct));
    }

    [Audit("Emptied the sponsor shop cart", Action = "webshop.cart-empty", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostEmptyAsync(CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var cart = await _orders.CartAsync(EventId, CompanyId!, ct);
        if (cart is not null)
        {
            foreach (var l in cart.Lines.ToList()) await _orders.SetQuantityAsync(EventId, CompanyId!, l.Id, 0, ct);
        }
        return Done(ShopResult.Success("The cart is empty. The daily reminder stops."));
    }

    public async Task<IActionResult> OnPostDetailsAsync(
        int orderId, string? purchaseOrderRef, int? signerId, int? coordinatorId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null) return Done(ShopResult.Fail("That order no longer exists."));
        return Done(await _orders.UpdateDetailsAsync(order, purchaseOrderRef, signerId, coordinatorId, ct));
    }

    public async Task<IActionResult> OnPostProfileAsync(
        string? publicName, string? website, string? linkedIn, string? twitter, string? invoiceEmail, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        await Companies.UpdateProfileAsync(EventId, CompanyId!, publicName, website, linkedIn, twitter, invoiceEmail, ct);
        return Done(ShopResult.Success("Company details saved."));
    }

    [Audit("Reserved a sponsor shop order", Action = "webshop.order-reserve", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostReserveAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null) return Done(ShopResult.Fail("That order no longer exists."));
        return Done(await _orders.ReserveAsync(order, ct));
    }

    /// <summary>§1312 — ask the organizers to reserve the cart; they accept (with deadline and reminders) or deny.</summary>
    [Audit("Requested a reservation", Action = "webshop.reservation-request", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostRequestAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        if (_reservations is null) return Done(ShopResult.Fail("Reservation requests are not available right now."));
        if (Participant.Current!.IsActingAs) return Done(ShopResult.Fail("A reservation is requested by the sponsor's own contact, not while acting as them."));
        return Done(await _reservations.RequestReservationAsync(EventId, CompanyId!, orderId, ParticipantId, ct));
    }

    /// <summary>
    /// "Digitally Sign Contract Now": open the contract to sign. §1312 — NOTHING is reserved or taken here (operator 2026-09-27:
    /// "i clicked digitally sign … i had NOT signed yet, and then i already received this [sold-out] email. order has not made
    /// until ticked off and signed"): signing reserves and takes the stock in one step.
    /// </summary>
    public async Task<IActionResult> OnPostSignAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null) return Done(ShopResult.Fail("That order no longer exists."));
        if (order.Lines.Count == 0) return Done(ShopResult.Fail("The cart is empty."));
        return RedirectToPage("/Sponsor/Shop/Contract", pageHandler: null, routeValues: new { id = order.Id }, fragment: "sign-panel");
    }

    [Audit("Sent a sponsor contract for signature", Action = "webshop.order-send-to-signer", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostSendAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null) return Done(ShopResult.Fail("That order no longer exists."));
        return Done(await _signing.SendForSignatureAsync(order, ct));
    }

    /// <summary>§1308 — move the cart into a reserved order (one contract), taking the stock for the added items.</summary>
    public async Task<IActionResult> OnPostMergeAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null) return Done(ShopResult.Fail("That order no longer exists."));
        return Done(await _orders.MergeCartIntoAsync(order, ct));
    }

    /// <summary>"Build Contract (offline PDF)": the contract as it stands, unsigned.</summary>
    public async Task<IActionResult> OnPostPdfAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null || !_settings.IsConfigured) return Done(ShopResult.Fail("The contract cannot be built right now."));
        var pdf = await _signing.RenderAsync(order, ct);
        return File(pdf, "application/pdf", $"Event-Sponsor-Contract-{order.Number ?? "draft-" + order.Id}.pdf");
    }

    [Audit("Cancelled a sponsor shop order", Action = "webshop.order-cancel", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostCancelAsync(int orderId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null || order.Status == ShopOrderStatus.Signed)
            return Done(ShopResult.Fail("A signed order can only be cancelled by the organizers."));
        return Done(await _orders.CancelAsync(order, ActorEmail, "Cancelled by the sponsor", ct));
    }

    /// <summary>
    /// §1312 — an ORGANIZER (signed in as themselves, or switched to this sponsor) may confirm an order the sponsor signed their
    /// own way. Only an organizer can start "switch to user", so an organizer-kind acting session is an organizer.
    /// </summary>
    public bool OrganizerMayConfirm => Participant.Current is { } me
        && (OrganizerAuth.IsRealOrganizer(me) || me.ActingAs is { Kind: ImpersonationActorKind.Organizer, ActorParticipantId: not null });

    private string OrganizerLabel => Participant.Current?.ActingAs?.ActorLabel ?? Participant.Current?.FullName ?? "an organizer";

    /// <summary>§1312 — "Manual Confirm Order": the sponsor signed offline; the organizer confirms (optionally with the signed PDF).</summary>
    [Audit("Confirmed a sponsor order signed offline", Action = "webshop.order-confirm-offline", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostConfirmOfflineAsync(int orderId, IFormFile? signedPdf, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        if (!OrganizerMayConfirm) return Forbid();
        var order = await _orders.GetAsync(EventId, CompanyId!, orderId, ct);
        if (order is null) return Done(ShopResult.Fail("That order no longer exists."));
        var (bytes, error) = await ReadSignedPdfAsync(signedPdf, ct);
        if (error is not null) return Done(ShopResult.Fail(error));
        return Done(await _signing.ConfirmOfflineAsync(order, OrganizerLabel, bytes, signedPdf?.FileName, ct));
    }

    /// <summary>The optional signed contract: a PDF, at most 20 MB.</summary>
    internal static async Task<(byte[]? Bytes, string? Error)> ReadSignedPdfAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, null);
        if (file.Length > 20 * 1024 * 1024) return (null, "The signed contract is larger than 20 MB.");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        return bytes.Length >= 5 && System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-"
            ? (bytes, null) : (null, "The signed contract must be a PDF file.");
    }

    private IActionResult Done(ShopResult r)
    {
        Message = string.IsNullOrWhiteSpace(r.Message) ? (r.Ok ? "Saved." : "That did not work.") : r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage();
    }
}
