using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Operator config for the SoMe-graphics SharePoint file store (REQUIREMENTS §18).
/// Bound from <c>Graphics:SharePoint</c>. Defaults keep the store INERT: the
/// per-edition site / drive / root folder are operator-entered (placeholder in
/// committed config), so <see cref="IsConfigured"/> is false and the live store is
/// not selected. SPN credentials are NOT here — they live in
/// <see cref="SharePointUploadOptions"/> (deployment-scoped, Key Vault).
/// </summary>
public sealed class GraphicsSharePointOptions
{
    public const string SectionName = "Graphics:SharePoint";

    /// <summary>Master on/off. DEFAULTS FALSE — the null store is used until an operator opts in.</summary>
    public bool Enabled { get; set; }

    /// <summary>SharePoint site URL the graphics are stored on (operator config; placeholder in public files).</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Document library / drive name (blank = the site default drive).</summary>
    public string DriveName { get; set; } = string.Empty;

    /// <summary>Root folder under the drive for all generated graphics (e.g. <c>/Graphics</c>).</summary>
    public string RootFolderPath { get; set; } = "Graphics";

    /// <summary>
    /// Drive-relative folder the operator uploads MASTER CLASS session graphics into
    /// (the PULL source for <see cref="CommunityHub.Core.Domain.SessionType.MasterClass"/>
    /// sessions — REQUIREMENTS §18). EMPTY by default so the pull is INERT (no folder ⇒ nothing
    /// listed) until an operator configures it.
    /// </summary>
    public string MasterClassFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative folder the operator uploads ALL OTHER session graphics into (the
    /// PULL source for non-master-class sessions). EMPTY by default ⇒ inert until set.
    /// </summary>
    public string SessionsFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Drive-relative folder holding the per-ROOM session-evaluation QR codes
    /// (REQUIREMENTS §124) — e.g.
    /// <c>General/Events/ELDK 2027/EventHub/Speakers/SessionEvals-QR</c>. Files are
    /// named with the room in the name (<c>Room-16-Floor-1-Device13.png</c>); a speaker
    /// downloads the QR for their session's ROOM, and an org-admin uploads / replaces
    /// the files here. EMPTY by default ⇒ the feature is INERT (nothing listed,
    /// nothing to download / upload) until an operator configures it.
    /// </summary>
    public string SessionEvalsQrFolderPath { get; set; } = string.Empty;

    /// <summary>True when enabled AND a site URL is present (the live store can run).</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(SiteUrl);
}

/// <summary>
/// Live Microsoft Graph-backed file store. Delegates to the existing
/// <see cref="SharePointUploadClient"/> (Graph auth + folder ensure + byte
/// upload/delete). Selected over the <see cref="NullSharePointFileStore"/> only
/// when <see cref="GraphicsSharePointOptions.IsConfigured"/> AND the upload client
/// has SPN credentials — otherwise the null store is registered (see Program.cs).
/// </summary>
public sealed class GraphSharePointFileStore : ISharePointFileStore
{
    private readonly SharePointUploadClient _client;
    private readonly GraphicsSharePointOptions _options;

    public GraphSharePointFileStore(
        SharePointUploadClient client, IOptions<GraphicsSharePointOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public bool CanStore => _options.IsConfigured && _client.IsConfigured;

    public async Task<StoredFile> StoreAsync(
        string relativePath, byte[] content, string contentType, CancellationToken ct = default)
    {
        var (path, webUrl, itemId) = await _client.UploadFileAsync(
            _options.SiteUrl, _options.DriveName, _options.RootFolderPath,
            relativePath, content, contentType, ct);
        return new StoredFile(path, webUrl, itemId);
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct = default) =>
        _client.DeleteFileAsync(
            _options.SiteUrl, _options.DriveName, _options.RootFolderPath, relativePath, ct);

    public bool CanRead => _options.IsConfigured && _client.IsConfigured;

    public async Task<IReadOnlyList<SharePointFileRef>> ListAsync(
        string relativeFolder, CancellationToken ct = default)
    {
        if (!CanRead || string.IsNullOrWhiteSpace(relativeFolder))
        {
            return Array.Empty<SharePointFileRef>();
        }

        // The configured folder paths are drive-relative (NOT under RootFolderPath),
        // so they are passed straight to the client's folder listing. A missing folder
        // yields an empty list (the client tolerates 404) — the pull stays inert.
        var files = await _client.ListFolderFilesAsync(
            _options.SiteUrl, _options.DriveName, relativeFolder, ct);

        return files
            .Select(f => new SharePointFileRef(f.ItemId, f.Name, f.WebUrl ?? string.Empty))
            .ToList();
    }

    public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
        !CanRead || string.IsNullOrWhiteSpace(itemId)
            ? Task.FromResult<byte[]?>(null)
            : _client.DownloadItemContentAsync(_options.SiteUrl, _options.DriveName, itemId, ct);

    public async Task<StoredFile> UploadToFolderAsync(
        string relativeFolder, string fileName, byte[] content, string contentType,
        CancellationToken ct = default)
    {
        // The folder is DRIVE-RELATIVE (like ListAsync), so pass an empty root and let
        // the relative path carry "<folder>/<file>" — the bytes land in the folder the
        // operator configured, not under the graphics RootFolderPath.
        var relative = $"{relativeFolder.Trim('/')}/{fileName}";
        var (path, webUrl, itemId) = await _client.UploadFileAsync(
            _options.SiteUrl, _options.DriveName, rootFolderPath: string.Empty,
            relative, content, contentType, ct);
        return new StoredFile(path, webUrl, itemId);
    }

    public Task DeleteFromFolderAsync(
        string relativeFolder, string fileName, CancellationToken ct = default) =>
        _client.DeleteFileAsync(
            _options.SiteUrl, _options.DriveName, rootFolderPath: string.Empty,
            $"{relativeFolder.Trim('/')}/{fileName}", ct);
}
