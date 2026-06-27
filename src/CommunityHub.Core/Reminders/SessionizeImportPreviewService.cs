using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One speaker that an import would touch, with what would happen to them.
/// </summary>
/// <param name="Email">The speaker's email (the match key).</param>
/// <param name="FullName">The speaker's name as it would be stored.</param>
/// <param name="Action">Create / Update / Skip — the participant-row outcome.</param>
/// <param name="OverwrittenFields">
/// The bio fields that this run WOULD overwrite with the incoming Sessionize value,
/// i.e. fields that currently hold a value (or a speaker-edited value) and would be
/// replaced. Empty for a create, and empty in Delta mode (delta never overwrites).
/// Each entry names the field and flags whether the speaker had edited it.
/// </param>
public sealed record SessionizeImportPreviewRow(
    string Email,
    string FullName,
    SessionizeImportAction Action,
    IReadOnlyList<SessionizeOverwriteField> OverwrittenFields);

/// <summary>A bio field a Full import would overwrite, and whether the speaker owned it.</summary>
/// <param name="Field">The bio field token (<see cref="SpeakerProfile.BioFields"/>).</param>
/// <param name="SpeakerEdited">
/// True when the speaker had edited this field in the hub — overwriting it discards
/// curated content. This is the case the dry-run exists to surface.
/// </param>
/// <param name="CurrentValue">The current hub value (trimmed for display).</param>
/// <param name="IncomingValue">The value Sessionize would write (trimmed for display).</param>
public sealed record SessionizeOverwriteField(
    string Field,
    bool SpeakerEdited,
    string? CurrentValue,
    string? IncomingValue);

/// <summary>The participant-row outcome an import would produce for a speaker.</summary>
public enum SessionizeImportAction
{
    /// <summary>A new participant would be created (new email).</summary>
    Create,
    /// <summary>An existing participant would be updated (name and/or bio fields).</summary>
    Update,
    /// <summary>Nothing would change for this participant row.</summary>
    Skip,
}

/// <summary>
/// The full outcome of a Sessionize import DRY-RUN — created / updated / skipped
/// counts and, crucially, exactly which speaker bios WOULD be overwritten, computed
/// WITHOUT writing anything. The organizer reviews this before confirming a real
/// import, so a blind Full import can never silently clobber curated speaker content.
/// </summary>
/// <param name="Mode">The mode previewed (Delta = additive; Full = force-refresh).</param>
/// <param name="Fetched">How many speaker rows the source yielded.</param>
/// <param name="Created">How many participants would be created.</param>
/// <param name="Updated">How many existing participants would change.</param>
/// <param name="Skipped">How many would be unchanged.</param>
/// <param name="Rows">Per-speaker detail (only those the import would touch).</param>
/// <param name="Warnings">Source warnings (e.g. rows skipped for a missing email).</param>
/// <param name="Error">Set when the source could not be read; counts are then zero.</param>
public sealed record SessionizeImportPreviewResult(
    SessionizeImportMode Mode,
    int Fetched,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<SessionizeImportPreviewRow> Rows,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    /// <summary>Speakers whose SPEAKER-EDITED bios this run would overwrite (the real danger).</summary>
    public IReadOnlyList<SessionizeImportPreviewRow> RowsClobberingSpeakerEdits =>
        Rows.Where(r => r.OverwrittenFields.Any(f => f.SpeakerEdited)).ToList();

    /// <summary>How many distinct speaker-edited bio fields this run would overwrite.</summary>
    public int SpeakerEditedFieldsOverwritten =>
        Rows.Sum(r => r.OverwrittenFields.Count(f => f.SpeakerEdited));

    /// <summary>How many bio fields in total this run would overwrite.</summary>
    public int FieldsOverwritten => Rows.Sum(r => r.OverwrittenFields.Count);
}

