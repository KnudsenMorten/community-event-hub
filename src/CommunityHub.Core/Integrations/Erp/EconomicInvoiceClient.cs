using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>The e-conomic customer facts a draft invoice is built from.</summary>
/// <param name="LayoutNumber">
/// 🔴 §811(d) — the customer's OWN invoice layout, and the answer to "how is the language chosen".
/// Measured 2026-08-04: Patch My PC (USA) carries <c>layout 23 = English</c>; 2linkIT (Denmark)
/// carries none. The retired script's invoices follow it exactly — the US customers got English —
/// which is why its output looks right and CEH's did not.
/// </param>
public sealed record EconomicCustomerDetail(
    int CustomerNumber,
    string Name,
    string? Address,
    string? Zip,
    string? City,
    string? Country,
    string Currency,
    int VatZoneNumber,
    string? VatZoneSelf,
    int PaymentTermsNumber,
    string? PaymentTermsSelf,
    string? Self,
    string? Ean,
    int? AttentionContactNumber,
    int? LayoutNumber = null,
    string? LayoutSelf = null,

    /// <summary>
    /// §1140 — the customer's CVR / VAT number, e-conomic's <c>corporateIdentificationNumber</c>.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ <b>The write side has always sent this field; only the READ was missing.</b>
    /// <c>EconomicErpClient</c> puts <c>corporateIdentificationNumber</c> into both its create and
    /// update payloads, so the value round-trips into the ERP perfectly — it simply never came back,
    /// which is why the webshop's "DATA OWNER: ERP" block could never show it.</para>
    ///
    /// <para>🔒 <b>Kept VERBATIM — no digits-only normalisation.</b> This is not necessarily a Danish
    /// CVR: the case that surfaced it was a DUTCH VAT number, <c>855963876B01</c>, which contains
    /// letters. Anything that stripped non-digits, or ran it through <c>CvrValidator</c> as a gate,
    /// would drop or reject a perfectly valid foreign VAT id.</para>
    /// </remarks>
    string? CorporateIdentificationNumber = null,

    /// <summary>§1140 — e-conomic's <c>telephoneAndFaxNumber</c>, the webshop's ERP-owned Phone.</summary>
    string? Phone = null);

/// <summary>One line on a draft invoice, in the shape e-conomic stores.</summary>
public sealed record EconomicInvoiceLine(
    int LineNumber,
    string Description,
    decimal? Quantity = null,
    decimal? UnitNetPrice = null,
    string? ProductNumber = null);

/// <summary>A complete draft invoice, ready to POST.</summary>
public sealed record EconomicDraftInvoice(
    EconomicCustomerDetail Customer,
    DateOnly Date,
    int LayoutNumber,
    string? LayoutSelf,
    string Currency,
    string OtherReference,
    int VendorEmployeeNumber,
    int? AttentionContactNumber,
    int? YourReferenceContactNumber,
    string Heading,
    string TextLine1,
    IReadOnlyList<EconomicInvoiceLine> Lines,
    /// <summary>
    /// §811(c) — the SECOND employee reference (<c>references.salesPerson</c>). Null omits it.
    /// </summary>
    int? SalesPersonEmployeeNumber = null);

/// <summary>
/// §795.4 — one invoice in e-conomic, as the coupon page needs it: which CEH reference it carries,
/// its number, and whether that number is real yet.
/// </summary>
/// <param name="Reference">The <c>references.other</c> marker CEH wrote when it created the invoice.</param>
/// <param name="Number">The invoice number — <c>bookedInvoiceNumber</c> or <c>draftInvoiceNumber</c>.</param>
/// <param name="IsBooked">
/// 🔴 <b>The difference that matters.</b> A DRAFT number is provisional and is replaced when a human
/// books the invoice, so it is not a number a partner will recognise. Quoting one as "the invoice
/// number" is how a partner ends up looking for an invoice that does not exist under that number —
/// and it is also the signal that there is still work to do (§795.3).
/// </param>
public sealed record EconomicInvoiceReference(string Reference, int Number, bool IsBooked)
{
    /// <summary>How it is written for a person: <c>invoice 20147 (booked)</c> / <c>draft 1042</c>.</summary>
    public string Display => IsBooked ? $"invoice {Number} (booked)" : $"draft {Number}";
}

