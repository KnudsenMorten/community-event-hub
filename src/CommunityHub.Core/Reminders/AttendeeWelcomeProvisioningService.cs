using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Auto-provisions login-capable Attendee Participants from the synced 2-day-ticket
/// holders (the <see cref="Attendee"/> rows with <see cref="TicketStatus.TwoDay"/>
/// that <see cref="AttendeeTicketSyncService"/> writes). For each holder that does
/// not yet have a Participant in the edition, it creates an ACTIVE, login-capable
/// Attendee-role Participant and returns the new participant ids so the caller can
/// send a one-click magic-link welcome to exactly the newly-created people.
///
/// <para>Idempotent: a holder who already has a Participant (any role) is skipped,
/// so re-runs create nothing and the welcome is never re-sent. Gated upstream by
/// the <c>attendee-welcome</c> feature flag (default OFF).</para>
///
/// <para>New Participants are created at <see cref="Rings.Default"/> (Broad/GA), NOT
/// Ring1 — a deliberate RELEASE-SAFETY choice: the central email ring-gate only
/// delivers to recipients at/below the email feature's released ring, so Broad
/// attendees are NOT auto-welcomed until the email feature is deliberately promoted
/// to Broad. This prevents a rogue mass-blast to all ticket holders (to pilot the
/// welcome, mark specific test attendees Ring1 via /Organizer/ResourceRings while the
/// email feature sits at Ring1). Lifecycle is set Active (not the legacy Inactive
/// booking-queue state) so the magic-link sign-in works.</para>
/// </summary>
public sealed class AttendeeWelcomeProvisioningService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<AttendeeWelcomeProvisioningService> _log;

    public AttendeeWelcomeProvisioningService(
        CommunityHubDbContext db,
        TimeProvider clock,
        ILogger<AttendeeWelcomeProvisioningService> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Create an active Attendee Participant for every 2-day-ticket holder in the
    /// edition that lacks one. Returns the ids of the participants CREATED by this
    /// call (empty when there is nothing new to do).
    /// </summary>
    public Task<IReadOnlyList<int>> ProvisionAsync(int eventId, CancellationToken ct = default)
        => ProvisionForTicketAsync(eventId, TicketStatus.TwoDay, ct);

    /// <summary>
    /// §208: create an active, login-capable Attendee Participant for every 1-day-ticket
    /// holder (<see cref="TicketStatus.Other"/> — a real ticket that is NOT the 2-day
    /// Master-Class class) in the edition that lacks one. Returns the ids CREATED by this
    /// call so the caller can send the new 1-day welcome to exactly the newly-created
    /// people (going forward — no backfill of those who already had a Participant).
    /// </summary>
    public Task<IReadOnlyList<int>> ProvisionOneDayAsync(int eventId, CancellationToken ct = default)
        => ProvisionForTicketAsync(eventId, TicketStatus.Other, ct);

    /// <summary>
    /// §242 (operator 2026-07-07) — reconcile EXISTING 1-day-only attendee logins to the
    /// <c>attendee-1day-access</c> flag, REVERSIBLY:
    /// <list type="bullet">
    /// <item><paramref name="enabled"/> = false (suspended): every Attendee-role login whose
    /// email holds NO active 2-day ticket (i.e. is 1-day-only in the mirror) is DEACTIVATED —
    /// they cannot sign in, their magic link stops resolving, the party seeder (which only
    /// seeds ACTIVE participants) skips them and the reminder builders stop nagging them.</item>
    /// <item><paramref name="enabled"/> = true (re-opened): the same logins are RESTORED
    /// (IsActive = true) — but only while their email still holds ≥1 ACTIVE ticket, so the
    /// §216 cancelled-ticket lockout is never undone by re-enabling the flag.</item>
    /// </list>
    /// 2-day holders (any email with an active TwoDay ticket) are NEVER touched — §216/§230
    /// remain the only writers for them. An Attendee-role participant with no mirror row at
    /// all (hand-added, tests) is also untouched: absence of the mirror is not evidence of a
    /// 1-day ticket. Idempotent — a re-run reconciles to the same state. Returns the number
    /// of logins toggled.
    /// </summary>
    public async Task<int> ReconcileOneDayAccessAsync(
        int eventId, bool enabled, CancellationToken ct = default)
    {
        // Per-email mirror knowledge in one query (same grouping rule the party reminder
        // builder uses): does the email hold an ACTIVE 2-day ticket / any ACTIVE ticket?
        var mirror = (await _db.Attendees
                .Where(a => a.EventId == eventId && a.Email != "")
                .Select(a => new { a.Email, a.TicketStatus, a.MirrorState })
                .ToListAsync(ct))
            .GroupBy(a => a.Email.Trim().ToLowerInvariant())
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    HasActiveTwoDay = g.Any(x => x.MirrorState == MirrorState.Active
                                                 && x.TicketStatus == TicketStatus.TwoDay),
                    HasActiveTicket = g.Any(x => x.MirrorState == MirrorState.Active),
                });

        var participants = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Attendee)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var p in participants)
        {
            var email = (p.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (email.Length == 0 || !mirror.TryGetValue(email, out var m)) continue;
            if (m.HasActiveTwoDay) continue;   // 2-day holder — §216 owns this login, never gate here

            // 1-day-only login: entitled to sign in iff the flag is ON and the email still
            // holds ≥1 ACTIVE ticket (preserving the §216 cancelled-ticket lockout).
            var desired = enabled && m.HasActiveTicket;
            if (p.IsActive != desired)
            {
                p.IsActive = desired;
                changed++;
            }
        }

        if (changed > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "AttendeeWelcomeProvisioning: 1-day access {State} — {Count} attendee login(s) {Verb} for event {EventId} (§242).",
                enabled ? "ENABLED" : "SUSPENDED", changed, enabled ? "restored" : "locked out", eventId);
        }
        return changed;
    }

    private async Task<IReadOnlyList<int>> ProvisionForTicketAsync(
        int eventId, TicketStatus ticket, CancellationToken ct)
    {
        var holders = await _db.Attendees
            .Where(a => a.EventId == eventId
                        && a.TicketStatus == ticket
                        && a.Email != "")
            .Select(a => new { a.Id, a.Email, a.FullName, a.FirstName, a.LastName, a.MirrorState, a.LastSyncedAt })
            .ToListAsync(ct);
        if (holders.Count == 0) return Array.Empty<int>();

        var emails = holders
            .Select(h => h.Email.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();

        // 🔒 §707.12 — the FULL rows, not just the addresses. Selecting only the email is what made
        // this bug invisible: an address that already existed was skipped wholesale, so nobody ever
        // asked whether that existing login could actually BE used.
        var existingRows = await _db.Participants
            .Where(p => p.EventId == eventId && emails.Contains(p.Email))
            .ToListAsync(ct);
        var existing = existingRows
            .Select(p => (p.Email ?? string.Empty).ToLowerInvariant())
            .ToHashSet();

        // 🔒 §707.12 — ACTIVATE AN EXISTING-BUT-INACTIVE LOGIN. Operator 2026-07-30, reporting it
        // live: *"i just bought a ticket for test-attendee3-2day@expertslive.dk, which already
        // existed in the db, could that be the cause"* — it was.
        //
        // A person who is already in the DB for ANY reason (an earlier import, a Sessionize
        // pre-selection queue row, a volunteer sign-up) sits at IsActive=false /
        // LifecycleState=Inactive. Buying a ticket used to leave them exactly there, because the
        // loop below skips known addresses. They were then mailed the selection invite WITH a
        // magic-link button — and `EmailMagicLinkService.ResolveAsync` refuses an inactive
        // participant ("Account is inactive"), so the button dropped them on the PIN page. The
        // grant itself was perfectly healthy; the ACCOUNT was not.
        //
        // ⚠️ This is the ticket-launch path (2026-08-11): every buyer whose address the hub already
        // knows would have hit it. `SponsorContactSyncService` has had this same re-activation since
        // §... — the attendee path simply never got it.
        //
        // 🔒 The organizer tombstone is honoured: `DeactivatedByOrganizerAt` means a human turned
        // this login off deliberately, and a ticket purchase must never silently undo that.
        // 🔒 AND ONLY WHILE THEY HOLD A LIVE TICKET. Operator 2026-07-30: *"this scenario would be
        // very common. a person buys a ticket, then he cancel the ticket, then he buys again"* — and
        // he is right that it is common, which is why this filter is not optional.
        //
        // ⚠️ `holders` above is keyed on TicketStatus and does NOT filter MirrorState, so a CANCELLED
        // ticket is still in it. Re-activating from that set would silently undo the §216
        // cancelled-ticket lockout on the very next 10-minute sync — the person would be locked out
        // by the cancellation and let straight back in minutes later. Entitlement is "≥1 ACTIVE
        // ticket for this address", the same rule ReconcileOneDayAccessAsync uses.
        //
        // Buy → cancel → buy therefore works end to end: the re-purchase creates an ACTIVE mirror
        // row, this set contains them again, and their existing login (and its magic link) comes
        // back to life without an organizer touching anything.
        var entitledEmails = holders
            .Where(h => h.MirrorState == MirrorState.Active)
            .Select(h => h.Email.Trim().ToLowerInvariant())
            .ToHashSet();

        // §707.15 — who we brought back, so their welcome stamps can be re-armed below.
        var reactivatedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 🔒 §707.22 — KEEP THE LOGIN'S NAME IN STEP WITH ZOHO. Operator 2026-07-30: *"the value in
        // zoho like company name, first name, last name is not updated … a change inside zoho of
        // names should be reflected in ceh"*.
        //
        // The MIRROR was never the problem — `AttendeeTicketSyncService.Apply` already refreshes
        // FirstName/LastName/CompanyName on every pass. The PARTICIPANT was: its FullName is set
        // once at creation and the loop below skips every address it already knows, so a name
        // corrected in Zoho never reached the login row. His example: the Attendee row read
        // "Test Purchaser / Test Company" while the Participant still read the junk name seeded by
        // an earlier test ticket.
        //
        // The ACTIVE row wins, most recently synced first — an address can hold several tickets
        // (he bought a second one), and the live one is the person's current identity.
        // 🔒 ORDERING IS NOT OPTIONAL HERE. Operator 2026-07-30: *"in zoho an attendee gets a new
        // row if a user cancels and sign-up again - one is inactive and one is active"* — so several
        // rows per address is NORMAL, not an anomaly, and one email already holds two rows in PROD.
        // Picking "the" name without an explicit order would let the login name FLIP between syncs
        // as the database returned rows in whatever order it liked.
        // Newest ticket wins: latest LastSyncedAt, then highest Id as the tie-break.
        var bestNameByEmail = holders
            .Where(h => h.MirrorState == MirrorState.Active)
            .OrderBy(h => h.LastSyncedAt).ThenBy(h => h.Id)
            .Select(h => new
            {
                Email = h.Email.Trim().ToLowerInvariant(),
                Name = !string.IsNullOrWhiteSpace(h.FullName)
                    ? h.FullName.Trim()
                    : $"{h.FirstName} {h.LastName}".Trim(),
            })
            .Where(x => x.Email.Length > 0 && x.Name.Length > 0)
            .GroupBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Name, StringComparer.OrdinalIgnoreCase);

        var renamed = 0;
        foreach (var p in existingRows)
        {
            var key = (p.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (!bestNameByEmail.TryGetValue(key, out var zohoName)) continue;
            if (string.Equals(p.FullName, zohoName, StringComparison.Ordinal)) continue;

            p.FullName = zohoName;
            renamed++;
        }
        if (renamed > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "AttendeeWelcomeProvisioning: refreshed {Count} login name(s) from Zoho for event "
                + "{EventId} (§707.22).", renamed, eventId);
        }

        var reactivated = 0;
        foreach (var p in existingRows)
        {
            if (p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active) continue;
            if (!entitledEmails.Contains((p.Email ?? string.Empty).Trim().ToLowerInvariant())) continue;

            // 🔒 §707.14 POLICY — A TICKET PURCHASE CLEARS AN ORGANIZER DEACTIVATION.
            // Operator 2026-07-30, deciding it: *"i agree it should flip it on"*.
            //
            // The first cut refused this, on the reasoning that a human decision should not be
            // undone by a sync. He chose the opposite, and for a good reason: someone who was
            // switched off and then BUYS A TICKET has re-entitled themselves, and the alternative
            // is a person holding a paid ticket who silently cannot sign in — with a dead magic
            // link as the only symptom (exactly how this whole thread started). The tombstone is
            // CLEARED, not bypassed, so the row stops reading as "an organizer turned this off"
            // when a purchase has since overruled it.
            p.IsActive = true;
            p.LifecycleState = ParticipantLifecycleState.Active;
            p.DeactivatedByOrganizerAt = null;

            // §707.15 — a reactivation RE-ONBOARDS: clear the "already welcomed" stamps so the
            // reconcile jobs send the welcome again on their next pass. Without this the person
            // comes back live but silent, which is the same defect in a different place.
            p.WelcomeWithLoginSentAt = null;
            reactivatedEmails.Add((p.Email ?? string.Empty).Trim().ToLowerInvariant());
            reactivated++;
        }

        // §707.15 — the ledger half of the welcome re-arm (WelcomeEmailService dedups on it), plus
        // the 2-day selection invite + its once-ever chaser, for everyone reactivated above.
        if (reactivatedEmails.Count > 0)
        {
            var welcomeLedger = await _db.SentReminders
                .Where(s => s.EventId == eventId && s.ReminderType == "welcome"
                            && reactivatedEmails.Contains(s.RecipientEmail.ToLower()))
                .ToListAsync(ct);
            if (welcomeLedger.Count > 0) _db.SentReminders.RemoveRange(welcomeLedger);

            // Re-arm the selection invite only where no confirmed seat is held (a held seat means
            // the reassignment-validation mail applies instead — the §253 G16 rule).
            var seated = (await _db.MasterClassSignups
                    .Where(s => s.EventId == eventId && s.Status == MasterClassSignupStatus.Confirmed)
                    .Select(s => s.Attendee.Email)
                    .ToListAsync(ct))
                .Select(e => (e ?? string.Empty).ToLowerInvariant())
                .ToHashSet();

            // 🔒 §707.24 — ONE ROW PER PERSON, NOT ONE PER TICKET. Clearing the stamp on every
            // active row made the invite sweep send one mail per row: on 2026-07-30 the operator
            // got two identical selection invites a second apart because his address held two
            // active tickets. An address can legitimately hold several, so re-arm only the NEWEST
            // (same winning-row rule as §707.22a) — the person gets one welcome.
            var toReinvite = (await _db.Attendees
                    .Where(a => a.EventId == eventId
                                && a.MirrorState == MirrorState.Active
                                && a.MasterClassInviteSentAt != null
                                && reactivatedEmails.Contains(a.Email.ToLower()))
                    .ToListAsync(ct))
                .Where(a => !seated.Contains(a.Email.ToLowerInvariant()))
                .GroupBy(a => a.Email.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(a => a.LastSyncedAt).ThenByDescending(a => a.Id).First());

            foreach (var a in toReinvite)
            {
                a.MasterClassInviteSentAt = null;
            }
        }
        if (reactivated > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "AttendeeWelcomeProvisioning: activated {Count} EXISTING {Ticket}-ticket login(s) for "
                + "event {EventId} — their magic links could not resolve while inactive (§707.12).",
                reactivated, ticket, eventId);
        }

        var now = _clock.GetUtcNow();
        var created = new List<Participant>();
        var seen = new HashSet<string>();

        foreach (var h in holders)
        {
            var email = h.Email.Trim().ToLowerInvariant();
            if (email.Length == 0 || existing.Contains(email) || !seen.Add(email)) continue;

            var name = !string.IsNullOrWhiteSpace(h.FullName)
                ? h.FullName.Trim()
                : $"{h.FirstName} {h.LastName}".Trim();

            var p = new Participant
            {
                EventId = eventId,
                Email = email,
                FullName = string.IsNullOrWhiteSpace(name) ? email : name,
                Role = ParticipantRole.Attendee,
                // Login-capable: active + lifecycle Active (the legacy booking path
                // deliberately uses Inactive to BLOCK sign-in — we want the opposite).
                IsActive = true,
                LifecycleState = ParticipantLifecycleState.Active,
                QueueSource = ParticipantQueueSource.Manual,
                // RELEASE SAFETY: provision real attendees at Broad/GA, NOT Ring1.
                // The email ring-gate only delivers to recipients at/below the email
                // feature's released ring, so Broad attendees are NOT auto-welcomed
                // until the email feature is deliberately promoted to Broad — this
                // prevents a rogue mass-blast to all ~1000 ticket holders. To test
                // the welcome with a small cohort, mark specific test attendees Ring1
                // (via /Organizer/ResourceRings) while the email feature is at Ring1.
                Ring = Rings.Default,
                CreatedAt = now,
            };
            _db.Participants.Add(p);
            created.Add(p);
        }

        if (created.Count == 0) return Array.Empty<int>();

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "AttendeeWelcomeProvisioning: created {Count} active {Ticket}-ticket attendee participants for event {EventId}.",
            created.Count, ticket, eventId);

        return created.Select(p => p.Id).ToList();
    }
}
