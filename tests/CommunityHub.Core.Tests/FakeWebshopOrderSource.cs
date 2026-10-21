using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — a stand-in for the stored old-webshop orders (<see cref="WebshopOrderArchiveSource"/>): returns the given
/// orders filtered by status exactly as the archive does (comma-separated, case-insensitive). With
/// <see cref="FromWooJson"/> the orders are read from the old webshop's REST shape, so a test that
/// already describes its orders as webshop JSON keeps doing so.
/// </summary>
internal sealed class FakeWebshopOrderSource : IWebshopOrderSource
{
    private readonly IReadOnlyList<WooOrder> _orders;

    public FakeWebshopOrderSource(params WooOrder[] orders) => _orders = orders;

    public FakeWebshopOrderSource(IEnumerable<WooOrder> orders) => _orders = orders.ToList();

    /// <summary>Statuses asked for, in order — lets a test see what a reader read.</summary>
    public List<string> Asked { get; } = new();

    /// <summary>When set, every read throws — the "could not check" path (a failed archive fill).</summary>
    public bool Fail { get; set; }

    public Task<IReadOnlyList<WooOrder>> GetOrdersAsync(string status = "completed", CancellationToken ct = default, bool enrichCategories = true)
    {
        Asked.Add(status);
        if (Fail) throw new InvalidOperationException("The old webshop's orders could not be stored in CEH yet; tried again on next use.");
        var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet();
        IReadOnlyList<WooOrder> hit = _orders.Where(o => statuses.Contains((o.Status ?? string.Empty).ToLowerInvariant())).ToList();
        return Task.FromResult(hit);
    }

    /// <summary>
    /// Orders described in the old webshop's REST JSON (one array, any statuses) — the shape the stored orders came from
    /// (§1312: the webshop client is gone, so this small reader keeps those tests' fixtures). Category names, when given in
    /// <paramref name="productsJson"/> (<c>[{ "id", "categories": [{ "name" }] }]</c>), are attached as they were captured.
    /// </summary>
    public static FakeWebshopOrderSource FromWooJson(string ordersJson, string? productsJson = null)
    {
        static string S(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
        static decimal D(System.Text.Json.JsonElement e, string n) =>
            decimal.TryParse(S(e, n), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
        static long L(System.Text.Json.JsonElement e, string n) => long.TryParse(S(e, n), out var l) ? l : 0;

        var cats = new Dictionary<long, string>();
        if (productsJson is not null)
            foreach (var p in System.Text.Json.JsonDocument.Parse(productsJson).RootElement.EnumerateArray())
                cats[L(p, "id")] = string.Join(", ", p.TryGetProperty("categories", out var cs) ? cs.EnumerateArray().Select(c => S(c, "name")) : Array.Empty<string>());

        var orders = new List<WooOrder>();
        foreach (var o in System.Text.Json.JsonDocument.Parse(ordersJson).RootElement.EnumerateArray())
        {
            var billing = o.TryGetProperty("billing", out var b) ? b : default;
            string? company = null;
            if (o.TryGetProperty("meta_data", out var meta))
                foreach (var m in meta.EnumerateArray())
                    if (S(m, "key") == "_cm_company_id") company = S(m, "value");
            var lines = o.TryGetProperty("line_items", out var items)
                ? items.EnumerateArray().Select(i => new WooLineItem(L(i, "product_id"), S(i, "name"), cats.GetValueOrDefault(L(i, "product_id"), ""),
                    Math.Max(1, (int)L(i, "quantity")), D(i, "price"), D(i, "subtotal"), D(i, "total"))).ToList()
                : new List<WooLineItem>();
            DateTimeOffset? created = DateTimeOffset.TryParse(S(o, "date_created_gmt"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
            var coupons = o.TryGetProperty("coupon_lines", out var cl) ? cl.EnumerateArray().Select(c => S(c, "code")).ToList() : null;
            orders.Add(new WooOrder(L(o, "id"), S(o, "status"),
                billing.ValueKind == System.Text.Json.JsonValueKind.Object ? S(billing, "email") : "",
                billing.ValueKind == System.Text.Json.JsonValueKind.Object ? S(billing, "company") : "",
                company, created, lines, coupons));
        }
        return new FakeWebshopOrderSource(orders);
    }
}
