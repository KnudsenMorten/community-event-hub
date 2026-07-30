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
    DateTimeOffset? LastModifiedUtc,
    string? WebUrl = null,
    // §467 — file size in BYTES from the Graph driveItem. Part of the DEFAULT driveItem
    // response (no $select is set), so carrying it costs no extra call. Null when Graph
    // omitted it. Used to decide whether a deck is too large for the embedded Office
    // viewer BEFORE the attendee clicks "View" and lands on an error page.
    long? SizeBytes = null);

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

                    // webUrl is part of the default driveItem response (no $select set);
                    // surfaced so the SoMe-graphics PULL has a display link per file.
                    string? webUrl = null;
                    if (item.TryGetProperty("webUrl", out var wu) && wu.ValueKind == JsonValueKind.String)
                    {
                        webUrl = wu.GetString();
                    }

                    // §467 — size comes back on the default driveItem; no extra request.
                    long? sizeBytes = null;
                    if (item.TryGetProperty("size", out var szEl)
                        && szEl.ValueKind == JsonValueKind.Number
                        && szEl.TryGetInt64(out var sz))
                    {
                        sizeBytes = sz;
                    }

                    results.Add(new SharePointFileSnapshot(id!, name!, etag, lastMod, webUrl, sizeBytes));
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

        // §322b: files beyond the Graph SIMPLE-upload comfort zone go through an UPLOAD
        // SESSION (chunked) — SharePoint itself has no 250 MB limit, only the single PUT
        // does. Small files keep the one-call PUT.
        const int SimpleUploadLimit = 4 * 1024 * 1024;
        string raw;
        if (content.Length > SimpleUploadLimit)
        {
            raw = await UploadViaSessionAsync(driveId, encoded, fullPath, content, ct);
        }
        else
        {
            var url = GraphRoot + $"/drives/{driveId}/root:/{encoded}:/content";

            using var req = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(content),
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));

            using var resp = await _http.SendAsync(req, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SharePointUploadException(
                    $"Graph PUT content {fullPath} failed (HTTP {(int)resp.StatusCode}): {Truncate(raw, 400)}");
            }
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
    /// §455 — STREAMING upload: the same chunked upload session, but the source is a
    /// <see cref="Stream"/> read one chunk at a time, so a large file is never held in
    /// memory in full.
    ///
    /// <para>Why this exists (operator 2026-07-27, after a speaker's slide upload failed):
    /// the web layer used to copy the whole request body into a <c>MemoryStream</c> and then
    /// call <c>ToArray()</c> — holding a multi-hundred-MB deck in RAM <b>twice</b>, on a shared
    /// App Service instance, before a single byte reached SharePoint. That is memory pressure
    /// and a long pre-upload stall on the request thread. Now the bytes flow
    /// browser → Graph a chunk at a time and peak memory is one chunk.</para>
    ///
    /// <para><paramref name="totalLength"/> must be the real length — Graph requires an exact
    /// <c>Content-Range</c> per chunk and a final chunk that completes the declared total.</para>
    /// </summary>
    public async Task<(string Path, string WebUrl, string ItemId)> UploadFileStreamAsync(
        string siteUrl,
        string driveName,
        string rootFolderPath,
        string relativePath,
        Stream content,
        long totalLength,
        string contentType,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);
        var fullPath = JoinPath(rootFolderPath, relativePath);

        var lastSlash = fullPath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            await EnsureFolderAsync(driveId, fullPath[..lastSlash], ct);
        }

        var encoded = EncodePath(fullPath);
        var raw = await UploadStreamViaSessionAsync(driveId, encoded, fullPath, content, totalLength, ct);

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
    /// §455 — the chunk loop for <see cref="UploadFileStreamAsync"/>. Identical session
    /// protocol to <see cref="UploadViaSessionAsync"/>; the only difference is that each
    /// chunk is READ FROM THE STREAM into one reusable buffer instead of being sliced out of
    /// a fully-materialised array.
    /// </summary>
    private async Task<string> UploadStreamViaSessionAsync(
        string driveId, string encodedPath, string fullPath, Stream content, long total,
        CancellationToken ct)
    {
        var uploadUrl = await CreateUploadSessionAsync(driveId, encodedPath, fullPath, ct);

        const int ChunkSize = 32 * 327_680;   // 10 MiB — a multiple of Graph's 320 KiB granularity
        var buffer = new byte[ChunkSize];
        long offset = 0;

        while (offset < total)
        {
            // Fill the buffer up to ChunkSize (a single ReadAsync may return fewer bytes).
            var want = (int)Math.Min(ChunkSize, total - offset);
            var filled = 0;
            while (filled < want)
            {
                var read = await content.ReadAsync(buffer.AsMemory(filled, want - filled), ct);
                if (read == 0) break;
                filled += read;
            }
            if (filled == 0)
            {
                throw new SharePointUploadException(
                    $"Upload stream for {fullPath} ended early at {offset}/{total} bytes.");
            }

            using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(buffer, 0, filled),
            };
            chunkReq.Content.Headers.ContentLength = filled;
            chunkReq.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(offset, offset + filled - 1, total);

            using var chunkResp = await _http.SendAsync(chunkReq, ct);
            var chunkRaw = await chunkResp.Content.ReadAsStringAsync(ct);
            if (!chunkResp.IsSuccessStatusCode)
            {
                throw new SharePointUploadException(
                    $"Graph upload-session chunk {offset}-{offset + filled - 1}/{total} for {fullPath} "
                    + $"failed (HTTP {(int)chunkResp.StatusCode}): {Truncate(chunkRaw, 400)}");
            }
            if (chunkResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                return chunkRaw;
            }
            offset += filled;
        }

        throw new SharePointUploadException(
            $"Graph upload session for {fullPath} finished without returning the driveItem.");
    }

    /// <summary>
    /// §494 — DIRECT-TO-STORAGE: hand the BROWSER a pre-authenticated upload URL so the file goes
    /// straight to SharePoint and never passes through the web app at all.
    ///
    /// <para><b>Why this is the end state.</b> §455 stopped us holding whole files in memory, but
    /// every byte still travelled browser → App Service → Graph: the request thread was occupied
    /// for the whole upload, a 25 MB brochure on a slow phone tied up a worker for minutes, and the
    /// upload competed with page traffic on a shared instance. Now the bytes bypass us entirely and
    /// the only thing crossing our servers is a short JSON exchange.</para>
    ///
    /// <para><b>Security: the CALLER decides the destination, never the browser.</b> This method
    /// takes a folder and file name resolved SERVER-side from the signed-in user's own company, and
    /// the URL Graph returns is scoped to that ONE item path. A caller must never pass a
    /// client-supplied path in here.</para>
    ///
    /// <para>The session expires on its own (~15 minutes) and replaces the item on conflict, which
    /// matches the versioned-name convention the callers already use.</para>
    /// </summary>
    /// <returns>The pre-authenticated upload URL, plus the drive-relative path it will write to.</returns>
    public async Task<(string UploadUrl, string Path)> BeginDirectUploadAsync(
        string siteUrl, string driveName, string folderPath, string fileName,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new SharePointUploadException("SharePoint integration is not fully configured.");
        }

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);
        var fullPath = JoinPath(folderPath, fileName);

        var lastSlash = fullPath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            await EnsureFolderAsync(driveId, fullPath[..lastSlash], ct);
        }

        var uploadUrl = await CreateUploadSessionAsync(driveId, EncodePath(fullPath), fullPath, ct);
        return (uploadUrl, fullPath);
    }

    /// <summary>
    /// §494 — read back ONE item by its drive-relative path, so a "the browser says it finished"
    /// callback can be VERIFIED against storage instead of believed. Returns null when absent.
    /// </summary>
    public async Task<SharePointFileSnapshot?> GetItemByPathAsync(
        string siteUrl, string driveName, string path, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var driveId = await GetDriveIdAsync(siteUrl, driveName, ct);
        var resp = await GraphGetAbsoluteOrNullAsync(
            GraphRoot + $"/drives/{driveId}/root:/{EncodePath(path)}", ct);
        if (resp is null) return null;

        var item = resp.Value;
        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;

        string? webUrl = item.TryGetProperty("webUrl", out var wu) && wu.ValueKind == JsonValueKind.String
            ? wu.GetString() : null;
        long? size = item.TryGetProperty("size", out var szEl) && szEl.ValueKind == JsonValueKind.Number
                     && szEl.TryGetInt64(out var sz) ? sz : null;

        return new SharePointFileSnapshot(id!, name!, null, null, webUrl, size);
    }

    /// <summary>
    /// createUploadSession + extract the pre-authenticated uploadUrl. Shared by the byte[] and
    /// stream chunk loops so the session protocol exists once.
    /// </summary>
    private async Task<string> CreateUploadSessionAsync(
        string driveId, string encodedPath, string fullPath, CancellationToken ct)
    {
        using var createReq = new HttpRequestMessage(
            HttpMethod.Post, GraphRoot + $"/drives/{driveId}/root:/{encodedPath}:/createUploadSession")
        {
            Content = JsonContent.Create(new
            {
                item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace" },
            }),
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        using var createResp = await _http.SendAsync(createReq, ct);
        var createRaw = await createResp.Content.ReadAsStringAsync(ct);
        if (!createResp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph createUploadSession {fullPath} failed (HTTP {(int)createResp.StatusCode}): {Truncate(createRaw, 400)}");
        }
        using var sessionDoc = JsonDocument.Parse(createRaw);
        var uploadUrl = sessionDoc.RootElement.TryGetProperty("uploadUrl", out var u) ? u.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new SharePointUploadException($"Graph createUploadSession {fullPath}: no uploadUrl in response.");
        }
        return uploadUrl;
    }

    /// <summary>
    /// §322b: upload a LARGE file through a Graph UPLOAD SESSION — createUploadSession,
    /// then sequential Content-Range chunk PUTs against the pre-authenticated session URL.
    /// This is the mechanism behind SharePoint's 250 GB per-file support (the ~250 MB cap
    /// applies only to the single-PUT simple upload / list-item attachments). Chunks are
    /// 10 MiB (a multiple of the required 320 KiB granularity); conflictBehavior=replace
    /// keeps the same-path-overwrite contract. Returns the final driveItem JSON.
    /// </summary>
    private async Task<string> UploadViaSessionAsync(
        string driveId, string encodedPath, string fullPath, byte[] content, CancellationToken ct)
    {
        // 1. Create the session (authenticated; the returned uploadUrl is pre-authorized).
        using var createReq = new HttpRequestMessage(
            HttpMethod.Post, GraphRoot + $"/drives/{driveId}/root:/{encodedPath}:/createUploadSession")
        {
            Content = JsonContent.Create(new
            {
                item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace" },
            }),
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        using var createResp = await _http.SendAsync(createReq, ct);
        var createRaw = await createResp.Content.ReadAsStringAsync(ct);
        if (!createResp.IsSuccessStatusCode)
        {
            throw new SharePointUploadException(
                $"Graph createUploadSession {fullPath} failed (HTTP {(int)createResp.StatusCode}): {Truncate(createRaw, 400)}");
        }
        string uploadUrl;
        using (var sessionDoc = JsonDocument.Parse(createRaw))
        {
            uploadUrl = sessionDoc.RootElement.TryGetProperty("uploadUrl", out var u) ? u.GetString() ?? "" : "";
        }
        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new SharePointUploadException($"Graph createUploadSession {fullPath}: no uploadUrl in response.");
        }

        // 2. Sequential chunk PUTs. 10 MiB = 32 × the 320 KiB granularity Graph requires.
        const int ChunkSize = 32 * 327_680;
        var total = content.Length;
        for (var offset = 0; offset < total; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, total - offset);
            using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(content, offset, length),
            };
            chunkReq.Content.Headers.ContentLength = length;
            chunkReq.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(offset, offset + length - 1, total);

            using var chunkResp = await _http.SendAsync(chunkReq, ct);
            var chunkRaw = await chunkResp.Content.ReadAsStringAsync(ct);
            if (!chunkResp.IsSuccessStatusCode)
            {
                throw new SharePointUploadException(
                    $"Graph upload-session chunk {offset}-{offset + length - 1}/{total} for {fullPath} "
                    + $"failed (HTTP {(int)chunkResp.StatusCode}): {Truncate(chunkRaw, 400)}");
            }
            // Intermediate chunks answer 202 (nextExpectedRanges); the LAST answers 200/201
            // with the created/updated driveItem.
            if (chunkResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                return chunkRaw;
            }
        }
        throw new SharePointUploadException(
            $"Graph upload session for {fullPath} finished without returning the driveItem.");
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

        // §330 (was §326bg item 4) — REFUSE AN EMPTY RELATIVE PATH.
        //
        // JoinPath(root, "") IS the root folder, so an empty relativePath silently turns
        // "delete one file" into "delete the folder and everything in it". That is reachable
        // today: SessionEvalsQr null-checks the raw FileName but passes
        // Path.GetFileName(...), which returns "" for any value ending in '/'. The guard
        // belongs HERE, not only at that caller — every present and future caller gets it,
        // and no argument to a file delete should ever be able to mean "the container".
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new SharePointUploadException(
                "Refusing to delete: an empty relative path would target the root folder, not a file.");
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
