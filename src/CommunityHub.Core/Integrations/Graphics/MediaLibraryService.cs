using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>§1078 — which media folder: the picture library or the video library.</summary>
public enum MediaLibraryKind
{
    Pictures = 0,
    Video = 1,
}

/// <summary>One file in a media folder, as the media crew sees it.</summary>
public sealed record MediaFile(
    string FileName, string ItemId, long? SizeBytes, DateTimeOffset? LastModified);

/// <summary>Bytes ready to stream back through the hub (never a SharePoint link — the §160 rule).</summary>
public sealed record DownloadedMediaFile(byte[] Content, string FileName, string ContentType);

/// <summary>
/// §1078 — THE MEDIA LIBRARY, run through the APP's credentials.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Both links give the media team permissions with the app so they
/// have full permissions to add/delete files. it should not run in their user context, but through
/// the app context"</i>.</para>
///
/// <para>🔴 <b>Why this is a service and not two menu links.</b> He supplied two SharePoint web URLs.
/// Following one opens SharePoint in the VISITOR's own session — their user context, needing their
/// own tenant account and their own permissions on the library, which is exactly what he ruled out.
/// A link cannot be made to run "through the app context"; only a page we serve can. This is the
/// same §160 rule every other document surface in the hub already follows: speakers, sponsors and
/// attendees upload and download <b>in the hub</b>, and the app's own SharePoint credentials do the
/// work.</para>
///
/// <para>🔒 <b>INERT until configured</b> — no wired store or no folder ⇒ listing returns empty,
/// uploads and deletes refuse. Nothing is faked and nothing throws at a photographer.</para>
///
/// <para>⚠️ <b>Delete is real and irreversible from here.</b> He asked for add AND delete, so the
/// capability is deliberate — but a delete only ever names a file the folder LISTING already
/// returned (see <see cref="DeleteAsync"/>), so a crafted name cannot reach outside the folder.</para>
/// </remarks>
public sealed class MediaLibraryService
{
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly IOptionsMonitor<DocLibraryOptions>? _docLibrary;
    private readonly GraphicsSharePointOptions? _graphics;
    private readonly SharePointUploadClient? _probe;

    /// <param name="docLibrary">
    /// The section that owns the FOLDER — including the per-environment root. Optional so tests can
    /// construct the service with the resolver alone.
    /// </param>
    /// <param name="graphics">
    /// The section that owns the SITE and DRIVE the store writes to. Held only to compare against
    /// <paramref name="docLibrary"/> — see <see cref="ConfigurationProblem"/>.
    /// </param>
    /// <param name="probe">Optional: used to ask whether the folder actually EXISTS in this environment.</param>
    public MediaLibraryService(
        ISharePointFileStore store,
        IDocLibraryPathResolver paths,
        IOptionsMonitor<DocLibraryOptions>? docLibrary = null,
        IOptions<GraphicsSharePointOptions>? graphics = null,
        SharePointUploadClient? probe = null)
    {
        _store = store;
        _paths = paths;
        _docLibrary = docLibrary;
        _graphics = graphics?.Value;
        _probe = probe;
    }

