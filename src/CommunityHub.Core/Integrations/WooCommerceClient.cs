using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>A WooCommerce order line item, as the hub needs it.</summary>
public sealed record WooLineItem(
    long ProductId,
    string ProductName,
    string CategoriesText);

/// <summary>A WooCommerce order, flattened to what the sponsor pipeline uses.</summary>
public sealed record WooOrder(
    long OrderId,
    string Status,
    string BillingEmail,
    string BillingCompany,
    string? CompanyId,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<WooLineItem> LineItems);

/// <summary>WooCommerce REST settings. Keys come from Key Vault.</summary>
public sealed class WooCommerceOptions
{
    public const string SectionName = "WooCommerce";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;   // e.g. https://shop.expertslive.dk
    public string ConsumerKey { get; set; } = string.Empty;
    public string ConsumerSecret { get; set; } = string.Empty;

    /// <summary>
    /// ISO date floor (YYYY-MM-DD or full ISO 8601) passed to the WooCommerce
    /// REST API as <c>?after=</c>. Only orders created on or after this date
    /// are returned, so legacy / previous-edition orders never reach the
    /// classifier. Empty / null = no floor (NOT recommended for live use).
    /// Sourced from <c>integrations.&lt;edition&gt;.json -&gt; woocommerce.ordersAfter</c>.
    /// </summary>
    public string OrdersAfter { get; set; } = string.Empty;

    /// <summary>
    /// ISO date ceiling (YYYY-MM-DD or full ISO 8601) passed to the
    /// WooCommerce REST API as <c>?before=</c>. STRICTLY-before semantics:
    /// "2027-02-09" returns orders up to and including 2027-02-08. Set to
    /// the day of the event so post-event orders don't sneak into the pull.
    /// Empty / null = no ceiling.
    /// Sourced from <c>integrations.&lt;edition&gt;.json -&gt; woocommerce.ordersBefore</c>.
    /// </summary>
    public string OrdersBefore { get; set; } = string.Empty;
}

/// <summary>
/// Read-only WooCommerce REST client (CONTEXT.md section 9 / 11). Pulls
/// completed orders for the sponsor pipeline + enriches each line item with
/// its product's category names (orders themselves carry no categories, so
/// a second products-endpoint pass is required for the classifier to match
/// any non-booth product such as a Branded Feature / Founding Partner).
/// The REST key must be Read-only. Credentials are injected from
/// Key Vault-backed config.
/// </summary>
public sealed class WooCommerceClient
{
    // WooCommerce REST caps include= filters at 100 ids per request.
    private const int ProductsBatchSize = 100;

    private readonly HttpClient _http;
    private readonly WooCommerceOptions _options;

