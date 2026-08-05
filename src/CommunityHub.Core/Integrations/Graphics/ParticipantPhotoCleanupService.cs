using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>What one cleanup pass decided, in words the log and a test can both read.</summary>
/// <param name="Deleted">Files actually removed (always 0 while <see cref="PhotoCleanupOptions.DryRun"/>).</param>
/// <param name="WouldDelete">Files identified for deletion — the dry-run's whole output.</param>
/// <param name="SkippedAmbiguous">Files NOT touched because the match was not certain.</param>
public sealed record PhotoCleanupResult(
    bool Ran,
    string? InactiveReason,
    int Deleted,
    IReadOnlyList<string> WouldDelete,
    IReadOnlyList<string> SkippedAmbiguous)
{
    public static PhotoCleanupResult Inactive(string reason) =>
        new(false, reason, 0, [], []);
}

/// <summary>Configuration for <see cref="ParticipantPhotoCleanupService"/>.</summary>
public sealed class PhotoCleanupOptions
{
    public const string SectionName = "PhotoCleanup";

    /// <summary>
    /// 🔒 <b>TRUE BY DEFAULT, and the default is the feature.</b> Work-order §6.9:
    /// <i>"Ships with dry-run logging enabled by default, logging what it would delete without
    /// deleting. Every other failure mode here is visible; this one loses data."</i>
    /// </summary>
    public bool DryRun { get; set; } = true;
}

/// <summary>
/// §6.9 — when a speaker or volunteer is deactivated, their PHOTO is removed from the document
/// library. The only service in CEH that deletes a document-library file.
/// </summary>
/// <remarks>
/// <para><b>Scope: photos only</b> (work order). <c>Speakers/Photos</c> and <c>Volunteers/Photo</c>.
/// QR codes, evaluation results, presentations and session graphics are untouched — they are event
/// material tied to a session, not personal data tied to a person.</para>
///
/// <para>🔒 <b>The signal is the DEACTIVATION CASCADE, not "LifecycleState == Inactive".</b> The work
/// order says to read the inactive state CEH already defines rather than invent one — and CEH defines
/// two different things with that word. <c>ParticipantLifecycleState.Inactive</c> is the DEFAULT for
/// somebody who has just landed in the pre-selection queue and was never activated at all; deleting
/// on that would mean "we never met you" and "you have left" trigger the same destructive action.
/// This service runs on <see cref="Participant.IsActive"/> = false — the organizer's deliberate
/// deactivation, which <c>ParticipantDeactivationService</c> is the one place that performs.</para>
///
/// <para>⚠️ <b>SPEAKER photos are matched by ID; VOLUNTEER photos can only be matched by NAME.</b>
/// A speaker's file carries the participant id (<see cref="SpeakerPhotoFileName"/>, §768.16), so the
/// match is exact and survives a rename. A volunteer's file is
/// <c>{SanitizedFullName}{ext}</c> — so two volunteers called the same thing produce one candidate
/// file, and deleting it would take the ACTIVE one's photo. <b>An ambiguous volunteer match is
/// therefore never deleted</b>, only reported. That asymmetry is not tidiness: it is the difference
/// between removing personal data and destroying somebody else's.</para>
/// </remarks>
public sealed class ParticipantPhotoCleanupService
{
    private const string SystemName = "SharePoint";

    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly DocLibrary.IDocLibraryPathResolver _paths;
    private readonly PhotoCleanupOptions _options;
    private readonly IExternalWriteGuard _writes;
    private readonly ILogger<ParticipantPhotoCleanupService>? _log;

    public ParticipantPhotoCleanupService(
        CommunityHubDbContext db,
        ISharePointFileStore store,
        DocLibrary.IDocLibraryPathResolver paths,
        PhotoCleanupOptions? options = null,
        IExternalWriteGuard? writes = null,
        ILogger<ParticipantPhotoCleanupService>? log = null)
    {
        _db = db;
        _store = store;
        _paths = paths;
        _options = options ?? new PhotoCleanupOptions();
        _writes = writes ?? new AllowAllExternalWrites();
        _log = log;
    }

    /// <summary>True when this pass would only log.</summary>
    public bool DryRun => _options.DryRun;

