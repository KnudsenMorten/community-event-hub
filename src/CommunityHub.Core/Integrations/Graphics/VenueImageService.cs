using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One downloaded venue image, ready to stream as a file response.</summary>
/// <param name="Content">The raw image bytes (fetched with the app's SharePoint creds).</param>
/// <param name="ContentType">The MIME type derived from the file extension.</param>
/// <param name="FileName">The image file name (leaf, sanitized).</param>
public sealed record VenueImageContent(byte[] Content, string ContentType, string FileName);

/// <summary>
/// REUSABLE, SERVER-PROXIED, LIVE venue-image read mechanism (REQUIREMENTS §146).
///
/// End users do NOT have SharePoint access — images are PROXIED through CEH using the
/// app's own SharePoint credentials (the existing §18/§110/§124
/// <see cref="ISharePointFileStore"/> read seam), so no SharePoint URL/link is ever
/// exposed. A page lists a folder's images (<see cref="ListFileNamesAsync"/>) and renders
/// <c>&lt;img src="/venue-image/{folder}/{name}"&gt;</c>; the endpoint streams the bytes via
/// <see cref="GetImageAsync"/>.
///
/// ALLOWLIST (no open proxy): only the four folder keys below resolve, each to a SUBFOLDER
/// under <see cref="GraphicsSharePointOptions.VenueRootFolderPath"/> (e.g.
/// <c>…/EventHub/Venue/Wayfinding</c>). Any other key is rejected. The requested file name is
/// sanitized (<see cref="SanitizeFileName"/>) so no path traversal is possible.
///
/// CACHING: both the folder listing and the downloaded bytes are held in
/// <see cref="IMemoryCache"/> for <see cref="CacheTtl"/> (~15 min) so replacing a file on
/// SharePoint auto-updates within the window without a redeploy.
///
/// INERT until configured: with no wired store (<see cref="ISharePointFileStore.CanRead"/>
/// false) or no <see cref="GraphicsSharePointOptions.VenueRootFolderPath"/>, every read
/// returns empty/null — nothing is faked. The web layer then applies the committed
/// wwwroot fallback (SharePoint is the source of truth when reachable).
/// </summary>
public sealed class VenueImageService
{
    /// <summary>How long a folder listing / image bytes stay cached (live-replace window).</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    // ALLOWLIST: external folder KEY -> SharePoint Venue SUBFOLDER name. Ordinal-ignore-case
    // keys; the values are the exact subfolder names under the Venue root on SharePoint.
    private static readonly IReadOnlyDictionary<string, string> Allowlist =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wayfinding"]   = "Wayfinding",
            ["good-to-know"] = "Good to know",
            ["evaluations"]  = "Evaluations",
            ["expo"]         = "Expo",
        };

    // /Info/{slug} content-page slug -> venue folder KEY (the slug is not always the key:
    // the "session-evaluations" content page maps to the "evaluations" folder).
    private static readonly IReadOnlyDictionary<string, string> SlugToFolderKey =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wayfinding"]          = "wayfinding",
            // §162: the "Venue images" gallery dropped from /Info/good-to-know (not relevant there).
            ["session-evaluations"] = "evaluations",
        };

    private static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg",
        };

    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<VenueImageService>? _log;

    public VenueImageService(
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        IMemoryCache cache,
        ILogger<VenueImageService>? log = null)
    {
        _store = store;
        _options = options.Value;
        _cache = cache;
        _log = log;
    }

    /// <summary>The allowlisted folder keys (for callers that enumerate / validate).</summary>
    public static IReadOnlyCollection<string> AllowedFolderKeys => Allowlist.Keys.ToArray();

    /// <summary>True when <paramref name="folderKey"/> is on the allowlist (no open proxy).</summary>
    public static bool IsAllowedFolder(string? folderKey) =>
        !string.IsNullOrWhiteSpace(folderKey) && Allowlist.ContainsKey(folderKey.Trim());

    /// <summary>
    /// The venue folder KEY for an /Info content-page <paramref name="slug"/>, or null when
    /// that slug has no mapped venue folder (so the page renders no gallery).
    /// </summary>
    public static string? FolderForSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugToFolderKey.TryGetValue(slug.Trim(), out var key) ? key : null;

    /// <summary>True when <paramref name="fileName"/> has a recognised image extension.</summary>
    public static bool IsImageFile(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && ImageExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// Drive-relative SharePoint folder for an allowlisted key (root + subfolder), or null
    /// when the key is rejected OR the Venue root is unset (inert).
    /// </summary>
    public string? ResolveFolderPath(string? folderKey)
    {
        if (string.IsNullOrWhiteSpace(folderKey)) return null;
        if (!Allowlist.TryGetValue(folderKey.Trim(), out var sub)) return null;
        var root = _options.VenueRootFolderPath?.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(root)) return null;
        return $"{root}/{sub}";
    }

    /// <summary>True when the live SharePoint venue proxy can read (store wired + root set).</summary>
    public bool CanRead => _store.CanRead && !string.IsNullOrWhiteSpace(_options.VenueRootFolderPath);

    /// <summary>
    /// Sanitize a requested file name to a safe LEAF: rejects path separators, traversal
    /// (<c>..</c>), control / OS-invalid chars, and anything without an image extension.
    /// Returns the safe name, or null when the request is invalid (caller 404s / rejects).
    /// </summary>
    public static string? SanitizeFileName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var name = file.Trim();

        // No directory components — only a single file leaf is ever valid.
        if (name.Contains('/') || name.Contains('\\')) return null;
        if (name.Contains("..")) return null;
        if (name is "." or "..") return null;

        // Reject control chars + OS-invalid file-name chars (defense in depth).
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        // Must be an image (also blocks extension-less / unexpected payloads).
        if (!IsImageFile(name)) return null;

        return name;
    }

    /// <summary>The MIME type for a file name, by extension (image types + a safe default).</summary>
    public static string ContentTypeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// List the image file refs in an allowlisted folder (cached ~15 min). Returns empty
    /// when the key is rejected / not configured / the folder is empty or unreachable — the
    /// caller then applies the committed fallback. Never throws on a missing folder.
    /// </summary>
    public async Task<IReadOnlyList<SharePointFileRef>> ListRefsAsync(
        string folderKey, CancellationToken ct = default)
    {
        var folder = ResolveFolderPath(folderKey);
        if (folder is null || !CanRead) return Array.Empty<SharePointFileRef>();

        var cacheKey = $"venueimg:list:{folder}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<SharePointFileRef>? cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<SharePointFileRef> refs;
        try
        {
            var files = await _store.ListAsync(folder, ct);
            refs = files
                .Where(f => IsImageFile(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // SharePoint hiccup: treat as empty so the committed fallback kicks in.
            _log?.LogWarning(ex,
                "VenueImage: listing folder {Folder} failed; treating as empty (fallback applies).", folder);
            refs = Array.Empty<SharePointFileRef>();
        }

        _cache.Set(cacheKey, refs, CacheTtl);
        return refs;
    }

    /// <summary>List the image file NAMES in an allowlisted folder (cached ~15 min).</summary>
    public async Task<IReadOnlyList<string>> ListFileNamesAsync(
        string folderKey, CancellationToken ct = default) =>
        (await ListRefsAsync(folderKey, ct)).Select(r => r.Name).ToList();

    /// <summary>
    /// Download one image's bytes from SharePoint via the app's creds (cached ~15 min).
    /// Returns null when the key is rejected, the file name is invalid (traversal etc.),
    /// the proxy is not configured, the file is not in the folder, or the download fails —
    /// the caller then applies the committed fallback. NEVER exposes a SharePoint URL.
    /// </summary>
    public async Task<VenueImageContent?> GetImageAsync(
        string folderKey, string fileName, CancellationToken ct = default)
    {
        var safe = SanitizeFileName(fileName);
        if (safe is null) return null;
        if (ResolveFolderPath(folderKey) is null || !CanRead) return null;

        var folder = ResolveFolderPath(folderKey)!;
        var cacheKey = $"venueimg:bytes:{folder}/{safe}";
        if (_cache.TryGetValue(cacheKey, out VenueImageContent? cached) && cached is not null)
        {
            return cached;
        }

        // Resolve the Graph item id from the (cached) listing, by name.
        var refs = await ListRefsAsync(folderKey, ct);
        var match = refs.FirstOrDefault(r => string.Equals(r.Name, safe, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        byte[]? bytes;
        try
        {
            bytes = await _store.DownloadAsync(match.ItemId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "VenueImage: download of {Folder}/{File} failed.", folder, safe);
            return null;
        }
        if (bytes is null || bytes.Length == 0) return null;

        var content = new VenueImageContent(bytes, ContentTypeFor(match.Name), match.Name);
        _cache.Set(cacheKey, content, CacheTtl);
        return content;
    }
}
