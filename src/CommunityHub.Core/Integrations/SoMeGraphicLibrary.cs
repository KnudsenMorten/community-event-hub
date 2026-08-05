using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>One picture in the event-graphics folder, as the picker shows it.</summary>
public sealed record SoMeGraphicRef(string FileName, long SizeBytes);

/// <summary>A fetched picture, ready to stream to the browser.</summary>
public sealed record SoMeGraphicFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// §841 — THE PICTURE LIBRARY BEHIND THE POST EDITOR: list what exists, and fetch one by name.
///
/// <para>Operator 2026-08-05: <i>"i need a way to change linked picture for a post in an easy way
/// where i can preview pic and link it"</i>. Both halves need this: the picker lists the folder so he
/// chooses from what is REALLY there rather than typing a path, and the preview streams the bytes
/// through the app — a browser cannot read SharePoint directly.</para>
///
/// <para>🔒 <b>LISTS, never guesses.</b> §767 shipped a sweep that matched nothing for four
/// production runs because a filename convention was assumed instead of listed. A picker built on a
/// convention would offer names that do not exist and silently drop ones that do.</para>
///
/// <para>⚠️ <b><see cref="SoMePost.ImageRef"/> keeps meaning what it already means</b> — for an event
/// post, the BARE FILE NAME in <c>EventSoMeGraphics</c> (§828.7). This class resolves a name to
/// bytes; it does not redefine the field, because the publisher (§324) reads it too.</para>
/// </summary>
public sealed class SoMeGraphicLibrary
{
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly ILogger<SoMeGraphicLibrary>? _log;

    public SoMeGraphicLibrary(
        ISharePointFileStore store,
        IDocLibraryPathResolver paths,
        ILogger<SoMeGraphicLibrary>? log = null)
    {
        _store = store;
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// §844.3 — which registry folder holds the media for one post type and medium.
    /// </summary>
    /// <remarks>
    /// 🔒 §844.2 — video is only offered for the three types he named (EVENT, SESSION, SPONSOR).
    /// Tracks and sponsor CATEGORIES get graphics only, so asking for their video returns nothing
    /// rather than inventing a folder that does not exist.
    /// </remarks>
    public static string? RegistryKeyFor(SoMeTemplateKind? kind, SoMePostMediaKind media)
    {
        if (media == SoMePostMediaKind.Video)
        {
            return kind switch
            {
                SoMeTemplateKind.EventPost => DocLibraryPaths.EventSoMeVideos,
                SoMeTemplateKind.Session => DocLibraryPaths.SpeakerSessionVideos,
                SoMeTemplateKind.Sponsor => DocLibraryPaths.SponsorVideos,
                _ => null,
            };
        }

        return kind switch
        {
            SoMeTemplateKind.Session => DocLibraryPaths.SpeakerSessionGraphics,
            SoMeTemplateKind.SpeakerTracks => DocLibraryPaths.SpeakerTrackGraphics,
            SoMeTemplateKind.Sponsor => DocLibraryPaths.SponsorGraphicsSponsors,
            SoMeTemplateKind.SponsorCategory => DocLibraryPaths.SponsorGraphicsCategories,
            // Event posts, and anything written by hand, draw on the event graphics folder.
            _ => DocLibraryPaths.EventSoMeGraphics,
        };
    }

    /// <summary>True when video is an option for this post type at all (§844.2).</summary>
    public static bool SupportsVideo(SoMeTemplateKind? kind) =>
        RegistryKeyFor(kind, SoMePostMediaKind.Video) is not null;

    private string FolderFor(string? registryKey) =>
        registryKey is not null && _paths.TryResolve(registryKey, out var p) ? p : string.Empty;

    /// <summary>The event SoMe graphics folder, or empty when the registry has no path for it.</summary>
    private string Folder => FolderFor(DocLibraryPaths.EventSoMeGraphics);

    /// <summary>True when the picker can actually show anything.</summary>
    public bool CanRead => _store.CanRead && !string.IsNullOrWhiteSpace(Folder);

    /// <summary>
    /// Every picture in the folder, newest names last. Returns empty rather than throwing when the
    /// library is not wired — the editor then still edits text and schedule.
    /// </summary>
    public Task<IReadOnlyList<SoMeGraphicRef>> ListAsync(CancellationToken ct = default) =>
        ListAsync(SoMeTemplateKind.EventPost, SoMePostMediaKind.Graphic, ct);

    /// <summary>
    /// §844.3 — what this post TYPE can choose from, for the given MEDIUM. Empty when the type has
    /// no video folder (§844.2) or the library is unwired — the editor then says so rather than
    /// showing an empty gallery that reads as "there are none".
    /// </summary>
    public async Task<IReadOnlyList<SoMeGraphicRef>> ListAsync(
        SoMeTemplateKind? kind, SoMePostMediaKind media, CancellationToken ct = default)
    {
        var folder = FolderFor(RegistryKeyFor(kind, media));
        if (!_store.CanRead || string.IsNullOrWhiteSpace(folder))
        {
            return Array.Empty<SoMeGraphicRef>();
        }

        try
        {
            var files = await _store.ListAsync(folder, ct);

            return files
                // Only files of the right medium: the event folder also holds his markdown deck and
                // an `alternates-…` subfolder, and offering those as a post's picture is nonsense.
                .Where(f => media == SoMePostMediaKind.Video ? IsVideo(f.Name) : IsPicture(f.Name))
                .Select(f => new SoMeGraphicRef(f.Name, f.SizeBytes ?? 0))
                .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "§841/§844: could not list the SoMe media folder {Folder}.", folder);
            return Array.Empty<SoMeGraphicRef>();
        }
    }

    /// <summary>
    /// One picture by FILE NAME (what <c>ImageRef</c> holds), streamed through the app because the
    /// organizer's browser has no SharePoint permission. Null when missing.
    /// </summary>
    public Task<SoMeGraphicFile?> GetAsync(string? fileName, CancellationToken ct = default) =>
        GetAsync(fileName, SoMeTemplateKind.EventPost, SoMePostMediaKind.Graphic, ct);

    /// <summary>
    /// §844.3 — one file by NAME from the folder that matches this post type and medium.
    /// </summary>
    public async Task<SoMeGraphicFile?> GetAsync(
        string? fileName, SoMeTemplateKind? kind, SoMePostMediaKind media,
        CancellationToken ct = default)
    {
        var folder = FolderFor(RegistryKeyFor(kind, media));
        if (!_store.CanRead || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // 🔒 A NAME, never a path. Without this an ImageRef of "../../Secrets/x.png" would walk the
        // library — the field is operator-editable, so it is untrusted input.
        var name = fileName.Trim();
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            _log?.LogWarning("§841: refused a graphic name that is a path: {Name}.", name);
            return null;
        }

        try
        {
            var files = await _store.ListAsync(folder, ct);
            var match = files.FirstOrDefault(
                f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;

            var bytes = await _store.DownloadAsync(match.ItemId, ct);
            if (bytes is null || bytes.Length == 0) return null;

            return new SoMeGraphicFile(bytes, ContentTypeFor(match.Name), match.Name);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "§841: could not fetch the event graphic {Name}.", name);
            return null;
        }
    }

    /// <summary>§844 — the video formats LinkedIn accepts for a native upload.</summary>
    private static bool IsVideo(string name) =>
        name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase);

    private static bool IsPicture(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    private static string ContentTypeFor(string name) =>
        name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
        : name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        // §844 — a video must be served with a video type, or the browser offers a download
        // instead of playing it in the preview.
        : name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4"
        : name.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase) ? "video/mp4"
        : name.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime"
        : "image/jpeg";
}
