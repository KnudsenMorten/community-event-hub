using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>The outcome of a Sessionize import run.</summary>
public sealed record SessionizeImportResult(
    int Fetched,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<string> Warnings,
    string? Error,
    // The companion SESSIONS import result, when the run also imported sessions
    // (the combined API pull). Null for the Excel upload (speakers only).
    SessionImportResult? Sessions = null);

/// <summary>
/// How a Sessionize import treats the speaker bio fields (Tagline, Biography,
/// Blog, LinkedIn, Twitter, PhotoUrl), which are seeded from Sessionize but
/// OWNED by the speaker once they edit them in the hub.
/// </summary>
public enum SessionizeImportMode
{
    /// <summary>
    /// Add NEW speakers and fill only bio fields that are genuinely empty AND
    /// have NOT been edited by the speaker. Never overwrites a speaker's own
    /// edit. This is the scheduled / auto-sync default — a re-import preserves
    /// every change a speaker made on their own page.
    /// </summary>
    Delta,

    /// <summary>
    /// Force-refresh ALL bio fields from Sessionize for a complete re-seed and
    /// clear each speaker's "edited" set — the deliberate organizer override
    /// ("Full import from Sessionize"). Still matches on email, never changes
    /// the role, never deletes, never touches the hub-collected fields or the
    /// ContactEmailOverride.
    /// </summary>
    Full,
}

/// <summary>
/// Imports Sessionize speakers from an uploaded Excel file as
/// <see cref="Participant"/> rows (CONTEXT.md / DESIGN_NOTES). The organizer
/// exports speakers from Sessionize to .xlsx and uploads it - no API endpoint,
/// no network dependency.
///
/// Rules (the documented defaults):
///  - Match on email within the edition (the existing unique key).
///  - New email  -> create a Participant, role Speaker, IsActive = true, and
///    send the welcome email.
///  - Existing email -> update the name; do NOT change the role (an organizer
///    may have re-classified them).
///  - Never delete: a speaker removed in Sessionize is deactivated by an
///    organizer on the Participants page, not auto-removed.
/// </summary>
public sealed class SessionizeImportService
{
    private readonly CommunityHubDbContext _db;
    private readonly SessionizeExcelParser _parser;
    private readonly WelcomeEmailService _welcome;
    private readonly TimeProvider _clock;

    public SessionizeImportService(
        CommunityHubDbContext db,
        SessionizeExcelParser parser,
        WelcomeEmailService welcome,
        TimeProvider clock)
    {
        _db = db;
        _parser = parser;
        _welcome = welcome;
        _clock = clock;
    }

    /// <summary>
    /// Run the import for an edition from an uploaded Excel stream. Returns
    /// counts + warnings; never throws for a bad file - the error is in the
    /// result.
    /// </summary>
    public async Task<SessionizeImportResult> ImportAsync(
        int eventId,
        Stream excelStream,
        CancellationToken ct = default,
        bool sendWelcome = true,
        SessionizeImportMode mode = SessionizeImportMode.Delta)
    {
        var parsed = _parser.Parse(excelStream);
        if (parsed.Error is not null)
        {
            return new SessionizeImportResult(
                0, 0, 0, 0, parsed.Warnings, parsed.Error);
        }

        return await ImportSpeakersAsync(
            eventId, parsed.Speakers, parsed.Warnings, ct, sendWelcome, mode);
    }

