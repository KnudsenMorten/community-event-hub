using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>A WooCommerce order line item, as the hub needs it.</summary>
/// <param name="Quantity">
/// §666 — HOW MANY of this product the line carries. The operator asked for the amount, not just
/// yes/no: <i>"did he buy TV (amount) or did he not"</i>, and a sponsor may have bought two on one
/// line. Defaulted to 1 so every existing construction site keeps its meaning (one line ⇒ one item)
/// rather than silently becoming zero.
/// </param>
/// <param name="UnitPrice">
/// §786 — the per-unit price WooCommerce charged, in the WEBSHOP's currency (EUR). The hub now
/// invoices these orders, and the retired PowerShell script read exactly this field.
/// <para>🔒 Defaulted to 0 so every existing construction site is unchanged. The sponsor task
/// pipeline reads this record for <i>"did he buy it"</i>, never for money, and must not start
/// depending on a price it never asked for. A zero therefore means NOT SUPPLIED — and the invoicing
/// service refuses such a line rather than sending a customer a 0.00 row.</para>
/// </param>
/// <param name="LineSubtotal">
/// §1017 — the line total BEFORE any coupon, i.e. list price × quantity. Compared against
/// <paramref name="LineTotal"/> to know whether this line was discounted at all.
/// </param>
/// <param name="LineTotal">
/// §1017 — the line total AFTER coupons. ✅ Live-verified on order 10841: subtotal 25000,
/// total 24400 for the `free3extratickets` coupon.
/// <para>🔒 <b>This, not <see cref="UnitPrice"/> × quantity, is the rounding-safe basis for an
/// amount.</b> WooCommerce derives `price` as total/quantity, so a discount that does not divide
/// evenly across a multi-unit line loses cents when re-multiplied.</para>
/// </param>
public sealed record WooLineItem(
    long ProductId,
    string ProductName,
    string CategoriesText,
    int Quantity = 1,
    decimal UnitPrice = 0m,
    decimal LineSubtotal = 0m,
    decimal LineTotal = 0m)
{
    /// <summary>§1017 — how much this line was reduced by coupons (0 when it was not).</summary>
    public decimal DiscountAmount =>
        LineSubtotal > 0m && LineTotal > 0m && LineSubtotal > LineTotal
            ? LineSubtotal - LineTotal
            : 0m;

    /// <summary>§1017 — true when a coupon actually reduced this line.</summary>
    public bool IsDiscounted => DiscountAmount > 0m;
}

