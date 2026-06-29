using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>One step in the speaker onboarding wizard (REQUIREMENTS §28).</summary>
/// <param name="Key">Stable key (drives the resx label/description + the progress chip).</param>
/// <param name="Route">The existing form page this step opens (design A — forms untouched).</param>
/// <param name="Done">True when the speaker has already completed this step (data exists).</param>
public sealed record SpeakerWizardStep(string Key, string Route, bool Done);

/// <summary>
/// The speaker's onboarding progress (REQUIREMENTS §28). The ordered list of steps
/// the speaker is ENTITLED to, each with done/not-done, plus derived progress. The
/// wizard is a resumable SHELL over the existing form pages: it reads current data
/// each time (stateless), so a refresh / re-entry is always correct.
/// </summary>
public sealed record SpeakerWizardView(IReadOnlyList<SpeakerWizardStep> Steps)
{
    public int EntitledCount => Steps.Count;
    public int DoneCount => Steps.Count(s => s.Done);
    public bool AllDone => EntitledCount > 0 && DoneCount >= EntitledCount;
    public int Percent => EntitledCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / EntitledCount);

    /// <summary>The next incomplete step (the "Continue" target), or null when all done.</summary>
    public SpeakerWizardStep? NextStep => Steps.FirstOrDefault(s => !s.Done);

    /// <summary>1-based position of the next incomplete step (for "Step X of N"); 0 when all done.</summary>
    public int NextStepNumber
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
                if (!Steps[i].Done) return i + 1;
            return 0;
        }
    }
}

/// <summary>
/// Builds the speaker onboarding wizard view (REQUIREMENTS §28, design A). Reuses
/// the existing entitlement model (<see cref="FormEntitlementGate"/>) to include
/// only the steps a speaker is entitled to (Calendar email + Speaker Details are
/// always shown), in a fixed guided order (Calendar email → Speaker Details →
/// Hotel → Dinner → Swag → Lunch → Promote → Signal → Accept), and detects
/// completion from each form's persisted data — reusing the existing form pages +
/// save logic untouched (lowest risk, resumable). Travel reimbursement + the two
/// presentation uploads were dropped from the wizard (operator 2026-06-27) — they
/// remain as deadline tasks, not onboarding steps.
/// </summary>
public sealed class SpeakerWizardService
{
    private readonly CommunityHubDbContext _db;
    private readonly SignalGroupsProvider? _signal;

    public SpeakerWizardService(CommunityHubDbContext db, SignalGroupsProvider? signal = null)
    {
        _db = db;
        _signal = signal;
    }

    public async Task<SpeakerWizardView> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var entitled = await FormEntitlementGate.EffectiveItemsAsync(_db, eventId, participantId, ct);
        var steps = new List<SpeakerWizardStep>();

        // 1. Calendar email (optional) — operator 2026-06-27. The FIRST step: an
        //    optional alternate address used for CALENDAR invites / notifications
        //    (some speakers don't use their Sessionize email for calendar). Done
        //    once the speaker has SAVED the step (CalendarEmailSetAt stamped), even
        //    if they left the field blank — the same "speaker acted" marker the
        //    details step uses, so an OPTIONAL step can still complete the wizard.
        var calendarDone = await _db.SpeakerProfiles.AnyAsync(
            p => p.EventId == eventId && p.ParticipantId == participantId
                 && p.CalendarEmailSetAt != null, ct);
        steps.Add(new("calendar", "/Forms/CalendarEmail", calendarDone));

        // 2. Speaker Details — always (the speaker's own profile). Done only once the
        //    SPEAKER has actually edited their own details (P13): a non-blank Biography
        //    can arrive from the Sessionize import before the speaker has touched
        //    anything, which used to mark this step done prematurely. The
        //    speaker-edit marker (BioLastEditedBySpeakerAt, stamped by MarkSpeakerEdited
        //    on the speaker-facing Details/Onboarding pages) is the true "speaker acted"
        //    signal — the import never sets it.
        var detailsDone = await _db.SpeakerProfiles.AnyAsync(
            p => p.EventId == eventId && p.ParticipantId == participantId
                 && p.BioLastEditedBySpeakerAt != null, ct);
        steps.Add(new("details", "/Speaker/Details", detailsDone));

