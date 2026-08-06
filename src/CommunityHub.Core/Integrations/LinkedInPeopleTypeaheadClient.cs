using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §858.16 — LinkedIn's SANCTIONED person lookup: <c>peopleTypeahead</c>, finder
/// <c>organizationFollowers</c>. Turns a speaker's name into the <c>urn:li:person:{shortId}</c>
/// that the Posts API accepts in a mention.
///
/// <para>Proven live 2026-08-06: a mention posted with a URN from here renders as a real
/// clickable mention. Nothing else does — Renidly's <c>ACoAA…</c> and numeric ids are both
/// rejected with <c>INVALID_MENTION_PERSON_URN_ID</c>, and the <c>fsd_profile</c> spelling is
/// accepted but silently rendered as literal text (§858.13b).</para>
///
/// <para>🔒 <b>Followers only.</b> The corpus is people who follow the page, and the limit binds at
/// POST time too: a genuine URN for a non-follower posts as a bare name (§858.16d). So the
/// fallback is not a nicety — it is the only correct behaviour for ~1 in 4 of our speakers.</para>
///
/// <para>🔒 <b>Requires scope <c>r_organization_followers</c> on a 3-legged token.</b> Without it the
/// call answers 403 naming the resource. Scopes are fixed at consent, so adding it needs a fresh
/// connect, never a refresh (§858.15b).</para>
/// </summary>
public sealed class LinkedInPeopleTypeaheadClient
{
    /// <summary>Measured: the default page size is 10, which silently truncates common surnames.</summary>
    public const int PageSize = 50;

    private readonly HttpClient _http;
    private readonly LinkedInOptions _options;
    private readonly LinkedInTokenStore? _tokenStore;
    private readonly ILogger<LinkedInPeopleTypeaheadClient>? _log;

    public LinkedInPeopleTypeaheadClient(
        HttpClient http,
        LinkedInOptions options,
        LinkedInTokenStore? tokenStore = null,
        ILogger<LinkedInPeopleTypeaheadClient>? log = null)
    {
        _http = http;
        _options = options;
        _tokenStore = tokenStore;
        _log = log;
    }

    /// <summary>
    /// Resolve one person to a mentionable URN. Always returns a reason — a caller must be able
    /// to tell "does not follow the page" from "we could not ask", because treating a failure as
    /// a negative is exactly how the §858.16h miscount happened.
    /// </summary>
    public async Task<MentionResolution> ResolveAsync(
        string organizationUrnOrId, string? firstName, string? lastName,
        string? profileUrl = null, CancellationToken ct = default)
    {
        var keyword = PersonMentionMatcher.BuildKeyword(firstName, lastName);
        if (keyword is null)
        {
            return new MentionResolution(null, MentionResolutionStatus.KeywordUnusable,
                $"no searchable name for '{firstName} {lastName}'".Trim());
        }

        var orgUrn = LiveLinkedInPostPublisher.ToOrganizationUrn(organizationUrnOrId);
        if (orgUrn is null)
        {
            return new MentionResolution(null, MentionResolutionStatus.LookupFailed,
                $"could not resolve an organization urn from '{organizationUrnOrId}'");
        }

        var token = _tokenStore is null ? null : await _tokenStore.GetOrgAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new MentionResolution(null, MentionResolutionStatus.LookupFailed,
                "LinkedIn is not connected (no org token), so nobody could be looked up");
        }

