using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>Which presentation deck a session carries (§322).</summary>
public enum PresentationKind
{
    Preview = 0,
    Final = 1,
}

/// <summary>One of the SPEAKER's sessions on the upload page, with both decks' state (§322c).</summary>
/// <param name="SessionId">The CEH session id (the file-name key).</param>
/// <param name="Title">The session title.</param>
/// <param name="PreviewFileName">The stored preview deck, or null.</param>
/// <param name="FinalFileName">The stored final deck, or null.</param>
public sealed record PresentationSessionSlot(
    int SessionId, string Title, string? PreviewFileName, string? FinalFileName);

/// <summary>One kind's DEADLINE state for the speaker (due date + Done) — §322.</summary>
public sealed record PresentationTaskInfo(PresentationKind Kind, DateOnly? DueDate, bool TaskDone);

/// <summary>One session row on the PUBLIC slides page (§322c) — anonymous attendees.</summary>
/// <param name="SessionId">The session id (the proxy-route key).</param>
/// <param name="Title">The session title.</param>
/// <param name="Track">The session's track, or null (drives the §322f filter).</param>
/// <param name="SpeakerNames">The session's speakers, alphabetical.</param>
/// <param name="PreviewFileName">The stored preview deck file name, or null.</param>
/// <param name="FinalFileName">The stored final deck file name, or null.</param>
public sealed record PublicSessionSlides(
    int SessionId, string Title, string? Track, IReadOnlyList<string> SpeakerNames,
    string? PreviewFileName, string? FinalFileName,
    // §467 — deck sizes in bytes (null when SharePoint reported none). Carried so the listing can
    // DISABLE "View" for a deck the embedded Office viewer will refuse, rather than letting the
    // attendee click, wait, and land on "File too large".
    long? PreviewSizeBytes = null, long? FinalSizeBytes = null)
{
    /// <summary>§322d: the EFFECTIVE deck kind — Final wins when both exist, else Preview,
    /// null when the session has no deck yet.</summary>
    public PresentationKind? EffectiveKind =>
        FinalFileName is not null ? PresentationKind.Final
        : PreviewFileName is not null ? PresentationKind.Preview
        : null;
}

/// <summary>A downloaded deck ready to stream (§322c).</summary>
public sealed record PresentationDownload(byte[] Content, string FileName, string ContentType);

/// <summary>
/// §322 (operator bug 2026-07-24): speakers were deep-linked to the SharePoint folders to
/// upload their preview/final decks — every account without SharePoint access hit a login
/// box. ALL SharePoint traffic must run under the APP REGISTRATION (which has full access,
/// so it can overwrite files), exactly like the graphics push (§18), the template proxy
/// (§153) and the eval-PDF proxy (§166/§192): the speaker uploads IN THE HUB and this
/// service writes the bytes to the configured per-kind folder through the same
/// <see cref="ISharePointFileStore"/> seam.
///
/// §322c: decks are PER SESSION (a session's deck is what attendees browse) and — for
/// CONSISTENCY with the sponsor logo/wall uploads (§68, operator 2026-07-24: "we need it to
/// be consistent") — VERSIONED: each upload writes "{sessionId} - {Session Title}_v{N}.{ext}"
/// with an auto-incrementing version; nothing is deleted, the LATEST (highest _vN) is what
/// every surface shows/serves. Uploading marks the speaker's matching
/// <c>speakerdl:{pid}:upload-…-presentation</c> task Done. The PUBLIC slides page
/// (/Sessions/Slides, anonymous) lists every session's latest decks and streams them through
/// hub proxy routes — attendees never touch SharePoint either.
///
/// INERT until configured: no wired store / no folder ⇒ <see cref="CanUpload"/> false, the
/// pages show a friendly note, nothing is faked.
/// </summary>
public sealed class SpeakerPresentationService
{
    /// <summary>Accepted upload extensions (operator: "we support pdf or pptx or zip").</summary>
    public static readonly IReadOnlyList<string> AllowedExtensions = new[] { ".pdf", ".pptx", ".zip" };

