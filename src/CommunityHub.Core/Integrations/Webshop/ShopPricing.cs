using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>One priced line of a cart or order.</summary>
public sealed record ShopPricedLine(
    int LineId,
    int ProductId,
    string Name,
    ShopGroup Group,
    int Quantity,
    decimal ListUnitEur,
    decimal SurchargePercent,
    decimal UnitEur,
    decimal LineEur,
    // §1305 — the part of the included-furniture allowance or the swag credit that reduces THIS line. Allocated per
    // line so the invoice (which bills lines, never a negative discount line) totals what the contract totals.
    decimal DiscountEur = 0m,
    // §1305 — the NORMAL unit price when the product was discounted (shown struck through next to the price paid).
    decimal? NormalUnitEur = null)
{
    /// <summary>The line after its discount — what the invoice bills for this line.</summary>
    public decimal NetEur => LineEur - DiscountEur;
}

/// <summary>The totals of a cart or order, in EUR.</summary>
public sealed record ShopTotals(
    IReadOnlyList<ShopPricedLine> Lines,
    decimal SubtotalEur,
    decimal IncludedFurnitureEur,
    string? IncludedFurnitureLabel,
    decimal NetEur,
    decimal VatRatePercent,
    decimal VatEur,
    decimal TotalEur,
    bool LateSurchargeApplies,
    // §1305 item 13 — the swag-catalogue credit applied (never more than the swag lines).
    decimal SwagCreditEur = 0m);

