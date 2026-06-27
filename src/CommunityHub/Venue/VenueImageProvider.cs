using CommunityHub.Core.Integrations.Graphics;

namespace CommunityHub.Venue;

/// <summary>One renderable venue image: a CEH PROXY url + the file name + alt text.</summary>
/// <param name="FileName">The image file name (leaf).</param>
/// <param name="Url">The CEH proxy URL (<c>/venue-image/{folder}/{name}</c>) — NEVER a SharePoint link.</param>
/// <param name="Alt">A human-friendly alt text derived from the file name.</param>
public sealed record VenueGalleryImage(string FileName, string Url, string Alt);

/// <summary>
/// Web-side façade over the reusable <see cref="VenueImageService"/> (REQUIREMENTS §146)
/// that adds the COMMITTED-wwwroot FALLBACK: SharePoint (live, app-credentialed) is the
/// source of truth when reachable; otherwise the committed images under
/// <c>wwwroot/content/eldk27/{folderKey}/</c> are used (e.g. the wayfinding floor plans),
/// or a graceful empty state.
///
/// EVERY rendered image points at the CEH proxy endpoint <c>/venue-image/{folder}/{name}</c>
/// — both live and fallback — so the URL shape is uniform and no SharePoint link is ever
/// exposed to the user. The endpoint resolves bytes via <see cref="GetImageAsync"/>
/// (SharePoint first, committed file second).
/// </summary>
public sealed class VenueImageProvider
{
    // Committed fallback root under wwwroot (per-edition content folder).
    private const string FallbackContentRoot = "content/eldk27";

    private readonly VenueImageService _service;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<VenueImageProvider>? _log;

    public VenueImageProvider(
        VenueImageService service, IWebHostEnvironment env, ILogger<VenueImageProvider>? log = null)
    {
        _service = service;
        _env = env;
        _log = log;
    }

    /// <summary>The venue folder KEY for an /Info content-page slug (null when unmapped).</summary>
    public string? FolderForSlug(string? slug) => VenueImageService.FolderForSlug(slug);

    /// <summary>
    /// The gallery for an allowlisted folder: LIVE SharePoint image names when present, else
    /// the committed wwwroot fallback, else empty. Every item carries the CEH proxy URL.
    /// </summary>
    public async Task<IReadOnlyList<VenueGalleryImage>> GetGalleryAsync(
        string folderKey, CancellationToken ct = default)
    {
        if (!VenueImageService.IsAllowedFolder(folderKey)) return Array.Empty<VenueGalleryImage>();

        var names = await _service.ListFileNamesAsync(folderKey, ct);
        if (names.Count == 0)
        {
            names = CommittedFileNames(folderKey);
        }

        return names
            .Select(n => new VenueGalleryImage(
                n, $"/venue-image/{Uri.EscapeDataString(folderKey)}/{Uri.EscapeDataString(n)}", PrettyAlt(n)))
            .ToList();
    }

    /// <summary>
    /// Resolve the bytes for the proxy endpoint: SharePoint (app creds) first, else the
    /// committed wwwroot file, else null (the endpoint 404s). The file name is sanitized
    /// (no traversal) and an unknown folder key is rejected.
    /// </summary>
    public async Task<VenueImageContent?> GetImageAsync(
        string folderKey, string fileName, CancellationToken ct = default)
    {
        if (!VenueImageService.IsAllowedFolder(folderKey)) return null;
        var safe = VenueImageService.SanitizeFileName(fileName);
        if (safe is null) return null;

        // Live SharePoint (cached) — the source of truth when reachable.
        var live = await _service.GetImageAsync(folderKey, safe, ct);
        if (live is not null) return live;

        // Committed wwwroot fallback.
        var dir = FallbackDir(folderKey);
        if (dir is null) return null;
        var fullPath = Path.GetFullPath(Path.Combine(dir, safe));
        // Defense in depth: the resolved path must stay inside the fallback folder.
        if (!fullPath.StartsWith(EnsureTrailingSep(dir), StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(fullPath)) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            return new VenueImageContent(bytes, VenueImageService.ContentTypeFor(safe), safe);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "VenueImage: reading committed fallback {Path} failed.", fullPath);
            return null;
        }
    }

    // --- committed-fallback helpers ----------------------------------------

    private string? FallbackDir(string folderKey)
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot)) return null;
        return Path.GetFullPath(Path.Combine(
            webRoot,
            FallbackContentRoot.Replace('/', Path.DirectorySeparatorChar),
            folderKey));
    }

    private IReadOnlyList<string> CommittedFileNames(string folderKey)
    {
        var dir = FallbackDir(folderKey);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && VenueImageService.IsImageFile(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string EnsureTrailingSep(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    /// <summary>A readable alt text from a file name (drop extension, dashes/underscores → spaces).</summary>
    private static string PrettyAlt(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var text = stem.Replace('-', ' ').Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(text) ? fileName : text;
    }
}