    /// <summary>Folder-listing cache TTL — the anonymous slides page must not hammer Graph.</summary>
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(90);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<SharePointFileRef> Files)>
        ListCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SpeakerPresentationService(
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        CommunityHubDbContext db,
        TimeProvider clock)
    {
        _store = store;
        _options = options.Value;
        _db = db;
        _clock = clock;
    }

    private string FolderFor(PresentationKind kind) => kind == PresentationKind.Final
        ? _options.PresentationFinalFolderPath
        : _options.PresentationPreviewFolderPath;

    /// <summary>True when the store + the kind's folder are wired for writes.</summary>
    public bool CanUpload(PresentationKind kind) =>
        _store.CanStore && !string.IsNullOrWhiteSpace(FolderFor(kind));

    /// <summary>True when the store + the kind's folder are wired for reads (the public page).</summary>
    public bool CanRead(PresentationKind kind) =>
        _store.CanRead && !string.IsNullOrWhiteSpace(FolderFor(kind));

    /// <summary>The speakerdl SourceKey whose task an upload of this kind completes —
    /// matches SpeakerDeadlineSeeder's Slug() of the configured deadline titles.</summary>
    public static string TaskSourceKey(int participantId, PresentationKind kind) =>
        kind == PresentationKind.Final
            ? $"speakerdl:{participantId}:upload-final-presentation"
            : $"speakerdl:{participantId}:upload-preview-presentation";

    /// <summary>The versioned SharePoint file name for a session's deck (§68-style).</summary>
    public static string FileNameFor(int sessionId, string sessionTitle, int version, string extension) =>
        $"{sessionId} - {SafeName(sessionTitle)}_v{version}{extension.ToLowerInvariant()}";

    /// <summary>File-system/SharePoint-safe title (keeps letters/digits/space/dash/dot).</summary>
    private static string SafeName(string name)
    {
        var cleaned = new string((name ?? string.Empty).Trim()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.' or '\'').ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Session" : cleaned;
    }

    /// <summary>Matches THIS session's decks regardless of a since-renamed title — the
    /// "{sessionId} - " prefix is the stable token.</summary>
    private static Regex SessionFilePattern(int sessionId) => new(
        $@"^{sessionId} - .*\.(pdf|pptx|zip)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>§322i (operator): the USER-FACING name — the "{sessionId} - " prefix is a
    /// storage/matching token only ("it adds no value" to people); strip it for display,
    /// download filenames and ZIP entries. The stored SharePoint name keeps it (it is what
    /// survives session-title renames).</summary>
    public static string DisplayName(string fileName) =>
        Regex.Replace(fileName ?? string.Empty, @"^\d+ - ", "");

    /// <summary>Parses the "_v{N}" version token (0 when absent — pre-versioning files sort lowest).</summary>
    private static int VersionOf(string fileName)
    {
        var m = Regex.Match(fileName, @"_v(\d+)\.[A-Za-z]+$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>The session's LATEST deck among the folder's files (highest version), or null.</summary>
    private static SharePointFileRef? Latest(IReadOnlyList<SharePointFileRef> files, int sessionId) =>
        files.Where(f => SessionFilePattern(sessionId).IsMatch(f.Name))
            .OrderByDescending(f => VersionOf(f.Name))
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static string ContentTypeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private async Task<IReadOnlyList<SharePointFileRef>> ListCachedAsync(
        PresentationKind kind, CancellationToken ct)
    {
        if (!CanRead(kind)) return Array.Empty<SharePointFileRef>();
        var folder = FolderFor(kind);
        if (ListCache.TryGetValue(folder, out var hit) && _clock.GetUtcNow() - hit.At < ListCacheTtl)
        {
            return hit.Files;
        }
        var files = await _store.ListAsync(folder, ct);
        ListCache[folder] = (_clock.GetUtcNow(), files);
        return files;
    }

    private static void InvalidateCache(string folder) => ListCache.TryRemove(folder, out _);

    // ===================================================================
    //  Speaker upload page — the speaker's sessions + both decks' state
    // ===================================================================

    /// <summary>The two deadlines' due/Done state for the speaker (badge display).</summary>
    public async Task<IReadOnlyList<PresentationTaskInfo>> GetTaskInfoAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var infos = new List<PresentationTaskInfo>();
        foreach (var kind in new[] { PresentationKind.Preview, PresentationKind.Final })
        {
            var key = TaskSourceKey(participantId, kind);
            var task = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.EventId == eventId && t.SourceKey == key)
                .Select(t => new { t.DueDate, t.State })
                .FirstOrDefaultAsync(ct);
            infos.Add(new PresentationTaskInfo(kind, task?.DueDate, task?.State == TaskState.Done));
        }
        return infos;
    }

    /// <summary>
    /// The speaker's OWN sessions (non-service, this edition) with each deck's stored file
    /// (when the store can read) — one upload slot per session per kind.
    /// </summary>
    public async Task<IReadOnlyList<PresentationSessionSlot>> GetSessionSlotsAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        // §428: the ONE shared "is this my session?" predicate — see SpeakerSessionScope.
        var sessions = await _db.Sessions
            .MineAsSpeaker(eventId, participantId)
            .Select(s => new { SessionId = s.Id, s.Title })
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

        var preview = await ListCachedAsync(PresentationKind.Preview, ct);
        var final = await ListCachedAsync(PresentationKind.Final, ct);

        return sessions.Select(s => new PresentationSessionSlot(
                s.SessionId, s.Title,
                Latest(preview, s.SessionId)?.Name,
                Latest(final, s.SessionId)?.Name))
            .ToList();
    }

    /// <summary>
    /// Upload a SESSION's deck of a kind under the app registration's credentials. Gates:
    /// the uploader must be a speaker ON the session (server-side); PDF/PPTX/ZIP only.
    /// §68-CONSISTENT VERSIONING: the file is written as "…_v{next}" (next = highest
    /// existing version + 1); older versions stay in the folder; every surface serves the
    /// latest. Marks the speaker's matching speakerdl task Done. Returns the stored name.
    /// </summary>
    public Task<string> UploadAsync(
        int eventId, int participantId, int sessionId, PresentationKind kind,
        string originalFileName, byte[] content, CancellationToken ct = default) =>
        UploadCoreAsync(eventId, participantId, sessionId, kind, originalFileName,
            content, stream: null, streamLength: 0, ct);

    /// <summary>
    /// §455 — STREAMING upload, for the web request path. The deck goes straight from the request
    /// body to SharePoint's chunked upload session; nothing is materialised in memory.
    ///
    /// <para>The buffered <see cref="UploadAsync(int,int,int,PresentationKind,string,byte[],CancellationToken)"/>
    /// overload stays for callers that already hold bytes (tests, and any future server-side
    /// generation). Both share <c>UploadCoreAsync</c>, so the file-type gate, the §428 own-session
    /// check, the §68 version numbering and the task auto-complete cannot diverge between the two
    /// paths — one upload rule, two sources of bytes.</para>
    /// </summary>
    public Task<string> UploadStreamAsync(
        int eventId, int participantId, int sessionId, PresentationKind kind,
        string originalFileName, Stream content, long contentLength, CancellationToken ct = default) =>
        UploadCoreAsync(eventId, participantId, sessionId, kind, originalFileName,
            content: null, stream: content, streamLength: contentLength, ct);

    private async Task<string> UploadCoreAsync(
        int eventId, int participantId, int sessionId, PresentationKind kind,
        string originalFileName, byte[]? content, Stream? stream, long streamLength,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException("Only PDF, PPTX or ZIP files are accepted.");
        }

        // §428: the SAME predicate the upload page's slot list and "My sessions" use. It
        // used to be spelled out here WITHOUT the service-session exclusion, which made a
        // service-flagged session uploadable but invisible — a deck filed against something
        // the speaker was told does not exist.
        var session = await _db.Sessions
            .MineAsSpeaker(eventId, participantId)
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("You can only upload decks for your own sessions.");

        var folder = FolderFor(kind);

        // §68-style next version: highest existing version for this session + 1 (a FRESH
        // listing, not the cache, so quick successive uploads still increment correctly).
        var version = 1;
        if (_store.CanRead)
        {
            var existing = await _store.ListAsync(folder, ct);
            var latest = Latest(existing, sessionId);
            if (latest is not null) version = VersionOf(latest.Name) + 1;
        }
        var fileName = FileNameFor(sessionId, session.Title, version, ext);

        if (stream is not null)
        {
            await _store.UploadStreamToFolderAsync(
                folder, fileName, stream, streamLength, ContentTypeFor(ext), ct);
        }
        else
        {
            await _store.UploadToFolderAsync(folder, fileName, content!, ContentTypeFor(ext), ct);
        }
        InvalidateCache(folder);

        // The upload IS the deadline's completion — close the speaker's matching task.
        var key = TaskSourceKey(participantId, kind);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.SourceKey == key, ct);
        if (task is not null && task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }

        return fileName;
    }

    // ===================================================================
    //  §494c — DIRECT-TO-STORAGE for speaker decks
    // ===================================================================

    /// <summary>
    /// §494c — resolve the destination for a deck upload the BROWSER will perform, and hand back
    /// the folder + versioned file name. The caller turns those into a pre-authenticated upload URL.
    ///
    /// <para>Decks are the largest files in the product (multi-hundred-MB PowerPoints), so this is
    /// where bypassing the web app matters most. The ownership and naming rules are the SAME ones
    /// <see cref="UploadCoreAsync"/> uses — extension allow-list, "your own sessions only", and the
    /// next free version — because a second copy of those rules is how the two paths would start
    /// disagreeing about what a deck is called or who may replace it.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong file type, or not the speaker's session.</exception>
    public async Task<(string Folder, string FileName)> ResolveDirectUploadTargetAsync(
        int eventId, int participantId, int sessionId, PresentationKind kind,
        string originalFileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException("Only PDF, PPTX or ZIP files are accepted.");
        }

        var session = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Where(s => s.EventId == eventId && !s.IsServiceSession
                        && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId))
            .Select(s => new { s.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("You can only upload decks for your own sessions.");

        var folder = FolderFor(kind);
        var version = 1;
        try
        {
            var existing = await _store.ListAsync(folder, ct);
            var latest = Latest(existing, sessionId);
            if (latest is not null) version = VersionOf(latest.Name) + 1;
        }
        catch { /* listing failure must not block an upload; replace-on-conflict covers it */ }

        return (folder, FileNameFor(sessionId, session.Title, version, ext));
    }

    /// <summary>
    /// §494c — the post-upload half: the deck is already in storage, so close the matching deadline
    /// task exactly as a server-proxied upload would, and drop the folder listing cache so the new
    /// version is visible immediately.
    ///
    /// <para>Deliberately does NOT re-check ownership: the caller must have proved it at
    /// <see cref="ResolveDirectUploadTargetAsync"/> time AND verified the item exists in storage.
    /// Splitting the checks that way keeps this method a recorder rather than a second gate that
    /// could drift from the first.</para>
    /// </summary>
    public async Task CompleteDirectUploadAsync(
        int eventId, int participantId, PresentationKind kind, CancellationToken ct = default)
    {
        InvalidateCache(FolderFor(kind));

        var key = TaskSourceKey(participantId, kind);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.SourceKey == key, ct);
        if (task is not null && task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
    }

    // ===================================================================
    //  PUBLIC (anonymous) slides page + proxy downloads — §322c
    // ===================================================================

    /// <summary>
    /// Every non-service session of the ACTIVE edition with its stored preview/final deck
    /// (null when none) — the anonymous /Sessions/Slides page. Folder listings are cached
    /// ~90 s so attendee traffic doesn't hammer Graph.
    /// </summary>
    public async Task<IReadOnlyList<PublicSessionSlides>> ListPublicAsync(CancellationToken ct = default)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) return Array.Empty<PublicSessionSlides>();

        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Track,
                Speakers = s.SessionSpeakers.Select(ss => ss.Participant.FullName).ToList(),
            })
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

        var preview = await ListCachedAsync(PresentationKind.Preview, ct);
        var final = await ListCachedAsync(PresentationKind.Final, ct);

        return sessions.Select(s => new PublicSessionSlides(
                s.Id,
                s.Title,
                string.IsNullOrWhiteSpace(s.Track) ? null : s.Track,
                s.Speakers.Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                Latest(preview, s.Id)?.Name,
                Latest(final, s.Id)?.Name,
                Latest(preview, s.Id)?.SizeBytes,
                Latest(final, s.Id)?.SizeBytes))
            .ToList();
    }

    /// <summary>
    /// PUBLIC proxy download of a session's deck of a kind — the CURRENT (latest) file,
    /// streamed from SharePoint with the app's creds. Returns null when not configured /
    /// no deck / the session is unknown or a service session.
    /// </summary>
    public async Task<PresentationDownload?> GetDeckAsync(
        int sessionId, PresentationKind kind, CancellationToken ct = default)
    {
        if (!CanRead(kind)) return null;

        var known = await _db.Sessions.AnyAsync(
            s => s.Id == sessionId && !s.IsServiceSession && s.Event.IsActive, ct);
        if (!known) return null;

        var files = await ListCachedAsync(kind, ct);
        var match = Latest(files, sessionId);   // §68 consistency: highest version wins
        if (match is null) return null;

        var bytes = await _store.DownloadAsync(match.ItemId, ct);
        if (bytes is null || bytes.Length == 0) return null;

        return new PresentationDownload(
            bytes, match.Name, ContentTypeFor(Path.GetExtension(match.Name)));
    }

    // ===================================================================
    //  §322h: views-or-downloads counter (ONE number per session)
    // ===================================================================

    /// <summary>Count one slides hit (viewer open / direct download / batch inclusion)
    /// for a session. Fail-soft — a counter must never break the (anonymous) page.</summary>
    public async Task RecordHitAsync(int sessionId, CancellationToken ct = default)
    {
        try
        {
            var eventId = await _db.Sessions
                .Where(s => s.Id == sessionId)
                .Select(s => (int?)s.EventId)
                .FirstOrDefaultAsync(ct);
            if (eventId is null) return;

            var row = await _db.SessionSlideStats
                .FirstOrDefaultAsync(x => x.EventId == eventId && x.SessionId == sessionId, ct);
            if (row is null)
            {
                row = new SessionSlideStat { EventId = eventId.Value, SessionId = sessionId };
                _db.SessionSlideStats.Add(row);
            }
            row.Count++;
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Best-effort — a unique-index race or transient DB hiccup must never fail
            // the anonymous page or a download.
        }
    }

    /// <summary>The per-session hit counts for a set of sessions (speaker My Sessions /
    /// organizer grid). Sessions with no hits simply have no entry.</summary>
    public async Task<IReadOnlyDictionary<int, int>> GetStatsAsync(
        int eventId, IEnumerable<int> sessionIds, CancellationToken ct = default)
    {
        var ids = sessionIds?.Distinct().ToList() ?? new List<int>();
        if (ids.Count == 0) return new Dictionary<int, int>();
        var rows = await _db.SessionSlideStats
            .AsNoTracking()
            .Where(x => x.EventId == eventId && ids.Contains(x.SessionId))
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.SessionId, x => x.Count);
    }

    /// <summary>§322h: the edition-wide TOTAL (all sessions combined) — the "motion
    /// number" shared with speakers and shown to organizers.</summary>
    public async Task<int> GetTotalAsync(int eventId, CancellationToken ct = default) =>
        await _db.SessionSlideStats
            .AsNoTracking()
            .Where(x => x.EventId == eventId)
            .SumAsync(x => (int?)x.Count, ct) ?? 0;
}