        try
        {
            var candidates = await SearchAsync(orgUrn, keyword, token!, ct);
            var result = PersonMentionMatcher.SelectMatch(candidates, firstName, lastName);

            // §858.17 — two followers really can share a name (measured: two "Peter Schmidt" both
            // following the page). Typeahead returns no slug, so the name alone cannot separate
            // them — but a URN DOES resolve to a vanityName (§858.1), and we hold the speaker's
            // profile URL. One extra call per candidate, ONLY when ambiguous.
            if (result.Status == MentionResolutionStatus.Ambiguous)
            {
                var disambiguated = await DisambiguateBySlugAsync(
                    candidates, firstName, lastName, profileUrl, token!, ct);
                if (disambiguated is not null) return disambiguated;
            }

            return result;
        }
        catch (Exception ex)
        {
            // 🔒 §858.16h: a failed lookup is NOT "not a follower". Say which it was.
            _log?.LogWarning(ex, "peopleTypeahead lookup failed for {Keyword}.", keyword);
            return new MentionResolution(null, MentionResolutionStatus.LookupFailed,
                $"the lookup failed ({ex.Message}) — this is NOT evidence they are a non-follower");
        }
    }

    /// <summary>
    /// §858.17 — separate identically-named followers by resolving each candidate's URN to its
    /// <c>vanityName</c> and matching that against the slug in the speaker's stored profile URL.
    /// <para>Returns null when it cannot decide — no stored slug, the lookup failed, or nothing
    /// matched — leaving the honest <c>Ambiguous</c> answer in place. 🔒 It must never fall back to
    /// "pick the first"; tagging the wrong person from the company page is the worst outcome here.</para>
    /// </summary>
    private async Task<MentionResolution?> DisambiguateBySlugAsync(
        IReadOnlyList<TypeaheadCandidate> candidates, string? firstName, string? lastName,
        string? profileUrl, string accessToken, CancellationToken ct)
    {
        var wantedSlug = PersonMentionMatcher.TryReadPersonSlug(profileUrl);
        if (wantedSlug is null) return null;

        foreach (var candidate in PersonMentionMatcher.Matching(candidates, firstName, lastName))
        {
            var shortId = ShortIdOf(candidate.Member);
            if (shortId is null) continue;

            var vanity = await TryGetVanityNameAsync(shortId, accessToken, ct);
            if (vanity is null) continue;    // throttled or unreadable — do NOT guess from silence

            if (string.Equals(vanity, wantedSlug, StringComparison.OrdinalIgnoreCase))
            {
                return new MentionResolution(candidate.Member, MentionResolutionStatus.Resolved,
                    $"matched follower '{candidate.DisplayName}' by profile slug '{wantedSlug}'");
            }
        }

        return null;
    }

    /// <summary>
    /// A person URN → their <c>vanityName</c>. This direction works where slug→URN does not
    /// (§858.1/§858.10). Returns null on any failure — the caller treats that as "cannot decide".
    /// </summary>
    public async Task<string?> TryGetVanityNameAsync(
        string shortId, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/people/(id:{shortId})");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
        req.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 🔒 A DAY throttle here is common and is NOT an answer about identity.
                _log?.LogInformation(
                    "vanityName lookup for {ShortId} returned {Status} — leaving the match ambiguous.",
                    shortId, (int)resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("vanityName", out var v) ? v.GetString() : null;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "vanityName lookup failed for {ShortId}.", shortId);
            return null;
        }
    }

    internal static string? ShortIdOf(string? memberUrn) =>
        string.IsNullOrWhiteSpace(memberUrn) ? null
        : memberUrn.StartsWith("urn:li:person:", StringComparison.OrdinalIgnoreCase)
            ? memberUrn["urn:li:person:".Length..]
            : null;

    /// <summary>The raw call. Public so a diagnostic page can show exactly what LinkedIn returned.</summary>
    public async Task<IReadOnlyList<TypeaheadCandidate>> SearchAsync(
        string organizationUrn, string keyword, string accessToken, CancellationToken ct = default)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/peopleTypeahead"
                + $"?q=organizationFollowers"
                + $"&organization={Uri.EscapeDataString(organizationUrn)}"
                + $"&keywords={Uri.EscapeDataString(keyword)}"
                + $"&count={PageSize}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
        req.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Name the two failures a caller must handle differently.
            var hint = resp.StatusCode switch
            {
                HttpStatusCode.Forbidden =>
                    " — the token is missing scope 'r_organization_followers'; re-consent is required (§858.15b)",
                HttpStatusCode.TooManyRequests =>
                    " — the DAY throttle is in force; this is NOT a permission answer (§858.8)",
                _ => string.Empty,
            };
            throw new HttpRequestException(
                $"peopleTypeahead returned {(int)resp.StatusCode}{hint}: {Trim(body, 400)}");
        }

        return Parse(body);
    }

    internal static IReadOnlyList<TypeaheadCandidate> Parse(string body)
    {
        var result = new List<TypeaheadCandidate>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var el in elements.EnumerateArray())
        {
            var member = el.TryGetProperty("member", out var m) ? m.GetString() : null;
            if (string.IsNullOrWhiteSpace(member)) continue;

            result.Add(new TypeaheadCandidate(
                member!,
                el.TryGetProperty("firstName", out var f) ? f.GetString() ?? string.Empty : string.Empty,
                el.TryGetProperty("lastName", out var l) ? l.GetString() ?? string.Empty : string.Empty));
        }

        return result;
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
