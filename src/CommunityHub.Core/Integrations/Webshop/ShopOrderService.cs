using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>The outcome of a shop action: ok, or a message a sponsor can act on.</summary>
public sealed record ShopResult(bool Ok, string Message)
{
    public static ShopResult Success(string message = "") => new(true, message);
    public static ShopResult Fail(string message) => new(false, message);
}

/// <summary>
/// §1305 — the CEH sponsor shop's orders: cart, reservation, signature, cancellation — and the stock
/// they hold in the webshop.
/// </summary>
/// <remarks>
/// <para>🔒 <b>A cart holds nothing.</b> Stock is taken when the order is RESERVED (or sent for signature,
/// which reserves first) and given back when it is cancelled. An abandoned cart can therefore never keep
/// a booth off the market — the classic failure of shops that hold stock at "add to cart".</para>
///
/// <para>🔑 <b>CEH is the one stock counter</b> (§1312 — the old webshop is closed and is neither read nor written). Taking
/// stock lowers the product's own count (refusing when too few remain); giving it back raises it. If ANY line of a
/// reservation cannot be taken, every line already taken is given back and the reservation fails as a whole — half a
/// reservation is worse than none.</para>
///
/// <para>Signing is completed by <see cref="ShopSigningService"/> (contract PDF, company becomes a sponsor);
/// onboarding follows through the existing pipeline (<see cref="ShopOrderSource"/>).</para>
/// </remarks>
/// <summary>Who is putting a product in a cart — decides which products may be added.</summary>
public enum ShopAddMode
{
    /// <summary>A sponsor in the shop: no organizer-only products, no swag outside the configurator.</summary>
    Sponsor = 0,
    /// <summary>An organizer pre-staging a cart: organizer-only products allowed; swag still goes through the configurator.</summary>
    Organizer = 1,
    /// <summary>The swag configurator, which sets the bag quantity.</summary>
    SwagConfigurator = 2,
}

