using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>A Zoho Backstage ticket order, flattened.</summary>
public sealed record ZohoTicket(
    string Email,
    string FirstName,
    string LastName,
    string TicketClassName);

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
}

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
                        TicketClassName: GetString(ticket, "ticket_name")));
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

    private static string Lower(string s) =>
        (s ?? string.Empty).Trim().ToLowerInvariant();
}
