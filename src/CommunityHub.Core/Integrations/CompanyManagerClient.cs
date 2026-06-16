using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>One Company Manager company (the per-company defaults the hub cares about).</summary>
public sealed record CompanyManagerCompany(
    int Id,
    string Name,
    string PublicName,
    string WebsiteUrl,
    string LinkedInUrl,
    string TwitterUrl,
    int DefaultSignerUserId,
    int EventCoordinationDefaultContactUserId,
    // --- ERP / billing fields (DESIGN §6; used by the e-conomic ERP sync) ----
    // company tax-id (CVR); currency + VAT zone; the existing e-conomic
    // customer number when the company is already in the ERP. All optional --
    // empty when the operator hasn't filled the Webshop Data fields.
    string CorporateIdentificationNumber = "",
    string Currency = "",
    string VatZone = "",
    string ErpCustomerNumber = "");

/// <summary>One user linked to a Company Manager company.</summary>
public sealed record CompanyManagerUser(
    int UserId,
    string Email,
    string FullName,
    string DisplayName);

/// <summary>Company Manager REST settings. Auth is WordPress Basic with an app-password.</summary>
public sealed class CompanyManagerOptions
{
    public const string SectionName = "CompanyManager";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;   // e.g. https://expertslive.dk/wp-json/company-manager/v1
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Read-only client for the Company Manager WordPress plugin
/// (CONTEXT.md section 11g + integrations.&lt;edition&gt;.json -&gt; companyManager).
/// Company Manager is the source of truth for sponsor companies, their linked
/// users (contacts), and the per-company default signer / event coordinator.
/// The hub uses this to create / refresh Participant rows for sponsor contacts
/// so they can PIN-log-in and see their company's tasks.
/// </summary>
public sealed class CompanyManagerClient
{
    private readonly HttpClient _http;
    private readonly CompanyManagerOptions _options;

    public CompanyManagerClient(HttpClient http, CompanyManagerOptions options)
    {
        _http = http;
        _options = options;

        // WordPress application password is HTTP Basic.
        var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes(
            $"{_options.Username}:{_options.Password}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);

        // Same WAF-friendly headers as the WooCommerce client: Wordfence on
        // expertslive.dk returns HTTP 455 to .NET's default User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CommunityHub/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// GET /companies/{id} -- the per-company defaults (signer + coordinator
    /// user ids). Returns null when the id doesn't exist.
    /// </summary>
    public async Task<CompanyManagerCompany?> GetCompanyAsync(
        int companyId, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/companies/{companyId}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        var o = doc.RootElement;
        return new CompanyManagerCompany(
            Id: GetInt(o, "id"),
            Name: GetString(o, "name"),
            // company_name_public is the SHORT public name used in
            // announcements / sponsor listings -- e.g. "2LINKIT" rather
            // than the legal "2linkIT ApS". Empty when the operator
            // hasn't filled the Webshop Data "Public Company Name"
            // field; callers fall back to Name in that case.
            PublicName: GetString(o, "company_name_public"),
            WebsiteUrl: GetString(o, "web_address"),
            LinkedInUrl: GetString(o, "linkedin_url"),
            TwitterUrl: GetString(o, "twitter_url"),
            DefaultSignerUserId: GetInt(o, "default_signer_id"),
            EventCoordinationDefaultContactUserId: GetInt(o, "event_coordination_default_contact_id"),
            CorporateIdentificationNumber: GetString(o, "corporate_identification_number"),
            Currency: GetString(o, "currency"),
            VatZone: GetString(o, "vat_zone"),
            ErpCustomerNumber: GetString(o, "erp_customer_number"));
    }

    /// <summary>
    /// GET /companies/{id}/users -- the slim list of users linked to this
    /// company. The list response carries email + display_name + full_name
    /// directly, so a per-user GET /users/{id} is NOT required.
    /// </summary>
    public async Task<IReadOnlyList<CompanyManagerUser>> GetCompanyUsersAsync(
        int companyId, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/companies/{companyId}/users";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Array.Empty<CompanyManagerUser>();
        }
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CompanyManagerUser>();
        }

        var list = new List<CompanyManagerUser>();
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            var email = GetString(u, "user_email");
            if (string.IsNullOrWhiteSpace(email)) continue; // skip rows with no login email

            list.Add(new CompanyManagerUser(
                UserId: GetInt(u, "user_id"),
                Email: email,
                FullName: GetString(u, "full_name"),
                DisplayName: GetString(u, "display_name")));
        }
        return list;
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Company Manager returns numeric ids as either JSON numbers OR strings
    /// (e.g. user_id is a string "68" in /companies/{id}/users but a number
    /// 68 in /users/{id}). Tolerate both.
    /// </summary>
    private static int GetInt(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }
}
