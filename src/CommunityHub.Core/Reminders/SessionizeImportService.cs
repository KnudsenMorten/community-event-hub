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
    SessionImportResult? Sessions = null,
    // §204 — how many email-less Sessionize speakers were brought into the
    // PRE-SELECTION QUEUE this run (created as inactive prospective participants
    // keyed by Sessionize id), as opposed to skipped. Reported to the organizer
    // as "added to pre-selection (no email yet)". Default 0.
    int PreselectedNoEmail = 0,
    // §827.6 — standing sign-in links revoked this run because their owner changed
    // e-mail address in Sessionize. Surfaced rather than silent: revoking a
    // credential is exactly the kind of thing that must be visible in the run's own
    // summary, since this service has no audit trail of its own (§827.4).
    int RevokedMagicLinks = 0);

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
    // §768.16 — where the photo goes. The SAME registry key every other speaker-photo writer and
    // reader uses; without it this service is the one that writes somewhere nobody looks.
    private readonly Core.Integrations.DocLibrary.IDocLibraryPathResolver? _paths;
    // Shared client for the best-effort picture fetch (low volume; one per process).
    private static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    // §768.16 — an INJECTABLE fetch seam. It was a static field, so where the picture copy landed
    // could not be asserted at all: the only test that could reach it would have to hit the real
    // network. That is why it went four §768 phases writing to a retired root unnoticed.
    private readonly HttpClient _http;

    public SessionizeImportService(
        CommunityHubDbContext db,
        WelcomeEmailService welcome,
        TimeProvider clock,
        ISharePointFileStore? pictureStore = null,
        Core.Integrations.DocLibrary.IDocLibraryPathResolver? paths = null,
        HttpClient? http = null)
    {
        _db = db;
        _welcome = welcome;
        _clock = clock;
        _pictureStore = pictureStore;
        _paths = paths;
        _http = http ?? _shared;
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
        SessionizeImportMode mode = SessionizeImportMode.Delta,
        IReadOnlyList<SessionizeSpeaker>? emailLessSpeakers = null)
    {
        var parsed = new SessionizeParseResult(speakers, warnings, null);

        // Existing speakers for this edition, by email — CASE-INSENSITIVE (§253
        // G17): other entry paths historically stored raw casing (e.g. the sponsor
        // contact sync before its normalization fix), and an Ordinal miss here
        // created a near-duplicate participant row for the same address. First
        // row wins if legacy rows differ only by case (the DB collation treats
        // them as one identity anyway).
        var existingRows = await _db.Participants
            .Where(p => p.EventId == eventId)
            .ToListAsync(ct);
        var existing = new Dictionary<string, Participant>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in existingRows) existing.TryAdd(row.Email, row);

        var existingProfiles = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId)
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp, ct);

        // §204: existing participants keyed by their Sessionize speaker id (via the
        // SpeakerProfile id-link). Used to (a) reconcile an email-less PLACEHOLDER
        // queue row onto its real email once the speaker accepts the invite, and
        // (b) avoid creating a duplicate queue row for an id we already hold.
        var bySessionizeId = await (
            from sp in _db.SpeakerProfiles
            join p in _db.Participants on sp.ParticipantId equals p.Id
            where sp.EventId == eventId && sp.SessionizeSpeakerId != null
            select new { sp.SessionizeSpeakerId, Participant = p })
            .ToListAsync(ct);
        // §827 — prefer an ACTIVE row when one Sessionize id already maps to several participants.
        // 🔒 Not hypothetical: the defect fixed below created exactly that pair for one speaker (two
        // participants sharing one Sessionize id), and the DEACTIVATED one was the older. Reconciling
        // onto a row an organizer has deliberately deactivated would resurrect it and collide its
        // e-mail with the live row.
        var participantBySessionizeId = bySessionizeId
            .GroupBy(x => x.SessionizeSpeakerId!)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Participant.DeactivatedByOrganizerAt != null ? 1 : 0)
                      .ThenByDescending(x => x.Participant.Id)
                      .First().Participant,
                StringComparer.OrdinalIgnoreCase);

        var now = _clock.GetUtcNow();
        int created = 0, updated = 0, skipped = 0;
        // §827.6 — standing sign-in links revoked because their owner changed e-mail address.
        int revokedLinks = 0;
        var newParticipants = new List<Participant>();
        // Stash the Sessionize-imported fields per email so we can fan them
        // into SpeakerProfile after EF assigns the new participants their Ids.
        var profileWrites = new Dictionary<string, SessionizeSpeaker>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in parsed.Speakers)
        {
            var fullName = $"{s.FirstName} {s.LastName}".Trim();
            profileWrites[s.Email] = s;

            // 🔴🔴 §1021 — THE SESSIONIZE ID IS TRIED **FIRST**. E-MAIL IS THE FALLBACK.
            //
            // §827 already wrote the rule down — *"e-mail is a MUTABLE ATTRIBUTE of a person; the
            // Sessionize id IS the person"* — and then kept testing e-mail first anyway, so a stale
            // row holding an address could PRE-EMPT the id match. That is not theoretical:
            //
            //   Measured in PROD 2026-08-09. One speaker, two participants sharing Sessionize id
            //   a09f9626…: #41 thomas@impnd.com (organizer-DEACTIVATED 4 Aug, and the row that
            //   still holds session 15) and #110 thomas.martinsen@hey.com (active, no sessions).
            //   He renamed himself in Sessionize BACK to thomas@impnd.com. The importer looked that
            //   address up, hit the DEACTIVATED #41, took this branch, updated a name and stopped —
            //   so the live row #110 never learned the new address, nothing pushed to Zoho, and the
            //   operator saw the import do nothing at all.
            //
            // 🔑 Trying the id first makes the LIVE row win, because `participantBySessionizeId`
            // already prefers a non-deactivated one. E-mail then only decides for a speaker whose
            // id we have never seen.
            var matchedById =
                !string.IsNullOrWhiteSpace(s.SessionizeId)
                && participantBySessionizeId.TryGetValue(s.SessionizeId, out var byId)
                && byId.DeactivatedByOrganizerAt is null
                    ? byId : null;

            // 🔒 A RENAME MAY NOT STEAL AN ADDRESS ANOTHER PARTICIPANT STILL HOLDS. The e-mail is
            // the login identity and is unique per edition, so writing it would either throw on
            // SaveChanges or fuse two people's logins. When the target address already belongs to
            // somebody else, the id match is abandoned and we fall through to the e-mail branch,
            // which touches only that row's name.
            //
            // ⚠️ That is Thomas exactly: #41 holds thomas@impnd.com and #110 carries the same
            // Sessionize id. Two hub records for one person is a MERGE, and a merge decides which
            // sessions, logins and Zoho ids survive — never something an importer should infer.
            if (matchedById is not null
                && existing.TryGetValue(s.Email, out var holder)
                && holder.Id != matchedById.Id)
            {
                matchedById = null;
            }

            if (existing.TryGetValue(s.Email, out var participant) && matchedById is null)
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
            else if (matchedById is not null
                     && participantBySessionizeId.TryGetValue(s.SessionizeId, out var placeholder)
                     // 🔴 §827 — THE `IsPlaceholderEmail` GUARD USED TO BE HERE, AND IT FORKED PEOPLE.
                     //
                     // Reconciling by Sessionize id only when the OLD address was a placeholder meant
                     // a speaker who changed one REAL e-mail for another fell through to "create a
                     // new participant" — with the same SessionizeSpeakerId as the row that already
                     // existed. Measured on 2026-08-04: a speaker became two participants, his
                     // SESSION moved to the new row leaving the old one with none, he was welcomed a
                     // second time, and he resurfaced as a pending speaker two days after completing
                     // Get Started. The importer had his stable identity in hand the whole time.
                     //
                     // 🔒 E-mail is a MUTABLE ATTRIBUTE of a person; the Sessionize id IS the person.
                     // So an id match now reconciles whatever the old address was — placeholder or
                     // real.
                     //
                     // ⚠️ The exemption is "an organizer DEACTIVATED this row", NOT "the row is
                     // inactive". A first guard used `IsActive` and broke §204 immediately: a newly
                     // imported speaker lands PRESELECTED and inactive by design (§299 6.1), so that
                     // test would have excluded the very rows the placeholder reconcile exists for.
                     // `DeactivatedByOrganizerAt` is the deliberate act; `IsActive == false` is also
                     // just "not reviewed yet".
                     && placeholder.DeactivatedByOrganizerAt is null)
            {
                // §204 RECONCILE (a placeholder gaining its real address) and §827 RECONCILE (a real
                // address CHANGING) are the same operation: this Sessionize id already has a
                // participant, so keep that person and move their e-mail.
                //
                // ◻ §827.4 — the address is the LOGIN identity, so a change deserves its own audit
                // entry naming the old and new value. This service has no logger or audit trail
                // injected, and adding one changes its constructor and every caller, so it is NOT
                // done here: the run's own audit line moves from "1 created" to "1 updated", which
                // is a weaker signal than it should be. Recorded rather than quietly skipped.
                //
                // Email is the match key from here on. We DO NOT auto-activate: the row
                // stays in the pre-selection queue for the organizer to review/activate
                // (pairs with §203 pending-approval notifications).
                var previousEmail = placeholder.Email;

                placeholder.Email = s.Email;
                if (!string.IsNullOrWhiteSpace(fullName)) placeholder.FullName = fullName;
                existing[s.Email] = placeholder; // so a duplicate row in the same pull is a no-op
                updated++;

                // 🔒 §827.6 — REVOKE THE MAGIC LINKS MINTED FOR THE ADDRESS THEY JUST LEFT.
                //
                // Operator 2026-08-04, on being told what a rename does to sign-in links: *"can your
                // reconsile fix this magic link issue"*. It can, and this is the ONLY place that
                // knows both the old and the new address at the moment they change.
                //
                // Why it is needed: a grant is keyed on ParticipantId and redemption never compares
                // RecipientEmail to the current address, so WITHOUT this every multi-use link ever
                // mailed to the old inbox keeps signing in as this person — and the usual reason an
                // address changes is that somebody left the company that owns the old one.
                //
                // ⚠️ Scoped to the OLD address, not to the participant: a link already issued at the
                // NEW address is theirs and must keep working. And a placeholder→real reconcile
                // (§204) revokes nothing, because no real link was ever sent to a placeholder.
                if (!string.IsNullOrWhiteSpace(previousEmail)
                    && !string.Equals(previousEmail, s.Email, StringComparison.OrdinalIgnoreCase))
                {
                    var stale = await _db.MagicLinkGrants
                        .Where(g => g.ParticipantId == placeholder.Id
                                    && g.RevokedAt == null
                                    && g.RecipientEmail == previousEmail)
                        .ToListAsync(ct);

                    foreach (var g in stale) g.RevokedAt = now;

                    // The count is the only trace this leaves, so it goes in the run's own summary
                    // rather than nowhere — see the §827.4 note about this service having no audit
                    // trail of its own.
                    revokedLinks += stale.Count;
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
                    // 🔑 §880 (operator 2026-08-05: *"can we change so speakers synced from
                    // sessionize are active by default … i need the blocking"*). A speaker Sessionize
                    // has already ACCEPTED arrives ACTIVE. This replaces §299 6.1's Preselected
                    // landing for the import path only.
                    //
                    // 🔒 THE BLOCKING SURVIVES, because it was never `IsActive` that did it.
                    // `SpeakerBackstagePushService` holds on three INDEPENDENT conditions and
                    // `SpeakerProfile.Category is null` blocks on its own — so arriving Active drops
                    // two of them and keeps the one an organizer actually uses. His pending entry
                    // goes from three blockers for one decision down to "needs a speaker category".
                    //
                    // ⚠️ WHAT ACTIVE COSTS, and it is a decision he took with the corrected facts
                    // (§880.5/§880.6): Active is what lets a person SIGN IN, so an accepted speaker
                    // has hub access before any organizer has looked at them. He accepted that.
                    // He did NOT accept being mailed — welcome-email is released to Ring 3 in PROD
                    // and speakers arrive Ring 3, so without a second gate this line alone would
                    // welcome an unreviewed import within ~10 minutes. ⇒ `WelcomeEmailService` now
                    // holds a speaker's welcome while their category is null, which is the SAME
                    // predicate as the push gate: one decision point, used in both places.
                    //
                    // EXISTING participants are never touched on re-import — only this create.
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
                    var storedPath = await FetchAndStorePictureAsync(p.Id, p.FullName, s.ProfilePictureUrl!, ct);
                    if (!string.IsNullOrWhiteSpace(storedPath)) prof.PhotoSharePointPath = storedPath;
                }
                catch { /* best-effort: a picture failure must not fail the import */ }
            }

            prof.UpdatedAt = now;
            prof.LastSessionizeImportAt = now;
        }

        await _db.SaveChangesAsync(ct);

        // §204: bring email-less Sessionize speakers (those with a stable speaker id
        // but no email yet — almost always: invite not accepted) into the
        // PRE-SELECTION QUEUE as INACTIVE prospective participants keyed by Sessionize
        // id, instead of dropping them. The organizer then SEES them and can act; when
        // their real email later appears it reconciles onto the same row (above). A row
        // we already hold for that id (placeholder OR a real participant) is a no-op, so
        // this is idempotent.
        int preselectedNoEmail = 0;
        if (emailLessSpeakers is { Count: > 0 })
        {
            foreach (var s in emailLessSpeakers)
            {
                if (string.IsNullOrWhiteSpace(s.SessionizeId)) continue; // no key — can't queue/reconcile
                if (participantBySessionizeId.ContainsKey(s.SessionizeId)) continue; // already held

                var fullName = $"{s.FirstName} {s.LastName}".Trim();
                var placeholderEmail = BuildPlaceholderEmail(s.SessionizeId);
                // Guard against a stale placeholder row from a prior run (its profile
                // id-link may be missing): never create a duplicate placeholder address.
                if (existing.ContainsKey(placeholderEmail)) continue;

                var prospect = new Participant
                {
                    EventId = eventId,
                    Email = placeholderEmail,
                    FullName = string.IsNullOrWhiteSpace(fullName) ? "(name pending)" : fullName,
                    Role = ParticipantRole.Speaker,
                    // Pre-selection queue entry: cannot sign in (no real email yet),
                    // lands Inactive in the queue, tagged as a Sessionize-sync arrival.
                    IsActive = false,
                    LifecycleState = ParticipantLifecycleState.Inactive,
                    QueueSource = ParticipantQueueSource.SessionizeSync,
                    Ring = Ring.Broad,
                    CreatedAt = now,
                };
                _db.Participants.Add(prospect);
                _db.SpeakerProfiles.Add(new SpeakerProfile
                {
                    EventId = eventId,
                    Participant = prospect,
                    SessionizeSpeakerId = s.SessionizeId,
                    FirstName = string.IsNullOrWhiteSpace(s.FirstName) ? null : s.FirstName,
                    LastName = string.IsNullOrWhiteSpace(s.LastName) ? null : s.LastName,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSessionizeImportAt = now,
                });
                existing[placeholderEmail] = prospect;
                participantBySessionizeId[s.SessionizeId] = prospect;
                preselectedNoEmail++;
            }
            if (preselectedNoEmail > 0) await _db.SaveChangesAsync(ct);
        }

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
            parsed.Warnings, null, PreselectedNoEmail: preselectedNoEmail,
            RevokedMagicLinks: revokedLinks);
    }

    /// <summary>
    /// §204 — the synthetic, deterministic, non-deliverable placeholder address a
    /// queued email-less Sessionize speaker is parked under (keyed by their stable
    /// Sessionize id). Uses the RFC 2606 reserved <c>.invalid</c> TLD so it can
    /// never be mailed, and is recognizable via <see cref="IsPlaceholderEmail"/> so
    /// the queue UI shows the "no email yet" flag and the importer reconciles the
    /// row onto the real email once it appears.
    /// </summary>
    public const string PlaceholderEmailDomain = "no-email.sessionize.invalid";

    /// <summary>Build the placeholder address for a Sessionize speaker id (§204).</summary>
    public static string BuildPlaceholderEmail(string sessionizeId)
    {
        var slug = new string((sessionizeId ?? string.Empty)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray());
        if (string.IsNullOrEmpty(slug)) slug = "unknown";
        return $"sessionize-{slug}@{PlaceholderEmailDomain}";
    }

    /// <summary>
    /// True when an address is a §204 placeholder for an email-less, not-yet-invited
    /// Sessionize speaker (so the pre-selection queue can flag it and the importer
    /// can reconcile it onto the speaker's real email when that appears).
    /// </summary>
    public static bool IsPlaceholderEmail(string? email) =>
        !string.IsNullOrEmpty(email)
        && email.EndsWith("@" + PlaceholderEmailDomain, StringComparison.OrdinalIgnoreCase);

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
    /// <summary>
    /// §26c — copy a Sessionize profile picture into the speaker-photo folder, once, best-effort.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>§768.16 — this used to write somewhere nobody reads.</b> It stored
    /// <c>Speakers/speaker-{id}.jpg</c> through <c>StoreAsync</c>, which is relative to the GRAPHICS
    /// root — the drive-root <c>Graphics/</c> that §768.7 retired. So the file landed outside both
    /// document-library roots, under a fourth naming convention, while
    /// <c>PhotoSharePointPath</c> was set to it — and <see cref="SpeakerPhotoUrl.Resolve"/> prefers
    /// that path, serving <c>/speaker-photo/speaker-42.jpg</c>, a leaf the §665 proxy looks for in
    /// <c>Speakers/Photos</c> where it has never existed. A broken image, healed later by the archive
    /// job overwriting the path, which is why it never looked like a bug.
    ///
    /// <para>Now: the registry's <c>SpeakerPhotos</c> folder, drive-relative
    /// (<c>UploadToFolderAsync</c>, NOT <c>StoreAsync</c>), named by
    /// <see cref="SpeakerPhotoFileName.Build"/>. With no resolver or no resolvable folder it stores
    /// NOTHING — a skipped best-effort copy is a non-event; a file in the wrong place is not.</para>
    /// </remarks>
    private async Task<string?> FetchAndStorePictureAsync(
        int participantId, string? fullName, string url, CancellationToken ct)
    {
        if (_pictureStore is null) return null;
        if (_paths is null
            || !_paths.TryResolve(Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerPhotos, out var folder)
            || string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return null;
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var ext = contentType.Contains("png") ? ".png"
                : contentType.Contains("gif") ? ".gif"
                : contentType.Contains("webp") ? ".webp" : ".jpg";
        var fileName = SpeakerPhotoFileName.Build(participantId, ext);
        var cleanFolder = folder.Trim().Trim('/');
        await _pictureStore.UploadToFolderAsync(cleanFolder, fileName, bytes, contentType, ct);

        // §1132 — the NAME ALIAS beside it, so the folder can be searched by person.
        // 🔒 Best-effort and after the real file: this whole method is already best-effort (the
        // caller swallows), and the returned leaf must stay the ID file, which is what §665 serves.
        var aliasName = SpeakerPhotoFileName.BuildAlias(participantId, fullName, ext);
        if (aliasName is not null)
        {
            try
            {
                await _pictureStore.UploadToFolderAsync(cleanFolder, aliasName, bytes, contentType, ct);
            }
            catch { /* the id-named photo is stored; the alias is a browsing convenience */ }
        }

        // 🔒 The LEAF, not the full path: the §665 proxy resolves a leaf inside the photo folder, and
        // storing a folder-qualified path here is what made the old copy unservable.
        return fileName;
    }
}
