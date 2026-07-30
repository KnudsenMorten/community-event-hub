using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>What a reset actually cleared — surfaced to the organizer and the audit trail.</summary>
/// <param name="Ok">False when the attendee could not be resolved for the edition.</param>
/// <param name="WelcomeReset">The 2-day selection invite / welcome may send again.</param>
/// <param name="MasterClassReset">Seat + waitlist place removed; the selection task re-opened.</param>
/// <param name="PartyReset">RSVP removed (so it counts as unanswered); the party task re-opened.</param>
/// <param name="SignupsRemoved">Master Class signup rows deleted.</param>
/// <param name="RsvpsRemoved">Party RSVP rows deleted.</param>
/// <param name="LedgerRowsRemoved">`SentReminder` rows deleted (mail may fire again).</param>
/// <param name="TasksReopened">Onboarding tasks moved back to Open.</param>
/// <param name="Detail">Human-readable summary for the audit entry.</param>
public sealed record AttendeeResetResult(
    bool Ok,
    bool WelcomeReset,
    bool MasterClassReset,
    bool PartyReset,
    int SignupsRemoved,
    int RsvpsRemoved,
    int LedgerRowsRemoved,
    int TasksReopened,
    string Detail);

/// <summary>
/// §355 — put an attendee back to "freshly synced from the ticket system" so the operator can
/// re-test onboarding end to end (operator 2026-07-26: <i>"lets make an organizer feature, so an
/// organizer can do this reset, which resets the welcome, master class + party sign-up — he selects
/// the attendee and have option to flip the 3 values so it forces attendee to setup again"</i>).
///
/// <para><b>Why this exists as a feature.</b> The same reset was first done by hand with SQL against
/// production. That works once, but it is unaudited, unrepeatable and easy to get subtly wrong —
/// the by-hand run showed why: it is not one flag but rows across FIVE tables, and which of them
/// exist differs per attendee. Encoding it here makes it repeatable, scoped, and recorded.</para>
///
/// <para><b>Three INDEPENDENT switches</b>, because the operator asked to flip them separately —
/// re-testing the party flow should not have to destroy a Master Class seat.</para>
///
/// <para><b>Deliberately does NOT touch</b>: the <see cref="Attendee"/> row itself, its ticket
/// status, the participant's login/identity or their ring. This is a re-onboarding reset, not a
/// deletion — the person keeps their seat in the world, they just get asked again.</para>
/// </summary>
public sealed class AttendeeOnboardingResetService
{
    private readonly CommunityHubDbContext _db;

