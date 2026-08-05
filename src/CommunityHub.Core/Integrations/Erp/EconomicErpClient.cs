using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// e-conomic ERP + sponsor-webshop settings. Secret VALUES live in Key Vault;
/// this binds secret NAMES + non-secret endpoints only. With no app/grant token
/// configured the live client reports <see cref="EconomicErpClient.CanWrite"/>
/// = false and performs no writes.
/// </summary>
public sealed class EconomicErpOptions
{
    public const string SectionName = "EconomicErp";

    public bool Enabled { get; set; }

    /// <summary>e-conomic REST API base URL (non-secret; supplied by the operator, blank until wired).</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>e-conomic App-Secret-Token header value (resolved from Key Vault by name).</summary>
    public string AppSecretToken { get; set; } = string.Empty;

    /// <summary>e-conomic Agreement-Grant-Token header value (resolved from Key Vault by name).</summary>
    public string AgreementGrantToken { get; set; } = string.Empty;

    /// <summary>Sponsor webshop sync base URL (non-secret).</summary>
    public string WebshopBaseUrl { get; set; } = string.Empty;

    /// <summary>The "home" currency the ERP keeps its books in (ISO 4217). Used by the FX check.</summary>
    public string BaseCurrency { get; set; } = "DKK";

    /// <summary>§323: the agreement's customer-group number for hub-created customers
    /// (the legacy automation's default group 1).</summary>
    public int CustomerGroupNumber { get; set; } = 1;

    /// <summary>§323: the agreement's payment-terms number for hub-created customers
    /// (the legacy automation's default terms 1).</summary>
    public int PaymentTermsNumber { get; set; } = 1;

    // --- §786 draft-invoice settings ------------------------------------------------------------
    // Defaults are the values the retired script hard-coded, so an unconfigured environment produces
    // the invoice he already gets rather than a differently-shaped one.

    /// <summary>§786: the e-conomic invoice LAYOUT to use, matched on the start of its name.</summary>
    /// <remarks>
    /// 🔴 <b>"Dansk" was WRONG and would have blocked the first real invoice</b> (§809). Listed
    /// against the live e-conomic agreement 2026-08-04 — the layouts are <c>21 'Danish'</c>,
    /// <c>23 'English'</c>, <c>24 'Allerede betalt'</c>. Nothing starts with "Dansk", so
    /// <c>FindLayoutAsync</c> returned null and every draft was refused with *"no e-conomic layout
    /// matching 'Dansk' was found"*.
    ///
    /// <para>⚠️ It stayed invisible because the 11 invoices in PROD were created by the RETIRED
    /// PowerShell script, not by CEH — so CEH's own layout lookup had never once succeeded against
    /// real data. The first order it had to invoice by itself (10726) is what exposed it.</para>
    ///
    /// <para>🔑 §767's rule, again: <b>list what is actually there before matching on a name.</b>
    /// This default was inferred from the script rather than read from the API.</para>
    /// </remarks>
    public string InvoiceLayoutNameLike { get; set; } = "Danish";

    /// <summary>§786: the employee behind "Our reference" on the invoice.</summary>
    /// <remarks>
    /// 🔴 §811(c) — <b>employee 3 (Morten Waltorp Knudsen)</b>, his instruction 2026-08-04:
    /// *"our ref should be employee 3 - Morten Waltorp Knudsen"*.
    ///
    /// <para>⚠️ The port inherited <b>1</b> from the retired script, and the script's own invoices do
    /// carry employee 1 (Martin Byskov) — so this is a CHANGE he is making, not a defect being
    /// repaired. Recorded that way so nobody "restores" it to match the old invoices.</para>
    ///
    /// <para>Employees on the agreement: 1 Martin Byskov · 2 Kasper Sven Mozart Johansen ·
    /// 3 Morten Waltorp Knudsen · 4 Thomas Poppelgaard · 5 Kent Agerlund · 6 Heine Koldbro Madsen ·
    /// 7 Henrik F. Wojcik · 100 Morten Leth Hedegaard.</para>
    /// </remarks>
    public int InvoiceVendorEmployeeNumber { get; set; } = 3;

    /// <summary>
    /// §811(c) — the SECOND employee reference on the invoice (<c>references.salesPerson</c>):
    /// **employee 1, Martin Byskov**. Operator 2026-08-04: *"ref is Morten Waltorp Knudsen, ref 2 is
    /// Martin Byskov"* … *"both must be mentioned"*.
    /// </summary>
    /// <remarks>
    /// ⚠️ Which of the two prints as "ref" and which as "ref 2" is the e-conomic LAYOUT's decision,
    /// not the API's — he said *"or opporsite"* himself. If the printed invoice has them the wrong
    /// way round, swap this with <see cref="InvoiceVendorEmployeeNumber"/>; nothing else changes.
    /// 🔒 Null omits the field entirely, which is the pre-§811 behaviour.
    /// </remarks>
    public int? InvoiceSalesPersonEmployeeNumber { get; set; } = 1;