    /// <summary>
    /// Remove one deactivated participant's photo. Called from the deactivation cascade, so the
    /// timing is IMMEDIATE (work-order §6.9) rather than a nightly sweep.
    /// </summary>
    /// <remarks>
    /// Never throws: a photo that cannot be deleted must not fail the deactivation itself. The
    /// person's hotel room, party seat and shifts are released by that same transaction, and those
    /// matter more than a file.
    /// </remarks>
    public async Task<PhotoCleanupResult> CleanupParticipantAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        try
        {
            var person = await _db.Participants
                .AsNoTracking()
                .Where(p => p.Id == participantId && p.EventId == eventId)
                .Select(p => new { p.Id, p.FullName, p.Role, p.IsActive })
                .FirstOrDefaultAsync(ct);

            if (person is null) return PhotoCleanupResult.Inactive("No such participant.");

            // 🔒 Re-read the flag rather than trusting the caller. A cleanup triggered for somebody
            // who is still ACTIVE is the one mistake with no undo.
            if (person.IsActive)
                return PhotoCleanupResult.Inactive("The participant is still active — nothing removed.");

            if (!_store.CanRead)
                return PhotoCleanupResult.Inactive("The document library is not readable on this host.");

            if (!await _writes.AllowAsync(SystemName, nameof(ParticipantPhotoCleanupService), ct))
                return PhotoCleanupResult.Inactive("External writes are disabled on this host.");

            return person.Role == ParticipantRole.Volunteer
                ? await CleanupVolunteerAsync(person.Id, person.FullName, ct)
                : await CleanupSpeakerAsync(person.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "§6.9 photo cleanup failed for participant {ParticipantId}; the deactivation itself "
                + "is unaffected.", participantId);
            return PhotoCleanupResult.Inactive($"Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A speaker's photos, matched on the PARTICIPANT ID — both the current
    /// <c>speaker-photo-{id}.{ext}</c> and any legacy <c>speaker-photo-{Name}-{id}.{ext}</c> still in
    /// the folder, because a sponsor-uploaded photo is never re-archived (§768.16).
    /// </summary>
    private async Task<PhotoCleanupResult> CleanupSpeakerAsync(int participantId, CancellationToken ct)
    {
        if (!_paths.TryResolve(DocLibrary.DocLibraryPaths.SpeakerPhotos, out var folder)
            || string.IsNullOrWhiteSpace(folder))
        {
            return PhotoCleanupResult.Inactive("No speaker-photo folder is configured.");
        }

        var files = await _store.ListAsync(folder, ct);
        var mine = files
            .Where(f => SpeakerPhotoFileName.TryParse(StripExtension(f.Name), out var id, out _)
                        && id == participantId)
            .ToList();

        return await ApplyAsync(folder, mine.Select(f => f.Name).ToList(), [], ct);
    }

    /// <summary>
    /// A volunteer's photo, matched on the sanitised NAME — the only key that file carries.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Ambiguity is refused, not resolved.</b> If more than one file matches, or another
    /// participant in the edition sanitises to the same name, nothing is deleted and the case is
    /// reported. Deleting on a name collision would remove an ACTIVE volunteer's photo — and unlike
    /// every other failure in this codebase, that one cannot be re-run into correctness.
    /// </remarks>
    private async Task<PhotoCleanupResult> CleanupVolunteerAsync(
        int participantId, string fullName, CancellationToken ct)
    {
        if (!_paths.TryResolve(DocLibrary.DocLibraryPaths.VolunteerPhotos, out var folder)
            || string.IsNullOrWhiteSpace(folder))
        {
            return PhotoCleanupResult.Inactive("No volunteer-photo folder is configured.");
        }

        var files = await _store.ListAsync(folder, ct);

        // §769.9 — an ID-named file is an EXACT match: delete it, no ambiguity possible.
        var byId = files
            .Where(f => VolunteerPhotoFileName.TryParse(f.Name, out var id) && id == participantId)
            .Select(f => f.Name)
            .ToList();

        // 🔒 A LEGACY {Full Name}.ext file can only be matched by name, and a name is not an
        // identity. If another ACTIVE participant carries the same name, or more than one file
        // matches, it is REPORTED and left alone — deleting it would take the active person's photo.
        // This branch shrinks to nothing as volunteers re-upload under the new convention.
        var legacy = files
            .Where(f => !VolunteerPhotoFileName.IsCurrentConvention(f.Name)
                        && VolunteerPhotoFileName.Matches(f.Name, participantId, fullName))
            .Select(f => f.Name)
            .ToList();

        var ambiguous = new List<string>();
        if (legacy.Count > 0)
        {
            var activeNamesake = await _db.Participants
                .AsNoTracking()
                .AnyAsync(p => p.FullName == fullName
                               && p.Id != participantId
                               && p.IsActive, ct);

            if (activeNamesake || legacy.Count > 1)
            {
                _log?.LogWarning(
                    "§6.9 photo cleanup: {Count} LEGACY volunteer photo(s) match '{Name}' and the "
                    + "match is not unambiguous — nothing deleted for those. They are name-keyed; "
                    + "an id-named replacement appears when that volunteer next uploads.",
                    legacy.Count, fullName);
                ambiguous.AddRange(legacy);
                legacy.Clear();
            }
        }

        return await ApplyAsync(folder, [.. byId, .. legacy], ambiguous, ct);
    }

    /// <summary>Delete, or — in dry run — say exactly what would have been deleted.</summary>
    private async Task<PhotoCleanupResult> ApplyAsync(
        string folder, IReadOnlyList<string> names, IReadOnlyList<string> ambiguous,
        CancellationToken ct)
    {
        if (names.Count == 0) return new PhotoCleanupResult(true, null, 0, [], ambiguous);

        if (_options.DryRun)
        {
            // The dry run IS the deliverable until he switches it off: a line naming every file, so
            // the first live run holds no surprises.
            _log?.LogInformation(
                "§6.9 photo cleanup (DRY RUN — nothing deleted): would remove {Count} file(s) from "
                + "'{Folder}': {Files}",
                names.Count, folder, string.Join(", ", names));
            return new PhotoCleanupResult(true, null, 0, names, ambiguous);
        }

        var deleted = 0;
        foreach (var name in names)
        {
            await _store.DeleteFromFolderAsync(folder, name, ct);
            deleted++;
            _log?.LogInformation(
                "§6.9 photo cleanup: deleted '{File}' from '{Folder}'.", name, folder);
        }

        return new PhotoCleanupResult(true, null, deleted, names, ambiguous);
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }
}
