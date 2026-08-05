using CommunityHub.Core.Config;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One speaker photo, ready to stream.</summary>
public sealed record SpeakerPhotoFile(byte[] Content, string ContentType);

/// <summary>
/// REQUIREMENTS §665 — SERVER-PROXIED speaker photo.
///
/// <para><b>Why this exists.</b> The sponsor session form uploads a speaker photo to SharePoint and
/// used to store the returned <i>SharePoint document URL</i> in <c>SpeakerProfile.PhotoUrl</c>. A
/// browser rendering <c>&lt;img src="https://…sharepoint.com/…"&gt;</c> sends no SharePoint
/// credentials, so SharePoint answers with a sign-in page and the image renders as a broken glyph —
/// while the form confidently said <i>"Photo on file"</i>. Re-uploading changed nothing, because
/// every upload wrote another equally unfetchable URL (operator 2026-07-29: <i>"it shows the same
/// even after new upload"</i>).</para>
///
/// <para>This restores the platform's standing rule that every other SharePoint-backed asset already
/// follows (§146 venue images, §153 the speaker template, §326f-c the logo pack): the app fetches
/// with its OWN credentials and streams the bytes; an end user never receives a SharePoint URL.</para>
///
/// <para>🔒 <b>Reads the SAME folder the UPLOAD writes to</b> — §768.14: the document-library
/// registry key <c>SpeakerPhotos</c>, which is now what every writer resolves too. It was the
/// edition config's <c>SpeakerPhotoFolderPath</c>; either way the rule is the point — two different
/// sources for one file is how a proxy ends up looking in a folder nothing was ever put in.</para>
///
/// <para>🔒 <b>Bounded on purpose.</b> Only that one configured folder is reachable, only image
/// extensions are servable, and the name is reduced to a LEAF (no traversal, no sub-paths) — so this
/// cannot become a general read-anything proxy over the drive.</para>
/// </summary>
public sealed class SpeakerPhotoService
{
    /// <summary>Live-replace window: re-uploading a photo propagates within this.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
        };

    private readonly SharePointUploadClient? _sp;
    private readonly EventEditionConfigLoader? _cfg;
    private readonly EventConfigOptions? _cfgOptions;
    private readonly DocLibrary.IDocLibraryPathResolver? _paths;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SpeakerPhotoService>? _log;

    public SpeakerPhotoService(
        IMemoryCache cache,
        SharePointUploadClient? sp = null,
        EventEditionConfigLoader? cfg = null,
        EventConfigOptions? cfgOptions = null,
        DocLibrary.IDocLibraryPathResolver? paths = null,
        ILogger<SpeakerPhotoService>? log = null)
    {
        _paths = paths;
        _cache = cache;
        _sp = sp;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _log = log;
    }

    /// <summary>
    /// The LEAF file name, with any directory part and traversal stripped. Returns null when what
    /// is left is not a servable image — the caller then 404s rather than reaching for the drive.
    /// </summary>
    public static string? SafeLeaf(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;

        // 🔒 Traversal is REFUSED OUTRIGHT, before any stripping. Taking the leaf first would
        // "helpfully" turn "../../secrets/p.jpg" into "p.jpg" and serve a different file than was
        // asked for — safe by accident, but it silently reinterprets hostile input instead of
        // rejecting it. A caller that sends "..' has no legitimate request to salvage.
        if (storedPath.Contains("..", StringComparison.Ordinal)) return null;

        // Take the last segment on EITHER separator, so a stored sub-path cannot widen the scope.
        // Path.GetFileName alone honours only the platform's own separator.
        var leaf = storedPath.Replace('\\', '/').TrimEnd('/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        leaf = leaf.Trim();

        if (leaf.Length == 0) return null;
        return ContentTypes.ContainsKey(Path.GetExtension(leaf)) ? leaf : null;
    }

    /// <summary>
    /// Fetch one speaker photo by its stored file name. Null when the proxy is not configured, the
    /// name is not a servable image, or the file is not in the speaker-photo folder.
    /// </summary>
    public async Task<SpeakerPhotoFile?> GetPhotoAsync(string? fileName, CancellationToken ct = default)
    {
        var leaf = SafeLeaf(fileName);
        if (leaf is null || _sp is null || _cfg is null || _cfgOptions is null) return null;

        SharePointEditionConfig? sp;
        try
        {
            sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SpeakerPhoto: edition config could not be read.");
            return null;
        }

        // §768.14 — the folder comes from the registry, not the edition config.
        if (sp is null || _paths is null
            || !_paths.TryResolve(DocLibrary.DocLibraryPaths.SpeakerPhotos, out var resolved))
            return null;
        var folder = resolved.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var cacheKey = $"speakerphoto:{folder}:{leaf}";
        if (_cache.TryGetValue(cacheKey, out SpeakerPhotoFile? cached) && cached is not null)
            return cached;

        try
        {
            // 🔒 §764 — ONE folder, no fallback.
            //
            // This method does NOT use the stored path: it takes the leaf filename and prepends the
            // configured folder. A legacy-folder fallback existed briefly so the config change could
            // land before the files were moved — the operator moved them the same day and asked for
            // it gone: *"i have move the pic over - no need for fallback as it confuses. speaker
            // photos are in 1 place only"*. He is right: a permanent fallback is exactly how "one
            // place" quietly becomes two again, which is what §764 was raised to fix.
            //
            // ⚠️ So this now resolves against the CONFIGURED folder and nothing else. If a photo
            // stops rendering, the file is not where the config says — fix the file or the config,
            // never by re-adding a second search path.
            var item = await _sp.GetItemByPathAsync(sp.SiteUrl, sp.DriveName, $"{folder}/{leaf}", ct);
            if (item is null) return null;

            var bytes = await _sp.DownloadItemContentAsync(sp.SiteUrl, sp.DriveName, item.ItemId, ct);
            if (bytes is null || bytes.Length == 0) return null;

            var ctype = ContentTypes.TryGetValue(Path.GetExtension(leaf), out var t) ? t : "image/jpeg";
            var photo = new SpeakerPhotoFile(bytes, ctype);
            _cache.Set(cacheKey, photo, CacheTtl);
            return photo;
        }
        catch (Exception ex)
        {
            // A missing/failed photo must never take a page down — the caller 404s and the UI
            // degrades to "a photo is on file but could not be shown".
            _log?.LogWarning(ex, "SpeakerPhoto: fetch of {File} failed.", leaf);
            return null;
        }
    }

}

