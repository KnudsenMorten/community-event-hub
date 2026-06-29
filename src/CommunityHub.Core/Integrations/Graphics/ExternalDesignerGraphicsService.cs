using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Where a speaker's current photo comes from — the PRECEDENCE marker for the
/// external-designer pipeline (REQUIREMENTS §165 step 2). DERIVED (never persisted): it is
/// read off the EXISTING <see cref="SpeakerProfile"/> state, so no schema change is needed.
/// </summary>
public enum PhotoSource
{
    /// <summary>The speaker has no usable photo URL on their profile — nothing to pull.</summary>
    None = 0,

    /// <summary>The photo URL is the Sessionize <c>profilePicture</c> (seeded, never speaker-edited).</summary>
    Sessionize = 1,

    /// <summary>
    /// The speaker EDITED their own photo in the hub (<see cref="SpeakerProfile.IsSpeakerEdited"/>
    /// for <see cref="SpeakerProfile.BioFields.PhotoUrl"/> is true). This is the "speaker uploaded a
    /// new pic" signal and it WINS over a Sessionize pull — the same field-ownership rule the
    /// Sessionize delta import already honours (<c>FillIfUntouched</c>).
    /// </summary>
    SpeakerUpload = 2,
}

/// <summary>One speaker's resolved photo for the designer pipeline (REQUIREMENTS §165).</summary>
/// <param name="ParticipantId">The speaker participant id.</param>
/// <param name="FullName">The speaker's display name (the file is NAMED by this).</param>
/// <param name="FileName">The collision-safe, sanitised file name (e.g. <c>Firstname-Lastname.jpg</c>).</param>
/// <param name="Source">Where the photo comes from (precedence: a SpeakerUpload WINS).</param>
/// <param name="SourceUrl">The URL the bytes are fetched from (null when <see cref="Source"/> is None).</param>
public sealed record DesignerSpeakerPhoto(
    int ParticipantId, string FullName, string FileName, PhotoSource Source, string? SourceUrl);

/// <summary>One row of the designer Excel brief — a session + the designer-facing facts (§165 step 4).</summary>
public sealed record DesignerBriefRow(
    int SessionId,
    string Title,
    string Type,
    string? Track,
    string? Level,
    string? Length,
    string? Scheduled,
    string? Room,
    IReadOnlyList<string> SpeakerNames,
    IReadOnlyList<string> PhotoFileNames,
    string FolderPath,
    string? FolderUrl);

/// <summary>Counts from a pipeline run, surfaced on the organizer trigger page (§165 step 5).</summary>
/// <param name="PhotosPulled">Speaker photos fetched + written into the Speakers/Photos folder.</param>
/// <param name="UploadsPreserved">Of those, how many were a speaker's OWN upload (won over Sessionize).</param>
/// <param name="PhotosSkipped">Speakers with no usable photo (or whose fetch failed) — nothing written.</param>
/// <param name="SessionFolders">Per-session build folders reconciled.</param>
/// <param name="MasterClassFolders">Per-master-class build folders reconciled.</param>
/// <param name="TrackFolders">Per-track build folders reconciled.</param>
public sealed record DesignerPipelineResult(
    int PhotosPulled,
    int UploadsPreserved,
    int PhotosSkipped,
    int SessionFolders,
    int MasterClassFolders,
    int TrackFolders);

