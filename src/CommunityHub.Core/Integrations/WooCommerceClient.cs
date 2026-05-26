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
}

/// <summary>
/// Read-only WooCommerce REST client (CONTEXT.md section 9 / 11). Pulls
/// completed orders for the sponsor pipeline. The REST key must be Read-only.
/// Credentials are injected from Key Vault-backed config.
/// </summary>
public sealed class WooCommerceClient
{
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
    }

    /// <summary>
    /// Fetch orders with the given status (default "completed"), following
    /// pagination. Returns them flattened to <see cref="WooOrder"/>.
    /// </summary>
    public async Task<IReadOnlyList<WooOrder>> GetOrdersAsync(
        string status = "completed",
        CancellationToken ct = default)
    {
        var orders = new List<WooOrder>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            var url =
                $"{_options.BaseUrl.TrimEnd('/')}/wp-json/wc/v3/orders" +
                $"?status={Uri.EscapeDataString(status)}" +
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

        return orders;
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
                    // Categories are not in the order payload; the classifier
                    // also accepts the product name. A later enrichment step
                    // can attach full category text per product id.
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
}