/// <summary>
/// §786 — the DRAFT-INVOICE seam on e-conomic, kept apart from <see cref="IEconomicErpClient"/>
/// (customers/orders) and <see cref="IEconomicContactAdminClient"/> (contact CRUD) because it is a
/// different job with a different blast radius: everything here either reads invoices or creates one.
/// </summary>
public interface IEconomicInvoiceClient
{
    /// <summary>True only when the base URL and both tokens are configured.</summary>
    bool CanWrite { get; }

    /// <summary>
    /// Every <c>references.other</c> value across BOTH booked and draft invoices.
    /// 🔒 This is the idempotency interlock — see <see cref="WebshopDraftInvoiceService"/>.
    /// </summary>
    Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default);

    /// <summary>
    /// §795.4 — the same scan, but keeping the invoice NUMBER as well as the reference.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>No new integration and no extra API traffic:</b>
    /// <see cref="ListInvoicedOrderReferencesAsync"/> already pages both <c>/invoices/booked</c> and
    /// <c>/invoices/drafts</c> and reads <c>references.other</c> — it simply discarded everything
    /// else. This returns what that scan already saw, and the reference-only overload is derived
    /// from it so the two can never disagree about what is invoiced.
    /// </remarks>
    Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
        CancellationToken ct = default);

    /// <summary>One customer's invoicing facts, or null when the customer number is unknown.</summary>
    Task<EconomicCustomerDetail?> GetCustomerAsync(int customerNumber, CancellationToken ct = default);

    /// <summary>The layout whose name starts with <paramref name="nameLike"/> (e.g. "Dansk").</summary>
    Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(string nameLike, CancellationToken ct = default);

    /// <summary>Creates the draft; returns e-conomic's draftInvoiceNumber.</summary>
    Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default);
}

/// <summary>
/// The live client. 🔒 <b>The payload is ported 1:1 from the operator's proven script</b>
/// (<c>Sync-Webshop-Orders-Create-ERP-Invoice.ps1</c>), which has been creating these drafts hourly
/// in production — the same <c>references.other</c>, the same recipient/vatZone/paymentTerms shape,
/// the same layout lookup. This is deliberately NOT an improved payload: the migration is only
/// trustworthy if the invoice it produces is the invoice he already gets, plus the six changes he
/// asked for (§786.1).
/// </summary>
public sealed class LiveEconomicInvoiceClient : IEconomicInvoiceClient
{
    private readonly HttpClient _http;
    private readonly EconomicErpOptions _options;
    private readonly IExternalWriteGuard _writes;
    private readonly ILogger<LiveEconomicInvoiceClient> _log;

    public LiveEconomicInvoiceClient(
        HttpClient http,
        EconomicErpOptions options,
        ILogger<LiveEconomicInvoiceClient> log,
        IExternalWriteGuard? writes = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _writes = writes ?? new AllowAllExternalWrites();
    }

