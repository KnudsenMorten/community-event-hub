using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>Outcome of the order currency/FX check (independent of the ERP write).</summary>
/// <param name="Ok">True when the order's currency is acceptable to proceed.</param>
/// <param name="Currency">Normalized order currency (ISO 4217, upper-case).</param>
/// <param name="Rate">Units of base currency per one unit of the order currency; 1 when same-currency; null when no live rate.</param>
/// <param name="Result">Short machine-friendly result code.</param>
public sealed record CurrencyCheckResult(bool Ok, string Currency, decimal? Rate, string Result);

/// <summary>
/// Creates e-conomic orders from sponsor webshop (WooCommerce) orders, with a
/// currency/FX check at order time (REQUIREMENTS §7a). Idempotent: an
/// <see cref="ErpOrderLink"/> per (event, webshop order) prevents double-create.
///
/// The currency check is pure, deterministic logic over the configured base
/// currency + (optionally) a live <see cref="IFxRateProvider"/>. With no live
/// provider it is a known-currency gate (rate left null); never invents a rate.
/// </summary>
public sealed class EconomicOrderCreationService
{
    private readonly CommunityHubDbContext _db;
    private readonly IEconomicErpClient _erp;
    private readonly IFxRateProvider _fx;
    private readonly EconomicErpOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<EconomicOrderCreationService> _log;

    public EconomicOrderCreationService(
        CommunityHubDbContext db,
        IEconomicErpClient erp,
        IFxRateProvider fx,
        EconomicErpOptions options,
        TimeProvider clock,
        ILogger<EconomicOrderCreationService> log)
    {
        _db = db;
        _erp = erp;
        _fx = fx;
        _options = options;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// The order currency/FX check. Same-currency → rate 1. Otherwise, if a live
    /// FX provider is wired, fetch today's rate; if not (or the lookup yields no
    /// rate) the check still passes as a known-currency gate with a null rate —
    /// it never fabricates a rate. A blank/malformed currency fails the check.
    /// </summary>
    public async Task<CurrencyCheckResult> CheckCurrencyAsync(string? orderCurrency, CancellationToken ct = default)
    {
        var cur = (orderCurrency ?? string.Empty).Trim().ToUpperInvariant();
        var baseCur = (_options.BaseCurrency ?? "DKK").Trim().ToUpperInvariant();

        // ISO 4217 codes are 3 letters.
        if (cur.Length != 3 || !cur.All(char.IsLetter))
        {
            return new CurrencyCheckResult(false, cur, null, "invalid-currency");
        }

        if (cur == baseCur)
        {
            return new CurrencyCheckResult(true, cur, 1m, "same-currency");
        }

        if (_fx.CanQuote)
        {
            try
            {
                var rate = await _fx.GetRateAsync(baseCur, cur, ct);
                if (rate is > 0m)
                {
                    return new CurrencyCheckResult(true, cur, rate, "fx-applied");
                }
                return new CurrencyCheckResult(true, cur, null, "fx-unavailable");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FX lookup failed for {Cur}; proceeding as known-currency gate.", cur);
                return new CurrencyCheckResult(true, cur, null, "fx-error");
            }
        }

        // No live FX provider — known-currency gate only (rate unknown).
        return new CurrencyCheckResult(true, cur, null, "fx-not-configured");
    }

    /// <summary>Map a webshop order into the ERP order shape. Pure mapping — no I/O.</summary>
    public static ErpOrder MapOrder(WooOrder woo, string companyId, string currency)
    {
        var date = woo.CreatedAt is { } c
            ? DateOnly.FromDateTime(c.UtcDateTime)
            : DateOnly.FromDateTime(DateTime.UtcNow);

        // The hub's WooLineItem is the slim sponsor-pipeline shape (no price/qty);
        // each line maps to a quantity-1 ERP line. Real pricing comes from the
        // ERP product/price-list keyed on product id — the hub never invents a price.
        var lines = woo.LineItems
            .Select(li => new ErpOrderLine(li.ProductId, li.ProductName, Quantity: 1m, UnitPrice: 0m))
            .ToList();

        return new ErpOrder(companyId, woo.OrderId, currency, date, lines);
    }

    /// <summary>
    /// Create one ERP order from a webshop order. Runs the currency check on the
    /// supplied order currency first; a failed check (e.g. malformed currency)
    /// blocks creation. Idempotent on (event, webshop order id).
    ///
    /// The hub's slim <see cref="WooOrder"/> shape does not carry the order
    /// currency, so the caller (which has the raw WooCommerce order) passes it
    /// in — the service never guesses a currency.
    /// </summary>
    public async Task<ErpOrderResult> CreateOrderFromWebshopAsync(
        int eventId, string erpCustomerNumber, WooOrder woo, string companyId, string orderCurrency, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var link = await _db.ErpOrderLinks.SingleOrDefaultAsync(
            l => l.EventId == eventId && l.WebshopOrderId == woo.OrderId, ct);

        if (link is { ErpOrderNumber.Length: > 0 })
        {
            return new ErpOrderResult(
                MapOrder(woo, companyId, link.Currency),
                ErpSyncOutcome.AlreadyExists, link.ErpOrderNumber, "idempotent-skip");
        }

        var check = await CheckCurrencyAsync(orderCurrency, ct);
        if (!check.Ok)
        {
            return new ErpOrderResult(MapOrder(woo, companyId, check.Currency),
                ErpSyncOutcome.Failed, null, $"currency-check-failed:{check.Result}");
        }

        return await CreateOrderAsync(eventId, erpCustomerNumber, woo, companyId, check, now, link, ct);
    }

    private async Task<ErpOrderResult> CreateOrderAsync(
        int eventId, string erpCustomerNumber, WooOrder woo, string companyId,
        CurrencyCheckResult check, DateTimeOffset now, ErpOrderLink? link, CancellationToken ct)
    {
        var order = MapOrder(woo, companyId, check.Currency);

        link ??= new ErpOrderLink
        {
            EventId = eventId,
            SponsorCompanyId = companyId,
            WebshopOrderId = woo.OrderId,
            CreatedAt = now,
        };
        link.Currency = check.Currency;
        link.FxRateApplied = check.Rate;
        link.CurrencyCheckResult = check.Result;
        link.LastSyncedAt = now;
        if (link.Id == 0) _db.ErpOrderLinks.Add(link);

        if (!_erp.CanWrite)
        {
            await _db.SaveChangesAsync(ct);
            return new ErpOrderResult(order, ErpSyncOutcome.WouldCreate, null, "erp-write-disabled");
        }

        try
        {
            var number = await _erp.CreateOrderAsync(erpCustomerNumber, order, ct);
            link.ErpOrderNumber = number;
            link.LastSyncedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return new ErpOrderResult(order, ErpSyncOutcome.Created, number, null);
        }
        catch (Exception ex)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogError(ex, "ERP order create failed for webshop order {Order}.", woo.OrderId);
            return new ErpOrderResult(order, ErpSyncOutcome.Failed, null, ex.Message);
        }
    }
}
