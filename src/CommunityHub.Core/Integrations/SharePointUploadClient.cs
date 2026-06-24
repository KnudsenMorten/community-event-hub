using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CommunityHub.Core.Security;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// SharePoint Online / Microsoft Graph auth settings. ONLY the SPN credentials
/// live here (deployment-environment scoped, pulled from Key Vault). The
/// per-edition site URL / drive / root folder path live in
/// <c>event.&lt;edition&gt;.json -&gt; sharepoint</c> and are passed as method
/// arguments to <see cref="SharePointUploadClient"/>.
/// </summary>
public sealed class SharePointUploadOptions
{
    public const string SectionName = "SharePoint";

    /// <summary>Enable / disable the integration. When false, the engine
    /// skips folder provisioning and the upload-folder placeholders fall
    /// back to the generic <c>uploadPortalUrl</c>.</summary>
    public bool Enabled { get; set; }

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Legacy client-secret. The ELDK SPNs are certificate-only (secrets were
    /// deleted), so this is normally empty; certificate auth (<see cref="CertificateThumbprint"/>)
    /// is preferred. Kept only for non-ELDK / local-dev fallback.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Thumbprint of the SPN's certificate in the machine certificate store
    /// (<c>LocalMachine\My</c>, then <c>CurrentUser\My</c>). When set, the client
    /// authenticates to Microsoft Graph with a signed client-assertion (JWT)
    /// instead of a client secret — the ELDK Code-Management SPN model.
    /// </summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    /// <summary>True when configured for certificate auth (preferred).</summary>
    public bool UsesCertificate => !string.IsNullOrWhiteSpace(CertificateThumbprint);

    /// <summary>Days after which the anonymous edit links expire. 0 = no expiry.</summary>
    public int LinkExpiryDays { get; set; } = 365;
}

/// <summary>One provisioned upload folder + its sponsor-facing shareable URL.</summary>
public sealed record SharePointUploadFolder(
    string FolderPath,
    string WebUrl);

/// <summary>One file observed inside a SharePoint folder by the upload watcher.</summary>
public sealed record SharePointFileSnapshot(
    string ItemId,
    string Name,
    string? ETag,
    DateTimeOffset? LastModifiedUtc);

/// <summary>
/// Microsoft Graph client for SharePoint Online. Pre-creates per-sponsor
/// upload folders (<c>{rootFolderPath}/{CompanyName}/{subfolder}</c>) and mints
/// anonymous "edit" sharing links so sponsors click one button instead of
/// navigating + creating folders + uploading.
///
/// Auth: client-credentials flow. The SPN must be granted
/// <c>Sites.Selected</c> (least-privilege) by tenant admin, then site admin
/// must grant the app Write access on the target SharePoint site via
/// <c>POST /sites/{site-id}/permissions</c> or PnP <c>Grant-PnPAzureADAppSitePermission</c>.
/// Without that grant the Graph site lookup returns 403.
///
/// Site / drive ids are cached in-memory per site URL across calls.
/// </summary>
public sealed class SharePointUploadClient
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _http;
    private readonly SharePointUploadOptions _options;

    private string? _cachedAccessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, string> _siteIdCache  = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _driveIdCache = new(StringComparer.OrdinalIgnoreCase);

    public SharePointUploadClient(HttpClient http, SharePointUploadOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>True when SPN credentials are present. Site URL / path are passed per-call.
    /// Certificate auth (preferred) or a legacy client secret satisfies the credential check.</summary>
    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.TenantId)
        && !string.IsNullOrWhiteSpace(_options.ClientId)
        && (_options.UsesCertificate || !string.IsNullOrWhiteSpace(_options.ClientSecret));

    /// <summary>
    /// Ensure a folder exists at <c>{rootFolderPath}/{relativePath}</c> on the
    /// specified SharePoint site, then return an anonymous edit-link URL the
    /// sponsor can click to upload / overwrite / delete files in that folder.
    /// Idempotent -- repeated calls return the same shareable URL (Graph
    /// dedups createLink on type+scope).
    /// </summary>
    public async Task<SharePointUploadFolder> EnsureFolderWithEditLinkAsync(
        string siteUrl,
        string driveName,
        string rootFolderPath,
        string relativePath,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }
        if (string.IsNullOrWhiteSpace(siteUrl))
        {
            throw new SharePointUploadException("siteUrl is required.");
        }