    public bool CanWrite =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.AppSecretToken)
        && !string.IsNullOrWhiteSpace(_options.AgreementGrantToken);

    private string Base => _options.ApiBaseUrl.TrimEnd('/');

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-AppSecretToken", _options.AppSecretToken);
        req.Headers.Add("X-AgreementGrantToken", _options.AgreementGrantToken);
        if (body is not null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return req;
    }

    private async Task<List<JsonElement>> PagedGetAsync(string url, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var next = url;
        while (!string.IsNullOrEmpty(next))
        {
            using var resp = await _http.SendAsync(Request(HttpMethod.Get, next), ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("collection", out var coll)
                && coll.ValueKind == JsonValueKind.Array)
            {
                all.AddRange(coll.EnumerateArray().Select(e => e.Clone()));
            }
            next = doc.RootElement.TryGetProperty("pagination", out var pag)
                   && pag.ValueKind == JsonValueKind.Object
                   && pag.TryGetProperty("nextPage", out var np)
                   && np.ValueKind == JsonValueKind.String
                ? np.GetString() : null;
        }
        return all;
    }

    public async Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default)
    {
        // 🔒 DERIVED from the numbered scan (§795.4) so the idempotency interlock and the numbers
        // shown on the page can never disagree about what is already invoiced. Same requests, same
        // rows — this only drops the numbers.
        var all = await ListInvoiceReferencesAsync(ct);
        var refs = new HashSet<string>(all.Select(r => r.Reference), StringComparer.Ordinal);

        _log.LogInformation("§786: {Count} existing invoice reference(s) read from e-conomic.", refs.Count);
        return refs;
    }

    public async Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
        CancellationToken ct = default)
    {
        if (!CanWrite) return Array.Empty<EconomicInvoiceReference>();

        var found = new List<EconomicInvoiceReference>();

        // BOTH lists, always. A draft that has since been BOOKED disappears from /drafts, so
        // scanning drafts alone would re-create an invoice the moment the operator books one —
        // the single most expensive way this job could fail.
        foreach (var (url, booked) in new[]
                 {
                     ($"{Base}/invoices/booked?pagesize=1000", true),
                     ($"{Base}/invoices/drafts?pagesize=1000", false),
                 })
        {
            foreach (var row in await PagedGetAsync(url, ct))
            {
                var value = row.TryGetProperty("references", out var r)
                            && r.ValueKind == JsonValueKind.Object
                            && r.TryGetProperty("other", out var other)
                            && other.ValueKind == JsonValueKind.String
                    ? other.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(value)) continue;

                // ⚠️ A booked invoice carries bookedInvoiceNumber, a draft carries
                // draftInvoiceNumber. They are DIFFERENT numbers for the same invoice, which is
                // exactly why EconomicInvoiceReference records which one this is.
                var number = Int(row, booked ? "bookedInvoiceNumber" : "draftInvoiceNumber") ?? 0;

                found.Add(new EconomicInvoiceReference(value!, number, booked));
            }
        }

        return found;
    }

    public async Task<EconomicCustomerDetail?> GetCustomerAsync(int customerNumber, CancellationToken ct = default)
    {
        if (!CanWrite) return null;

        using var resp = await _http.SendAsync(Request(HttpMethod.Get, $"{Base}/customers/{customerNumber}"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var c = doc.RootElement;

        return new EconomicCustomerDetail(
            CustomerNumber: customerNumber,
            Name: Str(c, "name") ?? string.Empty,
            Address: Str(c, "address"),
            Zip: Str(c, "zip"),
            City: Str(c, "city"),
            // The script defaults a blank country to DK; keep that, it is a real customer record.
            Country: Str(c, "country") ?? "DK",
            Currency: Str(c, "currency") ?? "EUR",
            VatZoneNumber: Nested(c, "vatZone", "vatZoneNumber") ?? 0,
            VatZoneSelf: NestedStr(c, "vatZone", "self"),
            PaymentTermsNumber: Nested(c, "paymentTerms", "paymentTermsNumber") ?? 0,
            PaymentTermsSelf: NestedStr(c, "paymentTerms", "self"),
            Self: Str(c, "self"),
            Ean: Str(c, "ean"),
            AttentionContactNumber: Nested(c, "attention", "customerContactNumber")
                                    ?? Nested(c, "customerContact", "customerContactNumber"),
            // §811(d) — the customer's own layout decides the invoice LANGUAGE. Null for a customer
            // that has none, which is when the configured fallback applies.
            LayoutNumber: Nested(c, "layout", "layoutNumber"),
            LayoutSelf: NestedStr(c, "layout", "self"),
            // §1140 — the ERP-owned identity fields the webshop's "DATA OWNER: ERP" block shows.
            // 🔒 Read VERBATIM: a CVR/VAT id may be foreign and alphanumeric (the Dutch
            // `855963876B01` that surfaced this), so nothing here strips or validates it.
            CorporateIdentificationNumber: Str(c, "corporateIdentificationNumber"),
            Phone: Str(c, "telephoneAndFaxNumber"));
    }

    public async Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(
        string nameLike, CancellationToken ct = default)
    {
        if (!CanWrite) return null;

        var wanted = (nameLike ?? string.Empty).Trim().TrimEnd('*');
        foreach (var row in await PagedGetAsync($"{Base}/layouts?pagesize=1000", ct))
        {
            var name = Str(row, "name");
            if (name is not null
                && name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                && row.TryGetProperty("layoutNumber", out var ln)
                && ln.TryGetInt32(out var number))
            {
                return (number, Str(row, "self"));
            }
        }
        return null;
    }

    public async Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default)
    {
        // 🔴 §1119 — INVOICES ARE CREATED IN PRODUCTION ONLY, AND THIS IS THE ONLY METHOD THAT
        // CREATES ONE.
        //
        // Operator 2026-08-21, having said it eight times: *"no erp create of invoice from dev. this
        // can only happend on prod"*.
        //
        // 🔑 Put HERE rather than at each caller because "here" is provably every caller: the coupon
        // sweep, the coupon prepaid button and the webshop sweep all reach e-conomic through this one
        // method, and so will the next one. A gate per caller is a gate somebody forgets.
        //
        // ⚠️ The DEV hole this closes was real and one app setting wide. The JOBS host swaps in
        // TestModeEconomicInvoiceClient when TestMode is on, so DEV's sweeps never reached e-conomic —
        // but the WEB host registers the LIVE client unconditionally (Program.cs §786), with real
        // tokens against the real agreement, so /Organizer/CouponInvoicing's "Create Invoice" button
        // was held back by nothing except `Invoicing:DryRun` defaulting to true on a host that never
        // sets it. A default is not a policy.
        //
        // 🔒 Reads stay live on DEV, deliberately — §1037: *"as a general rule, importing into ceh is
        // 100% fine - but sending data out from dev is controlled"*. Looking up a customer or listing
        // what is already invoiced changes nothing in e-conomic; creating a draft does.
        if (!await _writes.AllowAsync(
                ExternalSystems.ErpInvoiceCreate, nameof(CreateDraftInvoiceAsync), ct))
        {
            throw new InvalidOperationException(
                "e-conomic draft invoice refused: this host may not CREATE invoices "
                + "(Integrations:ExternalWrites:ErpInvoiceCreate, or the "
                + "Integrations:AllowExternalWrites default — §1119). Invoicing happens on PROD only.");
        }

        // §340-H — the same guard the other ERP writes use, and it THROWS for the same reason:
        // there is no meaningful "did nothing" return value for a create, and swallowing it would
        // let the caller believe an invoice exists.
        if (!await _writes.AllowAsync("e-conomic", nameof(CreateDraftInvoiceAsync), ct))
        {
            throw new InvalidOperationException(
                "e-conomic draft invoice refused: external writes are disabled for this host "
                + "(Integrations:AllowExternalWrites — §340-H).");
        }

        if (!CanWrite)
        {
            throw new InvalidOperationException("e-conomic is not configured (CanWrite is false).");
        }

        var body = BuildPayload(invoice);

        using var resp = await _http.SendAsync(
            Request(HttpMethod.Post, $"{Base}/invoices/drafts", body), ct);

        if (!resp.IsSuccessStatusCode)
        {
            // e-conomic returns a descriptive body on 4xx; without it the message is just "400".
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"e-conomic rejected the draft for '{invoice.OtherReference}' "
                + $"({(int)resp.StatusCode}): {detail}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var number = doc.RootElement.TryGetProperty("draftInvoiceNumber", out var dn) && dn.TryGetInt32(out var n)
            ? n : 0;

        _log.LogInformation(
            "§786: created e-conomic draft {Draft} for {Reference}.", number, invoice.OtherReference);

        return number;
    }

    /// <summary>
    /// The POST body. Kept as one visible object rather than spread across builders, because this
    /// shape is the contract with e-conomic and the thing a reader will want to compare against the
    /// retired script line by line.
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(EconomicDraftInvoice invoice)
    {
        var c = invoice.Customer;

        var recipient = new Dictionary<string, object?>
        {
            ["name"] = c.Name,
            ["address"] = c.Address,
            ["zip"] = c.Zip,
            ["city"] = c.City,
            ["country"] = string.IsNullOrWhiteSpace(c.Country) ? "DK" : c.Country,
            ["vatZone"] = new Dictionary<string, object?>
            {
                ["vatZoneNumber"] = c.VatZoneNumber,
                ["self"] = c.VatZoneSelf,
            },
        };

        // §786.1(a) — "Att person must be Default Signer". The script used whatever contact the
        // CUSTOMER record happened to point at; the signer is now resolved from Company Manager and
        // passed in. Falling back to the customer's own attention keeps an invoice producible when
        // a company has no signer set, rather than failing the run over a missing default.
        var attention = invoice.AttentionContactNumber ?? c.AttentionContactNumber;
        if (attention is { } att)
        {
            recipient["attention"] = new Dictionary<string, object?> { ["customerContactNumber"] = att };
        }

        if (!string.IsNullOrWhiteSpace(c.Ean)) recipient["ean"] = c.Ean;

        // 🔴 §811(c) — BOTH employee references are populated. Operator 2026-08-04: *"ref is Morten
        // Waltorp Knudsen, ref 2 is Martin Byskov"* … *"both must be mentioned"* … *"or opporsite"*.
        //
        // e-conomic carries two independent employee slots on an invoice, and a probe against the
        // live API (created, read back, deleted) confirmed both are accepted and stored:
        //   references.vendorReference  → employee 3 (Morten Waltorp Knudsen)
        //   references.salesPerson      → employee 1 (Martin Byskov)
        //
        // ⚠️ Which one PRINTS as "ref" and which as "ref 2" is decided by the e-conomic layout, not
        // by the API — he said "or opposite" himself. If the printed invoice has them the wrong way
        // round, swap the two option values; nothing else needs to change.
        var references = new Dictionary<string, object?>
        {
            ["other"] = invoice.OtherReference,
            ["vendorReference"] = new Dictionary<string, object?>
            {
                ["employeeNumber"] = invoice.VendorEmployeeNumber,
            },
        };

        if (invoice.SalesPersonEmployeeNumber is { } salesPerson)
        {
            references["salesPerson"] = new Dictionary<string, object?>
            {
                ["employeeNumber"] = salesPerson,
            };
        }

        // §786.1(b) — "Your references must be Default Signer". Same resolution as (a); e-conomic
        // calls this field customerContact and prints it as "Your reference".
        var yourRef = invoice.YourReferenceContactNumber ?? c.AttentionContactNumber;
        if (yourRef is { } yr)
        {
            references["customerContact"] = new Dictionary<string, object?> { ["customerContactNumber"] = yr };
        }

        var lines = invoice.Lines.Select(l =>
        {
            var line = new Dictionary<string, object?>
            {
                ["lineNumber"] = l.LineNumber,
                ["sortKey"] = l.LineNumber,
                ["description"] = l.Description,
            };

            // The header line carries text only. Sending quantity 0 / price 0 with a product would
            // put a billable zero row on the invoice instead of a caption.
            if (l.Quantity is { } q) line["quantity"] = q;
            if (l.UnitNetPrice is { } p) line["unitNetPrice"] = p;
            if (!string.IsNullOrWhiteSpace(l.ProductNumber))
            {
                line["product"] = new Dictionary<string, object?> { ["productNumber"] = l.ProductNumber };
                line["unit"] = new Dictionary<string, object?>
                {
                    ["unitNumber"] = 1,
                    ["self"] = "https://restapi.e-conomic.com/units/1",
                };
            }

            return line;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["currency"] = invoice.Currency,
            ["customer"] = new Dictionary<string, object?>
            {
                ["customerNumber"] = c.CustomerNumber,
                ["self"] = c.Self,
            },
            ["date"] = invoice.Date.ToString("yyyy-MM-dd"),
            // 🔴 §811(d) — THE CUSTOMER'S OWN LAYOUT WINS, and that is how the invoice LANGUAGE is
            // decided. Measured: Patch My PC (USA) carries layout 23 = English and the retired
            // script's invoice for them IS English; 2linkIT carries none and gets the configured
            // fallback (Danish). CEH used to force the fallback on everybody, which would have sent
            // every foreign sponsor a Danish invoice.
            ["layout"] = new Dictionary<string, object?>
            {
                ["layoutNumber"] = c.LayoutNumber ?? invoice.LayoutNumber,
                ["self"] = c.LayoutNumber is not null ? c.LayoutSelf : invoice.LayoutSelf,
            },

            // 🔴 §811(a) — `paymentTerms` IS REQUIRED BY THE SCHEMA, so it MUST be sent. Measured
            // 2026-08-04 by omitting it against the live API:
            //
            //   400 E00500 "Required properties are missing from object: paymentTerms"
            //
            // ⚠️ I removed it first on the strength of *"you are overruling the default terms defined
            // on customer"* and that would have broken invoice creation completely — caught by
            // probing the API instead of trusting the fix. [[verify-dont-assume]], on my own change.
            //
            // 🔒 What "not overruling" means here: the value sent is **the customer's own**, read
            // from their e-conomic record on this very run (`GetCustomerAsync`). CEH never picks a
            // term itself and has no default of its own. If an invoice shows terms he does not want,
            // the customer record is where they live — change it there and the next invoice follows.
            ["paymentTerms"] = new Dictionary<string, object?>
            {
                ["paymentTermsNumber"] = c.PaymentTermsNumber,
                ["self"] = c.PaymentTermsSelf,
            },
            ["recipient"] = recipient,
            ["notes"] = new Dictionary<string, object?>
            {
                ["heading"] = invoice.Heading,
                ["textLine1"] = invoice.TextLine1,
            },
            ["references"] = references,
            ["lines"] = lines,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static int? Nested(JsonElement e, string obj, string name) =>
        e.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
        && o.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static string? NestedStr(JsonElement e, string obj, string name) =>
        e.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, name) : null;
}

/// <summary>
/// TESTMODE: records what WOULD be invoiced and creates nothing. Mirrors the TestMode ERP/exhibitor
/// clients — the flag decides, and a disabled environment must never reach e-conomic.
/// </summary>
public sealed class TestModeEconomicInvoiceClient : IEconomicInvoiceClient
{
    private readonly ILogger<TestModeEconomicInvoiceClient> _log;
    private int _next = 900000;

    public TestModeEconomicInvoiceClient(ILogger<TestModeEconomicInvoiceClient> log) => _log = log;

    public bool CanWrite => true;

    /// <summary>
    /// ⚠️ Empty, and that is the honest answer: TESTMODE cannot see e-conomic, so it does not know
    /// what is already invoiced. It therefore reports "nothing invoiced yet" — which makes the
    /// service produce its full WouldCreate list, exactly what a dry run is for. It must never be
    /// mistaken for a real idempotency check.
    /// </summary>
    public Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

    /// <summary>
    /// ⚠️ Empty for the same reason: TESTMODE cannot see e-conomic, so it knows no invoice numbers.
    /// The page then shows "not invoiced" rather than inventing a number that would be quoted to a
    /// partner (§795.4).
    /// </summary>
    public Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<EconomicInvoiceReference>>(
            Array.Empty<EconomicInvoiceReference>());

    public Task<EconomicCustomerDetail?> GetCustomerAsync(int customerNumber, CancellationToken ct = default)
        => Task.FromResult<EconomicCustomerDetail?>(new EconomicCustomerDetail(
            customerNumber, $"TESTMODE customer {customerNumber}", "Testvej 1", "1000", "København",
            "DK", "EUR", 1, null, 1, null, null, null, null));

    public Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(
        string nameLike, CancellationToken ct = default)
        => Task.FromResult<(int, string?)?>((1, null));

    public Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default)
    {
        var number = Interlocked.Increment(ref _next);
        _log.LogInformation(
            "TESTMODE: WouldCreate e-conomic draft for {Reference} — {Lines} line(s), {Currency}. "
            + "Nothing was sent.", invoice.OtherReference, invoice.Lines.Count, invoice.Currency);
        return Task.FromResult(number);
    }
}