    public WooCommerceClient(HttpClient http, WooCommerceOptions options)
    {
        _http = http;
        _options = options;

        // WooCommerce REST: HTTP Basic with consumer key/secret.
        var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes(
            $"{_options.ConsumerKey}:{_options.ConsumerSecret}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);

        // Identify ourselves with a real UA. Wordfence / generic WAFs in front
        // of WordPress shops return HTTP 455 to requests bearing the default
        // .NET "Microsoft-HttpClient" string (or no UA at all), even when the
        // REST credentials are valid; a recognisable UA passes the same rule
        // set that curl / browsers pass.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CommunityHub/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Fetch all orders with the given status (default "completed"), following
    /// pagination, then enrich every line item with its product's categories
    /// (one batched products call per 100 distinct product ids).
    /// </summary>
    public async Task<IReadOnlyList<WooOrder>> GetOrdersAsync(
        string status = "completed",
        CancellationToken ct = default)
    {
        var orders = new List<WooOrder>();
        var page = 1;
        const int perPage = 100;

        // Build the static portion of the query (status + the optional
        // window from WooCommerceOptions.OrdersAfter / .OrdersBefore). The
        // WooCommerce REST API accepts ?after= / ?before= as ISO 8601 GMT;
        // a bare date is widened to T00:00:00 so the strictly-before
        // semantics align with the day of the event.
        var staticQuery = $"?status={Uri.EscapeDataString(status)}";
        var afterIso  = NormaliseIsoDate(_options.OrdersAfter);
        var beforeIso = NormaliseIsoDate(_options.OrdersBefore);
        if (!string.IsNullOrEmpty(afterIso))  staticQuery += $"&after={Uri.EscapeDataString(afterIso)}";
        if (!string.IsNullOrEmpty(beforeIso)) staticQuery += $"&before={Uri.EscapeDataString(beforeIso)}";

        while (true)
        {
            var url =
                $"{_options.BaseUrl.TrimEnd('/')}/wp-json/wc/v3/orders" +
                $"{staticQuery}" +
                $"&per_page={perPage}&page={page}";

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);

            var batch = doc.RootElement;
            if (batch.ValueKind != JsonValueKind.Array
                || batch.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var order in batch.EnumerateArray())
            {
                orders.Add(ParseOrder(order));
            }

            if (batch.GetArrayLength() < perPage)
            {
                break; // last page
            }
            page++;
        }

        // Enrich line items with product categories. Without this, the
        // sponsor classifier only matches booth products by name regex
        // ("Booth E-NN") and every Branded Feature / Session / Pre-day /
        // Addon product silently classifies as Addon with no tasks.
        var distinctProductIds = orders
            .SelectMany(o => o.LineItems)
            .Select(li => li.ProductId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (distinctProductIds.Count == 0)
        {
            return orders;
        }

        var categoriesByProduct =
            await GetProductCategoriesAsync(distinctProductIds, ct);

        for (var i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            var enrichedLines = new List<WooLineItem>(order.LineItems.Count);
            foreach (var li in order.LineItems)
            {
                enrichedLines.Add(categoriesByProduct.TryGetValue(li.ProductId, out var cats)
                    ? li with { CategoriesText = cats }
                    : li);
            }
            orders[i] = order with { LineItems = enrichedLines };
        }

        return orders;
    }

    /// <summary>
    /// Fetch the comma-joined category names for each product id. Batches
    /// up to 100 ids per request (WooCommerce <c>include</c> limit) and
    /// returns a map keyed by product id; ids the shop doesn't return are
    /// simply absent.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> GetProductCategoriesAsync(
        IEnumerable<long> productIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        var ids = productIds.Where(id => id > 0).Distinct().ToList();

        for (var start = 0; start < ids.Count; start += ProductsBatchSize)
        {
            var batch = ids.Skip(start).Take(ProductsBatchSize).ToList();
            var includeCsv = string.Join(",", batch);

            var url =
                $"{_options.BaseUrl.TrimEnd('/')}/wp-json/wc/v3/products" +
                $"?include={Uri.EscapeDataString(includeCsv)}" +
                $"&per_page={ProductsBatchSize}";

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var product in doc.RootElement.EnumerateArray())
            {
                var id = GetLong(product, "id");
                if (id <= 0) continue;

                var names = new List<string>();
                if (product.TryGetProperty("categories", out var cats)
                    && cats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cat in cats.EnumerateArray())
                    {
                        var name = GetString(cat, "name");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            // WooCommerce REST returns category names HTML-encoded
                            // ("Community &amp; Appreciation", "Hospitality &amp;
                            // Comfort"). Decode so the classifier's plain-text
                            // "Community & Appreciation" config strings match.
                            names.Add(WebUtility.HtmlDecode(name));
                        }
                    }
                }
                result[id] = string.Join(", ", names);
            }
        }

        return result;
    }

    private static WooOrder ParseOrder(JsonElement order)
    {
        var billing = order.TryGetProperty("billing", out var b)
            ? b : default;

        var lineItems = new List<WooLineItem>();
        if (order.TryGetProperty("line_items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                lineItems.Add(new WooLineItem(
                    ProductId: GetLong(item, "product_id"),
                    ProductName: GetString(item, "name"),
                    // Categories are not in the order payload; enriched later
                    // in GetOrdersAsync via the products endpoint.
                    CategoriesText: string.Empty));
            }
        }

        DateTimeOffset? created = null;
        if (order.TryGetProperty("date_created_gmt", out var dc)
            && dc.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dc.GetString(), out var parsed))
        {
            created = parsed;
        }

        // The Company Manager plugin stores the company id on the order as a
        // meta_data entry keyed "_cm_company_id" (CONTEXT.md 11g).
        string? companyId = null;
        if (order.TryGetProperty("meta_data", out var meta)
            && meta.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in meta.EnumerateArray())
            {
                if (GetString(m, "key") == "_cm_company_id")
                {
                    // The value may be a string or a number.
                    if (m.TryGetProperty("value", out var v))
                    {
                        companyId = v.ValueKind == JsonValueKind.String
                            ? v.GetString()
                            : v.ToString();
                    }
                    break;
                }
            }
        }

        return new WooOrder(
            OrderId: GetLong(order, "id"),
            Status: GetString(order, "status"),
            BillingEmail: billing.ValueKind == JsonValueKind.Object
                ? GetString(billing, "email") : string.Empty,
            BillingCompany: billing.ValueKind == JsonValueKind.Object
                ? GetString(billing, "company") : string.Empty,
            CompanyId: string.IsNullOrWhiteSpace(companyId) ? null : companyId,
            CreatedAt: created,
            LineItems: lineItems);
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static long GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// Widen a bare date (<c>YYYY-MM-DD</c>) to a full ISO 8601 timestamp
    /// (<c>YYYY-MM-DDT00:00:00</c>) so the WooCommerce REST
    /// <c>?after=</c> / <c>?before=</c> filter compares deterministically.
    /// A value that already contains <c>T</c> passes through unchanged.
    /// Blank input -&gt; blank output (caller skips the query param).
    /// </summary>
    private static string NormaliseIsoDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Contains('T') ? trimmed : $"{trimmed}T00:00:00";
    }
}