/// <summary>
/// REQUIREMENTS §665 — the ONE rule for turning a stored speaker photo into a URL a BROWSER can
/// actually fetch.
///
/// <para>Kept separate from the service so read sites (pages, catalogues, graphics) can resolve a
/// URL without taking a dependency on SharePoint, and so the rule exists in exactly one place.</para>
/// </summary>
public static class SpeakerPhotoUrl
{
    /// <summary>The hub route that proxies a speaker photo out of SharePoint.</summary>
    public const string Route = "/speaker-photo";

    /// <summary>
    /// The URL to render for a speaker.
    ///
    /// <para><b>The SharePoint copy WINS when there is one.</b> That is what repairs the profiles
    /// already carrying a broken <c>sharepoint.com</c> <c>PhotoUrl</c>: they also carry
    /// <c>PhotoSharePointPath</c>, so they start resolving to the proxy with no data migration. A
    /// Sessionize-imported photo has no SharePoint path and keeps its own public URL, which has
    /// always worked — which is exactly why this went unnoticed until a sponsor uploaded one.</para>
    /// </summary>
    public static string? Resolve(string? photoUrl, string? sharePointPath)
    {
        var leaf = SpeakerPhotoService.SafeLeaf(sharePointPath);
        if (leaf is not null) return $"{Route}/{Uri.EscapeDataString(leaf)}";

        // No SharePoint copy. A stored SharePoint URL is unfetchable by a browser, so treat it as
        // "no photo" rather than rendering a link that can only ever break.
        if (!string.IsNullOrWhiteSpace(photoUrl)
            && photoUrl.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(photoUrl) ? null : photoUrl;
    }
}