/// <summary>A WooCommerce order, flattened to what the sponsor pipeline uses.</summary>
public sealed record WooOrder(
    long OrderId,
    string Status,
    string BillingEmail,
    string BillingCompany,
    string? CompanyId,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<WooLineItem> LineItems,
    /// <summary>
    /// §1017 — the coupon codes applied to this order. WooCommerce reports coupons per ORDER while
    /// the reduction lands per LINE, so this is the only place the CODE exists — and the code is
    /// the thing a sponsor asks about when the total is not the list price.
    /// </summary>
    IReadOnlyList<string>? CouponCodes = null);

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

    private readonly IExternalWriteGuard _writes;

    public WooCommerceClient(
        HttpClient http, WooCommerceOptions options, IExternalWriteGuard? writes = null)
    {
        _http = http;
        _options = options;
        // 🔴 §1041 — the environment policy that stops DEV writing to the LIVE webshop. This client
        // was read-only until §1165k added coupon creation, and `ExternalWriteCoverageTests` caught
        // it on the first run: an ungated write here is exactly how a DEV run once created 53
        // records in the live shop. Defaulting to allow-all matches the sibling clients; the real
        // guard is injected in both hosts.
        _writes = writes ?? new AllowAllExternalWrites();

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
    /// Fetch all orders with the given status (default "completed"; WooCommerce
    /// accepts a comma-separated list, e.g. "cancelled,refunded"), following
    /// pagination, then enrich every line item with its product's categories
    /// (one batched products call per 100 distinct product ids).
    /// <paramref name="enrichCategories"/> = false skips that second pass —
    /// used by the §253 G8d refund-visibility probe, which only needs order
    /// ids/status/company, not the classifier's category text.
    /// </summary>
    public async Task<IReadOnlyList<WooOrder>> GetOrdersAsync(
        string status = "completed",
        CancellationToken ct = default,
        bool enrichCategories = true)
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

        if (!enrichCategories || distinctProductIds.Count == 0)
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
    /// <summary>
    /// §1165 — one product as the swag catalogue needs it: what a sponsor sees, plus whether it can
    /// still be bought.
    /// </summary>
    /// <param name="TeaserHtml">
    /// The product's SHORT description. ⚠️ Raw HTML from WordPress — a renderer must treat it as
    /// untrusted markup, not paste it into a page.
    /// </param>
    /// <param name="InStock">
    /// 🔑 This is what makes "ordered ⇒ unavailable" free: the webshop already carries real stock,
    /// and the live session slots use exactly this to stop being sellable once bought.
    /// </param>
    /// <param name="StockQuantity">
    /// How many remain, when the shop tracks a count. Null when the product is stocked as a plain
    /// in/out flag — which is NOT the same as zero, and a caller must not render it as "0 left".
    /// </param>
    public sealed record WooCatalogProduct(
        long Id,
        string Name,
        string? Status,
        string? TeaserHtml,
        string? PriceText,
        string? ImageUrl,
        string? PermalinkUrl,
        bool InStock,
        int? StockQuantity,
        IReadOnlyList<string> Categories);

    /// <summary>
    /// §1165 — every product in the shop, with the fields the sponsor swag catalogue renders.
    /// </summary>
    /// <remarks>
    /// <para>Reads the SAME products endpoint the classifier already uses, so there is one place
    /// that knows how a product is shaped. Category names are HTML-decoded here for the same reason
    /// they are in <see cref="GetProductCategoriesAsync"/>: WooCommerce returns
    /// <c>Community &amp;amp; Appreciation</c>, and a caller matching on <c>&amp;</c> would miss it.</para>
    ///
    /// <para>⚠️ Returns EVERY product; the caller filters by category. Filtering server-side would
    /// need the category's numeric id, which is one more thing to pin and to get wrong after a
    /// rename — and the shop is a few hundred products, read on demand.</para>
    /// </remarks>
    public async Task<IReadOnlyList<WooCatalogProduct>> GetAllProductsAsync(CancellationToken ct = default)
    {
        var all = new List<WooCatalogProduct>();
        for (var page = 1; page <= 20; page++)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/wp-json/wc/v3/products"
                + $"?per_page={ProductsBatchSize}&page={page}";

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) break;

            var count = 0;
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                count++;
                var id = GetLong(p, "id");
                if (id <= 0) continue;

                var cats = new List<string>();
                if (p.TryGetProperty("categories", out var catArr) && catArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in catArr.EnumerateArray())
                    {
                        var n = GetString(c, "name");
                        if (!string.IsNullOrWhiteSpace(n)) cats.Add(WebUtility.HtmlDecode(n));
                    }
                }

                string? image = null;
                if (p.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var i in imgs.EnumerateArray())
                    {
                        var src = GetString(i, "src");
                        if (!string.IsNullOrWhiteSpace(src)) { image = src; break; }
                    }
                }

                // "instock" / "outofstock" / "onbackorder". ⚠️ Anything we do not recognise is
                // treated as NOT available: offering an item we cannot confirm is sellable is the
                // expensive direction of the two.
                var stockStatus = GetString(p, "stock_status");
                var inStock = string.Equals(stockStatus, "instock", StringComparison.OrdinalIgnoreCase);

                int? stockQty = null;
                if (p.TryGetProperty("stock_quantity", out var sq) && sq.ValueKind == JsonValueKind.Number
                    && sq.TryGetInt32(out var q))
                {
                    stockQty = q;
                }

                all.Add(new WooCatalogProduct(
                    Id: id,
                    Name: WebUtility.HtmlDecode(GetString(p, "name")) ?? string.Empty,
                    // 🔴 §1165n — WHAT STATE IS THIS PRODUCT IN? Measured on the live shop
                    // 2026-09-02: the products endpoint returns 44 DRAFT and 2 PRIVATE products
                    // alongside 54 published ones, because its default status filter is "any".
                    // Without this the catalogue would show sponsors half-finished products.
                    Status: NullIfBlank(GetString(p, "status")),
                    TeaserHtml: NullIfBlank(GetString(p, "short_description")),
                    PriceText: NullIfBlank(GetString(p, "price")),
                    ImageUrl: image,
                    PermalinkUrl: NullIfBlank(GetString(p, "permalink")),
                    InStock: inStock,
                    StockQuantity: stockQty,
                    Categories: cats));
            }

            if (count < ProductsBatchSize) break;
        }
        return all;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    /// <summary>The result of creating a coupon: its Woo id, or why it could not be created.</summary>
    public sealed record WooCouponResult(long? Id, string? Error)
    {
        public bool Ok => Id is not null;
    }

    /// <summary>
    /// §1165k — resolve a product-category NAME to its Woo id.
    /// </summary>
    /// <remarks>
    /// ⚠️ Needed because a coupon is restricted by category ID, while everything else in this
    /// codebase names categories by their TEXT — deliberately, because a name survives a category
    /// being recreated and an id does not. Resolving late keeps the config human-editable.
    /// <para>Returns null when the name matches nothing, which the caller must treat as "do not
    /// create the coupon" rather than as "no restriction needed" — an unrestricted credit is
    /// spendable on anything in the shop.</para>
    /// </remarks>
    public async Task<long?> FindProductCategoryIdAsync(string categoryName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return null;

        for (var page = 1; page <= 10; page++)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/wp-json/wc/v3/products/categories"
                + $"?per_page=100&page={page}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var count = 0;
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                count++;
                var name = WebUtility.HtmlDecode(GetString(c, "name"));
                if (string.Equals(name?.Trim(), categoryName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var id = GetLong(c, "id");
                    return id > 0 ? id : null;
                }
            }
            if (count < 100) break;
        }
        return null;
    }

    /// <summary>
    /// §1165k — create a fixed-value coupon a sponsor can spend on catalogue items.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-01: <i>"add option to provide a coupon/discount code i provide them
    /// with a euro value"</i> — and, asked who should create it, <b>CEH through the API</b>.</para>
    ///
    /// <para>🔑 <b>That choice is only worth making because of the RESTRICTIONS.</b> A coupon typed
    /// by hand is one forgotten checkbox away from being spendable twice, on anything, for ever, by
    /// whoever it was forwarded to. Setting them in code makes them right every time:</para>
    /// <list type="bullet">
    ///   <item><c>fixed_cart</c> + amount — a euro value, not a percentage.</item>
    ///   <item><c>usage_limit = 1</c> — spent once; without it a code is re-usable indefinitely.</item>
    ///   <item><c>product_categories</c> — spendable only on catalogue items, so a swag credit
    ///   cannot quietly pay for a booth.</item>
    ///   <item><c>date_expires</c> — a credit with no end is a liability with no end.</item>
    ///   <item><c>email_restrictions</c> — usable only by that sponsor's own contacts, so forwarding
    ///   the code does not spend our money.</item>
    ///   <item><c>individual_use</c> — not combinable with another discount, because two stacked
    ///   discounts on one order is not something anybody intended.</item>
    /// </list>
    ///
    /// <para>🔒 The SHOP still decides whether a discount applies at checkout. CEH can misreport a
    /// credit; it can never give money away.</para>
    /// </remarks>
    public async Task<WooCouponResult> CreateCouponAsync(
        string code,
        decimal amount,
        long? restrictToCategoryId,
        DateOnly? expiresOn,
        IReadOnlyCollection<string>? emailRestrictions,
        string? description,
        CancellationToken ct = default)
    {
        // 🔴 §1041 — ASK THE GUARD BEFORE WRITING. A coupon created from DEV would be a real,
        // spendable discount in the live shop.
        if (!await _writes.AllowAsync(ExternalSystems.Webshop, nameof(CreateCouponAsync), ct))
            return new(null, "External writes to the webshop are disabled in this environment.");

        if (string.IsNullOrWhiteSpace(code)) return new(null, "No coupon code was given.");
        if (amount <= 0) return new(null, "A coupon must be worth more than zero.");

        var payload = new Dictionary<string, object?>
        {
            ["code"] = code.Trim(),
            ["discount_type"] = "fixed_cart",
            ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["individual_use"] = true,
            ["usage_limit"] = 1,
            ["usage_limit_per_user"] = 1,
        };

        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        if (restrictToCategoryId is long catId) payload["product_categories"] = new[] { catId };
        if (expiresOn is DateOnly d) payload["date_expires"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (emailRestrictions is { Count: > 0 })
            payload["email_restrictions"] = emailRestrictions.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();

        var url = $"{_options.BaseUrl.TrimEnd('/')}/wp-json/wc/v3/coupons";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };

        using var resp = await _http.SendAsync(req, ct);
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }

        if (!resp.IsSuccessStatusCode)
        {
            if (body.Length > 400) body = body[..400];
            return new(null, $"HTTP {(int)resp.StatusCode} — {body}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var id = GetLong(doc.RootElement, "id");
            // ⚠️ A success status with no id is NOT a success. Recording a credit whose coupon may
            // not exist would promise a sponsor money the shop refuses at checkout.
            return id > 0 ? new(id, null) : new(null, "The webshop accepted the coupon but returned no id.");
        }
        catch (JsonException)
        {
            return new(null, "The webshop's response to the coupon create could not be read.");
        }
    }


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
                    CategoriesText: string.Empty,
                    // §666 — a missing/zero quantity falls back to 1. WooCommerce always sends
                    // "quantity", but a line that somehow lacks it still represents a purchase, and
                    // counting it as 0 would tell a sponsor they had booked nothing when they had.
                    Quantity: Math.Max(1, (int)GetLong(item, "quantity")),
                    // §786 — the UNIT price, which is what the retired invoicing script billed.
                    // ⚠️ Not "total": WooCommerce's `total` is the line total (unit × quantity), and
                    // billing that as a unit price would multiply a 2-item line by two a second
                    // time. `price` is per unit.
                    //
                    // ✅ §1017 — LIVE-VERIFIED 2026-08-09 that `price` is the DISCOUNTED unit price,
                    // so a coupon is already reflected in what CEH bills. Order 10841
                    // (coupon `free3extratickets`): line `subtotal` 25000, `total` 24400,
                    // `price` 24400 — i.e. price = total / quantity, AFTER the discount. Order 10831
                    // (no coupon, qty 3): price 200, subtotal 600, total 600. **The invoiced amount
                    // was never wrong.**
                    UnitPrice: GetDecimal(item, "price"),
                    // §1017 — the PRE-discount line total and the POST-discount line total, kept so
                    // the invoice can SHOW a discount rather than silently charging a lower number.
                    // 🔒 `LineTotal` is also the rounding-safe basis for the amount: `price` is
                    // derived as total/quantity, so a discount that does not divide evenly across a
                    // multi-unit line (total 1000 over qty 3 ⇒ price 333.33 ⇒ 999.99) loses cents
                    // when re-multiplied. Nothing bills from it yet — see §1017.
                    LineSubtotal: GetDecimal(item, "subtotal"),
                    LineTotal: GetDecimal(item, "total")));
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
            LineItems: lineItems,
            CouponCodes: ReadCouponCodes(order));
    }

    /// <summary>
    /// §1017 — the order's applied coupon codes. ✅ Live-verified on order 10841:
    /// <c>coupon_lines: [{ code: "free3extratickets", discount: "600" }]</c>. Empty when none.
    /// </summary>
    private static IReadOnlyList<string> ReadCouponCodes(JsonElement order)
    {
        if (!order.TryGetProperty("coupon_lines", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var codes = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var code = GetString(el, "code");
            if (!string.IsNullOrWhiteSpace(code)) codes.Add(code.Trim());
        }
        return codes;
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static long GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// §786 — a money field. ⚠️ WooCommerce is INCONSISTENT about these: prices come back as a JSON
    /// number on some endpoints and as a quoted string on others, so both are accepted. Parsed with
    /// the invariant culture, because "13500.50" read under a Danish culture would become 1350050.
    /// Anything unparseable is 0, which the invoicing service treats as "no price" and refuses.
    /// </summary>
    private static decimal GetDecimal(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return 0m;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(
                v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }

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
