using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Why an organization lookup produced (or did not produce) an id.</summary>
public enum OrganizationLookupStatus
{
    /// <summary>LinkedIn answered with exactly the organization for that slug.</summary>
    Resolved,
    /// <summary>LinkedIn answered and knows no organization by that slug.</summary>
    NotFound,
    /// <summary>We could not ask (no token, throttle, network, unexpected response) — NOT a negative.</summary>
    LookupFailed,
}

/// <summary>The outcome of one lookup — the id when resolved, and always a reason.</summary>
public sealed record OrganizationLookup(string? OrganizationId, OrganizationLookupStatus Status, string Reason);

/// <summary>
/// §1300 — LinkedIn company VANITY SLUG → numeric organization id, via the finder
/// <c>GET /v2/organizations?q=vanityName&amp;vanityName={slug}</c>.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Measured working on the live token</b> (REQUIREMENTS §856.1, 2026-08-05): the finder answers
/// 200 on <c>r_organization_social</c> alone and resolved all 14 sponsor slugs on the first attempt. It
/// is NOT gated the way the by-id getter is — §824.14c's "403, impossible" was about that getter and was
/// superseded. Until §1300 this was only ever run by hand, so <c>SponsorInfo.LinkedInOrganizationId</c>
/// stayed empty and every sponsor was named in plain text.</para>
///
/// <para>🔒 Called ONLY from the scheduled mention-resolution routine (a cache fill), never in the request
/// path or at publish time — same rule as the people lookup.</para>
/// </remarks>
public sealed class LinkedInOrganizationLookupClient
{
    private readonly HttpClient _http;
    private readonly LinkedInOptions _options;
    private readonly LinkedInTokenStore? _tokenStore;
    private readonly ILogger<LinkedInOrganizationLookupClient>? _log;

    public LinkedInOrganizationLookupClient(
        HttpClient http,
        LinkedInOptions options,
        LinkedInTokenStore? tokenStore = null,
        ILogger<LinkedInOrganizationLookupClient>? log = null)
    {
        _http = http;
        _options = options;
        _tokenStore = tokenStore;
        _log = log;
    }

    /// <summary>Resolve a company page URL (or bare slug) to its numeric organization id.</summary>
    public async Task<OrganizationLookup> ResolveAsync(string? companyUrlOrSlug, CancellationToken ct = default)
    {
        var slug = LinkedInUrlParser.TryReadVanityName(companyUrlOrSlug)
                   ?? (IsBareSlug(companyUrlOrSlug) ? companyUrlOrSlug!.Trim() : null);
        if (slug is null)
            return new(null, OrganizationLookupStatus.NotFound,
                $"no LinkedIn company slug in '{companyUrlOrSlug}'");

        var token = _tokenStore is null ? null : await _tokenStore.GetOrgAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            return new(null, OrganizationLookupStatus.LookupFailed,
                "LinkedIn is not connected (no org token), so the company could not be looked up");

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/organizations?q=vanityName&vanityName={Uri.EscapeDataString(slug)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.LogInformation("Organization lookup for '{Slug}' returned {Status}.", slug, (int)resp.StatusCode);
                return new(null, OrganizationLookupStatus.LookupFailed,
                    $"LinkedIn answered {(int)resp.StatusCode} for '{slug}'");
            }
            return Parse(slug, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.LogWarning(ex, "Organization lookup failed for '{Slug}'.", slug);
            return new(null, OrganizationLookupStatus.LookupFailed, $"lookup failed for '{slug}': {ex.Message}");
        }
    }

    /// <summary>
    /// Read the finder's response. 🔒 Only an element whose <c>vanityName</c> matches the slug we asked
    /// for is accepted (when the field is present) — tagging a stranger's company in a public post is
    /// worse than naming the sponsor in plain text.
    /// </summary>
    internal static OrganizationLookup Parse(string slug, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
                return new(null, OrganizationLookupStatus.LookupFailed, "unexpected response shape (no elements)");

            foreach (var el in elements.EnumerateArray())
            {
                if (el.TryGetProperty("vanityName", out var v) && v.ValueKind == JsonValueKind.String
                    && !string.Equals(v.GetString(), slug, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!el.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.ValueKind switch
                {
                    JsonValueKind.Number => idEl.GetRawText(),
                    JsonValueKind.String => idEl.GetString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(id) && id.All(char.IsAsciiDigit))
                    return new(id, OrganizationLookupStatus.Resolved, $"'{slug}' → {id}");
            }
            return new(null, OrganizationLookupStatus.NotFound, $"LinkedIn knows no company '{slug}'");
        }
        catch (JsonException ex)
        {
            return new(null, OrganizationLookupStatus.LookupFailed, $"unreadable response: {ex.Message}");
        }
    }

    private static bool IsBareSlug(string? s) =>
        !string.IsNullOrWhiteSpace(s) && !s.Contains('/') && !s.Contains(' ') && !s.Trim().All(char.IsAsciiDigit);
}
