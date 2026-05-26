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
    string? Error);

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
///    may have re-classified them as a MasterclassSpeaker).
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
        bool sendWelcome = true)
    {
        var parsed = _parser.Parse(excelStream);
        if (parsed.Error is not null)
        {
            return new SessionizeImportResult(
                0, 0, 0, 0, parsed.Warnings, parsed.Error);
        }

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

        // Upsert SpeakerProfile rows with Sessionize-imported fields only.
        // Hub-collected fields (Accreditation, IsFirstTimeSpeaker, Country,
        // Gender) are NEVER touched by the import.
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
            prof.Tagline   = s.TagLine   ?? prof.Tagline;
            prof.Biography = s.Biography ?? prof.Biography;
            prof.Blog      = s.Blog      ?? prof.Blog;
            prof.LinkedIn  = s.LinkedIn  ?? prof.LinkedIn;
            prof.Twitter   = s.Twitter   ?? prof.Twitter;
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
}
