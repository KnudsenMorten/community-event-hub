using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Live LinkedIn company-page publisher (REQUIREMENTS §19/§31) over the LinkedIn
/// Posts API (<c>POST {ApiBaseUrl}/rest/posts</c>, scope <c>w_organization_social</c>).
/// It posts as the organization (<c>urn:li:organization:{id}</c>) resolved from the
/// per-post target (the operator-configured company page id/urn).
///
/// <b>NOTHING posts by default.</b> Two independent holds protect this:
///   1. <see cref="LinkedInOptions.DryRun"/> = true (default) — the publisher logs the
///      intended post and returns <c>Published=false</c> WITHOUT calling LinkedIn, so
///      the dispatcher leaves the post queued (hold-for-approval).
///   2. The publisher is only registered (instead of the Null no-op) when LinkedIn is
///      <see cref="LinkedInOptions.Enabled"/> AND credentialed; and the SoMe dispatcher
///      additionally requires the posting switch + a configured page.
/// Real posting happens only when DryRun is explicitly turned off.
/// </summary>
public sealed class LiveLinkedInPostPublisher : ILinkedInPostPublisher
{
    private readonly HttpClient _http;
    private readonly LinkedInOptions _options;
    private readonly ILogger<LiveLinkedInPostPublisher> _log;

    public LiveLinkedInPostPublisher(
        HttpClient http, LinkedInOptions options, ILogger<LiveLinkedInPostPublisher> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool CanPublish => _options.Enabled && _options.HasCredentials;

    public async Task<LinkedInPublishResult> PublishAsync(
        LinkedInPost post, CancellationToken ct = default)
    {
        if (!CanPublish)
            return new LinkedInPublishResult(false, null,
                "LinkedIn publisher is not configured (disabled or no token).");

        var orgUrn = ToOrganizationUrn(post.OrganizationUrnOrId);
        if (orgUrn is null)
            return new LinkedInPublishResult(false, null,
                $"Could not resolve a LinkedIn organization id from '{post.OrganizationUrnOrId}'. "
                + "Configure the company page as a numeric organization id or 'urn:li:organization:{id}'.");

        // HARD HOLD: dry-run never calls LinkedIn — log intent + leave the post queued.
        if (_options.DryRun)
        {
            _log.LogInformation(
                "[LinkedIn DRY-RUN] Would post to {Org}: {Text}", orgUrn, Trim(post.Text, 280));
            return new LinkedInPublishResult(false, null,
                "DRY-RUN — LinkedIn posting is wired but held (LinkedIn:DryRun=true); nothing was posted.");
        }

        string token;
        try { token = await GetAccessTokenAsync(ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LinkedIn token acquisition failed.");
            return new LinkedInPublishResult(false, null, $"LinkedIn token error: {ex.Message}");
        }

        // LinkedIn Posts API payload (text post to the org's main feed, published).
        var payload = new Dictionary<string, object?>
        {
            ["author"] = orgUrn,
            ["commentary"] = post.Text,
            ["visibility"] = "PUBLIC",
            ["distribution"] = new Dictionary<string, object?>
            {
                ["feedDistribution"] = "MAIN_FEED",
                ["targetEntities"] = Array.Empty<object>(),
                ["thirdPartyDistributionChannels"] = Array.Empty<object>(),
            },
            ["lifecycleState"] = "PUBLISHED",
            ["isReshareDisabledByAuthor"] = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/posts");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
        req.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), new UTF8Encoding(false), "application/json");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("LinkedIn post failed: {Status} {Body}", (int)resp.StatusCode, Trim(body, 500));
                return new LinkedInPublishResult(false, null,
                    $"LinkedIn returned {(int)resp.StatusCode}: {Trim(body, 300)}");
            }

            // The created post id comes back in a header (x-restli-id / x-linkedin-id),
            // and sometimes in the body 'id'. Prefer the header.
            var postId = HeaderValue(resp, "x-restli-id")
                         ?? HeaderValue(resp, "x-linkedin-id")
                         ?? TryBodyId(body);
            return new LinkedInPublishResult(true, postId,
                $"Posted to LinkedIn ({postId ?? "id unknown"}).");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LinkedIn post threw.");
            return new LinkedInPublishResult(false, null, $"LinkedIn call failed: {ex.Message}");
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // Static token wins when present; else refresh via the OAuth triplet.
        if (!string.IsNullOrWhiteSpace(_options.AccessToken)) return _options.AccessToken!;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _options.RefreshToken!,
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
        });
        using var resp = await _http.PostAsync(_options.TokenEndpoint, form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("LinkedIn token response had no access_token.");
    }

    /// <summary>
    /// Normalize the configured company-page value to <c>urn:li:organization:{id}</c>.
    /// Accepts an existing org URN, a bare numeric id, or a numeric id embedded in a
    /// company URL. Returns null for an unresolvable value (e.g. a vanity-name URL,
    /// which needs an API lookup the operator should avoid by configuring the id).
    /// </summary>
    public static string? ToOrganizationUrn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.StartsWith("urn:li:organization:", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.All(char.IsDigit)) return $"urn:li:organization:{s}";

        // A company URL like .../company/123456 — take the trailing numeric segment.
        var tail = s.TrimEnd('/').Split('/').LastOrDefault();
        if (!string.IsNullOrEmpty(tail) && tail.All(char.IsDigit))
            return $"urn:li:organization:{tail}";

        return null;
    }

    private static string? HeaderValue(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static string? TryBodyId(string body)
    {
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null; }
        catch { return null; }
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