    public AttendeeOnboardingResetService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Reset the selected parts of one attendee's onboarding. Returns what was cleared;
    /// <see cref="AttendeeResetResult.Ok"/> is false when the attendee is not in this edition.
    /// </summary>
    public async Task<AttendeeResetResult> ResetAsync(
        int eventId,
        int attendeeId,
        bool resetWelcome,
        bool resetMasterClass,
        bool resetParty,
        CancellationToken ct = default)
    {
        var attendee = await _db.Attendees
            .FirstOrDefaultAsync(a => a.Id == attendeeId && a.EventId == eventId, ct);
        if (attendee is null)
        {
            return new AttendeeResetResult(false, false, false, false, 0, 0, 0, 0,
                "Attendee not found in this edition.");
        }

        if (!resetWelcome && !resetMasterClass && !resetParty)
        {
            return new AttendeeResetResult(false, false, false, false, 0, 0, 0, 0,
                "Nothing selected — choose at least one thing to reset.");
        }

        var email = attendee.Email ?? string.Empty;

        // The attendee's LOGIN participant, matched the way every attendee mail matches it:
        // by e-mail within the edition. May be null when provisioning has not run yet.
        var participant = string.IsNullOrWhiteSpace(email)
            ? null
            : await _db.Participants.FirstOrDefaultAsync(
                p => p.EventId == eventId && p.Email == email, ct);

        int signups = 0, rsvps = 0, ledger = 0, tasks = 0;
        var parts = new List<string>();

        if (resetWelcome)
        {
            // The 2-day SELECTION INVITE is the de-facto attendee welcome (§241/§217), and its
            // once-ever marker is this stamp — clearing it lets AttendeeBackstageSyncJob send it
            // again on its next pass.
            attendee.MasterClassInviteSentAt = null;
            if (participant is not null) participant.WelcomeWithLoginSentAt = null;

            // Cleared by ADDRESS **or** by this participant's own welcome OCCASION KEY.
            //
            // §340-G-1: §326bf moved the `welcome` send's idempotency to `welcome:{participantId}`,
            // because an address is not an identity — a typo correction or a re-import that changes
            // the case leaves the stored row under the OLD address. An address-only clear therefore
            // misses that row, deletes nothing, and reports success, while the re-send stays
            // suppressed by the row that survived: a reset that silently does half its job, which
            // then reads as passing. (Same defect found and fixed the same day in
            // `SponsorWelcomeEmailService.ResetForCompanyAsync`.)
            //
            // Matching EITHER is deliberate rather than switching wholesale to the key: this is a
            // reset, both identifications point at the same person, and the address arm still
            // catches `pending-master-class-selection` rows and anything written before this
            // attendee was provisioned a participant at all.
            var welcomeKey = participant is null ? null : $"welcome:{participant.Id}";
            var welcomeRows = await _db.SentReminders
                .Where(s => s.EventId == eventId
                            && (s.RecipientEmail == email
                                || (welcomeKey != null && s.OccasionKey == welcomeKey))
                            && (s.ReminderType == "welcome"
                                || s.ReminderType == "pending-master-class-selection"))
                .ToListAsync(ct);
            _db.SentReminders.RemoveRange(welcomeRows);
            ledger += welcomeRows.Count;
            parts.Add($"welcome/selection invite re-armed ({welcomeRows.Count} ledger row(s) cleared)");
        }

        if (resetMasterClass)
        {
            var mcSignups = await _db.MasterClassSignups
                .Where(s => s.EventId == eventId && s.AttendeeId == attendeeId)
                .ToListAsync(ct);
            _db.MasterClassSignups.RemoveRange(mcSignups);
            signups = mcSignups.Count;

            // The per-class reminder ledger rows would otherwise suppress the reminders.
            var mcRows = await _db.SentReminders
                .Where(s => s.EventId == eventId
                            && s.RecipientEmail == email
                            && s.ReminderType == "attendee-masterclass")
                .ToListAsync(ct);
            _db.SentReminders.RemoveRange(mcRows);
            ledger += mcRows.Count;

            tasks += await ReopenTasksAsync(participant?.Id, "masterclass-form:", ct);
            parts.Add($"Master Class cleared ({signups} signup(s) removed)");
        }

        if (resetParty)
        {
            // A PartyRsvp row existing at all means "answered" (Yes or No) — so the row must go,
            // not just its Attending flag, or the step still counts as done.
            // Match by participant id when we have one AND by address — an RSVP can predate
            // provisioning (the anonymous /volunteer-style path), so id alone would miss it.
            // NOTE: participant may be null here; a `participant!.Id` inside the predicate would
            // throw rather than translate, so the id is captured first.
            var pid = participant?.Id;
            var partyRows = await _db.PartyRsvps
                .Where(r => r.EventId == eventId
                            && ((pid != null && r.ParticipantId == pid) || r.Email == email))
                .ToListAsync(ct);
            _db.PartyRsvps.RemoveRange(partyRows);
            rsvps = partyRows.Count;

            var partyLedger = await _db.SentReminders
                .Where(s => s.EventId == eventId
                            && s.RecipientEmail == email
                            && s.ReminderType == "attendee-party")
                .ToListAsync(ct);
            _db.SentReminders.RemoveRange(partyLedger);
            ledger += partyLedger.Count;

            tasks += await ReopenTasksAsync(participant?.Id, "party-form:", ct);
            parts.Add($"party RSVP cleared ({rsvps} answer(s) removed)");
        }

        await _db.SaveChangesAsync(ct);

        var detail = $"Reset onboarding for {email}: " + string.Join("; ", parts)
                     + $". Tasks re-opened: {tasks}.";
        return new AttendeeResetResult(
            true, resetWelcome, resetMasterClass, resetParty,
            signups, rsvps, ledger, tasks, detail);
    }

    /// <summary>
    /// Re-open the auto-seeded onboarding task for a step so the attendee is asked again and the
    /// Get Started progress bar tells the truth. Clears <c>CompletedAt</c> too — a Done task with a
    /// completion time left behind would keep reading as finished in the §332 ratios.
    /// </summary>
    private async Task<int> ReopenTasksAsync(int? participantId, string sourceKeyPrefix, CancellationToken ct)
    {
        if (participantId is not int pid) return 0;
        return await ReopenBySourceKeyAsync(
            t => t.AssignedParticipantId == pid && t.SourceKey!.StartsWith(sourceKeyPrefix), ct);
    }

    /// <summary>
    /// §366 (operator 2026-07-26: "the reset of sponsors didn't reset correctly, as 2 tasks were
    /// not reset") — re-open tasks that belong to a SPONSOR COMPANY rather than to a person.
    ///
    /// <para>A sponsor's own tasks carry <c>SourceKey = "sponsor:{companyId}:…"</c> and have
    /// <b><c>AssignedParticipantId = NULL</c></b>, so every assignee-based query misses them
    /// entirely — which is exactly why "Initial onboarding of sponsor" and "Register booth
    /// members" survived a reset.</para>
    /// </summary>
    public Task<int> ReopenSponsorCompanyTasksAsync(string sponsorCompanyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId)) return Task.FromResult(0);
        var prefix = $"sponsor:{sponsorCompanyId}:";
        return ReopenBySourceKeyAsync(t => t.SourceKey!.StartsWith(prefix), ct);
    }

    private async Task<int> ReopenBySourceKeyAsync(
        System.Linq.Expressions.Expression<Func<ParticipantTask, bool>> match, CancellationToken ct)
    {
        var rows = await _db.Tasks
            .Where(t => t.SourceKey != null && t.State != TaskState.Open)
            .Where(match)
            .ToListAsync(ct);

        foreach (var t in rows)
        {
            t.State = TaskState.Open;
            t.CompletedAt = null;
            // §358: the reminder cadence is anchored on the LATER of (invite sent, task created).
            // A reset that left an OLD CreatedAt behind would let the chaser fire immediately —
            // which is exactly the double-send the operator hit. Re-stamping makes the task as new
            // as the re-armed welcome, so the first reminder waits a full window again.
            t.CreatedAt = DateTimeOffset.UtcNow;
        }
        return rows.Count;
    }
}
