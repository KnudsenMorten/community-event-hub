using System.Net.Http;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Settings;
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
    // (the combined API pull). Null for a speakers-only import.
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
/// Imports Sessionize speakers (from the Sessionize v2 view API via
/// <see cref="SessionizeApiImportService"/>) as <see cref="Participant"/> rows.
/// (The legacy Excel/.xlsx upload entry point was removed — §82, API-only now.)
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
    private readonly WelcomeEmailService _welcome;
    private readonly TimeProvider _clock;
    // Optional (§26c): used to copy the Sessionize profile picture into SharePoint.
    // Null in unit tests / when SharePoint isn't configured — picture import is then
    // simply skipped (best-effort), never failing the import.
    private readonly ISharePointFileStore? _pictureStore;
    // Shared client for the best-effort picture fetch (low volume; one per process).
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SessionizeImportService(
        CommunityHubDbContext db,
        WelcomeEmailService welcome,
        TimeProvider clock,
        ISharePointFileStore? pictureStore = null)
    {
        _db = db;
        _welcome = welcome;
        _clock = clock;
        _pictureStore = pictureStore;
    }

    /// <summary>
    /// Run the import for an edition from an already-parsed speaker list. This
    /// is the shared core used by the Sessionize API path
    /// (<c>SessionizeApiImportService</c>): the upsert / match-on-email /
    /// never-change-role / never-delete semantics live here. <paramref name="warnings"/>
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
                    // Active by default — set BOTH fields so ParticipantActivation.IsActive
                    // is true. IsActive alone left LifecycleState defaulting to Inactive,
                    // so imported speakers read inactive everywhere (same dual-field bug
                    // fixed for synced sponsors). Ring gating still controls their emails.
                    IsActive = true,
                    LifecycleState = ParticipantLifecycleState.Active,
                    // §26c: imported speakers default to the LOCKED ring (Broad = released
                    // last) so NOTHING ring-gated (email, Zoho sync) fires for them until an
                    // organizer explicitly promotes them. Set explicitly, not relying on the
                    // model default. (Broad is "locked" because the speaker features are
                    // released to an inner ring; promotion = moving the speaker inward.)
                    Ring = Ring.Broad,
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
                prof.FirstName = string.IsNullOrWhiteSpace(s.FirstName) ? prof.FirstName : s.FirstName;
                prof.LastName  = string.IsNullOrWhiteSpace(s.LastName) ? prof.LastName : s.LastName;
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
                if (string.IsNullOrWhiteSpace(prof.FirstName)) prof.FirstName = s.FirstName;
                if (string.IsNullOrWhiteSpace(prof.LastName))  prof.LastName  = s.LastName;
            }

            // Identity: always track the Sessionize speaker id (it never "belongs" to
            // the speaker to edit). Match/dedup + future picture refresh key.
            if (!string.IsNullOrWhiteSpace(s.SessionizeId)) prof.SessionizeSpeakerId = s.SessionizeId;

            // Picture -> SharePoint (best-effort, §26c): fetch the Sessionize
            // profilePicture once and store a stable copy. Only when we have a URL, a
            // configured store, and no stored copy yet; a failure never fails the import.
            if (_pictureStore?.CanStore == true
                && !string.IsNullOrWhiteSpace(s.ProfilePictureUrl)
                && string.IsNullOrWhiteSpace(prof.PhotoSharePointPath))
            {
                try
                {
                    var storedPath = await FetchAndStorePictureAsync(p.Id, s.ProfilePictureUrl!, ct);
                    if (!string.IsNullOrWhiteSpace(storedPath)) prof.PhotoSharePointPath = storedPath;
                }
                catch { /* best-effort: a picture failure must not fail the import */ }
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

    /// <summary>
    /// Fetch a Sessionize profile-picture URL and store a stable copy in SharePoint
    /// (e.g. <c>Speakers/speaker-42.jpg</c>). Returns the stored relative path, or
    /// null when nothing could be fetched/stored. Best-effort: the caller swallows
    /// failures so a picture issue never fails the speaker import.
    /// </summary>
    private async Task<string?> FetchAndStorePictureAsync(int participantId, string url, CancellationToken ct)
    {
        if (_pictureStore is null) return null;
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return null;
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var ext = contentType.Contains("png") ? ".png"
                : contentType.Contains("gif") ? ".gif"
                : contentType.Contains("webp") ? ".webp" : ".jpg";
        var stored = await _pictureStore.StoreAsync($"Speakers/speaker-{participantId}{ext}", bytes, contentType, ct);
        return stored.Path;
    }
}