/// <summary>
/// The EXTERNAL-DESIGNER graphics pipeline (REQUIREMENTS §165). An organizer hands an external
/// designer a tidy SharePoint structure + an Excel brief so they can produce per-session
/// graphics. Reuses the existing <see cref="ISharePointFileStore"/> seam (drive-relative
/// folder writes, exactly like <see cref="SessionEvalPdfService"/> / the §124 QR upload) and the
/// <see cref="ISpeakerPictureFetcher"/> download seam — NO new SharePoint integration.
///
/// Three config-driven, INERT-when-unset stages (each gated on its own folder path, like every
/// sibling graphics feature):
///  1. PHOTOS BY NAME — fetch each speaker's photo and drop a name-keyed copy
///     (<c>Firstname-Lastname.jpg</c>, collision-safe) into
///     <see cref="GraphicsSharePointOptions.SpeakerPhotosFolderPath"/>.
///  2. SPEAKER-UPLOAD-WINS — a speaker who edited their own photo in the hub
///     (<see cref="PhotoSource.SpeakerUpload"/>) is authoritative; the pull uses THAT URL and a
///     re-run never clobbers it with a Sessionize photo. The precedence is read off the existing
///     <see cref="SpeakerProfile"/> field-ownership state, so there is NO migration.
///  3. BUILD FOLDERS — one folder per SESSION (under
///     <see cref="GraphicsSharePointOptions.SessionsFolderPath"/>), per MASTER CLASS (under
///     <see cref="GraphicsSharePointOptions.MasterClassFolderPath"/>) and per TRACK (under
///     <see cref="GraphicsSharePointOptions.TracksFolderPath"/>), each holding the relevant
///     speaker photo(s). Idempotent: a re-run reconciles in place (same names ⇒ replace, no dupes).
///
/// NEVER exposes a SharePoint URL to a speaker: this whole feature is ORGANIZER-only (the trigger
/// page + the brief). Inert + safe when the store or a folder path is unconfigured — every method
/// no-ops and nothing is faked.
/// </summary>
public sealed class ExternalDesignerGraphicsService
{
    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly ISpeakerPictureFetcher _pictureFetcher;
    private readonly GraphicsSharePointOptions _options;

    public ExternalDesignerGraphicsService(
        CommunityHubDbContext db,
        ISharePointFileStore store,
        ISpeakerPictureFetcher pictureFetcher,
        IOptions<GraphicsSharePointOptions> options)
    {
        _db = db;
        _store = store;
        _pictureFetcher = pictureFetcher;
        _options = options.Value;
    }

    private string? PhotosFolder => Trimmed(_options.SpeakerPhotosFolderPath);
    private string? SessionsFolder => Trimmed(_options.SessionsFolderPath);
    private string? MasterClassFolder => Trimmed(_options.MasterClassFolderPath);
    private string? TracksFolder => Trimmed(_options.TracksFolderPath);

