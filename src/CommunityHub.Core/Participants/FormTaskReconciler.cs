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

        // --- Compute the per-form DATA signals -------------------------------
        var hotel = await _db.HotelBookings.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        var dinner = await _db.DinnerSignups.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        var lunch = await _db.LunchSignups.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        var swag = await _db.SwagPreferences.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        var volunteer = await _db.VolunteerDayAvailabilities.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        // Travel only counts when the speaker is actually CLAIMING reimbursement
        // (an opt-out row is not a completed claim).
        var travel = await _db.TravelReimbursements.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId && x.RequestReimbursement, ct);

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

        var changed = false;
        if (answered && task.State == TaskState.Open)
        {
            task.State = TaskState.Done;
            task.CompletedAt ??= _clock.GetUtcNow();
            changed = true;
        }
        else if (!answered && task.State == TaskState.Done)
        {
            task.State = TaskState.Open;
            task.CompletedAt = null;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }
}
