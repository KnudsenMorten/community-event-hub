using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Participants;

/// <summary>
/// Brings a participant's OPEN logistics tasks in line with the actual per-form
/// DATA they have already submitted. Each self-service form (Hotel, Appreciation
/// Dinner, Lunch, Swag, Volunteer day-availability, Travel reimbursement) marks
/// its own task Done on save, but a submission saved BEFORE that wiring existed —
/// or a speaker-deadline task that mirrors the same form — can be left Open even
/// though the data is present. This reconciler closes that gap: for every form
/// whose data signal is TRUE it marks the matching OPEN task(s) Done.
///
/// <para>For each true signal it completes BOTH (a) the form-owned task
/// (e.g. <c>hotel-form:{pid}</c>) AND (b) any <c>speakerdl:{pid}:</c> deadline
/// task whose slug carries the form's keyword (hotel / dinner / lunch / swag) —
/// the slugger yields e.g. <c>swag--speaker-gift</c> and <c>preday-lunch</c>, so
/// matching is by substring. It NEVER touches sponsor-pull tasks
/// (<c>sponsor:…</c>) or the speaker upload-deck deadlines
/// (<c>speakerdl:{pid}:upload…</c>) — those carry no form data signal.</para>
///
/// <para>Idempotent + no-op when nothing needs changing: it only flips OPEN rows
/// and stamps <see cref="ParticipantTask.CompletedAt"/> when it is still null.</para>
/// </summary>
public sealed class FormTaskReconciler
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public FormTaskReconciler(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>One form's completion signal: its form-owned SourceKey plus the
    /// optional keyword used to match mirroring <c>speakerdl:</c> deadline slugs.</summary>
    private readonly record struct Signal(string FormKey, string? SpeakerDeadlineKeyword);

    /// <summary>
    /// Mark the OPEN form-owned + mirroring speaker-deadline tasks Done for every
    /// form the participant has already submitted. Idempotent; safe to call often.
    /// </summary>
    public async Task ReconcileAsync(int eventId, int participantId, CancellationToken ct)
    {
        // --- §164 Party RSVP — the ONE two-way signal -----------------------
        // Unlike every other form below (which only ever CLOSES a task), the party
        // sign-up task must always end up Yes or No: a saved RSVP ⇒ Done, and an
        // un-answer (the RSVP row removed) ⇒ reopen, so the reminder job nags again.
        // Handled before the early-returns so it runs even when no other form has
        // data. The task itself is created by PartyTaskSeeder for the staff roles.
        await ReconcilePartyAsync(eventId, participantId, ct);

        // --- §207 Master Class selection — a two-way signal for 2-day attendees ----
        // The masterclass-form: task is Done once the attendee (matched by email) has a
        // CONFIRMED Master Class signup, and reopens if that selection is lost — like the
        // party RSVP. No-op when the task doesn't exist (not a 2-day attendee).
        await ReconcileMasterClassAsync(eventId, participantId, ct);

        // 🔴 §1082 — A SYSTEM-CLOSED TASK IS NEVER REOPENED BY A DATA SIGNAL.
        //
        // The three two-way syncs below reopen a Done task when its underlying data is absent
        // ("they un-answered ⇒ nag again"). That is right for work a PERSON completed, and wrong for
        // a row the SYSTEM closed: a task retired from the catalog, or abandoned when its assignee
        // was deactivated, has no data by definition — so the reconciler would resurrect it on the
        // very next page load.
        //
        // ⚠️ Caught by RoleChange_volunteer_to_speaker_prunes_availability_but_keeps_party_task the
        // moment prunes started retiring instead of deleting: the reconciler retired `availability:`,
        // and this method reopened it a few lines later. A "retirement" that undoes itself is worse
        // than none — it is the §1081 shape (the onboarding reset key) all over again.
        //
        // 🔑 The guard is `ClosedReason == null`, i.e. "a person did this", which is exactly what that
        // column means (it labels only the closures the hub made on someone's behalf).

        // --- §173e Wizard-step tasks — the OTHER two-way signals --------------
        // Calendar email / Speaker details / Profile / Code-of-Conduct each gained a
        // task (WizardStepTaskSeeder) so My-Tasks mirrors the Get-Started journey. Like
        // the party RSVP they sync BOTH ways off a data signal: signal true ⇒ Done,
        // signal false (step un-answered) ⇒ reopen. Runs before the early-returns so it
        // works even when the participant has submitted no logistics form. No-op when the
        // task doesn't exist (the role doesn't have that step) or already matches.
        await ReconcileWizardStepsAsync(eventId, participantId, ct);

        // --- Compute the per-form DATA signals — in ONE round trip -----------
        //
        // §417 (operator 2026-07-27, from a real observation: "hotelbookings is not even used as i
        // was logged in as attendee when it was slow and failed"). He was right, and it was worse
        // than one stray query: this ran SIX separate EXISTS round trips on EVERY hub page load for
        // EVERY participant, regardless of role. An attendee has no hotel, dinner, lunch, swag,
        // volunteer or travel entitlement, so all six were guaranteed false — six round trips to
        // learn nothing. One of them (HotelBookings) is what hung 35s and 500'd his page during a
        // deploy restart.
        //
        // Collapsed into a SINGLE query of correlated EXISTS subqueries. Deliberately NOT
        // role-gated: these signals are entitlement-driven and people wear several hats (a
        // volunteer who also speaks), so a role filter would be a NEW rule that could silently stop
        // closing a real task. This keeps the semantics identical and simply stops paying six times
        // for the answer — and it does so for every role, not just attendees.
        var flags = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new
            {
                Hotel = _db.HotelBookings.Any(
                    x => x.EventId == eventId && x.ParticipantId == participantId),
                Dinner = _db.DinnerSignups.Any(
                    x => x.EventId == eventId && x.ParticipantId == participantId),
                Lunch = _db.LunchSignups.Any(
                    x => x.EventId == eventId && x.ParticipantId == participantId),
                Swag = _db.SwagPreferences.Any(
                    x => x.EventId == eventId && x.ParticipantId == participantId),
                // §234 (7a): the volunteer-form: task is OWNED by the SHIFTS wizard
                // (/Forms/VolunteerWizard → VolunteerAvailability with SelectedShifts), so its
                // completion signal must be THAT form's data — NOT the separate §148 per-day
                // availability form (VolunteerDayAvailability), which was cross-wiring the two live
                // forms (day availability saved ⇒ the shifts task closed with no shifts picked).
                // The per-day availability step's task is the availability: key below.
                Volunteer = _db.VolunteerAvailabilities.Any(
                    x => x.EventId == eventId && x.ParticipantId == participantId
                         && x.SelectedShifts != null && x.SelectedShifts != ""),
                // Travel only counts when the speaker is actually CLAIMING reimbursement
                // (an opt-out row is not a completed claim).
                Travel = _db.TravelReimbursements.Any(
                    x => x.EventId == eventId && x.ParticipantId == participantId
                         && x.RequestReimbursement),
            })
            .FirstOrDefaultAsync(ct);

        // No edition row ⇒ nothing to reconcile (and nothing could have signalled anyway).
        if (flags is null) return;

        var (hotel, dinner, lunch, swag, volunteer, travel) =
            (flags.Hotel, flags.Dinner, flags.Lunch, flags.Swag, flags.Volunteer, flags.Travel);

        var signals = new List<Signal>();
        if (hotel) signals.Add(new Signal($"hotel-form:{participantId}", "hotel"));
        if (dinner) signals.Add(new Signal($"dinner-form:{participantId}", "dinner"));
        if (lunch) signals.Add(new Signal($"lunch-form:{participantId}", "lunch"));
        if (swag) signals.Add(new Signal($"swag-form:{participantId}", "swag"));
        if (volunteer) signals.Add(new Signal($"volunteer-form:{participantId}", null));
        // A real travel CLAIM completes both the form-owned "submit ticket+invoice"
        // task AND the §143 speakerdl "submit-travel-reimbursement" deadline (the
        // "travel" keyword matches that slug). A non-claiming speaker still marks the
        // §143 task complete manually (opt-out) — that path is not a data signal.
        if (travel) signals.Add(new Signal($"travel:submit-ticket-invoice:{participantId}", "travel"));

        if (signals.Count == 0) return; // nothing submitted yet — no-op

        // --- Load this participant's OPEN, source-tagged tasks once ----------
        var openTasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.State == TaskState.Open
                        && t.SourceKey != null)
            .ToListAsync(ct);
        if (openTasks.Count == 0) return;

        var formKeys = signals
            .Select(s => s.FormKey)
            .ToHashSet(StringComparer.Ordinal);
        var keywords = signals
            .Where(s => s.SpeakerDeadlineKeyword is not null)
            .Select(s => s.SpeakerDeadlineKeyword!)
            .ToList();

        var speakerDeadlinePrefix = $"speakerdl:{participantId}:";
        var now = _clock.GetUtcNow();
        var changed = false;

        foreach (var task in openTasks)
        {
            var key = task.SourceKey!;

            // Never touch sponsor-pull tasks.
            if (key.StartsWith("sponsor:", StringComparison.Ordinal)) continue;

            var match = false;
            if (formKeys.Contains(key))
            {
                match = true;
            }
            else if (key.StartsWith(speakerDeadlinePrefix, StringComparison.Ordinal))
            {
                var slug = key[speakerDeadlinePrefix.Length..];
                // Never the speaker upload-deck deadlines — they carry no data signal.
                if (!slug.StartsWith("upload", StringComparison.Ordinal))
                {
                    match = keywords.Any(k => slug.Contains(k, StringComparison.Ordinal));
                }
            }

            if (!match) continue;

            task.State = TaskState.Done;
            task.CompletedAt ??= now;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §164: bring the participant's party sign-up task in line with their RSVP — the
    /// only signal here that reopens. A PartyRsvp row stamped with this participant ⇒
    /// the <c>party-form:{pid}</c> task is Done; no row (they un-answered) ⇒ the task
    /// reopens (cleared CompletedAt) so it nags again. No-op when the task doesn't
    /// exist (the role doesn't get a party task) or when it already matches.
    /// </summary>
    private async Task ReconcilePartyAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = PartyTaskSeeder.SourceKeyFor(participantId);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null) return;

        var answered = await _db.PartyRsvps.AnyAsync(
            r => r.EventId == eventId && r.ParticipantId == participantId, ct);

        // §228: a SPONSOR contact is also covered by their company's single GROUP
        // reservation — whoever on the team registered it, everyone's task completes.
        if (!answered)
        {
            var companyId = await _db.Participants
                .Where(p => p.Id == participantId && p.EventId == eventId
                            && p.Role == ParticipantRole.Sponsor)
                .Select(p => p.SponsorCompanyId)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(companyId))
            {
                answered = await _db.PartyRsvps.AnyAsync(
                    r => r.EventId == eventId && r.ParticipantId != null
                         && _db.Participants.Any(p => p.Id == r.ParticipantId
                             && p.EventId == eventId && p.SponsorCompanyId == companyId), ct);
            }
        }

        // §234: a soft-cancelled ATTENDEE is "no nag" — their open party task closes (and
        // reopens again if the ticket reactivates and they still haven't answered).
        if (!answered && await IsCancelledAttendeeAsync(eventId, participantId, ct))
            answered = true;

        var changed = false;
        if (answered && task.State == TaskState.Open)
        {
            task.State = TaskState.Done;
            task.CompletedAt ??= _clock.GetUtcNow();
            changed = true;
        }
        else if (!answered && task.State == TaskState.Done && task.ClosedReason == null)
        {
            task.State = TaskState.Open;
            task.CompletedAt = null;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §234: true when this participant is an ATTENDEE whose email no longer holds ANY
    /// <see cref="MirrorState.Active"/> ticket in the edition — i.e. soft-cancelled. The
    /// mirror keys on the ticket id and one email may hold both a cancelled and an active
    /// ticket (a normal state); entitlement = ≥1 active ticket per email. Crew roles have
    /// no Attendee row, so they are never "cancelled" here and keep their reminders.
    /// </summary>
    private async Task<bool> IsCancelledAttendeeAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var p = await _db.Participants
            .Where(x => x.Id == participantId && x.EventId == eventId)
            .Select(x => new { x.Role, x.Email })
            .FirstOrDefaultAsync(ct);
        if (p is null || p.Role != ParticipantRole.Attendee) return false;
        var norm = (p.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (norm.Length == 0) return false;
        return !await _db.Attendees.AnyAsync(
            a => a.EventId == eventId && a.MirrorState == MirrorState.Active
                 && a.Email.ToLower() == norm, ct);
    }

    /// <summary>
    /// §207: bring a 2-day attendee's Master Class selection task in line with their actual
    /// in-hub selection — a CONFIRMED <see cref="MasterClassSignup"/> for the attendee row
    /// matching this participant's email ⇒ the <c>masterclass-form:{pid}</c> task is Done; no
    /// confirmed seat (they un-selected) ⇒ it reopens so the reminder cadence nags again.
    /// No-op when the task doesn't exist (not a 2-day attendee) or when it already matches.
    /// </summary>
    private async Task ReconcileMasterClassAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = AttendeeMasterClassTaskSeeder.SourceKeyFor(participantId);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null) return;

        var email = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => p.Email)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email)) return;
        var norm = email.Trim().ToLowerInvariant();

        var selected = await _db.MasterClassSignups.AnyAsync(
            s => s.EventId == eventId
                 && s.Status == MasterClassSignupStatus.Confirmed
                 && s.Attendee.Email.ToLower() == norm, ct);

        // §234: a soft-cancelled ATTENDEE is "no nag" — their open selection task closes
        // (and reopens if the ticket reactivates while they still have no confirmed seat).
        if (!selected && await IsCancelledAttendeeAsync(eventId, participantId, ct))
            selected = true;

        var changed = false;
        if (selected && task.State == TaskState.Open)
        {
            task.State = TaskState.Done;
            task.CompletedAt ??= _clock.GetUtcNow();
            changed = true;
        }
        else if (!selected && task.State == TaskState.Done && task.ClosedReason == null)
        {
            task.State = TaskState.Open;
            task.CompletedAt = null;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §173e: bring the DATA-signal wizard-step tasks (Speaker details, Profile,
    /// Code-of-Conduct, Availability) in line with their step's completion, BOTH
    /// ways — the step's data present ⇒ task Done; the step un-answered ⇒ task reopens —
    /// exactly like the party RSVP. Only flips tasks that exist (the role has the step)
    /// and only when the state actually differs (idempotent). The signal for each:
    /// (§313: <c>calendar:</c> is gone — the optional step never has a task.)
    /// <list type="bullet">
    ///   <item><c>speaker-details:</c> — the SPEAKER edited their own details (BioLastEditedBySpeakerAt stamped; an import never sets it).</item>
    ///   <item><c>profile:</c> — a phone number is on file (the meaningful "contact basics done").</item>
    ///   <item><c>accept:</c> — a Code-of-Conduct / Privacy acceptance row exists.</item>
    /// </list>
    /// </summary>
    private async Task ReconcileWizardStepsAsync(int eventId, int participantId, CancellationToken ct)
    {
        // §313: calendar: is gone — the optional Calendar-email step never has a task
        // (WizardStepTaskSeeder no longer seeds it and prunes leftovers).
        var keys = new[]
        {
            WizardStepTaskKeys.SpeakerDetails(participantId),
            WizardStepTaskKeys.Profile(participantId),
            WizardStepTaskKeys.Accept(participantId),
            WizardStepTaskKeys.Availability(participantId),
        };

        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.SourceKey != null
                        && keys.Contains(t.SourceKey))
            .ToListAsync(ct);
        if (tasks.Count == 0) return; // this role has none of these steps

        // Compute each signal only when a task for it is actually present.
        bool? details = null, profile = null, accept = null, avail = null;
        async Task<bool> DetailsSignal() => await _db.SpeakerProfiles.AnyAsync(
            p => p.EventId == eventId && p.ParticipantId == participantId
                 && p.BioLastEditedBySpeakerAt != null, ct);
        async Task<bool> ProfileSignal()
        {
            // §262: phone is REQUIRED for volunteers (contact basics = name + phone), OPTIONAL
            // for every other role — so a non-volunteer's profile step completes once a name is
            // on file (which is always the case), while a volunteer's needs the phone too.
            var pp = await _db.Participants
                .Where(p => p.Id == participantId && p.EventId == eventId)
                .Select(p => new { p.Role, p.Phone, p.FullName })
                .FirstOrDefaultAsync(ct);
            if (pp is null) return false;
            return pp.Role == ParticipantRole.Volunteer
                ? !string.IsNullOrEmpty(pp.Phone)
                : !string.IsNullOrEmpty(pp.FullName);
        }
        async Task<bool> AcceptSignal() => await _db.ParticipantPolicyAcceptances.AnyAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);
        // §234 (7a): the per-day availability step — its own data, matching the step's
        // IsDone detection (§148: ≥1 saved VolunteerDayAvailability row).
        async Task<bool> AvailabilitySignal() => await _db.VolunteerDayAvailabilities.AnyAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);

        var changed = false;
        var now = _clock.GetUtcNow();
        foreach (var task in tasks)
        {
            var key = task.SourceKey!;
            bool answered;
            if (key.StartsWith(WizardStepTaskKeys.SpeakerDetailsPrefix, StringComparison.Ordinal))
                answered = details ??= await DetailsSignal();
            else if (key.StartsWith(WizardStepTaskKeys.ProfilePrefix, StringComparison.Ordinal))
                answered = profile ??= await ProfileSignal();
            else if (key.StartsWith(WizardStepTaskKeys.AcceptPrefix, StringComparison.Ordinal))
                answered = accept ??= await AcceptSignal();
            else if (key.StartsWith(WizardStepTaskKeys.AvailabilityPrefix, StringComparison.Ordinal))
                answered = avail ??= await AvailabilitySignal();
            else
                continue;

            if (answered && task.State == TaskState.Open)
            {
                task.State = TaskState.Done;
                task.CompletedAt ??= now;
                changed = true;
            }
            else if (!answered && task.State == TaskState.Done && task.ClosedReason == null)
            {
                task.State = TaskState.Open;
                task.CompletedAt = null;
                changed = true;
            }
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }
}
