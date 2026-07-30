using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The ONE deactivation cascade (REQUIREMENTS §253, gap G1). Before this service,
/// every organizer-side deactivation (grid toggle, soft-delete, bulk op, data-grid
/// row save) was a pure <c>IsActive=false</c> flag flip that left the person's
/// logistics rows live — so hotel room blocks, party headcounts, lunch/swag orders,
/// dinner plus-ones and volunteer shift coverage all kept counting people who had
/// left (the operator's "money is wrong" fear, confirmed by the §253 scenario
/// matrix). The only real cascade was the §216 attendee-ticket path
/// (<c>AttendeeTicketSyncService</c>); this service brings the same semantics to
/// EVERY organizer deactivation entry point.
///
/// What one deactivation does (all idempotent, one SaveChanges):
///   - flags: <see cref="Participant.IsActive"/> = false AND
///     <see cref="Participant.LifecycleState"/> = Inactive (kills the D1/D4
///     divergence where the toggle set only the flag), plus the
///     <see cref="Participant.DeactivatedByOrganizerAt"/> tombstone so the sponsor
///     contact sync never silently re-activates an organizer decision (G8);
///   - party: the person's RSVP is CANCELLED with the §209 semantics
///     (<c>Attending=false, HeadCount=null</c>, row kept for audit/re-ask). A
///     sponsor contact's company GROUP reservation is kept only when another
///     ACTIVE contact of the company exists (re-pointed to them so the active-only
///     counts keep it); the last contact leaving cancels it;
///   - hotel: <see cref="HotelBooking.NeedsRoom"/> = false and the hotel placement
///     (<see cref="Participant.HotelId"/>) is released, so the room block frees up;
///   - tasks: the person's open <see cref="ParticipantTask"/> rows are CLOSED
///     (State=Done + CompletedAt) — the due-date reminder track only mails open
///     tasks, so closing them is also what stops the mail;
///   - shifts: their <see cref="VolunteerTaskAssignment"/> rows are removed (the
///     established drop-out semantics of <c>SeedDropoutBackfillAsync</c>), so
///     coverage honestly shows the vacated shifts as uncovered;
///   - audit: one <see cref="AuditEntry"/> records what the cascade touched.
///
/// RE-ACTIVATION deliberately restores almost NOTHING (§253 G1): the person re-RSVPs /
/// the organizer re-books. It clears the flags + tombstone, safely and idempotently —
/// and, since §332, re-opens the tasks THIS cascade abandoned (identified by
/// <see cref="TaskClosedReason.AbandonedOnDeactivation"/>, so genuinely completed work
/// stays Done). Party RSVP, room claim and shift assignments still come back empty.
/// </summary>
public sealed class ParticipantDeactivationService
{
    /// <summary>Stable audit action codes for the cascade (§24 audit trail).</summary>
    public const string ActionDeactivate = "participant.deactivate-cascade";
    public const string ActionReactivate = "participant.reactivate";
    public const string ActionWithdrawCompany = "sponsor.company-withdraw";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IAuditTrail _audit;
    private readonly VolunteerAllocationService? _allocation;

    /// <summary><paramref name="allocation"/> is OPTIONAL (§253 G7): when present
    /// (the web host registers it), deactivating a volunteer also seeds backfill
    /// drafts for the vacated shifts into the acting organizer's allocation queue
    /// (review-then-commit — nothing is auto-assigned). Hosts without the
    /// allocation engine simply skip the seeding.</summary>
    /// <summary>§470 — OPTIONAL ops notifier. When present, deactivating a speaker who still exists
    /// in Zoho Backstage raises a mail telling the organizers to remove them BY HAND: the Backstage
    /// speaker API is CREATE-ONLY (§26c), so CEH cannot delete them and the person would otherwise
    /// stay on the public agenda with nothing flagging it.</summary>
    private readonly Email.ZohoChangeNotifier? _zohoNotifier;

