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
    private readonly LinkedInTokenStore? _tokenStore;

    public LiveLinkedInPostPublisher(
        HttpClient http, LinkedInOptions options, ILogger<LiveLinkedInPostPublisher> log,
        // §324: optional DB token source — the /Organizer/LinkedInConnect-minted token
        // wins over the static/refresh-triplet options.
        LinkedInTokenStore? tokenStore = null,
        // §340-H: the environment-level external-write switch. Optional so existing
        // constructions/tests are unchanged (null ⇒ allow); DI supplies the real guard.
        IExternalWriteGuard? writes = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _tokenStore = tokenStore;
        _writes = writes ?? new AllowAllExternalWrites();
    }

    private readonly IExternalWriteGuard _writes;

    public bool CanPublish => _options.Enabled && (_options.HasCredentials || _tokenStore is not null);

    public async Task<LinkedInPublishResult> PublishAsync(
        LinkedInPost post, CancellationToken ct = default)
    {
        if (!CanPublish)
            return new LinkedInPublishResult(false, null,
                "LinkedIn publisher is not configured (disabled or no token).");

        // §340-H: publishing to LinkedIn is a PUBLIC, irreversible act (a post can be
        // deleted afterwards but never unsent), so it belongs under the same environment
        // switch as the other third-party writes. Returns a SOFT failure, matching the
        // not-configured line above: SoMeDispatchService leaves such a post Queued rather
        // than marking it Failed, so it publishes by itself once the switch is on.
        if (!await _writes.AllowAsync("LinkedIn", nameof(PublishAsync), ct))
            return new LinkedInPublishResult(false, null,
                "External writes are disabled for this host (Integrations:AllowExternalWrites "
                + "— §340-H); the post stays queued and nothing was published.");

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
        if (!string.IsNullOrWhiteSpace(post.AccessTokenOverride))
        {
            // §324b: a MEMBER post — the speaker's own token, author = their person urn.
            token = post.AccessTokenOverride!;
        }
        else
        {
            try { token = await GetAccessTokenAsync(ct); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LinkedIn token acquisition failed.");
                return new LinkedInPublishResult(false, null, $"LinkedIn token error: {ex.Message}");
            }
        }

        // §324: NATIVE IMAGE upload (the operator's requirement: "png file upload with
        // text incl url") — initializeUpload → binary PUT → reference the image urn in
        // content.media. An upload failure fails the publish honestly (never a
        // text-only post when an image was requested).
        string? imageUrn = null;
        if (post.ImageBytes is { Length: > 0 })
        {
            try
            {
                imageUrn = await UploadImageAsync(token, orgUrn, post.ImageBytes, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LinkedIn image upload failed.");
                return new LinkedInPublishResult(false, null, $"LinkedIn image upload failed: {ex.Message}");
            }
        }

        _log.LogInformation(
            "LinkedIn publish: author={Author}, chars={Chars}, image={HasImage}",
            orgUrn, post.Text?.Length ?? 0, imageUrn is not null);

        // LinkedIn Posts API payload (org main feed, published; native image when uploaded).
        // §326k: commentary MUST be little-text-format escaped — see EscapeCommentary.
        var payload = new Dictionary<string, object?>
        {
            ["author"] = orgUrn,
            ["commentary"] = EscapeCommentary(post.Text ?? string.Empty),
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
        if (imageUrn is not null)
        {
            payload["content"] = new Dictionary<string, object?>
            {
                ["media"] = new Dictionary<string, object?>
                {
                    ["id"] = imageUrn,
                    ["altText"] = post.ImageAltText ?? "Session graphic",
                },
            };
        }

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

    /// <summary>
    /// §324: LinkedIn's two-step native image upload. POST /rest/images?action=
    /// initializeUpload (owner = the posting author) returns an uploadUrl + image urn;
    /// the raw bytes are PUT to the uploadUrl; the urn goes into content.media.id.
    /// </summary>
    private async Task<string> UploadImageAsync(
        string token, string ownerUrn, byte[] bytes, CancellationToken ct)
    {
        using var initReq = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/images?action=initializeUpload");
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        initReq.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
        initReq.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        initReq.Content = new StringContent(
            JsonSerializer.Serialize(new { initializeUploadRequest = new { owner = ownerUrn } }),
            new UTF8Encoding(false), "application/json");
        using var initResp = await _http.SendAsync(initReq, ct);
        var initBody = await initResp.Content.ReadAsStringAsync(ct);
        if (!initResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"initializeUpload returned {(int)initResp.StatusCode}: {Trim(initBody, 300)}");
        using var doc = JsonDocument.Parse(initBody);
        var value = doc.RootElement.GetProperty("value");
        var uploadUrl = value.GetProperty("uploadUrl").GetString()
            ?? throw new InvalidOperationException("initializeUpload returned no uploadUrl.");
        var imageUrn = value.GetProperty("image").GetString()
            ?? throw new InvalidOperationException("initializeUpload returned no image urn.");

        using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(bytes),
        };
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var putResp = await _http.SendAsync(putReq, ct);
        if (!putResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"image binary PUT returned {(int)putResp.StatusCode}.");

        return imageUrn;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // §324: a connected (DB-minted) org token wins; then the static token; then
        // the refresh triplet.
        if (_tokenStore is not null)
        {
            var stored = await _tokenStore.GetOrgAccessTokenAsync(ct);
            if (!string.IsNullOrWhiteSpace(stored)) return stored!;
            if (!_options.HasCredentials)
                throw new InvalidOperationException(
                    "No LinkedIn token connected yet — an organizer must run Connect LinkedIn "
                    + "(/Organizer/LinkedInConnect) once.");
        }
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
        // §324b: a MEMBER post authors as the person urn directly.
        if (s.StartsWith("urn:li:person:", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.All(char.IsDigit)) return $"urn:li:organization:{s}";

        // A company URL like .../company/123456 — take the trailing numeric segment.
        var tail = s.TrimEnd('/').Split('/').LastOrDefault();
        if (!string.IsNullOrEmpty(tail) && tail.All(char.IsDigit))
            return $"urn:li:organization:{tail}";

        return null;
    }

    /// <summary>
    /// §326k (operator live test 2026-07-25: "only half the text comes over"): the Posts
    /// API's <c>commentary</c> is LinkedIn "little text format" — <c>( ) { } [ ] &lt; &gt;
    /// @ | * _ ~ \</c> are CONTROL characters, and the first unescaped one makes LinkedIn
    /// truncate/mangle the rendered post (the §281 text is full of parentheses). Escape
    /// each with a backslash. <c>#</c> is deliberately NOT escaped so the #ELDK27 /
    /// #ExpertsLiveDK hashtags keep auto-linking.
    /// </summary>
    public static string EscapeCommentary(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c is '\\' or '|' or '{' or '}' or '@' or '[' or ']'
                  or '(' or ')' or '<' or '>' or '*' or '_' or '~')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
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