    /// <summary>§786: the heading printed on the draft invoice.</summary>
    /// <remarks>
    /// 🔴 <b>"Webshop Order" was wrong and reached a real invoice</b> (§811(b), operator 2026-08-04:
    /// *"the headline of the invoice is wrongly Webshop Order - It should state ELDK27 - Experts Live
    /// Denmark Sponsorship"*). Read off the retired script's own output: every invoice it made carries
    /// <c>notes.heading = "ELDK27 - Experts Live Denmark"</c> and
    /// <c>notes.textLine1 = "Sponsorship"</c> — two fields that print as one phrase.
    ///
    /// <para>⚠️ The value was invented when the port was written rather than read from an invoice the
    /// script had produced. §786's promise was *"the invoice it produces is the invoice he already
    /// gets"*, and this is the second place that promise was met by assumption (see also the layout
    /// name, §809.2).</para>
    /// </remarks>
    public string InvoiceHeading { get; set; } = "ELDK27 - Experts Live Denmark";

    /// <summary>
    /// §811(b) — the line under the heading. The script prints "Sponsorship" there; together the two
    /// read as *"ELDK27 - Experts Live Denmark Sponsorship"*, which is the phrase he quoted.
    /// </summary>
    public string InvoiceTextLine1 { get; set; } = "Sponsorship";
}

/// <summary>
/// TESTMODE implementation of <see cref="IEconomicErpClient"/>. Performs NO real
/// e-conomic / webshop calls. <see cref="CanWrite"/> is false, so the sync
/// services record <see cref="ErpSyncOutcome.WouldCreate"/>. Lets the whole
/// ERP/webshop flow be exercised offline against the interface.
/// </summary>
public sealed class TestModeEconomicErpClient : IEconomicErpClient
{
    private readonly ILogger<TestModeEconomicErpClient> _log;

    public TestModeEconomicErpClient(ILogger<TestModeEconomicErpClient> log) => _log = log;

    public bool CanWrite => false;

    public Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct)
    {
        _log.LogInformation("[TESTMODE] ERP FindCustomer for '{Company}' -> none.", customer.Name);
        return Task.FromResult<string?>(null);
    }

    public Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] CreateCustomerAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot create ERP customers (CanWrite is false).");
    }

    public Task UpdateCustomerAsync(string erpCustomerNumber, ErpCustomer customer, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] UpdateCustomerAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot update ERP customers (CanWrite is false).");
    }

    public Task CreateOrUpdateContactAsync(string erpCustomerNumber, ErpContact contact, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] CreateOrUpdateContactAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot write ERP contacts (CanWrite is false).");
    }

    public Task<string> CreateOrderAsync(string erpCustomerNumber, ErpOrder order, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] CreateOrderAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot create ERP orders (CanWrite is false).");
    }
}

/// <summary>
/// Live e-conomic ERP client (§323 — the payload contract is the operator's PROVEN
/// legacy automation, ported 1:1 from tools/legacy-automation: webhook-customer /
/// Create-ERP-Customer-via-Webhook.ps1). Auth = the two e-conomic headers
/// (X-AppSecretToken + X-AgreementGrantToken, Key-Vault-backed app settings);
/// customer DEDUPE is the legacy rule: paged GET /customers, NAME match
/// (trimmed, case-insensitive).
///
/// <para>ORDER→INVOICE is DELIBERATELY NOT here: the legacy Azure Function
/// (webhook-syncorders, DESIGN §18.2) turns completed webshop orders into
/// e-conomic DRAFT invoices live today (references.other = "WebshopOrderId-{n}",
/// EUR→customer-currency conversion, vatZone-derived product 1000/2000). Running
/// the same flow from CEH would double-invoice; <see cref="CreateOrderAsync"/>
/// therefore throws with a pointer at the owning webhook.</para>
/// </summary>
public sealed class LiveEconomicErpClient : IEconomicErpClient
{
    private readonly HttpClient _http;
    private readonly EconomicErpOptions _options;
    private readonly ILogger<LiveEconomicErpClient> _log;

    // §340-H: the environment-level external-write switch. Optional so existing
    // constructions/tests are unchanged (null ⇒ allow); DI supplies the real guard.
    private readonly IExternalWriteGuard _writes;

