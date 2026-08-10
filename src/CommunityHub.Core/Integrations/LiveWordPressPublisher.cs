using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Live WordPress posts connector (REQUIREMENTS §31) over the WordPress REST API
/// (<c>POST {SiteUrl}/wp-json/wp/v2/posts</c>) with Basic auth from a WordPress
/// <b>Application Password</b>. <b>v1 is draft-only</b>: every create forces
/// <c>status=draft</c>, so the operator validates in wp-admin before anything is
/// published — there is no publish path here.
///
/// Auth + headers mirror <see cref="CompanyManagerClient"/>: Basic
/// base64(user:app-password), a non-default <c>User-Agent</c> (Wordfence returns
/// HTTP 455 to the default .NET UA), JSON accept, and a UTF-8 body so Danish
/// characters survive. Self-gates via <see cref="CanWrite"/> so an unconfigured
/// deploy never calls out.
/// </summary>
public sealed class LiveWordPressPublisher : IWordPressPublisher
{
    private readonly HttpClient _http;
    private readonly WordPressOptions _options;
    private readonly ILogger<LiveWordPressPublisher> _log;

    /// <summary>
    /// §1041 — the write guard. This publisher POSTs to the LIVE WordPress site that DEV and PROD
    /// share, and it was never wired to the guard. ⚠️ It creates DRAFTS only (§31), so the blast
    /// radius is smaller than the Company Manager writes that caused the incident — but a stream of
    /// draft posts appearing on the public site from a DEV test is still DEV reaching outward, which
    /// the operator's rule forbids: <i>"sending data out from dev is controlled"</i>.
    /// </summary>
    private readonly IExternalWriteGuard _writes;

    public LiveWordPressPublisher(
        HttpClient http, WordPressOptions options, ILogger<LiveWordPressPublisher> log,
        IExternalWriteGuard? writes = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _writes = writes ?? new AllowAllExternalWrites();

        if (CanWrite)
        {
            var creds = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_options.Username}:{_options.AppPassword}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CommunityHub/1.0");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }
    }

    public bool CanWrite =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.SiteUrl)
        && !string.IsNullOrWhiteSpace(_options.Username)
        && !string.IsNullOrWhiteSpace(_options.AppPassword);

    public async Task<WordPressPublishResult> CreateDraftAsync(
        WordPressDraft draft, CancellationToken ct = default)
    {
        if (!CanWrite)
            return new WordPressPublishResult(false, null, null,
                "WordPress connector is not configured — no draft created.");

        // 🔴 §1041 — DEV must not create posts on the shared live site, draft or otherwise.
        if (!await _writes.AllowAsync(ExternalSystems.Webshop, "CreateWordPressDraft", ct))
            return new WordPressPublishResult(false, null, null,
                "External writes to the webshop are blocked on this host — no draft created.");

        var endpoint = $"{_options.SiteUrl.TrimEnd('/')}/wp-json/wp/v2/posts";

        // INVARIANT (§31): always a DRAFT. No publish path in v1.
        var payload = new Dictionary<string, object?>
        {
            ["title"] = draft.Title,
            ["content"] = draft.ContentHtml,
            ["status"] = "draft",
        };
        if (!string.IsNullOrWhiteSpace(draft.Excerpt)) payload["excerpt"] = draft.Excerpt;
        if (_options.CategoryIds.Length > 0) payload["categories"] = _options.CategoryIds;

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, new UTF8Encoding(false), "application/json"),
        };

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("WordPress draft create failed: {Status} {Body}",
                    (int)resp.StatusCode, Truncate(body, 500));
                return new WordPressPublishResult(false, null, null,
                    $"WordPress returned {(int)resp.StatusCode}: {Truncate(body, 300)}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            long? id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var v) ? v : null;
            var editUrl = id is not null
                ? $"{_options.SiteUrl.TrimEnd('/')}/wp-admin/post.php?post={id}&action=edit"
                : null;
            return new WordPressPublishResult(true, id, editUrl,
                $"Draft created in WordPress (post {id}). Validate it in wp-admin before publishing.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WordPress draft create threw.");
            return new WordPressPublishResult(false, null, null, $"WordPress call failed: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