/// <summary>
/// §1305 — every money rule of the CEH sponsor shop in one place, with no I/O: the late-order
/// surcharge, the included booth furniture, and VAT.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"price-conditions (in some cases price must change, if ordered less than
/// 30 days before the event +50%)"</i> · <i>"Cart must support TAX (Danish customers only) vs. No Tax
/// (other sponsors)"</i>. The old shop's included-furniture coupon (<c>eldk-&lt;tier&gt;-booth-included</c>)
/// becomes an automatic discount on furniture, capped at the tier's allowance, so a sponsor no longer
/// has to find and type a code.</para>
///
/// <para>🔒 All amounts are EUR, rounded to cents at the LINE, and totals are sums of rounded lines —
/// the same rounding the contract prints, so the contract always adds up.</para>
/// </remarks>
public static class ShopPricing
{
    /// <summary>
    /// §1312 — the ONE number format of the webshop (mails, contract, shop pages): no thousands separator, two decimals only
    /// when there are cents — "8150", "11.50". Operator 2026-09-27: "critical bug in all webshop emails + contract - it is
    /// using usa formatting, remove the comma and make it without (generic)". The currency sign is added by the caller.
    /// </summary>
    public static string Amount(decimal v) =>
        v.ToString(v % 1 == 0 ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>§1312 — a count (bags, attendees) without a thousands separator: "1250".</summary>
    public static string Count(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// §1305 — the ONE amount a swag sponsor pays for an item: price per piece × the pricing quantity × the sponsor's
    /// share % (the product's own, else the edition default, else 100 %); ELDK covers the rest. Operator 2026-09-26:
    /// <i>"33% sponsor contribution … times it with a backend amount (1250) … price reflected must be that instead of unit
    /// price"</i> · <i>"the % for swags are individual per product, default 33%"</i>. <paramref name="normal"/> uses the
    /// undiscounted price, for the discount marking.
    /// </summary>
    /// <summary>The split behind a swag price: the sponsor's share, the full price for every attendee, and ELDK's part.</summary>
    public sealed record SwagSplit(decimal ShareEur, decimal FullEur, decimal Percent, int Quantity)
    {
        public decimal EldkEur => FullEur - ShareEur;
    }

    /// <summary>The whole split for one swag product — what the configurator explains to the sponsor.</summary>
    public static SwagSplit SwagSplitFor(ShopProduct product, ShopSwagPricing rule, int bagPieces)
    {
        var qty = rule.PricingQuantity is int q && q > 0 ? q : Math.Max(1, bagPieces);
        var pct = product.SponsorContributionPercent is decimal own && own > 0 ? own
            : rule.SponsorContributionPercent is decimal p && p > 0 ? p : 100m;
        return new SwagSplit(SwagContributionEur(product, rule, bagPieces),
            Math.Round(product.EffectivePriceEur * qty, 2, MidpointRounding.AwayFromZero), pct, qty);
    }

    /// <summary>
    /// The split in one sentence, for the cart and the contract (operator 2026-09-26: "include the sponsors contribution
    /// rest price is covered by ELDK, full price - and mention attendee count (1250)" · "show the split in payment").
    /// </summary>
    public static string SwagSplitText(SwagSplit s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string Eur(decimal v) => "€" + Amount(v);
        return $"Your sponsor contribution ({s.Percent.ToString("0.##", inv)} %) of the full price {Eur(s.FullEur)} for "
            + $"{Count(s.Quantity)} attendee bags — ELDK covers the rest, {Eur(s.EldkEur)}.";
    }

    public static decimal SwagContributionEur(ShopProduct product, ShopSwagPricing rule, int bagPieces, bool normal = false)
    {
        var qty = rule.PricingQuantity is int q && q > 0 ? q : Math.Max(1, bagPieces);
        var pct = product.SponsorContributionPercent is decimal own && own > 0 ? own
            : rule.SponsorContributionPercent is decimal p && p > 0 ? p : 100m;
        var perPiece = normal ? product.PriceEur : product.EffectivePriceEur;
        return Math.Round(perPiece * qty * pct / 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Does the late surcharge apply to a product of <paramref name="group"/> ordered at <paramref name="at"/>?
    /// </summary>
    public static decimal SurchargePercentFor(
        ShopGroup group, DateTimeOffset at, DateTimeOffset? eventStartsAt, ShopLateSurcharge rule)
    {
        if (eventStartsAt is null || rule.Percent <= 0 || rule.DaysBeforeEvent <= 0) return 0m;
        if (!rule.Groups.Any(g => string.Equals(g, group.ToString(), StringComparison.OrdinalIgnoreCase))) return 0m;
        return at >= eventStartsAt.Value.AddDays(-rule.DaysBeforeEvent) ? rule.Percent : 0m;
    }

    /// <summary>
    /// VAT applies to Danish customers only. The e-conomic zone decides when known (1 = domestic with VAT;
    /// 4 = domestic exempt; 2/3 = abroad); otherwise the country does.
    /// </summary>
    public static bool VatApplies(string? countryIso2, int vatZone) => vatZone switch
    {
        1 => true,
        2 or 3 or 4 => false,
        _ => string.Equals(countryIso2?.Trim(), "DK", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The booth tier key (platinum/diamond/gold/feature) a name implies, or null.</summary>
    public static string? TierKeyFromName(string? name)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("platinum")) return "platinum";
        if (n.Contains("diamond")) return "diamond";
        if (n.Contains("feature")) return "feature";
        if (n.Contains("gold")) return "gold";
        return null;
    }

    /// <summary>The tier key for a <see cref="BoothTier"/>, or null for none.</summary>
    public static string? TierKey(BoothTier tier) => tier switch
    {
        BoothTier.Platinum => "platinum",
        BoothTier.Diamond => "diamond",
        BoothTier.Gold => "gold",
        BoothTier.Feature => "feature",
        _ => null,
    };

    /// <summary>
    /// Price a set of lines.
    /// </summary>
    /// <param name="lines">(line id, product, quantity, frozen surcharge or null = compute now).</param>
    /// <param name="boothTierKey">
    /// The company's booth tier (from an earlier order, or a booth in these same lines). Decides the
    /// included-furniture allowance; null ⇒ no allowance.
    /// </param>
    public static ShopTotals Price(
        IEnumerable<(int LineId, ShopProduct Product, int Quantity, decimal? FrozenSurchargePercent, decimal? FrozenUnitEur)> lines,
        ShopConfig config,
        DateTimeOffset at,
        DateTimeOffset? eventStartsAt,
        string? boothTierKey,
        string? countryIso2,
        int vatZone,
        decimal swagCreditEur = 0m)
    {
        var priced = new List<ShopPricedLine>();
        var anySurcharge = false;
        foreach (var (lineId, product, qty, frozenPct, frozenUnit) in lines)
        {
            var group = product.PrimaryCategory?.Group ?? ShopGroup.Unassigned;
            var list = frozenUnit ?? product.EffectivePriceEur;
            var pct = frozenPct ?? SurchargePercentFor(group, at, eventStartsAt, config.LateSurcharge);
            if (pct > 0) anySurcharge = true;
            var unit = Math.Round(list * (1 + pct / 100m), 2, MidpointRounding.AwayFromZero);
            var q = Math.Max(1, qty);
            priced.Add(new ShopPricedLine(lineId, product.Id, product.Name, group, q, list, pct, unit, unit * q));
        }

        var subtotal = priced.Sum(l => l.LineEur);

        // Included booth furniture: an allowance, never more than the furniture actually in the cart.
        decimal included = 0m;
        string? includedLabel = null;
        if (boothTierKey is not null
            && config.IncludedFurniture.AllowanceEurByTier.TryGetValue(boothTierKey, out var allowance)
            && allowance > 0)
        {
            var furniture = priced
                .Where(l => string.Equals(l.Group.ToString(), config.IncludedFurniture.Group, StringComparison.OrdinalIgnoreCase))
                // The allowance covers the furniture at LIST price; a late surcharge is still paid.
                .Sum(l => l.ListUnitEur * l.Quantity);
            included = Math.Min(allowance, furniture);
            if (included > 0)
                includedLabel = $"{config.IncludedFurniture.Label} ({boothTierKey}, up to €{allowance:0})";
        }

        // Allocate each discount to the lines it belongs to, in line order, never more than a line's amount.
        var discounts = new decimal[priced.Count];
        void Allocate(decimal amount, Func<ShopPricedLine, bool> eligible, Func<ShopPricedLine, decimal> cap)
        {
            for (var i = 0; i < priced.Count && amount > 0; i++)
            {
                if (!eligible(priced[i])) continue;
                var room = Math.Max(0m, cap(priced[i]) - discounts[i]);
                var take = Math.Min(room, amount);
                discounts[i] += take;
                amount -= take;
            }
        }
        Allocate(included,
            l => string.Equals(l.Group.ToString(), config.IncludedFurniture.Group, StringComparison.OrdinalIgnoreCase),
            l => l.ListUnitEur * l.Quantity);

        // §1305 item 13 — the swag credit: an organizer-granted euro value, spendable on SWAG only.
        var swagCredit = 0m;
        if (swagCreditEur > 0)
        {
            var swagLines = priced.Where(l => l.Group == ShopGroup.Swag).Sum(l => l.LineEur);
            swagCredit = Math.Min(swagCreditEur, swagLines);
            Allocate(swagCredit, l => l.Group == ShopGroup.Swag, l => l.LineEur);
        }
        for (var i = 0; i < priced.Count; i++)
            if (discounts[i] > 0) priced[i] = priced[i] with { DiscountEur = discounts[i] };

        var net = subtotal - included - swagCredit;
        var vatPct = VatApplies(countryIso2, vatZone) ? config.VatRatePercent : 0m;
        var vat = Math.Round(net * vatPct / 100m, 2, MidpointRounding.AwayFromZero);

        return new ShopTotals(priced, subtotal, included, includedLabel, net, vatPct, vat, net + vat, anySurcharge, swagCredit);
    }
}