        // 3. Hotel — when entitled to a room.
        if (entitled.Contains(OrderItem.Hotel))
        {
            var done = await _db.HotelBookings.AnyAsync(
                h => h.EventId == eventId && h.ParticipantId == participantId, ct);
            steps.Add(new("hotel", "/Forms/Hotel", done));
        }

        // 4. Appreciation dinner — when entitled to a seat.
        if (entitled.Contains(OrderItem.AppreciationDinner))
        {
            var done = await _db.DinnerSignups.AnyAsync(
                d => d.EventId == eventId && d.ParticipantId == participantId, ct);
            steps.Add(new("dinner", "/Forms/Dinner", done));
        }

        // 5. Swag / gift — when entitled to swag or a polo.
        if (entitled.Contains(OrderItem.Swag) || entitled.Contains(OrderItem.Polo))
        {
            var done = await _db.SwagPreferences.AnyAsync(
                s => s.EventId == eventId && s.ParticipantId == participantId, ct);
            steps.Add(new("swag", "/Forms/Swag", done));
        }

        // 6. Lunch — gated by ENTITLEMENT (LunchPreDay OR LunchMainDay), the SAME gate the
        //    nav (_Layout formEntitlementHidden) and the Lunch page itself apply, so all
        //    three agree: a speaker who lacks both lunch entitlements no longer sees a
        //    Lunch nav link, a Lunch wizard step AND a directly-rendered Lunch form.
        //    Completion reads from the speaker's persisted lunch signup.
        if (entitled.Contains(OrderItem.LunchPreDay) || entitled.Contains(OrderItem.LunchMainDay))
        {
            var done = await _db.LunchSignups.AnyAsync(
                l => l.EventId == eventId && l.ParticipantId == participantId, ct);
            steps.Add(new("lunch", "/Forms/Lunch", done));
        }

        // Travel reimbursement, "Upload preview presentation" and "Upload final
        // presentation" were REMOVED from the wizard (operator 2026-06-27, §141/§142):
        // they have deadlines months out, not onboarding actions. The two uploads
        // remain as §120 deadline TASKS (SpeakerDeadlineSeeder) on /Speaker/Tasks;
        // travel is replaced by the country-gated "Submit travel reimbursement" task
        // (§143) for non-Denmark speakers. None of them is a guided wizard step.

        // 7. Help to promote your session(s) on LinkedIn (§116). Drives the speaker
        //    LinkedIn publish path (Speaker/Graphics → SpeakerLinkedInPublishService,
        //    §52) + the #ELDK27 #ExpertsLiveDK tags. A manual "mark done" (the act of
        //    promoting is external/optional), tracked on a promote: task.
        var promoteDone = await _db.Tasks.AnyAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == WizardStepTasks.Promote(participantId) && t.State == TaskState.Done, ct);
        steps.Add(new("promote", "/Speaker/Promote", promoteDone));

        // 8. Join Signal groups (§109) — speakers get the Speakers chat + broadcast.
        //     Manual mark-done (joining is external), tracked on a signal: task. Gated
        //     on the signal-groups config being present + Speaker being in scope.
        if (_signal?.InScope(ParticipantRole.Speaker) == true)
        {
            var signalDone = await _db.Tasks.AnyAsync(
                t => t.EventId == eventId && t.AssignedParticipantId == participantId
                     && t.SourceKey == WizardStepTasks.Signal(participantId) && t.State == TaskState.Done, ct);
            steps.Add(new("signal", "/Forms/Signal", signalDone));
        }

        // 9. Party sign-up (§164) — every speaker RSVPs Yes/No to the pre-day party.
        //    Done once the speaker has a saved Party RSVP row (stamped with their id).
        //    The matching party-form: task + reminder is seeded by PartyTaskSeeder.
        var partyDone = await _db.PartyRsvps.AnyAsync(
            r => r.EventId == eventId && r.ParticipantId == participantId, ct);
        steps.Add(new("party", "/Party", partyDone));

        // 10. Accept Code of Conduct + Privacy (§119) — all roles, always last. Done
        //    once the speaker has a persisted acceptance row (who/when).
        var acceptDone = await _db.ParticipantPolicyAcceptances.AnyAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);
        steps.Add(new("accept", "/Forms/Accept", acceptDone));

        return new SpeakerWizardView(steps);
    }
}