    /// <summary>
    /// Run the import for an edition from an already-parsed speaker list. This
    /// is the shared core used by BOTH the Excel upload path (above) and the
    /// Sessionize API path (<c>SessionizeApiImportService</c>): the upsert /
    /// match-on-email / never-change-role / never-delete semantics live here
    /// once, so the two sources behave identically. <paramref name="warnings"/>
    /// from the source (e.g. rows skipped for a missing email) are carried
    /// through into the result.
    /// </summary>
    public async Task<SessionizeImportResult> ImportSpeakersAsync(
        int eventId,
        IReadOnlyList<SessionizeSpeaker> speakers,
        IReadOnlyList<string> warnings,
        CancellationToken ct = default,
        bool sendWelcome = true,
        SessionizeImportMode mode = SessionizeImportMode.Delta)
    {
        var parsed = new SessionizeParseResult(speakers, warnings, null);

        // Existing speakers for this edition, by email.
        var existing = await _db.Participants
            .Where(p => p.EventId == eventId)
            .ToDictionaryAsync(p => p.Email, p => p, ct);

        var existingProfiles = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId)
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp, ct);

        var now = _clock.GetUtcNow();
        int created = 0, updated = 0, skipped = 0;
        var newParticipants = new List<Participant>();
        // Stash the Sessionize-imported fields per email so we can fan them
        // into SpeakerProfile after EF assigns the new participants their Ids.
        var profileWrites = new Dictionary<string, SessionizeSpeaker>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in parsed.Speakers)
        {
            var fullName = $"{s.FirstName} {s.LastName}".Trim();
            profileWrites[s.Email] = s;

            if (existing.TryGetValue(s.Email, out var participant))
            {
                if (participant.FullName != fullName
                    && !string.IsNullOrWhiteSpace(fullName))
                {
                    participant.FullName = fullName;
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                var fresh = new Participant
                {
                    EventId = eventId,
                    Email = s.Email,
                    FullName = fullName,
                    Role = ParticipantRole.Speaker,
                    IsActive = true,
                    CreatedAt = now,
                };
                _db.Participants.Add(fresh);
                newParticipants.Add(fresh);
                created++;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Upsert SpeakerProfile rows with the speaker bio fields. Hub-collected
        // fields (Accreditation, IsFirstTimeSpeaker, Country, Gender,
        // ContactEmailOverride) are NEVER touched by the import.
        //
        // The bio fields (Tagline, Biography, Blog, LinkedIn, Twitter, PhotoUrl)
        // are seeded from Sessionize but OWNED by the speaker once edited:
        //  - Delta mode (scheduled / auto): fill a field only when it is empty
        //    AND the speaker has NOT edited it — a re-import never flushes a
        //    speaker's own change.
        //  - Full mode (organizer "Full import" override): force-refresh every
        //    field from Sessionize and clear the speaker-edited set.
        foreach (var p in (await _db.Participants
                     .Where(x => x.EventId == eventId)
                     .ToListAsync(ct)))
        {
            if (!profileWrites.TryGetValue(p.Email, out var s)) continue;

            if (!existingProfiles.TryGetValue(p.Id, out var prof))
            {
                prof = new SpeakerProfile
                {
                    EventId = eventId,
                    ParticipantId = p.Id,
                    CreatedAt = now,
                };
                _db.SpeakerProfiles.Add(prof);
            }

            if (mode == SessionizeImportMode.Full)
            {
                // Operator override: re-seed from Sessionize and drop the dirty
                // set so the profile tracks Sessionize again. A blank value in
                // the source clears the field (a true full refresh).
                prof.Tagline   = s.TagLine;
                prof.Biography = s.Biography;
                prof.Blog      = s.Blog;
                prof.LinkedIn  = s.LinkedIn;
                prof.Twitter   = s.Twitter;
                prof.PhotoUrl  = s.ProfilePictureUrl;
                prof.ClearSpeakerEdited();
            }
            else
            {
                // Delta: only fill genuinely-empty, never-speaker-edited fields.
                prof.Tagline   = FillIfUntouched(prof, SpeakerProfile.BioFields.Tagline,   prof.Tagline,   s.TagLine);
                prof.Biography = FillIfUntouched(prof, SpeakerProfile.BioFields.Biography, prof.Biography, s.Biography);
                prof.Blog      = FillIfUntouched(prof, SpeakerProfile.BioFields.Blog,      prof.Blog,      s.Blog);
                prof.LinkedIn  = FillIfUntouched(prof, SpeakerProfile.BioFields.LinkedIn,  prof.LinkedIn,  s.LinkedIn);
                prof.Twitter   = FillIfUntouched(prof, SpeakerProfile.BioFields.Twitter,   prof.Twitter,   s.Twitter);
                prof.PhotoUrl  = FillIfUntouched(prof, SpeakerProfile.BioFields.PhotoUrl,  prof.PhotoUrl,  s.ProfilePictureUrl);
            }

            prof.UpdatedAt = now;
            prof.LastSessionizeImportAt = now;
        }

        await _db.SaveChangesAsync(ct);

        // Send the welcome email to each newly-created speaker. Idempotent via
        // SentReminder ledger; re-imports never re-welcome. A send failure for
        // one person must not fail the whole import.
        // sendWelcome=false lets an organizer bulk-load a Sessionize export
        // (e.g. ahead of an event when speaker lineup is still being shuffled)
        // without spamming anyone -- they can send welcomes manually later.
        if (sendWelcome)
        {
            foreach (var p in newParticipants)
            {
                try { await _welcome.SendWelcomeAsync(p.Id, ct); }
                catch { /* logged in email layer; import result is unaffected */ }
            }
        }

        return new SessionizeImportResult(
            parsed.Speakers.Count, created, updated, skipped,
            parsed.Warnings, null);
    }

    /// <summary>
    /// Delta-merge a single bio field: keep the current value when the speaker
    /// has edited this field OR a value is already present; otherwise take the
    /// incoming Sessionize value (which may itself be null). This is what makes
    /// the scheduled sync "add NEW + fill empty, never flush speaker edits".
    /// </summary>
    private static string? FillIfUntouched(
        SpeakerProfile prof, string field, string? current, string? incoming)
    {
        if (prof.IsSpeakerEdited(field)) return current;          // speaker owns it
        if (!string.IsNullOrWhiteSpace(current)) return current;  // already populated
        return incoming;                                          // genuinely empty
    }
}
