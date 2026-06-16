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
}