    public ParticipantDeactivationService(
        CommunityHubDbContext db, TimeProvider clock, IAuditTrail audit,
        VolunteerAllocationService? allocation = null,
        Email.ZohoChangeNotifier? zohoNotifier = null)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
        _allocation = allocation;
        _zohoNotifier = zohoNotifier;
    }

    /// <summary>What one deactivation cascade actually touched (for messages/tests).</summary>
    public sealed record CascadeResult(
        bool Found,
        bool AlreadyInactive,
        int PartyRsvpsCancelled,
        bool GroupReservationKeptForCompany,
        int HotelBookingsReleased,
        bool HotelPlacementCleared,
        int TasksClosed,
        int ShiftAssignmentsVacated,
        int BackfillDraftsSeeded = 0);

    private static readonly CascadeResult NotFound =
        new(false, false, 0, false, 0, false, 0, 0);

    /// <summary>
    /// Deactivate one participant WITH the full cascade. Edition-scoped and
    /// idempotent: an already-inactive row still gets the cascade re-applied
    /// (a re-run converges; nothing double-applies) but is reported as
    /// <see cref="CascadeResult.AlreadyInactive"/>.
    /// </summary>
    public async Task<CascadeResult> DeactivateAsync(
        int eventId, int participantId, string reason,
        string? actorEmail = null, CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return NotFound;

        var now = _clock.GetUtcNow();
        var alreadyInactive = !p.IsActive
                              && p.LifecycleState == ParticipantLifecycleState.Inactive;

        p.IsActive = false;
        p.LifecycleState = ParticipantLifecycleState.Inactive;
        // G8 tombstone: an ORGANIZER deactivated this person — external syncs
        // (sponsor contact sync) must never silently re-activate them. Manual
        // re-activation clears it. Never overwrite an earlier stamp on a re-run.
        p.DeactivatedByOrganizerAt ??= now;

        var (rsvpsCancelled, groupKept) = await CancelPartyRsvpsAsync(p, now, ct);
        var (bookingsReleased, placementCleared) = ReleaseHotel(p, await LoadBookingsAsync(p, ct), now);
        var tasksClosed = await CloseOpenTasksAsync(p, now, ct);
        var (shiftsVacated, vacatedTaskIds, vacatedRecord) = await VacateShiftAssignmentsAsync(p, ct);

        await _db.SaveChangesAsync(ct);

        // §253 G7 (completes the G1 spec): the vacated shifts get backfill DRAFTS
        // seeded into the acting organizer's allocation queue — proposals only,
        // reviewed + committed on /Organizer/BucketAllocation, never auto-assigned.
        // Skipped when the actor isn't a resolvable organizer (system callers) or
        // this host has no allocation engine.
        var backfillSeeded = 0;
        if (shiftsVacated > 0 && _allocation is not null && !string.IsNullOrWhiteSpace(actorEmail))
        {
            // Case-insensitive on purpose: claims may carry the casing the row had
            // at sign-in, while sync-side normalization can lower-case it later.
            var actorNorm = actorEmail.Trim().ToLowerInvariant();
            var organizerId = await _db.Participants
                .Where(x => x.EventId == eventId
                            && x.Role == ParticipantRole.Organizer
                            && x.IsActive
                            && x.Email.ToLower() == actorNorm)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (organizerId is int oid)
            {
                backfillSeeded = await _allocation.SeedBackfillForTasksAsync(
                    eventId, oid, vacatedTaskIds, p.Id, ct);
            }
        }

        await _audit.RecordAsync(new AuditEntry
        {
            EventId = eventId,
            OccurredUtc = now,
            Category = AuditCategory.Admin,
            Action = ActionDeactivate,
            ActorEmail = actorEmail ?? "system",
            TargetType = "Participant",
            TargetId = p.Id.ToString(),
            Summary = $"Deactivated {p.FullName} ({reason}): "
                      + $"{rsvpsCancelled} party RSVP(s) cancelled"
                      + (groupKept ? " (company group reservation kept — other active contacts)" : string.Empty)
                      + $", {bookingsReleased} hotel booking(s) released, "
                      + $"{tasksClosed} open task(s) closed, "
                      + $"{shiftsVacated} shift assignment(s) vacated"
                      + (backfillSeeded > 0
                          ? $", {backfillSeeded} backfill draft(s) seeded for review."
                          : "."),
            // §331: WHICH shifts were vacated — the rows themselves are gone and
            // reactivation does not restore them, so this is the only surviving record.
            Detail = vacatedRecord,
        }, ct);

        // §470 — a deactivated SPEAKER who exists in Zoho Backstage must be removed there BY HAND:
        // the Backstage speaker API is CREATE-ONLY (§26c), so CEH physically cannot delete them,
        // and until someone does they stay on the PUBLIC agenda. Nothing used to say so.
        //
        // Fires on the DEACTIVATION itself, not a nightly sweep — a sweep would leave the public
        // agenda wrong for up to a day. Guarded on `!alreadyInactive` so re-running the cascade on
        // an already-inactive person cannot re-mail the organizers.
        if (!alreadyInactive && _zohoNotifier is not null && p.Role == ParticipantRole.Speaker)
        {
            var backstageId = await _db.SpeakerProfiles
                .Where(sp => sp.EventId == eventId && sp.ParticipantId == p.Id)
                .Select(sp => sp.BackstageSpeakerId)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(backstageId))
            {
                // Never throws (the notifier swallows), so a mail failure can never roll back or
                // block a deactivation that has already been committed above.
                await _zohoNotifier.NotifyAsync(
                    "Speakers — MANUAL removal needed in Zoho Backstage",
                    new[]
                    {
                        $"{p.FullName} <{p.Email}> was deactivated in the hub but still exists in " +
                        $"Zoho Backstage (speaker id {backstageId}). The Backstage speaker API is " +
                        "create-only, so CEH cannot delete them — please remove the speaker MANUALLY " +
                        "in Zoho Backstage, or they will stay on the public agenda.",
                    },
                    ct);
            }
        }

        return new CascadeResult(
            true, alreadyInactive, rsvpsCancelled, groupKept,
            bookingsReleased, placementCleared, tasksClosed, shiftsVacated,
            backfillSeeded);
    }

    /// <summary>What a re-activation silently brought BACK into the live counts
    /// (§253 R1-F5 "silent resurrection"): the cascade never deletes dinner /
    /// lunch / swag rows — the active-only filters merely hid them — so flipping
    /// the person back Active re-counts them instantly. The caller surfaces these
    /// so the organizer knows to re-verify the vendor numbers. (Party RSVP, room
    /// claim, open tasks and shifts were explicitly neutralised at deactivation
    /// and stay that way — re-RSVP / re-book.)</summary>
    public sealed record ReactivateResult(
        bool Found,
        int DinnerSignupsBackInCounts,
        int LunchSignupsBackInCounts,
        int SwagPreferencesBackInCounts,
        int MasterClassSeatsStillHeld,
        int TasksReopened = 0)
    {
        /// <summary>True when anything silently rejoined a headcount/export.</summary>
        public bool AnythingResurrected =>
            DinnerSignupsBackInCounts + LunchSignupsBackInCounts
            + SwagPreferencesBackInCounts + MasterClassSeatsStillHeld > 0;
    }

    /// <summary>
    /// Re-activate one participant. Restores NOTHING beyond the flags (the person
    /// re-RSVPs / the organizer re-books — §253 G1) and clears the
    /// <see cref="Participant.DeactivatedByOrganizerAt"/> tombstone so the sponsor
    /// sync may manage the contact again. Idempotent; safe on a never-deactivated
    /// row. The result reports which logistics rows silently rejoined the live
    /// counts so the organizer gets a signal instead of a surprise on the next
    /// vendor export.
    /// </summary>
    public async Task<ReactivateResult> ReactivateAsync(
        int eventId, int participantId, string? actorEmail = null, CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return new ReactivateResult(false, 0, 0, 0, 0);

        var changed = !p.IsActive
                      || p.LifecycleState != ParticipantLifecycleState.Active
                      || p.DeactivatedByOrganizerAt is not null;
        p.IsActive = true;
        p.LifecycleState = ParticipantLifecycleState.Active;
        p.DeactivatedByOrganizerAt = null;

        // The rows the active-only filters were hiding — live again the moment
        // the flag flips (dinner attending rows count plus-ones; swag rows feed
        // the vendor sheets; a still-Confirmed MC seat was never released by the
        // organizer cascade and keeps holding capacity).
        var dinner = await _db.DinnerSignups.CountAsync(
            d => d.EventId == eventId && d.ParticipantId == p.Id && d.Attending, ct);
        var lunch = await _db.LunchSignups.CountAsync(
            l => l.EventId == eventId && l.ParticipantId == p.Id, ct);
        var swag = await _db.SwagPreferences.CountAsync(
            s => s.EventId == eventId && s.ParticipantId == p.Id, ct);
        // MC signups key on the Attendee MIRROR (ticket id), so resolve via the
        // person's email (Attendee.Email is stored lower-cased + trimmed).
        var norm = (p.Email ?? string.Empty).Trim().ToLowerInvariant();
        var mcSeats = norm.Length == 0 ? 0 : await _db.MasterClassSignups.CountAsync(
            m => m.EventId == eventId
                 && m.Status == MasterClassSignupStatus.Confirmed
                 && m.Attendee.Email == norm, ct);

        // §332: RE-OPEN the tasks the cascade closed on their behalf — and ONLY those.
        // §253 G1 ("re-activation restores nothing") was written when a force-closed task was
        // indistinguishable from a completed one, so restoring anything would have re-opened
        // genuinely finished work. Now the closure is labelled, so the honest thing is possible:
        // a returning person gets their untouched work back (and is chased for it again),
        // while everything they actually completed stays Done. Party RSVP / room / shifts still
        // restore nothing — those rows are gone or cancelled, not merely labelled.
        var reopened = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == p.Id
                        && t.ClosedReason == TaskClosedReason.AbandonedOnDeactivation)
            .ToListAsync(ct);
        foreach (var t in reopened)
        {
            t.State = TaskState.Open;
            t.CompletedAt = null;
            t.ClosedReason = null;
        }
        changed |= reopened.Count > 0;

        // 🔒 §707.15 — RE-ACTIVATION RE-ONBOARDS. Operator 2026-07-30: *"so both an organizer change
        // and order, must send the the welcome again"*, *"inactive to active must send email again"*
        // — treat a returning person as a new user.
        //
        // An ORDER-driven return already did this (§253 G16 `ReEngageReturningAttendeesAsync` on the
        // ticket sync). An ORGANIZER re-activation did NOT: it is not a cancellation, so the sync's
        // re-engage never fires, and the "already sent" stamps stayed — so the person came back with
        // NO welcome and no magic link. That asymmetry is exactly what he hit on 2026-07-30, where
        // participant 82 had to be re-armed by hand.
        //
        // Clearing the stamps is all that is needed: the reconcile jobs own the actual sending, so
        // the mail goes out on their next pass through the normal, ring-gated path — never a
        // hand-rolled send from here.
        //
        // 🔒 Only when something ACTUALLY changed (`changed`), so a re-run on an already-active
        // person does not re-welcome them every time an organizer opens the page.
        if (changed)
        {
            // (1) EVERY role: re-arm the generic welcome. Both halves matter — the reconcile checks
            //     the participant stamp, and WelcomeEmailService dedups on the reminder ledger.
            p.WelcomeWithLoginSentAt = null;
            var welcomeLedger = await _db.SentReminders
                .Where(s => s.EventId == eventId
                            && s.ReminderType == "welcome"
                            && s.RecipientEmail.ToLower() == norm)
                .ToListAsync(ct);
            if (welcomeLedger.Count > 0) _db.SentReminders.RemoveRange(welcomeLedger);

            // (2) A 2-day ATTENDEE's real welcome is the Master Class selection invite (§241), so
            //     re-arm that too — but only while they hold a LIVE ticket, and only when no
            //     confirmed seat exists (a held seat means the validation mail applies instead,
            //     the same rule §253 G16 uses).
            if (norm.Length > 0)
            {
                var hasActiveTicket = await _db.Attendees.AnyAsync(a =>
                    a.EventId == eventId && a.MirrorState == MirrorState.Active
                    && a.Email.ToLower() == norm, ct);
                if (hasActiveTicket && mcSeats == 0)
                {
                    // 🔒 §707.24 — RE-ARM ONE ROW, NOT ALL OF THEM. Clearing the stamp on every
                    // active row meant the invite sweep sent ONE MAIL PER ROW: on 2026-07-30 the
                    // operator received two identical selection invites a second apart (09:00:03
                    // and 09:00:04) because his address held two active tickets. An address can
                    // legitimately hold several (he buys a second, or a seeded row lingers), so
                    // "all rows" is the wrong unit — the person gets one welcome, not one per
                    // ticket. Newest ticket wins, matching §707.22a's winning-row rule.
                    var newest = await _db.Attendees
                        .Where(a => a.EventId == eventId
                                    && a.MirrorState == MirrorState.Active
                                    && a.MasterClassInviteSentAt != null
                                    && a.Email.ToLower() == norm)
                        .OrderByDescending(a => a.LastSyncedAt).ThenByDescending(a => a.Id)
                        .FirstOrDefaultAsync(ct);
                    if (newest is not null) newest.MasterClassInviteSentAt = null;

                    // The once-ever chaser ledger, so it can nag this new engagement again.
                    var chaser = await _db.SentReminders
                        .Where(s => s.EventId == eventId
                                    && s.ReminderType == "pending-master-class-selection"
                                    && s.OccasionKey == "pendingmc:" + norm)
                        .ToListAsync(ct);
                    if (chaser.Count > 0) _db.SentReminders.RemoveRange(chaser);
                }
            }
        }

        var result = new ReactivateResult(true, dinner, lunch, swag, mcSeats, reopened.Count);
        if (changed)
        {
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = eventId,
                OccurredUtc = _clock.GetUtcNow(),
                Category = AuditCategory.Admin,
                Action = ActionReactivate,
                ActorEmail = actorEmail ?? "system",
                TargetType = "Participant",
                TargetId = p.Id.ToString(),
                Summary = $"Re-activated {p.FullName}. Party/room/shifts are NOT "
                          + "auto-restored (they re-RSVP / the organizer re-books). "
                          + (reopened.Count > 0
                              ? $"{reopened.Count} task(s) abandoned at deactivation were re-opened. "
                              : string.Empty)
                          + (result.AnythingResurrected
                              ? $"Back in the live counts: {dinner} dinner signup(s), "
                                + $"{lunch} lunch signup(s), {swag} swag preference(s); "
                                + $"{mcSeats} Master-Class seat(s) still held."
                              : "No dormant logistics rows rejoined the counts."),
            }, ct);
        }
        return result;
    }

    /// <summary>The result of a whole-company sponsor withdrawal (§253 G8b).</summary>
    public sealed record CompanyWithdrawalResult(
        bool Found, int ContactsDeactivated, int GroupRsvpsCancelled);

    /// <summary>
    /// G8b: WITHDRAW a whole sponsor company. Marks the company's
    /// <see cref="SponsorInfo.Status"/> = <see cref="SponsorStatus.Withdrawn"/>
    /// (excluded from the public sponsors page + organizer sponsor counts),
    /// runs the full deactivation cascade over every ACTIVE contact of the company
    /// (which also cancels the group party reservation when the last contact goes),
    /// then belt-and-braces cancels any remaining attending company RSVP rows
    /// (e.g. held by an already-inactive contact the per-contact cascade never
    /// visits). Zoho/ERP records are NEVER touched (§56 — no deletes). Idempotent.
    /// </summary>
    public async Task<CompanyWithdrawalResult> WithdrawSponsorCompanyAsync(
        int eventId, string sponsorCompanyId, string? actorEmail = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId))
            return new CompanyWithdrawalResult(false, 0, 0);

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct);

        var contactIds = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId == sponsorCompanyId)
            .Select(p => new { p.Id, p.IsActive })
            .ToListAsync(ct);
        // §529 — a company can be listed on the Sponsors page purely because it still owns TASKS:
        // that page builds its company list from contacts AND tasks. A leftover test company with
        // no contacts and no SponsorInfo row was therefore shown, offered a Withdraw button, and
        // then rejected as "could not be found in this edition" every time — the operator tried
        // three times (2026-07-28, "Company test-2linkit", "no contacts in this edition", 1 task).
        // If the page can list it, Withdraw must be able to act on it.
        var companyTasks = await _db.Tasks
            .Where(t => t.EventId == eventId && t.SponsorCompanyId == sponsorCompanyId)
            .ToListAsync(ct);

        if (info is null && contactIds.Count == 0 && companyTasks.Count == 0)
            return new CompanyWithdrawalResult(false, 0, 0);

        var now = _clock.GetUtcNow();
        if (info is not null && info.Status != SponsorStatus.Withdrawn)
        {
            info.Status = SponsorStatus.Withdrawn;
            info.WithdrawnAt = now;
            info.UpdatedAt = now;
            info.LastUpdatedByEmail = actorEmail;
            await _db.SaveChangesAsync(ct);
        }

        var deactivated = 0;
        foreach (var c in contactIds.Where(c => c.IsActive))
        {
            var r = await DeactivateAsync(
                eventId, c.Id, $"company {sponsorCompanyId} withdrew", actorEmail, ct);
            if (r.Found && !r.AlreadyInactive) deactivated++;
        }

        // Belt-and-braces: neutralise ANY remaining attending RSVP row held by one
        // of the company's contacts (the group HeadCount must stop counting toward
        // the venue food order, whoever registered it).
        var strayRsvps = await _db.PartyRsvps
            .Where(r => r.EventId == eventId && r.Attending && r.ParticipantId != null)
            .Where(r => _db.Participants.Any(p =>
                p.Id == r.ParticipantId && p.EventId == eventId
                && p.SponsorCompanyId == sponsorCompanyId))
            .ToListAsync(ct);
        foreach (var stray in strayRsvps)
        {
            stray.Attending = false;
            stray.HeadCount = null;
            stray.UpdatedAt = now;
        }
        if (strayRsvps.Count > 0) await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(new AuditEntry
        {
            EventId = eventId,
            OccurredUtc = now,
            Category = AuditCategory.Admin,
            Action = ActionWithdrawCompany,
            ActorEmail = actorEmail ?? "system",
            TargetType = "SponsorCompany",
            TargetId = sponsorCompanyId,
            Summary = $"Sponsor company {sponsorCompanyId} withdrawn: "
                      + $"{deactivated} contact(s) deactivated, "
                      + $"{strayRsvps.Count} lingering party RSVP row(s) cancelled. "
                      + "Zoho/ERP records untouched (§56).",
        }, ct);

        return new CompanyWithdrawalResult(true, deactivated, strayRsvps.Count);
    }

    // ----- cascade steps (none of these save; DeactivateAsync commits once) ----

    /// <summary>
    /// Cancel the person's party RSVP rows (§209 semantics: keep the row, flip to
    /// not-attending, drop the head count). Matched by ParticipantId AND — like the
    /// §216 ticket path — case-insensitively by email, because an RSVP submitted on
    /// the public form keeps its casing and may pre-date the participant link.
    /// For a sponsor contact whose row is the company GROUP reservation (§228), the
    /// reservation is KEPT and re-pointed to another ACTIVE contact when one
    /// exists; only the last contact leaving cancels the group answer.
    /// </summary>
    private async Task<(int Cancelled, bool GroupKept)> CancelPartyRsvpsAsync(
        Participant p, DateTimeOffset now, CancellationToken ct)
    {
        var norm = (p.Email ?? string.Empty).Trim().ToLowerInvariant();
        var rows = await _db.PartyRsvps
            .Where(r => r.EventId == p.EventId
                        && (r.ParticipantId == p.Id
                            || (norm != "" && r.Email.ToLower() == norm)))
            .ToListAsync(ct);

        // §228: the successor contact the group reservation is re-pointed to, when
        // the leaver is a sponsor contact and the company still has active people.
        Participant? successor = null;
        if (!string.IsNullOrWhiteSpace(p.SponsorCompanyId))
        {
            successor = await _db.Participants
                .Where(x => x.EventId == p.EventId
                            && x.Id != p.Id
                            && x.SponsorCompanyId == p.SponsorCompanyId
                            && x.IsActive)
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(ct);
        }

        var cancelled = 0;
        var groupKept = false;
        foreach (var rsvp in rows)
        {
            if (!rsvp.Attending && rsvp.HeadCount is null) continue;   // already neutral
            // §506 — the successor may ALREADY have their own RSVP row. PartyRsvps is uniquely
            // indexed on (EventId, Email), so re-pointing this row onto their address is a
            // duplicate-key violation and the whole save fails with a 500. It surfaced on a bulk
            // deactivation of seven contacts from one company: the first hand-off worked, and the
            // next one tried to move a second row onto the same successor.
            //
            // When the successor already has a row, the company's group answer is ALREADY carried
            // under an active contact — so there is nothing to preserve here and this row is simply
            // cancelled like any other. Checked per row, because an earlier iteration of this very
            // loop may have created that state.
            var successorHasOwnRsvp = successor is not null
                && rows.Any(r => !ReferenceEquals(r, rsvp)
                                 && r.ParticipantId == successor.Id)
                || (successor is not null && await _db.PartyRsvps.AnyAsync(
                        r => r.EventId == p.EventId
                             && r.Email.ToLower() == successor.Email.ToLower(), ct));

            if (successor is not null && rsvp.Attending && !successorHasOwnRsvp)
            {
                // Keep the company's group answer alive under an ACTIVE contact so
                // the active-only headcount keeps counting it (§253 G3).
                rsvp.ParticipantId = successor.Id;
                rsvp.Email = successor.Email;
                rsvp.Name = successor.FullName;
                rsvp.UpdatedAt = now;
                groupKept = true;
                continue;
            }
            rsvp.Attending = false;
            rsvp.HeadCount = null;
            rsvp.UpdatedAt = now;
            cancelled++;
        }
        return (cancelled, groupKept);
    }

    private Task<List<HotelBooking>> LoadBookingsAsync(Participant p, CancellationToken ct) =>
        _db.HotelBookings
            .Where(h => h.EventId == p.EventId && h.ParticipantId == p.Id)
            .ToListAsync(ct);

    /// <summary>Release the room-block claim: NeedsRoom off + un-place from the hotel.
    /// The booking row (dates, notes) is kept for audit — the §253 G2 active-only
    /// filters keep it out of every count/roster/export anyway.</summary>
    private static (int Released, bool PlacementCleared) ReleaseHotel(
        Participant p, List<HotelBooking> bookings, DateTimeOffset now)
    {
        var released = 0;
        foreach (var b in bookings.Where(b => b.NeedsRoom))
        {
            b.NeedsRoom = false;
            b.UpdatedAt = now;
            released++;
        }
        var hadPlacement = p.HotelId is not null;
        p.HotelId = null;
        return (released, hadPlacement);
    }

    /// <summary>Close (never delete — CEH mirror model) the person's open tasks.
    /// The due-date reminder track (§81) only mails open tasks, so closing them is
    /// also what silences their reminders without touching any builder.
    /// §332: the closure is now LABELLED <see cref="TaskClosedReason.AbandonedOnDeactivation"/>
    /// — the state stays Done so nothing re-opens or starts mailing a person who left, but
    /// the completion ratios can stop counting work nobody did, and re-activation can put
    /// back exactly these rows.</summary>
    private async Task<int> CloseOpenTasksAsync(Participant p, DateTimeOffset now, CancellationToken ct)
    {
        var open = await _db.Tasks
            .Where(t => t.EventId == p.EventId
                        && t.AssignedParticipantId == p.Id
                        && t.State != TaskState.Done)
            .ToListAsync(ct);
        foreach (var t in open)
        {
            t.State = TaskState.Done;
            t.CompletedAt = now;
            t.ClosedReason = TaskClosedReason.AbandonedOnDeactivation;
        }
        return open.Count;
    }

    /// <summary>Vacate the person's shift assignments (they left) — the same
    /// remove-the-rows semantics <c>VolunteerAllocationService.SeedDropoutBackfillAsync</c>
    /// established for a drop-out. Coverage then honestly reports the gap; the
    /// returned task ids feed the G7 backfill-draft seeding.</summary>
    private async Task<(int Count, List<int> TaskIds, string? Record)> VacateShiftAssignmentsAsync(
        Participant p, CancellationToken ct)
    {
        // §331 (was §326bg item 5) — CAPTURE WHAT WE ARE ABOUT TO DESTROY.
        //
        // These rows are hard-deleted on purpose (coverage must honestly report the gap, and
        // the §253 G7 backfill drafts are seeded from it), and reactivation restores nothing.
        // Until now that left NO record anywhere of which shifts the person held — so when a
        // drop-out returned, or somebody simply asked "what was Alex signed up for?", the
        // answer was gone. Read the detail BEFORE deleting and write it into the audit
        // entry's Detail field, which was previously left null.
        //
        // Recording rather than soft-deleting is deliberate: a soft-delete flag would have to
        // be honoured by every one of the ~45 assignment queries, and a single missed one
        // would count a vacated shift as COVERED — somebody believing a shift is staffed when
        // nobody is coming. The audit trail carries the memory; the live tables stay
        // unambiguous.
        var detail = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == p.EventId && a.ParticipantId == p.Id)
            .Select(a => new
            {
                a.TaskId,
                a.DecisionStatus,
                a.Task.Title,
                a.Task.DueDate,
                a.Task.Shift,
                Bucket = a.Task.Subcategory.Category.Name,
            })
            .ToListAsync(ct);

        string? record = detail.Count == 0 ? null
            : "Vacated shift assignments (hard-deleted; NOT restored on reactivation): "
              + string.Join(" | ", detail.Select(a =>
                  $"#{a.TaskId} {a.Title}"
                  + (a.DueDate is { } d ? $" on {d:yyyy-MM-dd}" : string.Empty)
                  + (string.IsNullOrWhiteSpace(a.Shift) ? string.Empty : $" {a.Shift}")
                  + (string.IsNullOrWhiteSpace(a.Bucket) ? string.Empty : $" [{a.Bucket}]")
                  + (a.DecisionStatus != ShiftDecisionStatus.None ? $" ({a.DecisionStatus})" : string.Empty)));

        var theirs = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == p.EventId && a.ParticipantId == p.Id)
            .ToListAsync(ct);
        _db.VolunteerTaskAssignments.RemoveRange(theirs);

        return (theirs.Count, theirs.Select(a => a.TaskId).Distinct().ToList(), record);
    }
}
