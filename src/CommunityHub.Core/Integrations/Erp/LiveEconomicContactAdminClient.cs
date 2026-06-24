using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Live e-conomic REST implementation of <see cref="IEconomicContactAdminClient"/>.
/// Talks to <c>{ApiBaseUrl}/customers</c> + <c>/customers/{n}/contacts</c> with the
/// <c>X-AppSecretToken</c> + <c>X-AgreementGrantToken</c> headers from
/// <see cref="EconomicErpOptions"/> (KV-backed; never hard-coded). API shapes per
/// the e-conomic REST API (paged <c>collection</c> + <c>pagination.nextPage</c>),
/// matching the mgmt1 billing scripts. Read methods return empty when not
/// configured; writes throw so a mis-call can't silently no-op.
/// </summary>
public sealed class LiveEconomicContactAdminClient : IEconomicContactAdminClient
{
    private readonly HttpClient _http;
    private readonly EconomicErpOptions _options;
    private readonly ILogger<LiveEconomicContactAdminClient> _log;

    public LiveEconomicContactAdminClient(
        HttpClient http, EconomicErpOptions options,
        ILogger<LiveEconomicContactAdminClient> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool CanWrite =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.AppSecretToken)
        && !string.IsNullOrWhiteSpace(_options.AgreementGrantToken);

    private string Base => _options.ApiBaseUrl.TrimEnd('/');

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-AppSecretToken", _options.AppSecretToken);
        r.Headers.Add("X-AgreementGrantToken", _options.AgreementGrantToken);
        r.Headers.Add("Accept", "application/json");
        return r;
    }

    private void EnsureWritable()
    {
        if (!CanWrite)
            throw new InvalidOperationException(
                "e-conomic is not configured (EconomicErp tokens / base URL missing) — cannot reach the API.");
    }

    public async Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
        string? search, int? customerGroup = null, CancellationToken ct = default)
    {
        EnsureWritable();
        var rows = new List<EconomicCustomerRow>();
        // e-conomic pages a large collection; follow pagination.nextPage. Restrict to
        // a customer group server-side (group 1 = sponsors) when requested.
        var url = customerGroup is int g
            ? $"{Base}/customers?pagesize=1000&filter=customerGroup.customerGroupNumber$eq:{g}"
            : $"{Base}/customers?pagesize=1000";
        var safety = 0;
        while (url is not null && safety++ < 100)
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, url), ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("collection", out var col) && col.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in col.EnumerateArray())
                {
                    var num = GetInt(c, "customerNumber");
                    if (num == 0) continue;
                    rows.Add(new EconomicCustomerRow(num, GetStr(c, "name"), NullIfEmpty(GetStr(c, "email"))));
                }
            }
            url = NextPage(root);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            rows = rows.Where(r =>
                r.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                || r.CustomerNumber.ToString().Contains(s)
                || (r.Email?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
        return rows.OrderBy(r => r.Name).ToList();
    }

    public async Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
        int customerNumber, CancellationToken ct = default)
    {
        EnsureWritable();
        var rows = new List<EconomicContactRow>();
        using var resp = await _http.SendAsync(
            Req(HttpMethod.Get, $"{Base}/customers/{customerNumber}/contacts?pagesize=1000"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("collection", out var col) && col.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in col.EnumerateArray())
            {
                var num = GetInt(c, "customerContactNumber");
                if (num == 0) continue;
                rows.Add(new EconomicContactRow(
                    num, GetStr(c, "name"), NullIfEmpty(GetStr(c, "email")), NullIfEmpty(GetStr(c, "phone")),
                    NullIfEmpty(GetStr(c, "notes"))));
            }
        }
        return rows.OrderBy(r => r.Name).ToList();
    }

    public async Task<int> CreateContactAsync(
        int customerNumber, EconomicContactInput input, CancellationToken ct = default)
    {
        EnsureWritable();
        var body = new
        {
            customer = new { customerNumber },
            name = input.Name,
            email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email,
            phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone,
            notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes,
        };
        var req = Req(HttpMethod.Post, $"{Base}/customers/{customerNumber}/contacts");
        req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return GetInt(doc.RootElement, "customerContactNumber");
    }

    public async Task UpdateContactAsync(
        int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default)
    {
        EnsureWritable();
        var body = new
        {
            customer = new { customerNumber },
            customerContactNumber = contactNumber,
            name = input.Name,
            email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email,
            phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone,
            notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes,
        };
        var req = Req(HttpMethod.Put, $"{Base}/customers/{customerNumber}/contacts/{contactNumber}");
        req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteContactAsync(
        int customerNumber, int contactNumber, CancellationToken ct = default)
    {
        EnsureWritable();
        using var resp = await _http.SendAsync(
            Req(HttpMethod.Delete, $"{Base}/customers/{customerNumber}/contacts/{contactNumber}"), ct);
        resp.EnsureSuccessStatusCode();
    }

    // --- helpers -------------------------------------------------------------

    private static string? NextPage(JsonElement root) =>
        root.TryGetProperty("pagination", out var p)
        && p.TryGetProperty("nextPage", out var n)
        && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim() : string.Empty;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var i) ? i : 0;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
