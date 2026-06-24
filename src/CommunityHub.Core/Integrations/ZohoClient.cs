using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>A Zoho Backstage ticket order, flattened.</summary>
public sealed record ZohoTicket(
    string Email,
    string FirstName,
    string LastName,
    string TicketClassName,
    // The STABLE per-ticket id from Backstage — survives a company reassigning the
    // ticket to a different person (same id, new name/email). The Master Class link
    // keys on this so a reassignment transfers the selection instead of orphaning it.
    string TicketId = "");

/// <summary>
/// A fully-enriched Backstage attendee: the ticket (stable id), contact details +
/// ALL custom fields, and the order's company/country/tax. One row per ticket.
/// </summary>
public sealed record BackstageAttendee(
    string TicketId,
    string OrderId,
    string Email,
    string FirstName,
    string LastName,
    string TicketClassName,
    bool Attending,
    string? CompanyName,
    string? JobTitle,
    string? Phone,
    string? Country,
    string? CountryCode,
    string? City,
    string? Postcode,
    string? TaxId,
    string? CustomFieldsJson);

/// <summary>A Zoho Bookings appointment, flattened.</summary>
public sealed record ZohoAppointment(
    string CustomerEmail,
    string CustomerName,
    string ServiceName,
    string Status,
    string? SummaryUrl);

/// <summary>Zoho integration settings (CONTEXT.md 9z). EU data centre.</summary>
public sealed class ZohoOptions
{
    public const string SectionName = "Zoho";

    public bool Enabled { get; set; }
    public string ApiDomain { get; set; } = "https://www.zohoapis.eu";
    public string TokenEndpoint { get; set; } = "https://accounts.zoho.eu/oauth/v2/token";
    public string BackstagePortalId { get; set; } = string.Empty;
    public string BackstageEventId { get; set; } = string.Empty;
    public string BookingServiceNameRegex { get; set; } = "(?i)master\\s*class";
    public string TwoDayTicketNameRegex { get; set; } = "(?i)\\b2-day\\b";
    public string MasterClassDate { get; set; } = "2027-02-09";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    // ---- Zoho CRM leads pull (sponsor leads pipeline) -------------------
    // Off by default: enabling requires the refresh token to carry the
    // ZohoCRM.modules.READ scope and the CRM to tag each record with the
    // sponsor company id (custom field named by CrmSponsorCompanyIdField).

    /// <summary>Master switch for the CRM lead pull. Default off.</summary>
    public bool CrmEnabled { get; set; }

    /// <summary>Comma-separated CRM modules to pull (Leads, Contacts, ...).</summary>
    public string CrmModules { get; set; } = "Leads";

    /// <summary>CRM field (API name) holding the sponsor company id each lead belongs to.</summary>
    public string CrmSponsorCompanyIdField { get; set; } = "Sponsor_Company_Id";
}

/// <summary>One Zoho CRM record, flattened for the sponsor-leads sync.</summary>
public sealed record ZohoCrmLead(
    string ZohoRecordId,
    string Module,
    string SponsorCompanyId,
    string FirstName,
    string LastName,
    string FullName,
    string Email,
    string Phone,
    string Company,
    string JobTitle,
    string City,
    string Country,
    string Source,
    string Notes,
    DateTimeOffset CreatedTime);

/// <summary>
/// Zoho client (CONTEXT.md 9z) - the C# port of the source PowerShell
/// reconciliation scripts. Refreshes an OAuth access token, fetches Backstage
/// ticket orders, and fetches Bookings Master Class appointments. Read-only.
///
/// The PowerShell scripts remain the behavioural specification: same EU OAuth
/// endpoint, same multipart fetchappointment call, same paging.
/// </summary>
public sealed class ZohoClient
{
    private readonly HttpClient _http;
    private readonly ZohoOptions _options;