public sealed class ShopOrderService
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopStockService _stock;
    private readonly ShopSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopOrderService>? _log;
    private readonly ShopMailer? _mail;

    public ShopOrderService(
        CommunityHubDbContext db, ShopStockService stock, ShopSettings settings,
        TimeProvider clock, ILogger<ShopOrderService>? log = null, ShopMailer? mail = null)
    {
        _db = db;
        _stock = stock;
        _settings = settings;
        _clock = clock;
        _log = log;
        _mail = mail;
    }

    // ------------------------------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------------------------------

    /// <summary>An order with its lines and products, scoped to the company.</summary>
    public Task<ShopOrder?> GetAsync(int eventId, string companyId, int orderId, CancellationToken ct = default) =>
        OrdersQuery()
            .FirstOrDefaultAsync(o => o.Id == orderId && o.EventId == eventId && o.SponsorCompanyId == companyId, ct);

    /// <summary>An order with its lines and products, any company (organizer use).</summary>
    public Task<ShopOrder?> GetAnyAsync(int eventId, int orderId, CancellationToken ct = default) =>
        OrdersQuery().FirstOrDefaultAsync(o => o.Id == orderId && o.EventId == eventId, ct);

    /// <summary>The company's open cart, or null.</summary>
    public Task<ShopOrder?> CartAsync(int eventId, string companyId, CancellationToken ct = default) =>
        OrdersQuery()
            .Where(o => o.EventId == eventId && o.SponsorCompanyId == companyId && o.Status == ShopOrderStatus.Cart)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Every order of the company, newest first.</summary>
    public async Task<IReadOnlyList<ShopOrder>> OrdersAsync(int eventId, string companyId, CancellationToken ct = default) =>
        await OrdersQuery()
            .Where(o => o.EventId == eventId && o.SponsorCompanyId == companyId)
            .OrderByDescending(o => o.Id)
            .ToListAsync(ct);

    private IQueryable<ShopOrder> OrdersQuery() =>
        _db.ShopOrders
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.PrimaryCategory)
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Images);

    /// <summary>
    /// §1305 — the ONE amount a swag sponsor pays for an item: price per piece × the pricing quantity × the sponsor's
    /// contribution % (<c>shop.&lt;edition&gt;.json → swagPricing</c>). <paramref name="normal"/> uses the undiscounted
    /// price, for the discount marking.
    /// </summary>
    public decimal SwagContributionEur(ShopProduct product, int bagPieces, bool normal = false) =>
        ShopPricing.SwagContributionEur(product, _settings.Shop().SwagPricing, bagPieces, normal);

    private static bool IsSwag(ShopOrderLine l) => l.Product?.PrimaryCategory?.Group == ShopGroup.Swag;

    /// <summary>
    /// The price a line is FROZEN at, and its normal price when that is higher (for "normal → your price"). §1312 — a price
    /// agreed with the company for this line wins over the product's own price and discount; the product's LIST price is
    /// then the normal one.
    /// </summary>
    private decimal UnitFor(ShopOrderLine line) =>
        line.AgreedUnitPriceEur is decimal agreed ? agreed
        : IsSwag(line) ? SwagContributionEur(line.Product, line.PiecesPerUnit)
        : line.Product.EffectivePriceEur * Math.Max(1, line.PiecesPerUnit);

    private decimal? NormalFor(ShopOrderLine line)
    {
        if (line.AgreedUnitPriceEur is decimal agreed)
        {
            var list = IsSwag(line) ? SwagContributionEur(line.Product, line.PiecesPerUnit, normal: true)
                : line.Product.PriceEur * Math.Max(1, line.PiecesPerUnit);
            return agreed < list ? list : null;
        }
        return !line.Product.IsDiscounted ? null
            : IsSwag(line) ? SwagContributionEur(line.Product, line.PiecesPerUnit, normal: true)
            : line.Product.PriceEur * Math.Max(1, line.PiecesPerUnit);
    }

    /// <summary>
    /// §1312 — set (or clear with null) the price agreed with the company for one line of an UNSIGNED order. A reserved or
    /// sent order is re-frozen at the new price at once; a signed order is refused (the contract is signed at its price).
    /// </summary>
    public async Task<ShopResult> SetAgreedPriceAsync(int eventId, int orderId, int lineId, decimal? price, CancellationToken ct = default)
    {
        if (price is < 0) return ShopResult.Fail("The price cannot be negative.");
        var order = await _db.ShopOrders.Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.PrimaryCategory)
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId, ct);
        var line = order?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (order is null || line is null) return ShopResult.Fail("That order line no longer exists.");
        if (order.Status is ShopOrderStatus.Signed or ShopOrderStatus.Cancelled)
            return ShopResult.Fail("A signed or cancelled order keeps its price.");
        line.AgreedUnitPriceEur = price is decimal p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null;
        if (order.Status != ShopOrderStatus.Cart)
        {
            line.UnitPriceEur = UnitFor(line);
            line.NormalUnitPriceEur = NormalFor(line);
        }
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success(price is null
            ? $"'{line.ProductName}' is back at its normal price."
            : $"'{line.ProductName}' is agreed at €{ShopPricing.Amount(line.AgreedUnitPriceEur!.Value)} for this company.");
    }

    /// <summary>Price an order. Uses frozen prices once the order has left the cart.</summary>
    public async Task<ShopTotals> PriceAsync(ShopOrder order, ShopCompany company, CancellationToken ct = default)
    {
        var cfg = _settings.Shop();
        var frozen = order.Status != ShopOrderStatus.Cart;
        var tier = await BoothTierKeyAsync(order, ct);
        var credit = frozen ? order.SwagCreditEur : (await LiveSwagCreditsAsync(order.EventId, order.SponsorCompanyId, ct)).Sum(c => c.Amount);
        var totals = ShopPricing.Price(
            order.Lines.Select(l => (l.Id, l.Product, l.Quantity,
                frozen ? (decimal?)l.SurchargePercent : null,
                // A swag line's unit is "price per piece × every bag"; frozen lines already carry it.
                // §1312 — a price agreed with the company is the line's unit (per whole line unit, like swag).
                frozen ? (decimal?)l.UnitPriceEur : l.AgreedUnitPriceEur is not null || IsSwag(l) ? UnitFor(l) : null)),
            cfg,
            frozen ? (order.ReservedAt ?? order.SentForSignatureAt ?? order.CreatedAt) : _clock.GetUtcNow(),
            _settings.EventStartsAt(),
            tier,
            company.CountryIso2,
            company.VatZone,
            credit);

        // Swag lines say how many pieces they are, on the cart, the contract and the invoice; a discounted line keeps
        // its normal price beside it (frozen with the price once the order left the cart).
        var byId = order.Lines.ToDictionary(l => l.Id);
        return totals with
        {
            Lines = totals.Lines.Select(l =>
            {
                if (!byId.TryGetValue(l.LineId, out var ol)) return l;
                var normal = frozen ? ol.NormalUnitPriceEur : NormalFor(ol);
                // A swag line is ONE amount — the sponsor's contribution — never a per-piece price or a piece count.
                var named = IsSwag(ol) ? l with { Name = $"{l.Name} — with your logo, in every attendee bag" } : l;
                return named with { NormalUnitEur = normal };
            }).ToList(),
        };
    }

    /// <summary>§1305 item 13 — the company's unspent swag credits (organizer-granted euro values).</summary>
    public async Task<IReadOnlyList<SwagCatalogCredit>> LiveSwagCreditsAsync(int eventId, string companyId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var all = await _db.SwagCatalogCredits
            .Where(c => c.EventId == eventId && c.SponsorCompanyId == companyId).ToListAsync(ct);
        return all.Where(c => c.IsLiveOn(today)).OrderBy(c => c.CreatedAt).ToList();
    }

    /// <summary>
    /// §1305 item 13 — put a swag-catalogue item in the company's cart: the webshop product, ONE unit of stock (one
    /// sponsor takes the item), priced per piece × <paramref name="pieces"/> (the bag quantity). Operator: "keep the
    /// swag catalog as i originally designed it … end result is a cart entry + contract + signing + invoice".
    /// </summary>
    public async Task<ShopResult> AddSwagAsync(
        int eventId, string companyId, long wooProductId, int pieces, int? byParticipantId, CancellationToken ct = default)
    {
        if (pieces < 1) return ShopResult.Fail("The bag quantity is not configured.");
        var product = await _db.ShopProducts.FirstOrDefaultAsync(p => p.EventId == eventId && p.WooProductId == wooProductId, ct);
        if (product is null)
            return ShopResult.Fail("This item is not in the shop's catalogue yet (the catalogue syncs every 2 minutes).");

        var cart = await CartAsync(eventId, companyId, ct);
        var existing = cart?.Lines.FirstOrDefault(l => l.ProductId == product.Id);
        if (existing is not null)
        {
            existing.PiecesPerUnit = pieces;
            await _db.SaveChangesAsync(ct);
            return ShopResult.Success($"'{product.Name}' is already in your order.");
        }

        var r = await AddAsync(eventId, companyId, product.Id, 1, byParticipantId, ct, ShopAddMode.SwagConfigurator);
        if (!r.Ok) return r;
        cart = await CartAsync(eventId, companyId, ct);
        var line = cart!.Lines.First(l => l.ProductId == product.Id);
        line.PiecesPerUnit = pieces;
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"'{product.Name}' for all {ShopPricing.Count(pieces)} bags is in your order."));
    }

    /// <summary>
    /// §1312 — the old webshop's coupon code for this order's included furniture ("eldk-gold-booth-included"), so the invoice
    /// line reads exactly as the webshop invoice did. Null when the order has no booth tier.
    /// </summary>
    public async Task<string?> FurnitureCouponCodeAsync(ShopOrder order, CancellationToken ct = default)
    {
        var tier = await BoothTierKeyAsync(order, ct);
        var pattern = _settings.Shop().IncludedFurniture.CouponCode;
        return tier is null || string.IsNullOrWhiteSpace(pattern) ? null : pattern.Replace("{tier}", tier, StringComparison.Ordinal);
    }

    /// <summary>
    /// The company's booth tier for the furniture allowance: from its sponsor record, or from a booth in
    /// this order (a first-time exhibitor buys the booth and the furniture together).
    /// </summary>
    public async Task<string?> BoothTierKeyAsync(ShopOrder order, CancellationToken ct = default)
    {
        var info = await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.EventId == order.EventId && s.SponsorCompanyId == order.SponsorCompanyId)
            .Select(s => (BoothTier?)s.Tier)
            .FirstOrDefaultAsync(ct);
        var fromInfo = info is BoothTier t ? ShopPricing.TierKey(t) : null;
        if (fromInfo is not null) return fromInfo;

        foreach (var l in order.Lines.Where(l => l.Product?.PrimaryCategory?.Group == ShopGroup.Booths && l.Product.BoothId is not null))
        {
            var key = ShopPricing.TierKeyFromName(l.Product.PrimaryCategory?.Name) ?? ShopPricing.TierKeyFromName(l.ProductName);
            if (key is not null) return key;
        }

        // A booth bought earlier in another CEH order (signed or reserved) of the same company.
        var earlier = await _db.ShopOrderLines.AsNoTracking()
            .Where(l => l.Order.EventId == order.EventId && l.Order.SponsorCompanyId == order.SponsorCompanyId
                        && l.Order.Id != order.Id
                        && (l.Order.Status == ShopOrderStatus.Signed || l.Order.Status == ShopOrderStatus.Reserved
                            || l.Order.Status == ShopOrderStatus.AwaitingSignature)
                        && l.Product.BoothId != null)
            .Select(l => new { Cat = l.Product.PrimaryCategory!.Name, l.ProductName })
            .ToListAsync(ct);
        return earlier.Select(e => ShopPricing.TierKeyFromName(e.Cat) ?? ShopPricing.TierKeyFromName(e.ProductName))
            .FirstOrDefault(k => k is not null);
    }

    // ------------------------------------------------------------------------------------------
    // Cart
    // ------------------------------------------------------------------------------------------

    /// <summary>Put a product in the company's cart (creating the cart), checked against LIVE stock.</summary>
    public async Task<ShopResult> AddAsync(
        int eventId, string companyId, int productId, int quantity, int? byParticipantId, CancellationToken ct = default,
        ShopAddMode mode = ShopAddMode.Sponsor)
    {
        if (quantity < 1) quantity = 1;
        var product = await _db.ShopProducts.Include(p => p.PrimaryCategory)
            .FirstOrDefaultAsync(p => p.Id == productId && p.EventId == eventId, ct);
        if (product is null || product.Status != ShopProductStatus.Published)
            return ShopResult.Fail("That product is not available.");

        var group = product.PrimaryCategory?.Group ?? ShopGroup.Unassigned;
        // Operator 2026-09-26: "swag sponsor should not be selectable as a visible product … it is used when 2 sponsor
        // give us swags for free" — only an organizer can put it in a cart.
        if (group == ShopGroup.OrganizerOnly && mode != ShopAddMode.Organizer)
            return ShopResult.Fail("That product is not available.");
        // Swag is bought for EVERY attendee bag, so it only enters a cart through the swag configurator, which sets the
        // bag quantity. A plain add put ONE piece in the cart at the per-piece price (operator 2026-09-26: "people are
        // confused when i select 1 item of powerbank in the configurator for 11.50 euro").
        if (group == ShopGroup.Swag && mode != ShopAddMode.SwagConfigurator)
            return ShopResult.Fail("Swag is chosen in the swag configurator — it is bought for every attendee bag, with your logo.");
        if (group == ShopGroup.Swag) quantity = 1;

        var cart = await CartAsync(eventId, companyId, ct);
        var existing = cart?.Lines.FirstOrDefault(l => l.ProductId == productId);
        var wanted = (existing?.Quantity ?? 0) + quantity;

        var check = await CheckAvailableAsync(product, wanted, ct);
        if (!check.Ok) return check;

        if (cart is null)
        {
            var company = await _db.ShopCompanies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
            var contacts = await _db.SponsorCompanyContacts.AsNoTracking()
                .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId)
                .ToListAsync(ct);
            cart = new ShopOrder
            {
                EventId = eventId,
                SponsorCompanyId = companyId,
                Status = ShopOrderStatus.Cart,
                Currency = company?.Currency ?? "EUR",
                CreatedAt = _clock.GetUtcNow(),
                // Defaults: the company's default signer / coordinator (operator: "we must support a
                // default signer and default event coordinator").
                SignerParticipantId = contacts.FirstOrDefault(c => c.IsSigner)?.ParticipantId ?? byParticipantId,
                CoordinatorParticipantId = contacts.FirstOrDefault(c => c.IsEventCoordinator)?.ParticipantId ?? byParticipantId,
            };
            _db.ShopOrders.Add(cart);
        }

        if (existing is not null)
        {
            existing.Quantity = wanted;
        }
        else
        {
            cart.Lines.Add(new ShopOrderLine
            {
                ProductId = product.Id,
                Product = product,
                Quantity = quantity,
                UnitPriceEur = product.EffectivePriceEur,
                ProductName = product.Name,
                AddedAt = _clock.GetUtcNow(),
            });
        }
        cart.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success($"'{product.Name}' is in your cart.");
    }

    /// <summary>Change a cart line's quantity (0 removes it).</summary>
    public async Task<ShopResult> SetQuantityAsync(int eventId, string companyId, int lineId, int quantity, CancellationToken ct = default)
    {
        var cart = await CartAsync(eventId, companyId, ct);
        var line = cart?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (cart is null || line is null) return ShopResult.Fail("That line is no longer in your cart.");
        if (quantity <= 0)
        {
            cart.Lines.Remove(line);
            _db.ShopOrderLines.Remove(line);
        }
        else
        {
            if (quantity > 1 && line.Product.PrimaryCategory?.Group == ShopGroup.Swag)
                return ShopResult.Fail("A swag item is ordered once — it already covers every attendee bag.");
            var check = await CheckAvailableAsync(line.Product, quantity, ct);
            if (!check.Ok) return check;
            line.Quantity = quantity;
        }
        cart.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success();
    }

    /// <summary>
    /// Save the order's details: PO / reference, signer and event coordinator (chosen from the company's
    /// own people — nobody else can be named).
    /// </summary>
    public async Task<ShopResult> UpdateDetailsAsync(
        ShopOrder order, string? purchaseOrderRef, int? signerId, int? coordinatorId, CancellationToken ct = default)
    {
        if (order.Status is ShopOrderStatus.Signed or ShopOrderStatus.Cancelled)
            return ShopResult.Fail("A signed or cancelled order can no longer be changed.");

        var people = await CompanyPeopleIdsAsync(order.EventId, order.SponsorCompanyId, ct);
        if (signerId is int s && !people.Contains(s)) return ShopResult.Fail("The signer must be one of your company's contacts.");
        if (coordinatorId is int c && !people.Contains(c)) return ShopResult.Fail("The event coordinator must be one of your company's contacts.");

        order.PurchaseOrderRef = string.IsNullOrWhiteSpace(purchaseOrderRef) ? null : purchaseOrderRef.Trim();
        order.SignerParticipantId = signerId;
        order.CoordinatorParticipantId = coordinatorId;
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success("Saved.");
    }

    /// <summary>Organizer: special conditions for the order (printed as "Agreed Terms" and kept for invoicing).</summary>
    public async Task SetSpecialTermsAsync(
        ShopOrder order, string? terms, DateOnly? invoiceNotBefore, int? paymentDays, CancellationToken ct = default)
    {
        order.SpecialTerms = string.IsNullOrWhiteSpace(terms) ? null : terms.Trim();
        order.InvoiceNotBefore = invoiceNotBefore;
        order.PaymentDays = paymentDays is > 0 ? paymentDays : null;
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------------------------------
    // Reservation, cancellation — the two-way stock
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reserve a cart: freeze its prices and take its stock in the webshop. All or nothing.
    /// </summary>
    /// <param name="stockAlreadyHeld">§1308 — the webshop stock for these items was already taken (e.g. the same booth held
    /// by a reservation made from the other environment, which shares the one live shop): mark the lines as holding it —
    /// so a release gives it back — without taking it a second time.</param>
    /// <param name="takeStock">§1312 — false: freeze prices and give the order its number, but take NO stock. Used when a
    /// contract goes out for signature: nothing is ordered until it is signed (operator 2026-09-27: "order has not made until
    /// ticked off and signed … it must happen when you tick and sign - or organizer manually complete the order").
    /// <see cref="TakeOutstandingStockAsync"/> takes it at signing.</param>
    public async Task<ShopResult> ReserveAsync(ShopOrder order, CancellationToken ct = default, bool stockAlreadyHeld = false,
        bool takeStock = true)
    {
        if (order.Status != ShopOrderStatus.Cart) return ShopResult.Success();
        if (order.Lines.Count == 0) return ShopResult.Fail("The cart is empty.");

        var now = _clock.GetUtcNow();
        var cfg = _settings.Shop();
        var eventStart = _settings.EventStartsAt();
        var taken = new List<ShopOrderLine>();

        foreach (var line in order.Lines)
        {
            // Freeze the price the sponsor is committing to, including a late surcharge if it applies NOW.
            line.UnitPriceEur = UnitFor(line);            // §1312 — an agreed price wins
            line.NormalUnitPriceEur = NormalFor(line);
            line.SurchargePercent = ShopPricing.SurchargePercentFor(
                line.Product.PrimaryCategory?.Group ?? ShopGroup.Unassigned, now, eventStart, cfg.LateSurcharge);
            line.ProductName = line.Product.Name;

            if (stockAlreadyHeld)
            {
                line.StockTaken = line.Product.ManageStock;
                continue;
            }
            if (!takeStock) continue;
            var r = await TakeStockAsync(line, ct);
            if (!r.Ok)
            {
                foreach (var t in taken) await GiveBackStockAsync(t, ct);
                await _db.SaveChangesAsync(ct);
                return ShopResult.Fail($"'{line.Product.Name}': {r.Message} Nothing was reserved.");
            }
            if (line.StockTaken) taken.Add(line);
        }

        // §1305 item 13 — spend the company's swag credits on this order's swag lines, whole credits in date order,
        // only as much as the swag lines need. Marked used so the same credit can never pay twice.
        var swagLines = order.Lines.Where(l => l.Product.PrimaryCategory?.Group == ShopGroup.Swag)
            .Sum(l => l.UnitPriceEur * l.Quantity * (1 + l.SurchargePercent / 100m));
        if (swagLines > 0)
        {
            var remaining = swagLines;
            foreach (var credit in await LiveSwagCreditsAsync(order.EventId, order.SponsorCompanyId, ct))
            {
                if (remaining <= 0) break;
                credit.UsedOnShopOrderId = order.Id;
                credit.UsedAt = now;
                var applied = Math.Min(credit.Amount, remaining);
                order.SwagCreditEur += applied;
                remaining -= applied;
            }
        }

        order.Status = ShopOrderStatus.Reserved;
        order.ReservedAt = now;
        order.UpdatedAt = now;
        var company = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        order.VatRatePercent = company is not null && ShopPricing.VatApplies(company.CountryIso2, company.VatZone)
            ? cfg.VatRatePercent : 0m;
        await _db.SaveChangesAsync(ct);

        order.Number ??= $"{(string.IsNullOrWhiteSpace(cfg.Event.Name) ? "CEH" : cfg.Event.Name)}-{order.Id:0000}";
        await ClaimEntitlementsAsync(order, now, ct);
        await _db.SaveChangesAsync(ct);

        // This order took the last units — tell the organizers now rather than waiting for the sync. It is a RESERVATION
        // (nothing is signed), so the mail says so — not "sold out" (operator 2026-09-28).
        var soldOut = taken.Select(l => l.Product).Where(p => p.ManageStock && !p.InStock).Distinct().ToList();
        if (_mail is not null && soldOut.Count > 0)
            await _mail.NotifySoldOutAsync(order.EventId, soldOut, $"for {await CompanyLabelAsync(order, ct)} by reservation {order.Number}", ct,
                reservation: true);

        return ShopResult.Success(takeStock
            ? $"Reserved as order {order.Number}. The items are held for you."
            : $"Order {order.Number} is ready to sign. Nothing is ordered until it is signed.");
    }

    /// <summary>The company an order is for, by its public name (the legal name when there is none).</summary>
    private async Task<string> CompanyLabelAsync(ShopOrder order, CancellationToken ct) =>
        await _db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId)
            .Select(c => c.PublicName ?? c.LegalName).FirstOrDefaultAsync(ct) ?? order.SponsorCompanyId;

    /// <summary>
    /// §1312 — at SIGNING (digital, or an organizer confirming an offline-signed contract): take the stock for every line that
    /// does not hold it yet. All or nothing — if one item is gone, what this step took is given back and the caller must not
    /// sign. Lines already held (an accepted reservation) are left alone.
    /// </summary>
    public async Task<ShopResult> TakeOutstandingStockAsync(ShopOrder order, CancellationToken ct = default)
    {
        var taken = new List<ShopOrderLine>();
        foreach (var line in order.Lines.Where(l => !l.StockTaken && l.Product.ManageStock))
        {
            var r = await TakeStockAsync(line, ct);
            if (!r.Ok)
            {
                foreach (var t in taken) await GiveBackStockAsync(t, ct);
                await _db.SaveChangesAsync(ct);
                return ShopResult.Fail($"'{line.Product.Name}': {r.Message} Nothing was ordered — please contact the organizers.");
            }
            if (line.StockTaken) taken.Add(line);
        }
        await _db.SaveChangesAsync(ct);

        // Every line this order holds counts — not only what was taken just now. Items held by an accepted reservation were
        // reported as "Reserved — no longer available"; signing turns them into a SALE, and that is the sold-out mail the
        // organizers act on (operator 2026-09-29: signed ELDK27-0008, got only the reserved mail, never the sold-out one).
        var soldOut = order.Lines.Where(l => l.StockTaken).Select(l => l.Product)
            .Where(p => p.ManageStock && !p.InStock).Distinct().ToList();
        if (_mail is not null && soldOut.Count > 0)
            await _mail.NotifySoldOutAsync(order.EventId, soldOut, $"to {await CompanyLabelAsync(order, ct)}, order {order.Number} (signed)", ct);
        return ShopResult.Success();
    }

    /// <summary>
    /// §1308 — move the company's CART into a RESERVED order, taking the stock for the moved items now, so one contract
    /// covers the reservation and what the sponsor adds (operator 2026-09-27: "it must be possible for a potential sponsor
    /// to login and flip a reservation to an order in the cart and sign"). All or nothing: if one item is gone, the stock
    /// already taken in this step is given back and nothing moves. The reservation's own items stay held throughout.
    /// </summary>
    public async Task<ShopResult> MergeCartIntoAsync(ShopOrder target, CancellationToken ct = default)
    {
        if (target.Status != ShopOrderStatus.Reserved)
            return ShopResult.Fail("Items can only be added to a reserved order that has not been sent for signature.");
        var cart = await CartAsync(target.EventId, target.SponsorCompanyId, ct);
        if (cart is null || cart.Lines.Count == 0) return ShopResult.Fail("Your cart is empty.");

        var now = _clock.GetUtcNow();
        var cfg = _settings.Shop();
        var eventStart = _settings.EventStartsAt();
        var taken = new List<ShopOrderLine>();
        foreach (var line in cart.Lines)
        {
            line.UnitPriceEur = UnitFor(line);            // §1312 — an agreed price wins
            line.NormalUnitPriceEur = NormalFor(line);
            line.SurchargePercent = ShopPricing.SurchargePercentFor(
                line.Product.PrimaryCategory?.Group ?? ShopGroup.Unassigned, now, eventStart, cfg.LateSurcharge);
            line.ProductName = line.Product.Name;

            var r = await TakeStockAsync(line, ct);
            if (!r.Ok)
            {
                foreach (var t in taken) await GiveBackStockAsync(t, ct);
                await _db.SaveChangesAsync(ct);
                return ShopResult.Fail($"'{line.Product.Name}': {r.Message} Nothing was added.");
            }
            if (line.StockTaken) taken.Add(line);
        }

        // 🔒 Re-point each line to the reservation — never Remove() it from the cart's collection: on a required
        // relationship EF treats a removed dependent as an ORPHAN and deletes it.
        var moved = cart.Lines.ToList();
        foreach (var line in moved)
        {
            line.Order = target;
            line.OrderId = target.Id;
        }
        cart.UpdatedAt = now;
        target.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var soldOut = taken.Select(l => l.Product).Where(p => p.ManageStock && !p.InStock).Distinct().ToList();
        if (_mail is not null && soldOut.Count > 0)
            await _mail.NotifySoldOutAsync(target.EventId, soldOut, $"for {await CompanyLabelAsync(target, ct)} by reservation {target.Number}", ct,
                reservation: true);
        return ShopResult.Success($"{moved.Sum(l => l.Quantity)} item(s) added to {target.Number}. Review and sign it below.");
    }

    /// <summary>Cancel an order, giving back every unit it took from the webshop.</summary>
    public async Task<ShopResult> CancelAsync(ShopOrder order, string byEmail, string? reason, CancellationToken ct = default,
        bool byOrganizers = false)
    {
        if (order.Status == ShopOrderStatus.Cancelled) return ShopResult.Success();
        var was = order.Status;
        var problems = new List<string>();
        foreach (var line in order.Lines.Where(l => l.StockTaken))
        {
            var r = await GiveBackStockAsync(line, ct);
            if (!r.Ok) problems.Add($"'{line.ProductName}': {r.Message}");
        }

        var now = _clock.GetUtcNow();
        order.Status = ShopOrderStatus.Cancelled;
        order.CancelledAt = now;
        order.CancelledByEmail = byEmail;
        order.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        order.UpdatedAt = now;

        // Entitlements claimed by this order become open again.
        var lineIds = order.Lines.Select(l => l.Id).ToList();
        var claimed = await _db.ShopEntitlements
            .Where(e => e.ClaimedOrderLineId != null && lineIds.Contains(e.ClaimedOrderLineId.Value))
            .ToListAsync(ct);
        foreach (var e in claimed) { e.ClaimedOrderLineId = null; e.ClaimedAt = null; }

        // Swag credits spent on this order are live again.
        var credits = await _db.SwagCatalogCredits.Where(c => c.UsedOnShopOrderId == order.Id).ToListAsync(ct);
        foreach (var c in credits) { c.UsedOnShopOrderId = null; c.UsedAt = null; }
        order.SwagCreditEur = 0m;

        await _db.SaveChangesAsync(ct);

        // §1312 — a reservation or order (not a plain cart that held nothing) that is cancelled tells the sponsor, and the
        // organizers through the CC (operator 2026-09-27: "sponsor + me must get an email when an reservation is cancelled.
        // sponsor + me must get an email when order is cancelled"). Never fails the cancel.
        if (_mail is not null && was is ShopOrderStatus.Reserved or ShopOrderStatus.AwaitingSignature or ShopOrderStatus.Signed)
        {
            try
            {
                var company = await _db.ShopCompanies.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
                var totals = company is null ? null : await PriceAsync(order, company, ct);
                await _mail.SendCancelledAsync(order, byOrganizers, reason, totals, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1312 cancellation mail for {Number} could not be sent.", order.Number);
            }
        }
        return problems.Count == 0
            ? ShopResult.Success("The order is cancelled and its items are available again.")
            : ShopResult.Fail("The order is cancelled, but stock could not be given back for: "
                + string.Join("; ", problems) + ". Adjust the stock on the product page by hand.");
    }

    private async Task<ShopResult> CheckAvailableAsync(ShopProduct product, int quantity, CancellationToken ct)
    {
        var live = await _stock.GetAsync(new[] { product }, ct);
        if (!live.TryGetValue(product.Id, out var a) || !a.Available)
            return ShopResult.Fail($"'{product.Name}' is sold out.");
        if (!a.Live)
            return ShopResult.Fail("The shop cannot confirm availability right now. Please try again in a minute.");
        if (!a.Unlimited && a.Remaining is int left && quantity > left)
            return ShopResult.Fail($"Only {left} of '{product.Name}' left.");
        return ShopResult.Success();
    }

    // 🔴 §1312 — CEH is the ONE stock counter: the old webshop is closed and is neither read nor written. A concurrent sale of
    // the last unit is caught by the concurrency check on ShopProduct.StockQuantity when the order is saved.
    private Task<ShopResult> TakeStockAsync(ShopOrderLine line, CancellationToken ct)
    {
        var p = line.Product;
        if (!p.ManageStock) return Task.FromResult(ShopResult.Success());   // unlimited: nothing to take
        if ((p.StockQuantity ?? 0) < line.Quantity) return Task.FromResult(ShopResult.Fail("Sold out."));
        p.StockQuantity -= line.Quantity;
        p.InStock = p.StockQuantity > 0;
        line.StockTaken = true;
        return Task.FromResult(ShopResult.Success());
    }

    private Task<ShopResult> GiveBackStockAsync(ShopOrderLine line, CancellationToken ct)
    {
        if (!line.StockTaken) return Task.FromResult(ShopResult.Success());
        var p = line.Product;
        p.StockQuantity = (p.StockQuantity ?? 0) + line.Quantity;
        p.InStock = true;
        line.StockTaken = false;
        return Task.FromResult(ShopResult.Success());
    }

    private async Task ClaimEntitlementsAsync(ShopOrder order, DateTimeOffset now, CancellationToken ct)
    {
        var productIds = order.Lines.Select(l => l.ProductId).ToList();
        var open = await _db.ShopEntitlements
            .Where(e => e.EventId == order.EventId && e.SponsorCompanyId == order.SponsorCompanyId
                        && e.ClaimedOrderLineId == null && e.MatchedProductId != null
                        && productIds.Contains(e.MatchedProductId.Value))
            .ToListAsync(ct);
        foreach (var e in open)
        {
            var line = order.Lines.First(l => l.ProductId == e.MatchedProductId);
            e.ClaimedOrderLineId = line.Id;
            e.ClaimedAt = now;
        }
    }

    private async Task<HashSet<int>> CompanyPeopleIdsAsync(int eventId, string companyId, CancellationToken ct)
    {
        var linked = await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId)
            .Select(l => l.ParticipantId).ToListAsync(ct);
        var primary = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.SponsorCompanyId == companyId)
            .Select(p => p.Id).ToListAsync(ct);
        return linked.Concat(primary).ToHashSet();
    }
}
