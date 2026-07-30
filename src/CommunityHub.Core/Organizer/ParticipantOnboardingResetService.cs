using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>One resettable Get-Started step, keyed exactly as the wizard keys it.</summary>
/// <param name="Key">The wizard step key (<c>calendar</c>, <c>details</c>, <c>hotel</c>, …).</param>
/// <param name="Label">What the organizer sees next to the checkbox.</param>
/// <param name="What">One line saying what clearing it actually does, shown as help text.</param>
public sealed record OnboardingResetStep(string Key, string Label, string What);

/// <summary>What a participant reset cleared — surfaced to the organizer and the audit trail.</summary>
/// <param name="Ok">False when the participant could not be resolved, or nothing was selected.</param>
/// <param name="StepsReset">The step keys that were actually processed.</param>
/// <param name="RowsRemoved">Form/answer rows deleted across all selected steps.</param>
/// <param name="StampsCleared">Speaker-profile "has acted" markers set back to null.</param>
/// <param name="TasksReopened">Onboarding tasks moved back to Open.</param>
/// <param name="LedgerRowsRemoved">`SentReminder` rows deleted so reminders may fire again.</param>
/// <param name="Detail">Human-readable summary for the audit entry.</param>
public sealed record ParticipantResetResult(
    bool Ok,
    IReadOnlyList<string> StepsReset,
    int RowsRemoved,
    int StampsCleared,
    int TasksReopened,
    int LedgerRowsRemoved,
    string Detail);

/// <summary>
/// §355 — the SPEAKER (and any non-attendee participant) equivalent of
/// <see cref="AttendeeOnboardingResetService"/>: put one person's Get Started back to
/// not-yet-done, per step or in full, so onboarding can be re-tested end to end (operator
/// 2026-07-26: <i>"should we have a similar organizer interface for this reset functionality, so i
/// can reset both per step + full reset"</i>).
///
/// <para><b>THE TRAP THIS EXISTS TO AVOID, stated plainly: re-opening the TASKS would not re-open
/// the WIZARD.</b> <c>SpeakerWizardService</c> derives every step's <c>Done</c> from the DATA, not
/// from the task — a saved <c>HotelBookings</c> row means "hotel done" whether or not a task
/// exists. A reset that only touched tasks would leave the organizer looking at a wizard that
/// still says 100%, which is exactly what the by-hand run showed: it took EIGHT different sources
/// for one speaker. So each step here clears its own done-deriving data AND its task.</para>
///
/// <para><b>Keys match the wizard's keys</b> so the two cannot drift: a step the wizard renders is
/// a step this can reset, by the same name.</para>
///
/// <para><b>Deliberately does NOT touch</b>: the participant row, their role, login identity, ring,
/// sessions, or anything imported from Sessionize. This is a re-onboarding reset, not a deletion —
/// and notably it does not clear a speaker's BIOGRAPHY text, only the "the speaker has edited it"
/// marker, so re-testing onboarding never destroys authored content.</para>
/// </summary>
public sealed class ParticipantOnboardingResetService
{
    private readonly CommunityHubDbContext _db;

    public ParticipantOnboardingResetService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// The resettable steps, in the wizard's own order. The organizer UI renders this list, so a
    /// step added to the wizard shows up here the moment it is added below — and
    /// <c>ParticipantOnboardingResetTests</c> asserts the keys still match the wizard's.
    /// </summary>
    public static readonly IReadOnlyList<OnboardingResetStep> Steps = new List<OnboardingResetStep>
    {
        new("calendar", "Calendar e-mail",
            "Clears the 'answered' stamp. The address itself is kept."),
        new("details", "Speaker details",
            "Clears accreditation, company, country, gender and the first-time answer, plus the "
            + "'the speaker edited this' marker. Biography, tagline and links are NOT deleted."),
        new("hotel", "Hotel booking", "Deletes the hotel answer."),
        new("dinner", "Appreciation dinner", "Deletes the dinner RSVP."),
        new("swag", "Swag / gift", "Deletes the swag preferences."),
        new("lunch", "Lunch", "Deletes the lunch signup."),
        new("signal", "Join Signal groups", "Re-opens the manual 'mark done' task."),
        new("party", "Party sign-up", "Deletes the RSVP — its existence alone counts as answered."),
        new("accept", "Code of Conduct + Privacy", "Deletes the acceptance record."),
    };

    /// <summary>Every key <see cref="Steps"/> knows about.</summary>
    public static IReadOnlySet<string> StepKeys =>
        Steps.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Reset the selected steps for one participant. Unknown keys are ignored rather than throwing —
    /// a stale checkbox in a posted form must never 500 an organizer page.
    /// </summary>
    public async Task<ParticipantResetResult> ResetAsync(
        int eventId,
        int participantId,
        IEnumerable<string> stepKeys,
        CancellationToken ct = default)
    {
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == eventId, ct);
        if (participant is null)
        {
            return Fail("Participant not found in this edition.");
        }