    public ZohoClient(HttpClient http, ZohoOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>One Backstage exhibitor — its system id + company name (for matching by name).</summary>
    public sealed record BackstageExhibitor(string Id, string CompanyName);

    /// <summary>
    /// List every exhibitor in the configured Backstage event (id + company_name)
    /// so a sponsor can be matched by company name. Scope:
    /// <c>ZohoBackstage.exhibitor.READ</c>. Returns empty on auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageExhibitor>> GetExhibitorsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<BackstageExhibitor>();
        await foreach (var el in PageV3Async("exhibitors", "exhibitors", accessToken, ct))
        {
            var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = el.TryGetProperty("company_name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new BackstageExhibitor(id!, name!));
        }
        return list;
    }

    /// <summary>
    /// PUT updated profile fields onto a Backstage exhibitor. Only non-null
    /// fields are sent. Scope: <c>ZohoBackstage.exhibitor.UPDATE</c>. Returns
    /// whether the update succeeded.
    /// </summary>
    public async Task<bool> UpdateExhibitorAsync(
        string accessToken, string exhibitorId,
        string? companyOverview, string? companyShortDescription,
        CancellationToken ct = default,
        string? companyName = null, string? contactFirstName = null, string? contactLastName = null,
        string? websiteUrl = null, string? linkedInUrl = null, string? twitterUrl = null,
        string? contactEmail = null, string? contactMobile = null)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}";
        var payload = new Dictionary<string, object?>();
        if (companyOverview is not null) payload["company_overview"] = companyOverview;
        if (companyShortDescription is not null) payload["company_short_description"] = companyShortDescription;
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        // company_social_pages is an object keyed by platform (linkedin / twitter / facebook).
        if (!string.IsNullOrWhiteSpace(linkedInUrl) || !string.IsNullOrWhiteSpace(twitterUrl))
        {
            var social = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(linkedInUrl)) social["linkedin"] = linkedInUrl;
            if (!string.IsNullOrWhiteSpace(twitterUrl)) social["twitter"] = twitterUrl;
            payload["company_social_pages"] = social;
        }
        // Re-push the correct UTF-8 company + contact name so Zoho's mojibake
        // (æøåÆØÅ shown as "?") is overwritten. JsonContent serializes UTF-8.
        if (!string.IsNullOrWhiteSpace(companyName)) payload["company_name"] = companyName;
        if (!string.IsNullOrWhiteSpace(contactFirstName) || !string.IsNullOrWhiteSpace(contactLastName)
            || !string.IsNullOrWhiteSpace(contactEmail) || !string.IsNullOrWhiteSpace(contactMobile))
        {
            var contact = new Dictionary<string, object?>
            {
                ["first_name"] = contactFirstName ?? string.Empty,
                ["last_name"] = contactLastName ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(contactEmail)) contact["email"] = contactEmail;
            if (!string.IsNullOrWhiteSpace(contactMobile)) contact["mobile_no"] = contactMobile;
            payload["contact"] = contact;
        }

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>One Backstage sponsor — its system id + company name (for matching by name).</summary>
    public sealed record BackstageSponsor(string Id, string CompanyName);

    /// <summary>
    /// List every sponsor in the configured Backstage event (id + company_name) so a
    /// CEH sponsor can be matched by company name. Scope: <c>ZohoBackstage.sponsor.READ</c>.
    /// Returns empty on auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageSponsor>> GetSponsorsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<BackstageSponsor>();
        await foreach (var el in PageV3Async("sponsors", "sponsors", accessToken, ct))
        {
            var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = el.TryGetProperty("company_name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new BackstageSponsor(id!, name!));
        }
        return list;
    }

    /// <summary>
    /// PUT updated profile fields onto a Backstage SPONSOR record. Only non-null
    /// fields are sent. Scope: <c>ZohoBackstage.sponsor.UPDATE</c>. Returns whether
    /// the update succeeded.
    /// </summary>
    public async Task<bool> UpdateSponsorAsync(
        string accessToken, string sponsorId,
        string? description, string? websiteUrl, string? companyName,
        CancellationToken ct = default,
        string? contactFirstName = null, string? contactLastName = null, string? contactEmail = null)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsors/{sponsorId}";
        var payload = new Dictionary<string, object?>();
        if (description is not null) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        if (!string.IsNullOrWhiteSpace(companyName)) payload["company_name"] = companyName;
        // Sponsor contact = first/last/email only (Zoho rejects phone on a sponsor).
        if (!string.IsNullOrWhiteSpace(contactFirstName) || !string.IsNullOrWhiteSpace(contactLastName)
            || !string.IsNullOrWhiteSpace(contactEmail))
        {
            var contact = new Dictionary<string, object?>
            {
                ["first_name"] = contactFirstName ?? string.Empty,
                ["last_name"] = contactLastName ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(contactEmail)) contact["email"] = contactEmail;
            payload["contact"] = contact;
        }

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>One Zoho sponsorship type / sponsor category (id + name).</summary>
    public sealed record BackstageSponsorshipType(string Id, string Name);

    /// <summary>
    /// GET the event's sponsorship types (sponsor categories) — name → id, used to
    /// set <c>sponsorship_type</c> when creating a sponsor. Small finite set, no paging.
    /// </summary>
    public async Task<IReadOnlyList<BackstageSponsorshipType>> GetSponsorshipTypesAsync(
        string accessToken, CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsorship_types";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<BackstageSponsorshipType>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<BackstageSponsorshipType>();
        foreach (var prop in new[] { "sponsorship_types", "sponsor_categories", "data" })
        {
            if (!doc.RootElement.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var el in arr.EnumerateArray())
            {
                // Field names vary across Zoho events — try the same candidates the
                // legacy script used (name/category_name/title, id/sponsorship_type_id/…).
                var id = FirstNonEmpty(
                    GetString(el, "id"), GetString(el, "sponsorship_type_id"),
                    GetString(el, "sponsor_category_id"), GetString(el, "category_id"));
                var name = FirstNonEmpty(
                    GetString(el, "name"), GetString(el, "category_name"),
                    GetString(el, "title"), GetString(el, "sponsorship_type_name"));
                if (id.Length > 0 && name.Length > 0)
                    list.Add(new BackstageSponsorshipType(id, name));
            }
            if (list.Count > 0) break;
        }
        return list;
    }

    /// <summary>
    /// POST create a sponsor. Returns the new sponsor id, or null on failure.
    /// Body: company_name, website_url, description, sponsorship_type (category id),
    /// contact{first_name,last_name,email}. (currency_code/language are server-set.)
    /// </summary>
    public async Task<string?> CreateSponsorAsync(
        string accessToken, string companyName, string? websiteUrl, string? description,
        string sponsorshipTypeId, string? contactFirstName, string? contactLastName, string? contactEmail,
        CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsors";
        var payload = new Dictionary<string, object?>
        {
            ["company_name"] = companyName,
            ["sponsorship_type"] = sponsorshipTypeId,
        };
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(contactFirstName) || !string.IsNullOrWhiteSpace(contactLastName)
            || !string.IsNullOrWhiteSpace(contactEmail))
        {
            var contact = new Dictionary<string, object?>
            {
                ["first_name"] = contactFirstName ?? string.Empty,
                ["last_name"] = contactLastName ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(contactEmail)) contact["email"] = contactEmail;
            payload["contact"] = contact;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            // The created sponsor may be at the root or nested under "sponsor".
            if (root.TryGetProperty("sponsor", out var sp) && sp.ValueKind == JsonValueKind.Object) root = sp;
            foreach (var key in new[] { "id", "sponsor_id" })
                if (root.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    return idEl.GetString();
        }
        catch { /* created but couldn't parse id — a later run links it by name */ }
        return string.Empty;   // non-null = created (id unknown), so caller doesn't retry-create
    }

    /// <summary>One exhibitor booth member as Zoho returns it.</summary>
    public sealed record BackstageBoothMember(
        string Email, string FirstName, string LastName, string Role);

    /// <summary>
    /// GET all booth members of an exhibitor. Response shape:
    /// <c>{ members: [ { role, status, contact: { first_name, last_name, email } } ] }</c>.
    /// Scope: <c>ZohoBackstage.exhibitor.READ</c>. Empty on failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageBoothMember>> GetBoothMembersAsync(
        string accessToken, string exhibitorId, CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}/members";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<BackstageBoothMember>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<BackstageBoothMember>();
        if (doc.RootElement.TryGetProperty("members", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var contact = m.TryGetProperty("contact", out var c) ? c : default;
                var email = Lower(GetString(contact, "email"));
                if (email.Length == 0) continue;
                list.Add(new BackstageBoothMember(
                    Email: email,
                    FirstName: GetString(contact, "first_name"),
                    LastName: GetString(contact, "last_name"),
                    Role: GetString(m, "role")));
            }
        }
        return list;
    }

    /// <summary>
    /// POST create booth members in bulk for an exhibitor.
    /// Body: <c>{ members: [ { role, first_name, last_name, email, company_name } ] }</c>
    /// (role = "ADMIN" | "staff"). Scope: <c>ZohoBackstage.exhibitor.CREATE</c>.
    /// </summary>
    public async Task<bool> CreateBoothMembersAsync(
        string accessToken, string exhibitorId,
        IReadOnlyList<(string FirstName, string LastName, string Email, string Role, string? CompanyName)> members,
        CancellationToken ct = default)
    {
        if (members.Count == 0) return true;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}/members";
        var payload = new Dictionary<string, object?>
        {
            ["members"] = members.Select(m => new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["first_name"] = m.FirstName,
                ["last_name"] = m.LastName,
                ["email"] = m.Email,
                ["company_name"] = m.CompanyName ?? string.Empty,
            }).ToList(),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Exchange the refresh token for a short-lived access token.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = _options.RefreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "refresh_token",
        });

        using var resp = await _http.PostAsync(_options.TokenEndpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        return doc.RootElement.TryGetProperty("access_token", out var t)
            ? t.GetString()
            : null;
    }

    /// <summary>
    /// Fetch all Backstage attendees (v3) — one enriched row per ticket: the stable
    /// ticket id, contact details + ALL custom fields, and the order's company /
    /// country / tax (joined by order id). The source of truth for the attendee +
    /// Master Class flow (keyed on ticket id).
    /// </summary>
    public async Task<IReadOnlyList<BackstageAttendee>> GetBackstageAttendeesAsync(
        string accessToken, CancellationToken ct = default)
    {
        // Known contact keys — everything else on the contact is a CUSTOM field.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "first_name", "last_name", "email", "company_name", "designation", "mobile_no" };

        // 1. Orders -> billing/company/country/tax by order id.
        var orderInfo = new Dictionary<string, (string? Company, string? Country, string? CountryCode, string? City, string? Postcode, string? Tax)>(StringComparer.Ordinal);
        await foreach (var order in PageV3Async("orders", "orders", accessToken, ct))
        {
            var id = GetString(order, "id");
            if (id.Length == 0) continue;
            var billing = order.TryGetProperty("billing_address", out var b) ? b : default;
            string? country = null, code = null;
            if (billing.ValueKind == JsonValueKind.Object && billing.TryGetProperty("country_data", out var cd) && cd.ValueKind == JsonValueKind.Object)
            { country = NullIf(GetString(cd, "display_name")); code = NullIf(GetString(cd, "code")); }
            var contact = order.TryGetProperty("contact", out var oc) ? oc : default;
            orderInfo[id] = (
                NullIf(GetString(billing, "name")),
                country ?? NullIf(GetString(billing, "country")),
                code,
                NullIf(GetString(billing, "city")),
                NullIf(GetString(billing, "zipcode")),
                NullIf(GetString(contact, "tax_registration_no")));
        }

        // 2. Attendees -> one enriched row per ticket.
        var list = new List<BackstageAttendee>();
        await foreach (var a in PageV3Async("attendees", "attendees", accessToken, ct))
        {
            var ticketId = FirstNonEmpty(GetString(a, "ticket_id"), GetString(a, "id"));
            if (ticketId.Length == 0) continue;
            var orderId = GetString(a, "order_id");
            var contact = a.TryGetProperty("contact", out var c) ? c : default;

            string? customJson = null;
            if (contact.ValueKind == JsonValueKind.Object)
            {
                var custom = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in contact.EnumerateObject())
                    if (!known.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                        custom[prop.Name] = prop.Value.GetString() ?? "";
                if (custom.Count > 0) customJson = JsonSerializer.Serialize(custom);
            }

            orderInfo.TryGetValue(orderId, out var oi);
            var statusStr = GetString(a, "status_string");
            list.Add(new BackstageAttendee(
                TicketId: ticketId,
                OrderId: orderId,
                Email: Lower(GetString(contact, "email")),
                FirstName: GetString(contact, "first_name"),
                LastName: GetString(contact, "last_name"),
                TicketClassName: FirstNonEmpty(GetString(a, "ticket_name"), GetString(contact, "ticket_name")),
                Attending: string.Equals(statusStr, "attending", StringComparison.OrdinalIgnoreCase),
                CompanyName: NullIf(GetString(contact, "company_name")) ?? oi.Company,
                JobTitle: NullIf(GetString(contact, "designation")),
                Phone: NullIf(GetString(contact, "mobile_no")),
                Country: oi.Country, CountryCode: oi.CountryCode, City: oi.City, Postcode: oi.Postcode,
                TaxId: oi.Tax,
                CustomFieldsJson: customJson));
        }
        return list;
    }

    /// <summary>Enumerate a paginated v3 Backstage collection, following pagination.nextPage.</summary>
    private async IAsyncEnumerable<JsonElement> PageV3Async(
        string resource, string arrayProp, string accessToken,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}/events/{_options.BackstageEventId}/{resource}";
        var safety = 0;
        while (url is not null && safety++ < 200)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) yield break;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty(arrayProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray()) yield return el.Clone();
            url = root.TryGetProperty("pagination", out var p)
                  && p.TryGetProperty("nextPage", out var n) && n.ValueKind == JsonValueKind.String
                  ? n.GetString() : null;
        }
    }

    private static string? NullIf(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Fetch all Backstage ticket orders, flattened to one row per ticket.
    /// </summary>
    public async Task<IReadOnlyList<ZohoTicket>> GetBackstageTicketsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var tickets = new List<ZohoTicket>();
        var page = 1;

        while (true)
        {
            var url =
                $"{_options.ApiDomain}/backstage/v1/portals/" +
                $"{_options.BackstagePortalId}/events/" +
                $"{_options.BackstageEventId}/orders?page={page}&per_page=100";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                break;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);

            if (!doc.RootElement.TryGetProperty("orders", out var orders)
                || orders.ValueKind != JsonValueKind.Array
                || orders.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var order in orders.EnumerateArray())
            {
                if (!order.TryGetProperty("tickets", out var ts)
                    || ts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var ticket in ts.EnumerateArray())
                {
                    var contact = ticket.TryGetProperty("contact", out var c)
                        ? c : default;
                    tickets.Add(new ZohoTicket(
                        Email: Lower(GetString(contact, "email")),
                        FirstName: GetString(contact, "first_name"),
                        LastName: GetString(contact, "last_name"),
                        TicketClassName: GetString(ticket, "ticket_name"),
                        TicketId: FirstNonEmpty(GetString(ticket, "id"), GetString(ticket, "ticket_id"), GetString(ticket, "barcode"))));
                }
            }

            if (orders.GetArrayLength() < 100)
            {
                break;
            }
            page++;
        }

        return tickets;
    }

    /// <summary>
    /// Fetch Bookings appointments for the Master Class date window. Mirrors
    /// the source script's multipart fetchappointment call.
    /// </summary>
    public async Task<IReadOnlyList<ZohoAppointment>> GetBookingsAppointmentsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var appointments = new List<ZohoAppointment>();
        var page = 1;

        while (true)
        {
            var fromTime = $"{FormatDate(_options.MasterClassDate)} 00:00:00";
            var toTime = $"{FormatDate(_options.MasterClassDate)} 23:59:59";

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["from_time"] = fromTime,
                ["to_time"] = toTime,
                ["page"] = page,
                ["per_page"] = 100,
            });

            var boundary = Guid.NewGuid().ToString();
            var body =
                $"--{boundary}\r\n" +
                "Content-Disposition: form-data; name=\"data\"\r\n\r\n" +
                $"{payload}\r\n--{boundary}--\r\n";

            var url = $"{_options.ApiDomain}/bookings/v1/json/fetchappointment";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.Remove("Content-Type");
            req.Content.Headers.TryAddWithoutValidation(
                "Content-Type", $"multipart/form-data; boundary={boundary}");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                break;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);

            if (!TryGetReturnData(doc.RootElement, out var data)
                || data.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var appt in data.EnumerateArray())
            {
                appointments.Add(new ZohoAppointment(
                    CustomerEmail: Lower(GetString(appt, "customer_email")),
                    CustomerName: GetString(appt, "customer_name"),
                    ServiceName: GetString(appt, "service_name"),
                    Status: GetString(appt, "status"),
                    SummaryUrl: GetString(appt, "summary_url")));
            }

            if (data.GetArrayLength() < 100)
            {
                break;
            }
            page++;
        }

        return appointments;
    }

    /// <summary>
    /// Pull every record from one Zoho CRM module (standard v2 REST paging).
    /// Records without a value in the sponsor-company-id field are skipped —
    /// a lead the CRM hasn't attributed to a sponsor can't be routed.
    /// </summary>
    public async Task<IReadOnlyList<ZohoCrmLead>> GetCrmLeadsAsync(
        string accessToken, string module, CancellationToken ct = default)
    {
        var leads = new List<ZohoCrmLead>();
        var page = 1;

        while (true)
        {
            var url = $"{_options.ApiDomain}/crm/v2/{module}?page={page}&per_page=200";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

            using var resp = await _http.SendAsync(req, ct);
            // 204 = module empty; anything non-200 ends the page loop.
            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            {
                break;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var rec in data.EnumerateArray())
            {
                var sponsorId = GetString(rec, _options.CrmSponsorCompanyIdField);
                if (string.IsNullOrWhiteSpace(sponsorId)) continue;

                var first = GetString(rec, "First_Name");
                var last  = GetString(rec, "Last_Name");
                var full  = GetString(rec, "Full_Name");
                if (string.IsNullOrWhiteSpace(full))
                {
                    full = $"{first} {last}".Trim();
                }

                DateTimeOffset created = DateTimeOffset.UtcNow;
                var createdRaw = GetString(rec, "Created_Time");
                if (!string.IsNullOrWhiteSpace(createdRaw)
                    && DateTimeOffset.TryParse(createdRaw, out var parsed))
                {
                    created = parsed;
                }

                leads.Add(new ZohoCrmLead(
                    ZohoRecordId: GetString(rec, "id"),
                    Module: module,
                    SponsorCompanyId: sponsorId.Trim(),
                    FirstName: first,
                    LastName: last,
                    FullName: full,
                    Email: Lower(GetString(rec, "Email")),
                    Phone: GetString(rec, "Phone"),
                    Company: GetString(rec, "Company"),
                    JobTitle: GetString(rec, "Designation"),
                    City: GetString(rec, "City"),
                    Country: GetString(rec, "Country"),
                    Source: GetString(rec, "Lead_Source"),
                    Notes: GetString(rec, "Description"),
                    CreatedTime: created));
            }

            // CRM "info.more_records" is authoritative; fall back to page size.
            var more = doc.RootElement.TryGetProperty("info", out var info)
                       && info.TryGetProperty("more_records", out var mr)
                       && mr.ValueKind == JsonValueKind.True;
            if (!more) break;
            page++;
        }

        return leads;
    }

    private static bool TryGetReturnData(JsonElement root, out JsonElement data)
    {
        data = default;
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("returnvalue", out var rv)
            && rv.TryGetProperty("data", out var d)
            && d.ValueKind == JsonValueKind.Array)
        {
            data = d;
            return true;
        }
        return false;
    }

    private static string FormatDate(string isoDate) =>
        DateTime.TryParse(isoDate, out var dt)
            ? dt.ToString("dd-MMM-yyyy",
                System.Globalization.CultureInfo.InvariantCulture)
            : isoDate;

    private static string GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstNonEmpty(params string[] xs)
    {
        foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x.Trim();
        return string.Empty;
    }

    private static string Lower(string s) =>
        (s ?? string.Empty).Trim().ToLowerInvariant();
}
