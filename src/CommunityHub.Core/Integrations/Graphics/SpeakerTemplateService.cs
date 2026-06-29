using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>The speaker presentation template, ready to stream as a download.</summary>
/// <param name="Content">The raw file bytes (fetched with the app's SharePoint creds).</param>
/// <param name="ContentType">The MIME type derived from the file extension.</param>
/// <param name="FileName">The template file name (leaf, e.g. <c>ELDK27_PPT_Template.potx</c>).</param>
public sealed record SpeakerTemplateFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// SERVER-PROXIED, LIVE speaker presentation-template download (§153). The operator drops the
/// template (e.g. <c>ELDK27_PPT_Template.potx</c>) in the configured SharePoint folder
/// (<see cref="GraphicsSharePointOptions.SpeakerTemplateFolderPath"/>); this streams the FIRST
/// (most-recently-named) file there through CEH using the app's own SharePoint credentials — so a
/// speaker gets a DIRECT download (always the current file) instead of a link to the SharePoint
/// site. Mirrors <see cref="VenueImageService"/> (proxy + ~15 min cache; never exposes a
/// SharePoint URL). INERT (returns null) when the store isn't wired or the folder is unset — the
/// page then falls back to the configured template URL.
/// </summary>
public sealed class SpeakerTemplateService
{
    /// <summary>How long the resolved template (ref + bytes) stays cached (live-replace window).</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    // Office / common template extensions we are willing to serve (no open proxy of arbitrary types).
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".potx"] = "application/vnd.openxmlformats-officedocument.presentationml.template",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".ppt"]  = "application/vnd.ms-powerpoint",
            [".pdf"]  = "application/pdf",
        };

    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SpeakerTemplateService>? _log;

    public SpeakerTemplateService(
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        IMemoryCache cache,
        ILogger<SpeakerTemplateService>? log = null)
    {
        _store = store;
        _options = options.Value;
        _cache = cache;
        _log = log;
    }

    private string? Folder => string.IsNullOrWhiteSpace(_options.SpeakerTemplateFolderPath)
        ? null
        : _options.SpeakerTemplateFolderPath.Trim().Trim('/');

    /// <summary>True when the live proxy can serve the template (store wired + folder set).</summary>
    public bool IsAvailable => _store.CanRead && Folder is not null;

    /// <summary>
    /// Download the template bytes from SharePoint via the app's creds (cached ~15 min). Returns
    /// null when the proxy isn't configured, the folder has no servable file, or the download
    /// fails — the caller then falls back to the configured template URL. Never exposes a
    /// SharePoint URL.
    /// </summary>
    public async Task<SpeakerTemplateFile?> GetTemplateAsync(CancellationToken ct = default)
    {
        var folder = Folder;
        if (folder is null || !_store.CanRead) return null;

        const string cacheKey = "speakertemplate:file";
        if (_cache.TryGetValue(cacheKey, out SpeakerTemplateFile? cached) && cached is not null)
            return cached;

        SharePointFileRef? match;
        try
        {
            var files = await _store.ListAsync(folder, ct);
            // The servable template: prefer a .potx, else the first servable type; stable order by name.
            match = files
                .Where(f => ContentTypes.ContainsKey(Path.GetExtension(f.Name)))
                .OrderByDescending(f => Path.GetExtension(f.Name).Equals(".potx", StringComparison.OrdinalIgnoreCase))
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SpeakerTemplate: listing folder {Folder} failed.", folder);
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
            _log?.LogWarning(ex, "SpeakerTemplate: download of {File} failed.", match.Name);
            return null;
        }
        if (bytes is null || bytes.Length == 0) return null;

        var ext = Path.GetExtension(match.Name);
        var ctype = ContentTypes.TryGetValue(ext, out var t) ? t : "application/octet-stream";
        var file = new SpeakerTemplateFile(bytes, ctype, match.Name);
        _cache.Set(cacheKey, file, CacheTtl);
        return file;
    }
}