        var fullPath = JoinPath(rootFolderPath, relativePath);
        var driveId  = await GetDriveIdAsync(siteUrl, driveName, ct);

        var folderId = await EnsureFolderAsync(driveId, fullPath, ct);
        var webUrl   = await CreateOrGetAnonymousEditLinkAsync(driveId, folderId, ct);

        return new SharePointUploadFolder(FolderPath: fullPath, WebUrl: webUrl);
    }

    /// <summary>
    /// List every (non-folder) file directly inside the given folder on the
    /// SharePoint site. Used by the upload watcher to diff against the
    /// last-known state and detect new / replaced uploads. Returns an empty
    /// list if the folder is missing (e.g. provisioned then deleted) so the
    /// watcher tolerates a stale row without crashing.
    /// </summary>
    public async Task<IReadOnlyList<SharePointFileSnapshot>> ListFolderFilesAsync(
        string siteUrl,
        string driveName,
        string folderPath,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);
        var encoded = EncodePath(folderPath);

        // Page through every child. Graph returns at most $top items per page and
        // an @odata.nextLink (an absolute URL) when more remain; follow it until
        // exhausted so folders with more than one page are listed completely.
        var results = new List<SharePointFileSnapshot>();
        string? nextUrl = GraphRoot + $"/drives/{driveId}/root:/{encoded}:/children?$top=200";

        while (nextUrl is not null)
        {
            var resp = await GraphGetAbsoluteOrNullAsync(nextUrl, ct);
            if (resp is null) return results;   // folder missing => stop, tolerate

            if (resp.Value.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    // Skip sub-folders -- we only notify on file uploads.
                    if (item.TryGetProperty("folder", out _)) continue;

                    var id   = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

                    string? etag = null;
                    if (item.TryGetProperty("eTag", out var et) && et.ValueKind == JsonValueKind.String)
                    {
                        etag = et.GetString();
                    }
                    else if (item.TryGetProperty("cTag", out var ct2) && ct2.ValueKind == JsonValueKind.String)
                    {
                        etag = ct2.GetString();
                    }

                    DateTimeOffset? lastMod = null;
                    if (item.TryGetProperty("lastModifiedDateTime", out var lm)
                        && lm.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(lm.GetString(), out var lmParsed))
                    {
                        lastMod = lmParsed;
                    }

                    results.Add(new SharePointFileSnapshot(id!, name!, etag, lastMod));
                }
            }

            // Follow @odata.nextLink (absolute URL) if there is another page.
            nextUrl = resp.Value.TryGetProperty("@odata.nextLink", out var nl)
                      && nl.ValueKind == JsonValueKind.String
                ? nl.GetString()
                : null;
        }

        return results;
    }

    /// <summary>
    /// Upload (create or REPLACE) a small file's bytes at
    /// <c>{rootFolderPath}/{relativePath}</c> on the site, returning the stored
    /// path + a download/web URL + the Graph driveItem id. Replacing an existing
    /// path keeps the same item / URL (the overrule contract for SoMe graphics).
    /// Intermediate folders are created as needed. Uses the simple PUT upload
    /// (fine for graphics PNGs, well under the 4&#160;MB simple-upload limit).
    /// </summary>
    public async Task<(string Path, string WebUrl, string ItemId)> UploadFileAsync(
        string siteUrl,
        string driveName,
        string rootFolderPath,
        string relativePath,
        byte[] content,
        string contentType,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);
        var fullPath = JoinPath(rootFolderPath, relativePath);

        // Ensure the parent folder chain exists so the PUT lands.
        var lastSlash = fullPath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            await EnsureFolderAsync(driveId, fullPath[..lastSlash], ct);
        }

        var encoded = EncodePath(fullPath);
        var url = GraphRoot + $"/drives/{driveId}/root:/{encoded}:/content";

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(content),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph PUT content {fullPath} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }

        using var doc = JsonDocument.Parse(raw);
        var item = doc.RootElement;
        var itemId = item.GetProperty("id").GetString()
            ?? throw new SharePointUploadException("Upload response missing id.");
        var webUrl = item.TryGetProperty("webUrl", out var w) && w.ValueKind == JsonValueKind.String
            ? w.GetString() ?? string.Empty
            : string.Empty;

        return (fullPath, webUrl, itemId);
    }

    /// <summary>
    /// Download the raw bytes of a drive item by its Graph id. Used to COPY a
    /// sponsor-uploaded file into a second folder (download here, then
    /// <see cref="UploadFileAsync"/> to the destination) — a download+upload copy
    /// gives clean overwrite semantics when a sponsor re-uploads the same name.
    /// Returns null if the item no longer exists (tolerated by callers).
    /// </summary>
    public async Task<byte[]?> DownloadItemContentAsync(
        string siteUrl,
        string driveName,
        string itemId,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);

        using var req = new HttpRequestMessage(
            HttpMethod.Get, GraphRoot + $"/drives/{driveId}/items/{itemId}/content");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // gone — tolerate
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            throw new SharePointUploadException(
                $"Graph GET item content {itemId} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Delete the file at <c>{rootFolderPath}/{relativePath}</c>. Idempotent — a
    /// 404 (already gone) is treated as success so callers can delete freely.
    /// </summary>
    public async Task DeleteFileAsync(
        string siteUrl,
        string driveName,
        string rootFolderPath,
        string relativePath,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);
        var fullPath = JoinPath(rootFolderPath, relativePath);
        var encoded = EncodePath(fullPath);

        using var req = new HttpRequestMessage(
            HttpMethod.Delete, GraphRoot + $"/drives/{driveId}/root:/{encoded}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // already gone — idempotent
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            throw new SharePointUploadException(
                $"Graph DELETE {fullPath} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }
    }

    // ----- internals -------------------------------------------------------

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddMinutes(-2))
        {
            return _cachedAccessToken;
        }

        var tokenUrl = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

        var form = new Dictionary<string, string>
        {
            ["client_id"]  = _options.ClientId,
            ["scope"]      = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        };

        if (_options.UsesCertificate)
        {
            // Certificate auth (preferred, ELDK Code-Management): sign a JWT
            // client-assertion with the SPN's private key — no secret on the wire.
            using var cert = CertificateLoader.LoadByThumbprint(_options.CertificateThumbprint);
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"]      = ClientAssertionJwt.Build(_options.ClientId, tokenUrl, cert);
        }
        else
        {
            form["client_secret"] = _options.ClientSecret;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph token request failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }

        using var doc = JsonDocument.Parse(raw);
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new SharePointUploadException("Graph token response missing access_token.");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n)
            ? n : 3600;

        _cachedAccessToken = token;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return token;
    }

    private async Task<string> GetSiteIdAsync(string siteUrl, CancellationToken ct)
    {
        if (_siteIdCache.TryGetValue(siteUrl, out var cached)) return cached;

        var uri = new Uri(siteUrl);
        var sitePath = uri.AbsolutePath.TrimEnd('/');
        var graphPath = string.IsNullOrEmpty(sitePath) || sitePath == "/"
            ? $"/sites/{uri.Host}"
            : $"/sites/{uri.Host}:{sitePath}";

        var resp = await GraphGetAsync(graphPath, ct);
        var siteId = resp.GetProperty("id").GetString()
            ?? throw new SharePointUploadException("Graph /sites response missing id.");
        _siteIdCache[siteUrl] = siteId;
        return siteId;
    }

    private async Task<string> GetDriveIdAsync(string siteUrl, string driveName, CancellationToken ct)
    {
        var cacheKey = $"{siteUrl}|{driveName}";
        if (_driveIdCache.TryGetValue(cacheKey, out var cached)) return cached;

        var siteId = await GetSiteIdAsync(siteUrl, ct);
        string driveId;

        if (string.IsNullOrWhiteSpace(driveName))
        {
            var resp = await GraphGetAsync($"/sites/{siteId}/drive", ct);
            driveId = resp.GetProperty("id").GetString()
                ?? throw new SharePointUploadException("Graph /sites/{id}/drive response missing id.");
        }
        else
        {
            var list = await GraphGetAsync($"/sites/{siteId}/drives", ct);
            string? matchId = null;
            if (list.TryGetProperty("value", out var drives) && drives.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in drives.EnumerateArray())
                {
                    if (d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        && string.Equals(n.GetString(), driveName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchId = d.GetProperty("id").GetString();
                        break;
                    }
                }
            }
            driveId = matchId
                ?? throw new SharePointUploadException(
                    $"Drive named '{driveName}' not found on site '{siteUrl}'.");
        }

        _driveIdCache[cacheKey] = driveId;
        return driveId;
    }

    private async Task<string> EnsureFolderAsync(string driveId, string fullPath, CancellationToken ct)
    {
        var encoded = EncodePath(fullPath);

        var existing = await GraphGetOrNullAsync($"/drives/{driveId}/root:/{encoded}", ct);
        if (existing is not null)
        {
            return existing.Value.GetProperty("id").GetString()
                ?? throw new SharePointUploadException("Existing folder response missing id.");
        }

        // Walk path segment-by-segment so each missing intermediate folder is
        // created under its (now existing) parent. SharePoint cannot create
        // deep paths in a single POST.
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var built = string.Empty;
        var lastId = string.Empty;
        for (var i = 0; i < segments.Length; i++)
        {
            var segName = segments[i];
            var parentPath = built;
            built = string.IsNullOrEmpty(built) ? segName : $"{built}/{segName}";

            var segEncoded = EncodePath(built);
            var segExisting = await GraphGetOrNullAsync($"/drives/{driveId}/root:/{segEncoded}", ct);
            if (segExisting is not null)
            {
                lastId = segExisting.Value.GetProperty("id").GetString() ?? string.Empty;
                continue;
            }

            var createUrl = string.IsNullOrEmpty(parentPath)
                ? $"/drives/{driveId}/root/children"
                : $"/drives/{driveId}/root:/{EncodePath(parentPath)}:/children";

            var body = new Dictionary<string, object?>
            {
                ["name"] = segName,
                ["folder"] = new Dictionary<string, object?>(),
                ["@microsoft.graph.conflictBehavior"] = "fail",
            };

            var created = await GraphPostAsync(createUrl, body, ct);
            lastId = created.GetProperty("id").GetString()
                ?? throw new SharePointUploadException("Folder-create response missing id.");
        }

        return lastId;
    }

    private async Task<string> CreateOrGetAnonymousEditLinkAsync(string driveId, string folderId, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"]  = "edit",
            ["scope"] = "anonymous",
        };
        if (_options.LinkExpiryDays > 0)
        {
            body["expirationDateTime"] = DateTimeOffset.UtcNow
                .AddDays(_options.LinkExpiryDays)
                .ToString("o");
        }

        var resp = await GraphPostAsync($"/drives/{driveId}/items/{folderId}/createLink", body, ct);
        return resp.GetProperty("link").GetProperty("webUrl").GetString()
            ?? throw new SharePointUploadException("createLink response missing link.webUrl.");
    }

    private async Task<JsonElement> GraphGetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GraphRoot + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph GET {path} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private Task<JsonElement?> GraphGetOrNullAsync(string path, CancellationToken ct)
        => GraphGetAbsoluteOrNullAsync(GraphRoot + path, ct);

    /// <summary>
    /// GET an absolute Graph URL (used both for a composed path and for an
    /// @odata.nextLink, which Graph returns as a fully-qualified URL).
    /// </summary>
    private async Task<JsonElement?> GraphGetAbsoluteOrNullAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph GET {url} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private async Task<JsonElement> GraphPostAsync(string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphRoot + path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph POST {path} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
        }
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private static string JoinPath(string root, string rel)
    {
        var r = (root ?? string.Empty).Trim('/');
        var s = (rel  ?? string.Empty).Trim('/');
        if (string.IsNullOrEmpty(r)) return s;
        if (string.IsNullOrEmpty(s)) return r;
        return $"{r}/{s}";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Typed exception so callers can degrade gracefully.</summary>
public sealed class SharePointUploadException : Exception
{
    public SharePointUploadException(string message) : base(message) { }
    public SharePointUploadException(string message, Exception inner) : base(message, inner) { }
}
