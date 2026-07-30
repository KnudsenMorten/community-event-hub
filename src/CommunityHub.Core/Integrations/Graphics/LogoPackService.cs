using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>The logo-pack zip, ready to stream as a download.</summary>
/// <param name="Content">The raw file bytes (fetched with the app's SharePoint creds).</param>
/// <param name="ContentType">The MIME type (zip).</param>
/// <param name="FileName">The pack file name (leaf, e.g. <c>LogoPack.zip</c>).</param>
public sealed record LogoPackFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// §326f-c (operator 2026-07-25: "have everything run on the app reg to make it
/// consistent"): SERVER-PROXIED download of the operator-prepared logo pack (Experts Live
/// DK + ELDK27 event logos, a zip in the configured SharePoint folder,
/// <see cref="GraphicsSharePointOptions.LogoPackFolderPath"/>). Streams the FIRST zip in
/// the folder through CEH using the app registration's own SharePoint credentials — the
/// speaker/sponsor gets a direct browser download and never touches SharePoint (no share
/// link, no sharing-scope surprises). Mirrors <see cref="SpeakerTemplateService"/>
/// (proxy + ~15 min cache). INERT (returns null) when the store isn't wired or the folder
/// is unset — the nav item is then a dead 404, so keep the config in place.
/// </summary>
public sealed class LogoPackService
{
    /// <summary>How long the resolved pack stays cached (live-replace window).</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LogoPackService>? _log;

    public LogoPackService(
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        IMemoryCache cache,
        ILogger<LogoPackService>? log = null)
    {
        _store = store;
        _options = options.Value;
        _cache = cache;
        _log = log;
    }

    private string? Folder => string.IsNullOrWhiteSpace(_options.LogoPackFolderPath)
        ? null
        : _options.LogoPackFolderPath.Trim().Trim('/');

    /// <summary>True when the live proxy can serve the pack (store wired + folder set).</summary>
    public bool IsAvailable => _store.CanRead && Folder is not null;

    /// <summary>
    /// Download the logo-pack bytes from SharePoint via the app's creds (cached ~15 min).
    /// Returns null when the proxy isn't configured, the folder has no zip, or the
    /// download fails. Never exposes a SharePoint URL.
    /// </summary>
    public async Task<LogoPackFile?> GetAsync(CancellationToken ct = default)
    {
        var folder = Folder;
        if (folder is null || !_store.CanRead) return null;

        const string cacheKey = "logopack:file";
        if (_cache.TryGetValue(cacheKey, out LogoPackFile? cached) && cached is not null)
            return cached;

        SharePointFileRef? match;
        try
        {
            var files = await _store.ListAsync(folder, ct);
            // Only zips are servable (no open proxy of arbitrary types); stable order by name.
            match = files
                .Where(f => Path.GetExtension(f.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "LogoPack: listing folder {Folder} failed.", folder);
            return null;
        }
        if (match is null) return null;

        byte[]? bytes;
        try
        {
            bytes = await _store.DownloadAsync(match.ItemId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "LogoPack: download of {File} failed.", match.Name);
            return null;
        }
        if (bytes is null || bytes.Length == 0) return null;

        var file = new LogoPackFile(bytes, "application/zip", match.Name);
        _cache.Set(cacheKey, file, CacheTtl);
        return file;
    }
}
