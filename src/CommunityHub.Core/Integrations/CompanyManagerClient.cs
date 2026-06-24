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
    string ErpCustomerNumber = "",
    // Internal notes / special agreements (CM company "notes" field).
    string Notes = "");

/// <summary>One user linked to a Company Manager company.</summary>
public sealed record CompanyManagerUser(
    int UserId,
    string Email,
    string FullName,
    string DisplayName);

/// <summary>A resolved Company Manager person (from GET /users/{id}) with split name + phone.</summary>
public sealed record CompanyManagerUserDetail(
    int UserId,
    string FirstName,
    string LastName,
    string Email,
    string Phone);

/// <summary>Slim company row for the ERP→webshop reconcile (id + erp number + defaults).</summary>
public sealed record CompanyManagerCompanyRef(
    int Id, string ErpCustomerNumber, int DefaultSignerUserId,
    int EventCoordinationDefaultContactUserId, string Name);

/// <summary>The default event coordinator resolved for a company (contact for Zoho).</summary>
public sealed record CompanyManagerCoordinator(
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string CompanyName);

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
            ErpCustomerNumber: GetString(o, "erp_customer_number"),
            Notes: GetString(o, "notes"));
    }

    /// <summary>
    /// PATCH/PUT arbitrary fields onto a Company Manager company (e.g.
    /// <c>default_signer_id</c>, <c>event_coordination_default_contact_id</c>,
    /// <c>notes</c>, <c>company_name_public</c>, <c>web_address</c>,
    /// <c>linkedin_url</c>, <c>twitter_url</c>). Only the keys you pass are written.
    /// Returns whether the update succeeded.
    /// </summary>
    public async Task<bool> UpdateCompanyAsync(
        int companyId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/companies/{companyId}";
        // UTF-8 JSON so Danish æøå survive (WordPress is charset-sensitive).
        var json = JsonSerializer.Serialize(fields);
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(json, new UTF8Encoding(false), "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// GET all companies (paged), slim — id + erp_customer_number + the two default
    /// contact ids — for the ERP→webshop reconcile to map an ERP customer to its
    /// webshop company and check/set the default signer + event coordinator.
    /// </summary>
    public async Task<IReadOnlyList<CompanyManagerCompanyRef>> ListCompaniesAsync(CancellationToken ct = default)
    {
        var list = new List<CompanyManagerCompanyRef>();
        var page = 1;
        while (page < 100)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/companies?per_page=200&page={page}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) break;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), default, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) break;
            var n = 0;
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                n++;
                list.Add(new CompanyManagerCompanyRef(
                    Id: GetInt(c, "id"),
                    ErpCustomerNumber: GetString(c, "erp_customer_number"),
                    DefaultSignerUserId: GetInt(c, "default_signer_id"),
                    EventCoordinationDefaultContactUserId: GetInt(c, "event_coordination_default_contact_id"),
                    Name: GetString(c, "name")));
            }
            if (n < 200) break;
            page++;
        }
        return list;
    }

    /// <summary>
    /// POST a new Company Manager user (webshop contact). Returns the new user id
    /// (0 on failure). Links to <paramref name="companyId"/> when given.
    /// </summary>
    public async Task<int> CreateUserAsync(
        string email, string firstName, string lastName, int? companyId, CancellationToken ct = default)
    {
        var local = (email ?? string.Empty).Split('@')[0];
        var body = new Dictionary<string, object?>
        {
            ["username"] = string.IsNullOrWhiteSpace(local) ? email : local,
            ["email"] = email,
            ["first_name"] = firstName ?? string.Empty,
            ["last_name"] = lastName ?? string.Empty,
        };
        if (companyId is int cid) body["company_id"] = cid;

        var url = $"{_options.BaseUrl.TrimEnd('/')}/users";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), new UTF8Encoding(false), "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return 0;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), default, ct);
        return GetInt(doc.RootElement, "user_id") is var u && u > 0 ? u : GetInt(doc.RootElement, "id");
    }

    /// <summary>POST link an existing user to a company (companies/{id}/users {user_id}).</summary>
    public async Task<bool> LinkUserToCompanyAsync(int companyId, int userId, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/companies/{companyId}/users";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { user_id = userId }), new UTF8Encoding(false), "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
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

    /// <summary>
    /// GET /users/{id} — a single user's detail with SPLIT first/last name + phone
    /// (the /companies/{id}/users list only carries full_name + email). Used to
    /// resolve a company's default event coordinator into a Zoho contact. Returns
    /// null when the id doesn't exist.
    /// </summary>
    public async Task<CompanyManagerUserDetail?> GetUserAsync(int userId, CancellationToken ct = default)
    {
        if (userId <= 0) return null;
        var url = $"{_options.BaseUrl.TrimEnd('/')}/users/{userId}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        var o = doc.RootElement;
        return new CompanyManagerUserDetail(
            UserId: userId,
            FirstName: GetStringAny(o, "first_name", "firstname", "given_name"),
            LastName: GetStringAny(o, "last_name", "lastname", "surname", "family_name"),
            Email: GetStringAny(o, "email", "user_email", "email_address", "billing_email"),
            Phone: GetStringAny(o, "billing_phone", "phone", "phone_number", "telephone", "mobile"));
    }

    /// <summary>
    /// Resolve a company's DEFAULT EVENT COORDINATOR as a Zoho contact: company's
    /// event_coordination_default_contact_id (fallback default_signer_id) → GET
    /// /users/{id} → first/last/email/phone. Company-level billing_email/phone are
    /// used as a last-resort fallback. Returns null if nothing resolves.
    /// </summary>
    public async Task<CompanyManagerCoordinator?> GetDefaultCoordinatorAsync(
        int companyId, CancellationToken ct = default)
    {
        var company = await GetCompanyAsync(companyId, ct);
        if (company is null) return null;
        var companyName = !string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name;

        var coordId = company.EventCoordinationDefaultContactUserId > 0
            ? company.EventCoordinationDefaultContactUserId
            : company.DefaultSignerUserId;

        var user = coordId > 0 ? await GetUserAsync(coordId, ct) : null;
        var first = user?.FirstName ?? string.Empty;
        var last = user?.LastName ?? string.Empty;
        var email = user?.Email ?? string.Empty;
        var phone = user?.Phone ?? string.Empty;

        if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(last)
            && string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
            return null;   // nothing usable

        return new CompanyManagerCoordinator(first, last, email, phone, companyName);
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string GetStringAny(JsonElement e, params string[] props)
    {
        foreach (var p in props)
        {
            var s = GetString(e, p);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return string.Empty;
    }

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
