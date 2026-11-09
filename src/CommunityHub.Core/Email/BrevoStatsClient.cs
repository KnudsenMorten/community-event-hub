using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityHub.Core.Email;

/// <summary>§1381 — the Brevo REST key used ONLY to read campaign statistics (config <c>Brevo:ApiKey</c>).</summary>
public sealed class BrevoApiOptions
{
    public const string SectionName = "Brevo";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.brevo.com/v3";
}

/// <summary>One campaign's numbers as Brevo counts them, for one tag.</summary>
public sealed record BrevoTagStats(int Requests, int Delivered, int UniqueOpens, int Opens, int UniqueClicks, int Clicks,
    int Unsubscribed, int HardBounces, int SoftBounces, int SpamReports, int Blocked)
{
    public double OpenRate => Delivered == 0 ? 0 : (double)UniqueOpens / Delivered;
    public double ClickRate => Delivered == 0 ? 0 : (double)UniqueClicks / Delivered;
}

/// <summary>The honest outcome of a stats read: numbers, or the reason there are none.</summary>
public sealed record BrevoStatsResult(BrevoTagStats? Stats, string? Problem)
{
    public static BrevoStatsResult NotConnected { get; } =
        new(null, "Stats not connected — no Brevo API key is configured (Brevo:ApiKey).");
}

/// <summary>
/// §1381 — reads opens/clicks for one campaign from Brevo's transactional statistics, by the tag the campaign's mails
/// carry (<see cref="MailCampaignService.TagFor"/>, sent as <c>X-Mailin-Tag</c>). READ-ONLY: this client never sends.
/// </summary>
/// <remarks>
/// ⚠️ Brevo blocks API calls from IP addresses not on the account's allow-list; that refusal comes back as a 401 with
/// Brevo's own sentence, which is passed through verbatim so the page says exactly what to fix.
/// ⚠️ Opens are approximate by nature (image blocking, Apple Mail Privacy Protection pre-loads) — the page says so.
/// </remarks>
public sealed class BrevoStatsClient
{
    private readonly HttpClient _http;
    private readonly BrevoApiOptions _options;

    public BrevoStatsClient(HttpClient http, BrevoApiOptions options)
    {
        _http = http;
        _options = options;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<BrevoStatsResult> ForTagAsync(string tag, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (!Configured) return BrevoStatsResult.NotConnected;
        if (to < from) to = from;

        var url = $"{_options.BaseUrl.TrimEnd('/')}/smtp/statistics/aggregatedReport"
                  + $"?startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}&tag={Uri.EscapeDataString(tag)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("api-key", _options.ApiKey);
        req.Headers.Add("accept", "application/json");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string? message = null;
                try { message = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString(); } catch { /* not JSON */ }
                return new BrevoStatsResult(null, $"Brevo refused the stats call ({(int)resp.StatusCode}): {message ?? body}");
            }

            var r = JsonSerializer.Deserialize<Aggregated>(body) ?? new Aggregated();
            return new BrevoStatsResult(new BrevoTagStats(r.Requests, r.Delivered, r.UniqueOpens, r.Opens, r.UniqueClicks,
                r.Clicks, r.Unsubscribed, r.HardBounces, r.SoftBounces, r.SpamReports, r.Blocked), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new BrevoStatsResult(null, $"Could not reach Brevo: {ex.Message}");
        }
    }

    private sealed class Aggregated
    {
        [JsonPropertyName("requests")] public int Requests { get; set; }
        [JsonPropertyName("delivered")] public int Delivered { get; set; }
        [JsonPropertyName("hardBounces")] public int HardBounces { get; set; }
        [JsonPropertyName("softBounces")] public int SoftBounces { get; set; }
        [JsonPropertyName("clicks")] public int Clicks { get; set; }
        [JsonPropertyName("uniqueClicks")] public int UniqueClicks { get; set; }
        [JsonPropertyName("opens")] public int Opens { get; set; }
        [JsonPropertyName("uniqueOpens")] public int UniqueOpens { get; set; }
        [JsonPropertyName("spamReports")] public int SpamReports { get; set; }
        [JsonPropertyName("blocked")] public int Blocked { get; set; }
        [JsonPropertyName("unsubscribed")] public int Unsubscribed { get; set; }
    }
}