/// <summary>
/// Computes a Sessionize import DRY-RUN (REQUIREMENTS §21 organizer "import
/// dry-run/preview"). It reads the SAME source the real import reads — the v2 view
/// API (<see cref="SessionizeApiClient"/>) — and applies the SAME upsert / bio-merge
/// rules as <see cref="SessionizeImportService"/>, but in a read-only pass that
/// NEVER writes. It reports created / updated / skipped counts AND which speaker
/// bios would be overwritten (and whether the speaker had curated them), so the
/// organizer confirms with full context. The real import is run separately, only
/// after the organizer confirms.
///
/// The merge logic here MUST stay in lock-step with
/// <see cref="SessionizeImportService.ImportSpeakersAsync"/>:
///   - Delta fills only genuinely-empty, never-speaker-edited fields, so it
///     overwrites NOTHING (its preview rows carry no overwritten fields).
///   - Full force-refreshes every bio field and clears the speaker-edited set, so
///     it overwrites any field whose current value differs from the incoming one —
///     including fields the speaker had edited. Those are surfaced explicitly.
/// </summary>
public sealed class SessionizeImportPreviewService
{
    private readonly CommunityHubDbContext _db;
    private readonly SessionizeApiClient _apiClient;
    private readonly SessionizeApiOptions _apiOptions;

    public SessionizeImportPreviewService(
        CommunityHubDbContext db,
        SessionizeApiClient apiClient,
        SessionizeApiOptions apiOptions)
    {
        _db = db;
        _apiClient = apiClient;
        _apiOptions = apiOptions;
    }

    /// <summary>Dry-run a Sessionize API pull (no file, no writes).</summary>
    public async Task<SessionizeImportPreviewResult> PreviewApiAsync(
        int eventId,
        SessionizeImportMode mode,
        CancellationToken ct = default)
    {
        if (!_apiOptions.Enabled
            || string.IsNullOrWhiteSpace(_apiOptions.EndpointId))
        {
            return Empty(mode,
                "The Sessionize API integration is not configured "
                + "(Sessionize:Enabled = false or no endpoint id).");
        }

        var fetched = await _apiClient.FetchSpeakersAsync(ct);
        if (fetched.Error is not null)
        {
            return Empty(mode, fetched.Error, fetched.Warnings);
        }

        return await ComputeAsync(eventId, mode, fetched.Speakers, fetched.Warnings, ct);
    }

    /// <summary>
    /// The read-only core: replays the upsert + bio-merge rules over the supplied
    /// speakers against the current DB state and reports what WOULD change.
    /// </summary>
    public async Task<SessionizeImportPreviewResult> ComputeAsync(
        int eventId,
        SessionizeImportMode mode,
        IReadOnlyList<SessionizeSpeaker> speakers,
        IReadOnlyList<string> warnings,
        CancellationToken ct = default)
    {
        // Read-only snapshots of the current state (no tracking — we never save).
        var existing = await _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId)
            .ToDictionaryAsync(p => p.Email, p => p, StringComparer.OrdinalIgnoreCase, ct);