    /// <summary>
    /// 🔒 The one file class we refuse, in BOTH folders: things a browser or a colleague's machine
    /// would EXECUTE.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately a small DENYLIST, not an allowlist of image/video types. A photographer
    /// arrives with formats we did not think of — <c>.cr3</c>, <c>.arw</c>, <c>.heif</c>, a
    /// <c>.xmp</c> sidecar, an <c>.srt</c> subtitle — and an allowlist would reject their real work
    /// while teaching them the hub is broken. What we actually need to prevent is the hub becoming a
    /// convenient drop for an executable inside the event's own document library.
    /// </remarks>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".psm1", ".vbs", ".js",
        ".jse", ".jar", ".sh", ".hta", ".lnk", ".reg", ".htm", ".html", ".svg",
    };

    public string FolderKeyOf(MediaLibraryKind kind) => kind switch
    {
        MediaLibraryKind.Video => DocLibraryPaths.MediaVideo,
        _ => DocLibraryPaths.MediaPictures,
    };

    private string Folder(MediaLibraryKind kind) =>
        _paths.TryResolve(FolderKeyOf(kind), out var p) ? p : string.Empty;

    /// <summary>
    /// 🔑 <b>WHERE THIS ENVIRONMENT POINTS</b> — the full drive-relative folder, root included.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-11: <i>"sharepoint goes to diff urls depending on env"</i>. He is right, and
    /// it is by design: the registry key is RELATIVE (<c>Event/Media/Pictures</c>) and the
    /// environment supplies <c>DocLibrary:RootFolderPath</c> — <c>General/DEVELOPMENT/EventHub</c> on
    /// DEV, <c>General/Events/ELDK 2027/EventHub</c> on PROD. ⇒ The page SHOWS the resolved folder,
    /// because "which environment's library am I looking at?" is otherwise unanswerable from the
    /// screen, and the two look identical when both happen to be empty.
    /// </remarks>
    public string ResolvedFolder(MediaLibraryKind kind) => Folder(kind);

    /// <summary>The site this environment's library lives on, for the same reason.</summary>
    public string SiteUrl => _docLibrary?.CurrentValue.SiteUrl ?? string.Empty;

    /// <summary>
    /// 🔴 <b>The invariant: the section that owns the PATH and the section that owns the SITE must
    /// agree.</b> Null when they do (or when either is unknown).
    /// </summary>
    /// <remarks>
    /// <para>⚠️ The folder comes from <c>DocLibrary:*</c> and the store writes to
    /// <c>Graphics:SharePoint:*</c>. Both sections carry a site URL and a drive name, and today
    /// every environment sets them identically — which is a fact about the CONFIGURATION, not a
    /// guarantee from the code. If one is ever pointed at a different site, files would be written
    /// to a folder path computed for the other, and the failure is silent: the upload succeeds, the
    /// listing is empty, and nothing says why.</para>
    ///
    /// <para>⇒ Rather than let that be discovered by a photographer, the mismatch is stated and the
    /// library refuses to manage. It is the §767 lesson in a different costume: a path that resolves
    /// against the wrong site matches nothing, and matching nothing looks exactly like "nobody has
    /// uploaded anything yet".</para>
    /// </remarks>
    public string? ConfigurationProblem
    {
        get
        {
            var doc = _docLibrary?.CurrentValue;
            if (doc is null || _graphics is null) return null;
            if (string.IsNullOrWhiteSpace(doc.SiteUrl) || string.IsNullOrWhiteSpace(_graphics.SiteUrl))
                return null;

            if (!string.Equals(doc.SiteUrl.TrimEnd('/'), _graphics.SiteUrl.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                return $"The folder is configured on {doc.SiteUrl} but files are written to "
                     + $"{_graphics.SiteUrl}. Point DocLibrary:SiteUrl and Graphics:SharePoint:SiteUrl "
                     + "at the same site.";

            if (!string.Equals(doc.DriveName ?? string.Empty, _graphics.DriveName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                return $"The folder is configured in the “{doc.DriveName}” library but files are "
                     + $"written to “{_graphics.DriveName}”. Point DocLibrary:DriveName and "
                     + "Graphics:SharePoint:DriveName at the same library.";

            return null;
        }
    }

    /// <summary>
    /// Does the folder actually EXIST in this environment? Null when it cannot be asked.
    /// </summary>
    /// <remarks>
    /// 🔑 A missing folder and an empty one are the same picture on screen — and only one of them
    /// means "upload your pictures here". PROD's <c>Event/Media</c> did not exist on the day this
    /// shipped (the folders were staged in the DEV tree), so this is not a hypothetical: without the
    /// probe, the first person to open the PROD page would read "Nothing here yet" and believe it.
    /// </remarks>
    public async Task<DocLibraryFolderProbe?> ProbeAsync(
        MediaLibraryKind kind, CancellationToken ct = default)
    {
        var doc = _docLibrary?.CurrentValue;
        if (_probe is null || doc is null) return null;

        var folder = Folder(kind);
        if (string.IsNullOrWhiteSpace(folder)) return null;

        return await _probe.ProbeFolderAsync(doc.SiteUrl, doc.DriveName, folder, ct);
    }

    /// <summary>True when this folder can be LISTED / downloaded from.</summary>
    public bool CanRead(MediaLibraryKind kind) =>
        _store.CanRead && !string.IsNullOrWhiteSpace(Folder(kind));

    /// <summary>
    /// True when this folder accepts uploads and deletes. 🔒 <b>A configuration mismatch closes
    /// it</b> — writing a file to a path computed for a different site is worse than refusing.
    /// </summary>
    public bool CanManage(MediaLibraryKind kind) =>
        _store.CanStore && !string.IsNullOrWhiteSpace(Folder(kind)) && ConfigurationProblem is null;

    /// <summary>Newest first — a media crew looks for what was just added, not what is alphabetically first.</summary>
    public async Task<IReadOnlyList<MediaFile>> ListAsync(
        MediaLibraryKind kind, CancellationToken ct = default)
    {
        if (!CanRead(kind)) return Array.Empty<MediaFile>();

        var files = await _store.ListAsync(Folder(kind), ct);
        return files
            .Select(f => new MediaFile(f.Name, f.ItemId, f.SizeBytes, f.LastModified))
            .OrderByDescending(f => f.LastModified ?? DateTimeOffset.MinValue)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Why an upload was refused, in words a photographer can act on. Null = accepted.</summary>
    public string? RejectionReason(string? fileName)
    {
        var name = SafeName(fileName);
        if (name.Length == 0) return "The file has no usable name.";

        var ext = Path.GetExtension(name);
        if (BlockedExtensions.Contains(ext))
            return $"“{ext}” files are not accepted here — this library is for pictures and video.";

        return null;
    }

    /// <summary>
    /// Upload (or REPLACE) one file. 🔑 Streamed: a 2 GB video must never be held in the web app's
    /// memory, let alone twice (§455).
    /// </summary>
    public async Task<bool> UploadAsync(
        MediaLibraryKind kind, string fileName, Stream content, long contentLength,
        string contentType, CancellationToken ct = default)
    {
        if (!CanManage(kind)) return false;
        if (RejectionReason(fileName) is not null) return false;

        await _store.UploadStreamToFolderAsync(
            Folder(kind), SafeName(fileName), content, contentLength, contentType, ct);
        return true;
    }

    /// <summary>
    /// Delete one file by name.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The folder's own listing is the allowlist.</b> The name arrives from a form, and it is
    /// only acted on when the folder actually contains a file of that name — so a crafted value can
    /// never address anything outside this folder, whatever the path handling underneath does.
    /// </remarks>
    public async Task<bool> DeleteAsync(
        MediaLibraryKind kind, string fileName, CancellationToken ct = default)
    {
        if (!CanManage(kind)) return false;

        var name = SafeName(fileName);
        var existing = await ListAsync(kind, ct);
        if (!existing.Any(f => string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        await _store.DeleteFromFolderAsync(Folder(kind), name, ct);
        return true;
    }

    /// <summary>
    /// Download one file THROUGH the hub. ⚠️ By item id from the listing, never by a caller-supplied
    /// path — the same reason as <see cref="DeleteAsync"/>.
    /// </summary>
    public async Task<DownloadedMediaFile?> DownloadAsync(
        MediaLibraryKind kind, string fileName, CancellationToken ct = default)
    {
        if (!CanRead(kind)) return null;

        var name = SafeName(fileName);
        var match = (await ListAsync(kind, ct))
            .FirstOrDefault(f => string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        var bytes = await _store.DownloadAsync(match.ItemId, ct);
        return bytes is null ? null : new DownloadedMediaFile(bytes, match.FileName, ContentTypeOf(name));
    }

    /// <summary>
    /// Strip everything but the file's own name. A browser may send a full client path
    /// (<c>C:\photos\a.jpg</c>), and a hostile caller may send <c>../../something</c>; neither may
    /// decide where a byte lands.
    /// </summary>
    public static string SafeName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim().Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return name.Trim().TrimStart('.', ' ');
    }

    /// <summary>A content type for the DOWNLOAD response. Unknown ⇒ the neutral binary type.</summary>
    public static string ContentTypeOf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" or ".heif" => "image/heic",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            _ => "application/octet-stream",
        };
}
