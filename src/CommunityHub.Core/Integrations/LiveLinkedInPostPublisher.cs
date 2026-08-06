using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        string? videoUrn = null;

        // §844 — VIDEO WINS when the post carries one (§844.2: "we prefer videos more than
        // graphics"). The graphic fallback is decided UPSTREAM by whoever builds the record; by the
        // time bytes arrive here the choice has been made.
        if (post.VideoBytes is { Length: > 0 })
        {
            try
            {
                videoUrn = await UploadVideoAsync(token, orgUrn, post.VideoBytes, ct);
            }
            catch (VideoStillProcessingException ex)
            {
                // 🔒 §844.2 — WAIT; DO NOT FALL BACK. The video exists and he prefers it, so
                // publishing the graphic instead would quietly ship the asset he chose against.
                // Returning not-published leaves the post QUEUED and the dispatcher retries it.
                _log.LogInformation(
                    "LinkedIn video {Urn} is still processing — the post stays queued.", ex.VideoUrn);
                return new LinkedInPublishResult(
                    false, null,
                    "The video is still being processed by LinkedIn. The post stays queued and will "
                    + "publish on a later run — it has NOT been downgraded to the graphic.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LinkedIn video upload failed.");
                return new LinkedInPublishResult(false, null, $"LinkedIn video upload failed: {ex.Message}");
            }
        }
        else if (post.ImageBytes is { Length: > 0 })
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
            ["commentary"] = EscapeCommentaryPreservingMentions(post.Text ?? string.Empty),
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
        if (videoUrn is not null)
        {
            // §844 — a video attaches through the SAME content.media shape as an image; only the
            // urn differs (urn:li:video:… rather than urn:li:image:…).
            payload["content"] = new Dictionary<string, object?>
            {
                ["media"] = new Dictionary<string, object?>
                {
                    ["id"] = videoUrn,
                    ["title"] = post.ImageAltText ?? "Experts Live Denmark",
                },
            };
        }
        else if (imageUrn is not null)
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

    /// <summary>
    /// §844 — LinkedIn's MULTIPART native video upload, and the processing wait that follows it.
    /// </summary>
    /// <remarks>
    /// <para>Four steps, and it is <b>not</b> the image flow with a different URL:</para>
    /// <list type="number">
    ///   <item><c>POST /rest/videos?action=initializeUpload</c> with the file SIZE — LinkedIn
    ///         replies with an upload URL <b>per part</b>, a video urn and an upload token.</item>
    ///   <item>Each part is PUT separately, and each response's <b>ETag must be kept</b>.</item>
    ///   <item><c>POST /rest/videos?action=finalizeUpload</c> with those ETags in order.</item>
    ///   <item>🔴 <b>The video is then PROCESSED ASYNCHRONOUSLY.</b> Attaching it before it is
    ///         AVAILABLE produces a post whose video never plays, so its status is checked and
    ///         <see cref="VideoStillProcessingException"/> is thrown while it is not ready.</item>
    /// </list>
    ///
    /// <para>⚠️ His real samples are 19–80 MB (§844.7), so multipart is the normal path here, not an
    /// edge case — a single PUT would not carry them.</para>
    /// </remarks>
    private async Task<string> UploadVideoAsync(
        string token, string ownerUrn, byte[] bytes, CancellationToken ct)
    {
        // --- 1. initialize -------------------------------------------------------------------
        using var initReq = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/videos?action=initializeUpload");
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        initReq.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
        initReq.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        initReq.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                initializeUploadRequest = new
                {
                    owner = ownerUrn,
                    fileSizeBytes = bytes.LongLength,
                    uploadCaptions = false,
                    uploadThumbnail = false,
                },
            }),
            new UTF8Encoding(false), "application/json");

        using var initResp = await _http.SendAsync(initReq, ct);
        var initBody = await initResp.Content.ReadAsStringAsync(ct);
        if (!initResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"video initializeUpload returned {(int)initResp.StatusCode}: {Trim(initBody, 300)}");
        }

        using var doc = JsonDocument.Parse(initBody);
        var value = doc.RootElement.GetProperty("value");
        var videoUrn = value.GetProperty("video").GetString()
            ?? throw new InvalidOperationException("video initializeUpload returned no video urn.");
        var uploadToken = value.TryGetProperty("uploadToken", out var ut) ? ut.GetString() ?? string.Empty : string.Empty;

        // --- 2. upload each part, keeping the ETags IN ORDER ----------------------------------
        var etags = new List<string>();
        foreach (var instruction in value.GetProperty("uploadInstructions").EnumerateArray())
        {
            var url = instruction.GetProperty("uploadUrl").GetString()!;
            var first = instruction.GetProperty("firstByte").GetInt64();
            var last = instruction.GetProperty("lastByte").GetInt64();
            var length = (int)(last - first + 1);

            using var partReq = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(bytes, (int)first, length),
            };
            partReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            partReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var partResp = await _http.SendAsync(partReq, ct);
            if (!partResp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"video part {first}-{last} PUT returned {(int)partResp.StatusCode}.");
            }

            // 🔒 The ETag is what finalize verifies each part by. A missing one fails the upload
            // here rather than producing a corrupt video LinkedIn would reject later, opaquely.
            var etag = partResp.Headers.ETag?.Tag
                       ?? (partResp.Headers.TryGetValues("etag", out var v) ? v.FirstOrDefault() : null)
                       ?? throw new InvalidOperationException(
                           $"video part {first}-{last} returned no ETag; cannot finalize.");

            etags.Add(etag.Trim('"'));
        }

        // --- 3. finalize ---------------------------------------------------------------------
        using var finReq = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/videos?action=finalizeUpload");
        finReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        finReq.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
        finReq.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        finReq.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                finalizeUploadRequest = new
                {
                    video = videoUrn,
                    uploadToken,
                    uploadedPartIds = etags,
                },
            }),
            new UTF8Encoding(false), "application/json");

        using var finResp = await _http.SendAsync(finReq, ct);
        if (!finResp.IsSuccessStatusCode)
        {
            var finBody = await finResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"video finalizeUpload returned {(int)finResp.StatusCode}: {Trim(finBody, 300)}");
        }

        // --- 4. is it actually ready? ---------------------------------------------------------
        if (!await IsVideoAvailableAsync(token, videoUrn, ct))
        {
            throw new VideoStillProcessingException(videoUrn);
        }

        return videoUrn;
    }

    /// <summary>
    /// §844 — whether an uploaded video has finished processing and can be attached to a post.
    /// </summary>
    /// <remarks>
    /// ⚠️ Treated as NOT-READY on any doubt (an unreadable status, an error response): holding a
    /// post for one more tick costs a few minutes, whereas publishing a video that never plays is
    /// public and permanent.
    /// </remarks>
    private async Task<bool> IsVideoAvailableAsync(string token, string videoUrn, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_options.ApiBaseUrl.TrimEnd('/')}/rest/videos/{Uri.EscapeDataString(videoUrn)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("LinkedIn-Version", _options.ApiVersion);
            req.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            return string.Equals(status, "AVAILABLE", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read LinkedIn video status for {Urn}; treating as not ready.", videoUrn);
            return false;
        }
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

    /// <summary>
    /// §858.13d: a little-text-format MENTION is written <c>@[Display Name](urn:li:person:{id})</c> —
    /// built from the very characters <see cref="EscapeCommentary"/> escapes. Escaping the whole
    /// string therefore posts the markup as visible text (measured live 2026-08-06: LinkedIn returned
    /// 201 and rendered the raw construct, because it never recognised a mention at all).
    /// <para>So: escape everything EXCEPT well-formed mention spans, which pass through verbatim.
    /// §326k's protection is unchanged for all other text — the parentheses that truncated posts are
    /// still escaped.</para>
    /// <para>🔒 Only the SHORT id form is treated as a mention. <c>urn:li:fsd_profile:</c> and numeric
    /// ids are NOT mentions to LinkedIn (both rejected/ignored live), so they escape as plain text
    /// rather than silently producing markup.</para>
    /// </summary>
    public static string EscapeCommentaryPreservingMentions(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length + 16);
        var last = 0;
        foreach (Match m in MentionSpan.Matches(text))
        {
            sb.Append(EscapeCommentary(text[last..m.Index]));
            sb.Append(m.Value);          // verbatim — this is the mention
            last = m.Index + m.Length;
        }
        sb.Append(EscapeCommentary(text[last..]));
        return sb.ToString();
    }

    /// <summary>
    /// A well-formed person mention. The display name may not contain brackets, and the id is
    /// restricted to LinkedIn's short-URN charset, so a stray "@[" in prose cannot be mistaken
    /// for a mention and smuggle unescaped text past §326k.
    /// </summary>
    private static readonly Regex MentionSpan = new(
        @"@\[[^\[\]]+\]\(urn:li:person:[A-Za-z0-9_-]+\)", RegexOptions.Compiled);

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