        var wanted = stepKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Where(StepKeys.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (wanted.Count == 0)
        {
            return Fail("Nothing selected — choose at least one step to reset.");
        }

        var email = participant.Email ?? string.Empty;
        int rows = 0, stamps = 0, tasks = 0, ledger = 0;
        var parts = new List<string>();

        foreach (var key in wanted)
        {
            switch (key)
            {
                case "calendar":
                {
                    // The step is DONE once the speaker saved it, even leaving the field blank —
                    // so the stamp is the completion signal, and only the stamp is cleared. Wiping
                    // the address itself would lose a real preference to a test reset.
                    var profiles = await SpeakerProfilesAsync(eventId, participantId, ct);
                    foreach (var p in profiles.Where(p => p.CalendarEmailSetAt != null))
                    {
                        p.CalendarEmailSetAt = null;
                        stamps++;
                    }
                    parts.Add("calendar e-mail step re-armed");
                    break;
                }

                case "details":
                {
                    // The MARKER is what the wizard reads for done-ness, and it must stay the marker:
                    // a non-blank biography can arrive from the Sessionize import, so deriving from
                    // the prose would mark the step done again on its own.
                    //
                    // §401 (operator 2026-07-26: "speaker reset of speaker details didnt reset the
                    // fields i had manually set like company, country, accrediation"). Clearing only
                    // the marker re-armed the STEP but left the ANSWERS on screen, so re-testing
                    // onboarding showed a pre-filled form — the reset silently did half its job,
                    // which is worse than refusing, because the next test run reads as passing.
                    //
                    // So the HUB-COLLECTED answers go too. They are exactly the fields the speaker
                    // types on this form and that the Sessionize import is documented never to touch
                    // (SpeakerProfile: Accreditation, CompanyName, IsFirstTimeSpeaker, Country,
                    // Gender). Everything IMPORTED or AUTHORED — Biography, Tagline, Blog, LinkedIn,
                    // Twitter — is left alone: it is content, not an onboarding answer, and
                    // destroying it to re-arm a test step would be data loss wearing a test
                    // feature's clothes.
                    var profiles = await SpeakerProfilesAsync(eventId, participantId, ct);
                    foreach (var p in profiles)
                    {
                        if (p.BioLastEditedBySpeakerAt != null)
                        {
                            p.BioLastEditedBySpeakerAt = null;
                            stamps++;
                        }

                        if (p.Accreditation is not null) { p.Accreditation = null; rows++; }
                        if (p.CompanyName is not null) { p.CompanyName = null; rows++; }
                        if (p.Country is not null) { p.Country = null; rows++; }
                        if (p.Gender is not null) { p.Gender = null; rows++; }
                        if (p.IsFirstTimeSpeaker is not null) { p.IsFirstTimeSpeaker = null; rows++; }
                    }
                    tasks += await ReopenTasksAsync(eventId, participantId, "speaker-form:", ct);
                    parts.Add("speaker details step re-armed; accreditation, company, country, "
                              + "gender and first-time answer cleared (biography/tagline/links kept)");
                    break;
                }

                case "hotel":
                {
                    var booked = await _db.HotelBookings
                        .Where(h => h.EventId == eventId && h.ParticipantId == participantId)
                        .ToListAsync(ct);
                    _db.HotelBookings.RemoveRange(booked);
                    rows += booked.Count;
                    tasks += await ReopenTasksAsync(eventId, participantId, "hotel-form:", ct);
                    parts.Add($"hotel cleared ({booked.Count} row(s))");
                    break;
                }

                case "dinner":
                {
                    var signups = await _db.DinnerSignups
                        .Where(d => d.EventId == eventId && d.ParticipantId == participantId)
                        .ToListAsync(ct);
                    _db.DinnerSignups.RemoveRange(signups);
                    rows += signups.Count;
                    tasks += await ReopenTasksAsync(eventId, participantId, "dinner-form:", ct);
                    parts.Add($"dinner cleared ({signups.Count} row(s))");
                    break;
                }

                case "swag":
                {
                    var prefs = await _db.SwagPreferences
                        .Where(s => s.EventId == eventId && s.ParticipantId == participantId)
                        .ToListAsync(ct);
                    _db.SwagPreferences.RemoveRange(prefs);
                    rows += prefs.Count;
                    tasks += await ReopenTasksAsync(eventId, participantId, "swag-form:", ct);
                    parts.Add($"swag cleared ({prefs.Count} row(s))");
                    break;
                }

                case "lunch":
                {
                    var lunches = await _db.LunchSignups
                        .Where(l => l.EventId == eventId && l.ParticipantId == participantId)
                        .ToListAsync(ct);
                    _db.LunchSignups.RemoveRange(lunches);
                    rows += lunches.Count;
                    tasks += await ReopenTasksAsync(eventId, participantId, "lunch-form:", ct);
                    parts.Add($"lunch cleared ({lunches.Count} row(s))");
                    break;
                }

                case "signal":
                {
                    // The ONE step with no form data: joining is external, so the task IS the state.
                    tasks += await ReopenTasksAsync(
                        eventId, participantId, $"signal:{participantId}", ct);
                    parts.Add("Signal groups step re-opened");
                    break;
                }

                case "party":
                {
                    // A row existing at all means "answered" (Yes or No) — so the ROW goes, not just
                    // its Attending flag, or the step still counts as done. Matched by id AND by
                    // address, because an RSVP can predate provisioning.
                    var rsvps = await _db.PartyRsvps
                        .Where(r => r.EventId == eventId
                                    && (r.ParticipantId == participantId
                                        || (email != string.Empty && r.Email == email)))
                        .ToListAsync(ct);
                    _db.PartyRsvps.RemoveRange(rsvps);
                    rows += rsvps.Count;
                    tasks += await ReopenTasksAsync(eventId, participantId, "party-form:", ct);
                    parts.Add($"party RSVP cleared ({rsvps.Count} answer(s))");
                    break;
                }

                case "accept":
                {
                    var accepts = await _db.ParticipantPolicyAcceptances
                        .Where(a => a.EventId == eventId && a.ParticipantId == participantId)
                        .ToListAsync(ct);
                    _db.ParticipantPolicyAcceptances.RemoveRange(accepts);
                    rows += accepts.Count;
                    parts.Add($"policy acceptance cleared ({accepts.Count} row(s))");
                    break;
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        // The task-deadline ledger is keyed `task:{id}:due` and a re-opened task keeps its ID, so
        // without this the re-opened task could never remind again — the reset would look complete
        // and silently produce no mail. Done AFTER SaveChanges so the re-opened set is settled.
        ledger += await ClearTaskDeadlineLedgerAsync(eventId, email, participantId, ct);

        var detail = $"Reset Get Started for {email} [{string.Join(", ", wanted)}]: "
                     + string.Join("; ", parts)
                     + $". Tasks re-opened: {tasks}; stamps cleared: {stamps}; "
                     + $"ledger rows cleared: {ledger}.";

        return new ParticipantResetResult(true, wanted, rows, stamps, tasks, ledger, detail);

        static ParticipantResetResult Fail(string why) =>
            new(false, Array.Empty<string>(), 0, 0, 0, 0, why);
    }

    private Task<List<SpeakerProfile>> SpeakerProfilesAsync(
        int eventId, int participantId, CancellationToken ct) =>
        _db.SpeakerProfiles
            .Where(p => p.EventId == eventId && p.ParticipantId == participantId)
            .ToListAsync(ct);

    /// <summary>
    /// Re-open this participant's onboarding task for a step and re-stamp <c>CreatedAt</c>.
    ///
    /// <para>The re-stamp is not cosmetic: §358 anchors the reminder cadence on the task's creation,
    /// so a reset leaving an OLD <c>CreatedAt</c> would let the chaser fire immediately — the exact
    /// double-send the operator hit. A re-opened task must look as new as it now is.</para>
    /// </summary>
    private async Task<int> ReopenTasksAsync(
        int eventId, int participantId, string sourceKeyPrefix, CancellationToken ct)
    {
        var rows = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith(sourceKeyPrefix)
                        && t.State != TaskState.Open)
            .ToListAsync(ct);

        foreach (var t in rows)
        {
            t.State = TaskState.Open;
            t.CompletedAt = null;
            t.CreatedAt = DateTimeOffset.UtcNow;
        }
        return rows.Count;
    }

    /// <summary>
    /// Drop the once-ever <c>task-deadline</c> ledger rows for this participant's still-open tasks,
    /// so a re-opened task can remind again. The engine dedups on <c>task:{id}:due</c> and a reset
    /// keeps the task ID, so the row left behind would suppress the reminder forever.
    /// </summary>
    private async Task<int> ClearTaskDeadlineLedgerAsync(
        int eventId, string email, int participantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return 0;

        var openTaskIds = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.State == TaskState.Open)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (openTaskIds.Count == 0) return 0;

        var keys = openTaskIds.Select(id => $"task:{id}:due").ToHashSet(StringComparer.Ordinal);

        // The prefix match runs in MEMORY on purpose: `keys.Any(k => s.OccasionKey.StartsWith(k))`
        // is not SQL-translatable (EF throws rather than silently client-evaluating). The pulled set
        // is bounded to this edition's task-deadline rows, which is small.
        var candidates = await _db.SentReminders
            .Where(s => s.EventId == eventId
                        && s.ReminderType == "task-deadline"
                        && s.OccasionKey.StartsWith("task:"))
            .ToListAsync(ct);

        // StartsWith, not equality: the sponsor path appends ":{coordinatorEmail}" to the same key.
        var rows = candidates
            .Where(s => keys.Any(k => s.OccasionKey.StartsWith(k, StringComparison.Ordinal)))
            .ToList();

        _db.SentReminders.RemoveRange(rows);
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