    public LiveEconomicErpClient(
        HttpClient http,
        EconomicErpOptions options,
        ILogger<LiveEconomicErpClient> log,
        IExternalWriteGuard? writes = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _writes = writes ?? new AllowAllExternalWrites();
    }

    /// <summary>
    /// §340-H — refuse the write when this host may not reach third-party systems.
    ///
    /// <para>THROWS rather than returning a soft failure, deliberately: it mirrors the
    /// <c>CanWrite</c> guard that is the first line of each method below, so "not configured"
    /// and "not permitted in this environment" fail identically and every caller already
    /// handles it. A silent no-op would be worse here than in the Zoho client — creating a
    /// customer has no meaningful "did nothing" return value, so swallowing it would let the
    /// caller record an ERP customer link that does not exist.</para>
    /// </summary>
    private async Task EnsureMayWriteAsync(string operation, CancellationToken ct)
    {
        if (!await _writes.AllowAsync("e-conomic", operation, ct))
        {
            throw new InvalidOperationException(
                $"e-conomic {operation} refused: external writes are disabled for this host "
                + "(Integrations:AllowExternalWrites — §340-H).");
        }
    }

    /// <summary>True once the e-conomic tokens + base URL are configured.</summary>
    public bool CanWrite =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.AppSecretToken)
        && !string.IsNullOrWhiteSpace(_options.AgreementGrantToken);

    private System.Net.Http.HttpRequestMessage Request(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-AppSecretToken", _options.AppSecretToken);
        req.Headers.Add("X-AgreementGrantToken", _options.AgreementGrantToken);
        if (body is not null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return req;
    }

    /// <summary>The legacy paged-GET helper: follows pagination.nextPage, concatenating collection[].</summary>
    private async Task<List<System.Text.Json.JsonElement>> PagedGetAsync(string url, CancellationToken ct)
    {
        var all = new List<System.Text.Json.JsonElement>();
        var next = url;
        while (!string.IsNullOrEmpty(next))
        {
            using var resp = await _http.SendAsync(Request(HttpMethod.Get, next), ct);
            resp.EnsureSuccessStatusCode();
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("collection", out var coll)
                && coll.ValueKind == System.Text.Json.JsonValueKind.Array)
                all.AddRange(coll.EnumerateArray().Select(e => e.Clone()));
            next = doc.RootElement.TryGetProperty("pagination", out var pag)
                   && pag.ValueKind == System.Text.Json.JsonValueKind.Object
                   && pag.TryGetProperty("nextPage", out var np)
                   && np.ValueKind == System.Text.Json.JsonValueKind.String
                ? np.GetString() : null;
        }
        return all;
    }

    public async Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct)
    {
        if (!CanWrite) return null;
        if (!string.IsNullOrWhiteSpace(customer.ErpCustomerNumber)) return customer.ErpCustomerNumber;

        // Legacy dedupe rule: full paged list, name match (trim, case-insensitive).
        var wanted = customer.Name.Trim();
        var rows = await PagedGetAsync($"{_options.ApiBaseUrl.TrimEnd('/')}/customers?pagesize=1000", ct);
        foreach (var row in rows)
        {
            if (row.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(n.GetString()?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                && row.TryGetProperty("customerNumber", out var num))
                return num.GetRawText().Trim('"');
        }
        return null;
    }

    public async Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct)
    {
        await EnsureMayWriteAsync(nameof(CreateCustomerAsync), ct);
        if (!CanWrite) throw new InvalidOperationException("e-conomic is not configured (CanWrite is false).");

        // The proven create payload (legacy webhook-customer): required name/currency/
        // customerGroup/paymentTerms/vatZone. Group + terms come from options (agreement
        // defaults); the vatZone number rides the Company Manager company (falls back to
        // the domestic zone 1).
        var vatZone = int.TryParse(customer.VatZone?.Trim(), out var vz) && vz is >= 1 and <= 4 ? vz : 1;
        var payload = new Dictionary<string, object?>
        {
            ["name"] = customer.Name.Trim(),
            ["currency"] = string.IsNullOrWhiteSpace(customer.Currency) ? _options.BaseCurrency : customer.Currency!.Trim().ToUpperInvariant(),
            ["customerGroup"] = new { customerGroupNumber = _options.CustomerGroupNumber },
            ["paymentTerms"] = new { paymentTermsNumber = _options.PaymentTermsNumber },
            ["vatZone"] = new { vatZoneNumber = vatZone },
        };
        if (!string.IsNullOrWhiteSpace(customer.Email)) payload["email"] = customer.Email!.Trim();
        if (!string.IsNullOrWhiteSpace(customer.CorporateIdentificationNumber))
            payload["corporateIdentificationNumber"] = customer.CorporateIdentificationNumber!.Trim();

        using var resp = await _http.SendAsync(
            Request(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/customers", payload), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"e-conomic customer create failed (HTTP {(int)resp.StatusCode}): {Clip(raw)}");
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var number = doc.RootElement.GetProperty("customerNumber").GetRawText().Trim('"');
        _log.LogInformation("e-conomic customer created: {Name} -> {Number}", customer.Name, number);
        return number;
    }

    public async Task UpdateCustomerAsync(string erpCustomerNumber, ErpCustomer customer, CancellationToken ct)
    {
        await EnsureMayWriteAsync(nameof(UpdateCustomerAsync), ct);
        if (!CanWrite) throw new InvalidOperationException("e-conomic is not configured (CanWrite is false).");

        // e-conomic PUT replaces the record — read-merge-write: fetch the current
        // object, overlay only the fields the hub owns, PUT it back.
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/customers/{Uri.EscapeDataString(erpCustomerNumber)}";
        using var getResp = await _http.SendAsync(Request(HttpMethod.Get, url), ct);
        getResp.EnsureSuccessStatusCode();
        var node = System.Text.Json.Nodes.JsonNode.Parse(await getResp.Content.ReadAsStringAsync(ct))!;
        node["name"] = customer.Name.Trim();
        if (!string.IsNullOrWhiteSpace(customer.Email)) node["email"] = customer.Email!.Trim();
        if (!string.IsNullOrWhiteSpace(customer.CorporateIdentificationNumber))
            node["corporateIdentificationNumber"] = customer.CorporateIdentificationNumber!.Trim();

        using var putReq = Request(HttpMethod.Put, url);
        putReq.Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var putResp = await _http.SendAsync(putReq, ct);
        if (!putResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"e-conomic customer update failed (HTTP {(int)putResp.StatusCode}): {Clip(await putResp.Content.ReadAsStringAsync(ct))}");
    }

    public async Task CreateOrUpdateContactAsync(string erpCustomerNumber, ErpContact contact, CancellationToken ct)
    {
        await EnsureMayWriteAsync(nameof(CreateOrUpdateContactAsync), ct);
        if (!CanWrite) throw new InvalidOperationException("e-conomic is not configured (CanWrite is false).");

        // Dedupe by e-mail under the customer (the legacy webhook-contact rule); the
        // role rides the notes field (Role:1 signer / Role:2 event coordinator), the
        // §26g convention the reconcile + role-audience readers already parse.
        var baseUrl = $"{_options.ApiBaseUrl.TrimEnd('/')}/customers/{Uri.EscapeDataString(erpCustomerNumber)}/contacts";
        var existing = await PagedGetAsync($"{baseUrl}?pagesize=1000", ct);
        var match = existing.FirstOrDefault(
            e => e.TryGetProperty("email", out var em) && em.ValueKind == System.Text.Json.JsonValueKind.String
                 && string.Equals(em.GetString()?.Trim(), contact.Email.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            return;   // idempotent — the contact exists; roles/edits stay with the reconcile flow
        }

        var notes = contact.Role switch
        {
            ErpContactRole.Signer => "Role:1",
            ErpContactRole.EventCoordinator => "Role:2",
            _ => null,
        };
        var payload = new Dictionary<string, object?> { ["name"] = contact.FullName.Trim(), ["email"] = contact.Email.Trim() };
        if (notes is not null) payload["notes"] = notes;

        using var resp = await _http.SendAsync(Request(HttpMethod.Post, baseUrl, payload), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"e-conomic contact create failed (HTTP {(int)resp.StatusCode}): {Clip(await resp.Content.ReadAsStringAsync(ct))}");
        _log.LogInformation("e-conomic contact created under {Customer}: {Email} ({Role})",
            erpCustomerNumber, contact.Email, contact.Role);
    }

    public Task<string> CreateOrderAsync(string erpCustomerNumber, ErpOrder order, CancellationToken ct) =>
        throw new InvalidOperationException(
            "Order→invoice is OWNED by the legacy webshop webhook (webhook-syncorders, DESIGN §18.2) — "
            + "it creates the e-conomic DRAFT invoice (references.other = 'WebshopOrderId-{n}') live today. "
            + "Creating it from CEH as well would double-invoice; deliberately not implemented here (§323).");

    private static string Clip(string v) => v.Length <= 400 ? v : v[..400] + "…";
}
