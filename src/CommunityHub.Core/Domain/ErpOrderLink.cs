namespace CommunityHub.Core.Domain;

/// <summary>
/// The hub-local link between a sponsor webshop (WooCommerce) order and the
/// e-conomic ERP order created from it (REQUIREMENTS §7a — ERP order creation
/// from webshop orders, with a currency/FX check). Scoped to
/// (EventId, WebshopOrderId). This is the idempotency record: once a webshop
/// order has an <see cref="ErpOrderNumber"/>, a re-run skips it rather than
/// creating a duplicate ERP order.
///
/// The currency check outcome is recorded here so operators can see the order's
/// currency + the FX rate applied at creation time without re-querying any
/// external FX service.
/// </summary>
public class ErpOrderLink
{
    public int Id { get; set; }

    public int EventId { get; set; }

    /// <summary>WooCommerce / Company Manager company id that owns the order.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>The WooCommerce order id this ERP order was created from.</summary>
    public long WebshopOrderId { get; set; }

    /// <summary>The e-conomic order number, once created. Empty = not yet in the ERP.</summary>
    public string ErpOrderNumber { get; set; } = string.Empty;

    /// <summary>The order's own currency (ISO 4217, e.g. "DKK", "EUR").</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// The FX rate applied at creation (units of base currency per one unit of
    /// the order currency). 1 when same-currency; null when no live rate was
    /// available (the currency check then ran as a known-currency gate only).
    /// </summary>
    public decimal? FxRateApplied { get; set; }

    /// <summary>Short result of the currency check (e.g. "same-currency", "fx-applied", "fx-unavailable").</summary>
    public string? CurrencyCheckResult { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; set; }
}