        var profilesByParticipant = await _db.SpeakerProfiles
            .AsNoTracking()
            .Where(sp => sp.EventId == eventId)
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp, ct);

        int created = 0, updated = 0, skipped = 0;
        var rows = new List<SessionizeImportPreviewRow>();

        foreach (var s in speakers)
        {
            var fullName = $"{s.FirstName} {s.LastName}".Trim();

            if (!existing.TryGetValue(s.Email, out var participant))
            {
                created++;
                rows.Add(new SessionizeImportPreviewRow(
                    s.Email, fullName, SessionizeImportAction.Create,
                    Array.Empty<SessionizeOverwriteField>()));
                continue;
            }

            // Existing participant: name change?
            var nameWouldChange =
                participant.FullName != fullName && !string.IsNullOrWhiteSpace(fullName);

            // Bio-field overwrites (only Full overwrites; Delta only fills empties).
            var overwrites = new List<SessionizeOverwriteField>();
            profilesByParticipant.TryGetValue(participant.Id, out var prof);

            if (mode == SessionizeImportMode.Full)
            {
                AddOverwrite(overwrites, prof, SpeakerProfile.BioFields.Tagline,   prof?.Tagline,   s.TagLine);
                AddOverwrite(overwrites, prof, SpeakerProfile.BioFields.Biography, prof?.Biography, s.Biography);
                AddOverwrite(overwrites, prof, SpeakerProfile.BioFields.Blog,      prof?.Blog,      s.Blog);
                AddOverwrite(overwrites, prof, SpeakerProfile.BioFields.LinkedIn,  prof?.LinkedIn,  s.LinkedIn);
                AddOverwrite(overwrites, prof, SpeakerProfile.BioFields.Twitter,   prof?.Twitter,   s.Twitter);
                AddOverwrite(overwrites, prof, SpeakerProfile.BioFields.PhotoUrl,  prof?.PhotoUrl,  s.ProfilePictureUrl);
            }

            // Counts mirror SessionizeImportService EXACTLY so the dry-run numbers
            // equal what the real import reports: it counts a row as Updated ONLY
            // when the participant NAME changes, else Skipped. Bio-field changes do
            // not move that counter — they are surfaced separately as
            // OverwrittenFields / the delta-fill flag, which is what the organizer
            // actually needs to see before a Full import.
            if (nameWouldChange) updated++;
            else skipped++;

            // Emit a detail row when there is anything worth showing for this
            // speaker: a name change, fields a Full import would overwrite, or an
            // empty field a Delta would fill. Pure no-ops are omitted (just counted).
            var deltaFills = mode == SessionizeImportMode.Delta
                && DeltaWouldFill(prof, s);
            if (nameWouldChange || overwrites.Count > 0 || deltaFills)
            {
                rows.Add(new SessionizeImportPreviewRow(
                    s.Email, fullName, SessionizeImportAction.Update, overwrites));
            }
        }

        return new SessionizeImportPreviewResult(
            mode, speakers.Count, created, updated, skipped, rows, warnings, null);
    }

    /// <summary>
    /// A Full import overwrites a bio field when the field currently holds a value
    /// that DIFFERS from the incoming Sessionize value (replacing identical content
    /// is not a meaningful overwrite). A speaker-edited field that differs is the
    /// dangerous case and is flagged.
    /// </summary>
    private static void AddOverwrite(
        List<SessionizeOverwriteField> sink,
        SpeakerProfile? prof,
        string field,
        string? current,
        string? incoming)
    {
        // Nothing to lose if the field is currently empty.
        if (string.IsNullOrWhiteSpace(current)) return;
        // Replacing a value with the SAME value is not an overwrite worth flagging.
        if (string.Equals(current?.Trim(), incoming?.Trim(), StringComparison.Ordinal)) return;

        var edited = prof?.IsSpeakerEdited(field) ?? false;
        sink.Add(new SessionizeOverwriteField(
            field, edited, Trim(current), Trim(incoming)));
    }

    /// <summary>
    /// True when a Delta run would FILL at least one genuinely-empty,
    /// never-speaker-edited field (the only way delta changes a profile). Mirrors
    /// <c>SessionizeImportService.FillIfUntouched</c>.
    /// </summary>
    private static bool DeltaWouldFill(SpeakerProfile? prof, SessionizeSpeaker s)
    {
        return WouldFill(prof, SpeakerProfile.BioFields.Tagline,   prof?.Tagline,   s.TagLine)
            || WouldFill(prof, SpeakerProfile.BioFields.Biography, prof?.Biography, s.Biography)
            || WouldFill(prof, SpeakerProfile.BioFields.Blog,      prof?.Blog,      s.Blog)
            || WouldFill(prof, SpeakerProfile.BioFields.LinkedIn,  prof?.LinkedIn,  s.LinkedIn)
            || WouldFill(prof, SpeakerProfile.BioFields.Twitter,   prof?.Twitter,   s.Twitter)
            || WouldFill(prof, SpeakerProfile.BioFields.PhotoUrl,  prof?.PhotoUrl,  s.ProfilePictureUrl);

        static bool WouldFill(SpeakerProfile? p, string field, string? current, string? incoming)
        {
            if (p?.IsSpeakerEdited(field) ?? false) return false;     // speaker owns it
            if (!string.IsNullOrWhiteSpace(current)) return false;    // already populated
            return !string.IsNullOrWhiteSpace(incoming);              // empty + something to fill
        }
    }

    private static string? Trim(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        return v.Length <= 160 ? v : v.Substring(0, 157) + "…";
    }

    private static SessionizeImportPreviewResult Empty(
        SessionizeImportMode mode, string error, IReadOnlyList<string>? warnings = null) =>
        new(mode, 0, 0, 0, 0,
            Array.Empty<SessionizeImportPreviewRow>(),
            warnings ?? Array.Empty<string>(), error);
}
