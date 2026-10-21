using System.Text.Json;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1312 — the old webshop's orders, as every reader consumes them. The same method the WooCommerce client had, so a reader
/// switched from it keeps its logic unchanged.
/// </summary>
public interface IWebshopOrderSource
{
    /// <summary>The orders with the given status(es) — comma-separated, e.g. <c>"cancelled,refunded"</c>.</summary>
    /// <param name="enrichCategories">Ignored: the category names were captured when the orders were stored.</param>
    Task<IReadOnlyList<WooOrder>> GetOrdersAsync(string status = "completed", CancellationToken ct = default, bool enrichCategories = true);
}

/// <summary>
/// 🔴 §1312 — the old webshop's orders read from CEH's own <see cref="WebshopOrderArchive"/> (operator 2026-10-01: "store the
/// information in ceh about the orders/products that was bought using the old webshop, so we dont need to lookup against
/// woocommerce" · "old webshop is closed for new orders").
/// </summary>
/// <remarks>
/// The orders were stored once, completely, on 2026-10-01 (30 orders of the ELDK27 window: 25 completed, 5 cancelled, every
/// one with its category names captured — verified against the webshop). The webshop client is gone; this reads CEH only. An
/// edition without stored orders (the webshop closed before it) simply has none.
/// </remarks>
public sealed class WebshopOrderArchiveSource : IWebshopOrderSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CommunityHubDbContext _db;

    public WebshopOrderArchiveSource(CommunityHubDbContext db) => _db = db;

    public async Task<IReadOnlyList<WooOrder>> GetOrdersAsync(string status = "completed", CancellationToken ct = default, bool enrichCategories = true)
    {
        var eventId = await _db.Events.AsNoTracking().Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is not int ev) return Array.Empty<WooOrder>();

        var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToList();
        var rows = await _db.WebshopOrderArchives.AsNoTracking()
            .Where(a => a.EventId == ev && statuses.Contains(a.Status))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.OrderId)
            .Select(a => a.Json).ToListAsync(ct);
        return rows.Select(Read).ToList();
    }

    internal static string Write(WooOrder order) => JsonSerializer.Serialize(order, Json);
    internal static WooOrder Read(string json) => JsonSerializer.Deserialize<WooOrder>(json, Json)!;
}