    private static string? Trimmed(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('/');

    /// <summary>True when the live store can WRITE — the pipeline can run (and at least one folder is set).</summary>
    public bool CanManage =>
        _store.CanStore
        && (PhotosFolder is not null || SessionsFolder is not null
            || MasterClassFolder is not null || TracksFolder is not null);

    /// <summary>True when the store can read (used to resolve folder links for the brief).</summary>
    public bool CanRead => _store.CanRead;

    /// <summary>The MIME type of the generated .xlsx brief (OpenXML spreadsheet).</summary>
    public const string BriefContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The download file name of the designer brief.</summary>
    public const string BriefFileName = "designer-graphics-brief.xlsx";

    // ===================================================================
    //  Run the pipeline: pull photos → build folders → (counts)
    // ===================================================================

    /// <summary>
    /// PULL photos (named by speaker, speaker-upload-wins) then BUILD the per-session /
    /// per-master-class / per-track folders. Idempotent: a re-run reconciles in place. INERT:
    /// returns all-zero when the store cannot write; each stage independently no-ops when its
    /// folder path is unset.
    /// </summary>
    public async Task<DesignerPipelineResult> RunPipelineAsync(int eventId, CancellationToken ct = default)
    {
        if (!_store.CanStore)
        {
            return new DesignerPipelineResult(0, 0, 0, 0, 0, 0);
        }

        var photos = await ResolveSpeakerPhotosAsync(eventId, ct);

        // Fetch each resolvable photo's bytes ONCE (cache by participant) — a single bad URL is
        // tolerated (the speaker is then skipped), never crashes the run.
        var bytesByParticipant = new Dictionary<int, FetchedImage>();
        var skipped = 0;
        foreach (var p in photos)
        {
            if (p.Source == PhotoSource.None || string.IsNullOrWhiteSpace(p.SourceUrl))
            {
                skipped++;
                continue;
            }

            var image = await _pictureFetcher.FetchAsync(p.SourceUrl, ct);
            if (image is null || image.Content.Length == 0)
            {
                skipped++;
                continue;
            }
            bytesByParticipant[p.ParticipantId] = image;
        }

        // STAGE 1+2: photos by name into Speakers/Photos (speaker-upload-wins — the URL already
        // reflects the speaker's own edit when they took ownership of the field).
        var pulled = 0;
        var preserved = 0;
        if (PhotosFolder is { } photosFolder)
        {
            foreach (var p in photos)
            {
                if (!bytesByParticipant.TryGetValue(p.ParticipantId, out var image)) continue;
                await _store.UploadToFolderAsync(
                    photosFolder, p.FileName, image.Content, image.ContentType, ct);
                pulled++;
                if (p.Source == PhotoSource.SpeakerUpload) preserved++;
            }
        }

        // STAGE 3: per-session / per-master-class / per-track build folders.
        var photoByParticipant = photos.ToDictionary(p => p.ParticipantId);
        var sessions = await LoadSessionsAsync(eventId, ct);

        var sessionFolders = 0;
        var masterClassFolders = 0;
        foreach (var s in sessions)
        {
            var root = s.Type == SessionType.MasterClass ? MasterClassFolder : SessionsFolder;
            if (root is null) continue; // that build root not configured ⇒ skip this session type

            var segment = DesignerGraphicsNaming.SanitizeFolderSegment(s.Title);
            var folder = $"{root}/{segment}";

            var any = await UploadSessionPhotosAsync(folder, s.SpeakerIds, bytesByParticipant, photoByParticipant, ct);
            if (!any) continue; // no fetched photo for any speaker ⇒ nothing to put in the folder

            if (s.Type == SessionType.MasterClass) masterClassFolders++;
            else sessionFolders++;
        }

        // Tracks: one folder per distinct non-blank track, holding every photo across its sessions.
        var trackFolders = 0;
        if (TracksFolder is { } tracksRoot)
        {
            var byTrack = sessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Track))
                .GroupBy(s => s.Track!.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var grp in byTrack)
            {
                var segment = DesignerGraphicsNaming.SanitizeFolderSegment(grp.Key);
                var folder = $"{tracksRoot}/{segment}";
                var speakerIds = grp.SelectMany(s => s.SpeakerIds).Distinct().ToList();

                var any = await UploadSessionPhotosAsync(folder, speakerIds, bytesByParticipant, photoByParticipant, ct);
                if (any) trackFolders++;
            }
        }

        return new DesignerPipelineResult(
            pulled, preserved, skipped, sessionFolders, masterClassFolders, trackFolders);
    }

    /// <summary>Upload each given speaker's (cached) photo into one build folder; true if anything was written.</summary>
    private async Task<bool> UploadSessionPhotosAsync(
        string folder,
        IReadOnlyList<int> speakerIds,
        IReadOnlyDictionary<int, FetchedImage> bytesByParticipant,
        IReadOnlyDictionary<int, DesignerSpeakerPhoto> photoByParticipant,
        CancellationToken ct)
    {
        var wrote = false;
        foreach (var pid in speakerIds)
        {
            if (!bytesByParticipant.TryGetValue(pid, out var image)) continue;
            if (!photoByParticipant.TryGetValue(pid, out var p)) continue;
            await _store.UploadToFolderAsync(folder, p.FileName, image.Content, image.ContentType, ct);
            wrote = true;
        }
        return wrote;
    }

    // ===================================================================
    //  Speaker-photo resolution (precedence + collision-safe names)
    // ===================================================================

    /// <summary>
    /// Resolve every speaker's photo for the edition: the source (precedence: a speaker upload
    /// WINS over Sessionize), the source URL, and a collision-safe file name keyed by the
    /// speaker's name. The de-dupe is deterministic across the whole edition (same-name speakers
    /// get a stable <c>-{participantId}</c> suffix), so a run and the brief agree on every name.
    /// </summary>
    public async Task<IReadOnlyList<DesignerSpeakerPhoto>> ResolveSpeakerPhotosAsync(
        int eventId, CancellationToken ct = default)
    {
        var speakers = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Speaker)
            .Select(p => new { p.Id, p.FullName })
            .ToListAsync(ct);

        var profilesById = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId)
            .ToDictionaryAsync(sp => sp.ParticipantId, ct);

        // First pass: source + URL per speaker.
        var resolved = speakers
            .Select(s =>
            {
                profilesById.TryGetValue(s.Id, out var profile);
                var (source, url) = ResolvePhotoSource(profile);
                return (s.Id, FullName: s.FullName ?? string.Empty, source, url);
            })
            .OrderBy(s => s.Id)
            .ToList();

        // Second pass: collision-safe names. Group speakers (that HAVE a photo) by the sanitised
        // base; a group with >1 member appends the participant id to every member so the set is
        // unique AND deterministic regardless of ordering.
        var baseNameById = new Dictionary<int, string>();
        foreach (var s in resolved)
        {
            baseNameById[s.Id] = DesignerGraphicsNaming.SanitizeNameBase(s.FullName, s.Id);
        }

        var collisionBases = resolved
            .Where(s => s.source != PhotoSource.None)
            .GroupBy(s => baseNameById[s.Id], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return resolved
            .Select(s =>
            {
                var baseName = baseNameById[s.Id];
                var needSuffix = collisionBases.Contains(baseName);
                var fileName = DesignerGraphicsNaming.BuildPhotoFileName(baseName, s.url, s.Id, needSuffix);
                return new DesignerSpeakerPhoto(s.Id, s.FullName, fileName, s.source, s.url);
            })
            .ToList();
    }

    /// <summary>
    /// The precedence rule (§165 step 2), derived from EXISTING profile state: a speaker who
    /// edited their own photo in the hub owns it (<see cref="PhotoSource.SpeakerUpload"/>);
    /// otherwise a present URL is the Sessionize seed; a blank URL is <see cref="PhotoSource.None"/>.
    /// </summary>
    public static (PhotoSource Source, string? Url) ResolvePhotoSource(SpeakerProfile? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.PhotoUrl))
        {
            return (PhotoSource.None, null);
        }

        var url = profile.PhotoUrl.Trim();
        return profile.IsSpeakerEdited(SpeakerProfile.BioFields.PhotoUrl)
            ? (PhotoSource.SpeakerUpload, url)
            : (PhotoSource.Sessionize, url);
    }

    // ===================================================================
    //  Excel brief (§165 step 4)
    // ===================================================================

    /// <summary>
    /// Build the designer brief: one row per non-service session with title / type / track /
    /// level / length / time / room, the speaker name(s), the build-folder PATH + (best-effort)
    /// LINK, and the photo file name(s) for that session. The folder link is resolved through the
    /// store (organizer-only — never shown to a speaker) and is blank when the store cannot read
    /// or the folder has no file yet. Returns valid .xlsx bytes (ClosedXML — already referenced).
    /// </summary>
    public async Task<byte[]> BuildBriefAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await BuildBriefRowsAsync(eventId, ct);
        return DesignerBriefWorkbook.Build(rows);
    }

    /// <summary>The brief rows (exposed for tests + the brief builder).</summary>
    public async Task<IReadOnlyList<DesignerBriefRow>> BuildBriefRowsAsync(
        int eventId, CancellationToken ct = default)
    {
        var photos = (await ResolveSpeakerPhotosAsync(eventId, ct)).ToDictionary(p => p.ParticipantId);
        var sessions = await LoadSessionsAsync(eventId, ct);

        var rows = new List<DesignerBriefRow>(sessions.Count);
        foreach (var s in sessions)
        {
            var root = s.Type == SessionType.MasterClass ? MasterClassFolder : SessionsFolder;
            var segment = DesignerGraphicsNaming.SanitizeFolderSegment(s.Title);
            var folderPath = root is null ? string.Empty : $"{root}/{segment}";

            var sessionPhotos = s.SpeakerIds
                .Where(id => photos.ContainsKey(id))
                .Select(id => photos[id])
                .ToList();

            var photoNames = sessionPhotos
                .Where(p => p.Source != PhotoSource.None)
                .Select(p => p.FileName)
                .ToList();

            var folderUrl = root is null ? null : await ResolveFolderUrlAsync(folderPath, ct);

            rows.Add(new DesignerBriefRow(
                s.Id,
                s.Title,
                FriendlyType(s.Type),
                NullIfBlank(s.Track),
                NullIfBlank(s.Level),
                FormatLength(s),
                FormatScheduled(s.StartsAt, s.EndsAt),
                NullIfBlank(s.Room),
                s.SpeakerNames,
                photoNames,
                folderPath,
                folderUrl));
        }

        return rows;
    }

    /// <summary>
    /// Best-effort organizer-facing folder LINK: list the folder and derive the parent-folder
    /// web URL from a contained file's URL (the store never hands back a folder URL directly).
    /// Null when the store cannot read, the folder is empty, or no usable URL is present.
    /// </summary>
    private async Task<string?> ResolveFolderUrlAsync(string folderPath, CancellationToken ct)
    {
        if (!_store.CanRead || string.IsNullOrWhiteSpace(folderPath)) return null;
        try
        {
            var files = await _store.ListAsync(folderPath, ct);
            var withUrl = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.WebUrl));
            return withUrl is null ? null : DesignerGraphicsNaming.FolderUrlFromFileUrl(withUrl.WebUrl);
        }
        catch
        {
            return null; // a SharePoint hiccup must not fail brief generation
        }
    }

    // ===================================================================
    //  Shared session loading + formatting
    // ===================================================================

    private sealed record SessionRow(
        int Id, string Title, SessionType Type, string? Track, string? Level,
        SessionLength Length, int? LengthMinutes, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
        string? Room, IReadOnlyList<int> SpeakerIds, IReadOnlyList<string> SpeakerNames);

    private async Task<List<SessionRow>> LoadSessionsAsync(int eventId, CancellationToken ct)
    {
        var raw = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Type,
                s.Track,
                s.Level,
                s.Length,
                s.LengthMinutes,
                s.StartsAt,
                s.EndsAt,
                s.Room,
                Speakers = s.SessionSpeakers
                    .OrderBy(ss => ss.Participant.FullName)
                    .Select(ss => new { ss.ParticipantId, ss.Participant.FullName })
                    .ToList(),
            })
            .ToListAsync(ct);

        return raw
            .OrderBy(s => s.StartsAt == null)
            .ThenBy(s => s.StartsAt)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SessionRow(
                s.Id, s.Title ?? string.Empty, s.Type, s.Track, s.Level, s.Length, s.LengthMinutes,
                s.StartsAt, s.EndsAt, s.Room,
                s.Speakers.Select(x => x.ParticipantId).ToList(),
                s.Speakers.Where(x => !string.IsNullOrWhiteSpace(x.FullName)).Select(x => x.FullName).ToList()))
            .ToList();
    }

    private static string FriendlyType(SessionType type) => type switch
    {
        SessionType.MasterClass => "Master Class",
        SessionType.TechnicalSession => "Technical Session",
        SessionType.AskTheExperts => "Ask the Experts",
        SessionType.PanelDiscussion => "Panel",
        _ => type.ToString(),
    };

    private static string? FormatLength(SessionRow s)
    {
        if (s.LengthMinutes is > 0) return $"{s.LengthMinutes} min";
        return s.Length == SessionLength.FullDay ? "Full day" : $"{(int)s.Length} min";
    }

    private static string? FormatScheduled(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null) return null;
        return end is null
            ? start.Value.ToString("ddd d MMM HH:mm")
            : $"{start.Value:ddd d MMM HH:mm}–{end.Value:HH:mm}";
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
