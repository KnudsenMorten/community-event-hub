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
/// §784.9(c) — every per-speaker FACT the Get-Started step list is derived from, resolved.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this record exists.</b> The organizer readiness roster has to show the same
/// Get-Started steps the speaker sees (party, lunch, …), and it builds for EVERY speaker at once.
/// Re-deriving the steps there from a second set of queries would have created two rules that must
/// agree and silently diverge the first time one is changed — the failure this codebase keeps
/// meeting. So the step list is composed by ONE pure function
/// (<see cref="SpeakerWizardService.ComposeSteps"/>) over these facts, and the two callers differ
/// only in HOW they load them: one speaker at a time, or batch-loaded for the roster.</para>
///
/// <para>⚠️ Add a step ⇒ add its fact here, and both surfaces pick it up. Add a step that reads the
/// database inside <c>ComposeSteps</c> and the roster silently loses it.</para>
/// </remarks>
public sealed record SpeakerWizardFacts(
    IReadOnlySet<OrderItem> Entitled,
    bool WelcomeConfigured,
    bool CalendarSet,
    bool DetailsEdited,
    bool HotelBooked,
    bool DinnerSignedUp,
    bool SwagSaved,
    bool LunchSignedUp,
    bool SignalInScope,
    bool SignalTaskDone,
    bool PartyRsvped,
    bool PolicyAccepted,
    bool HasOutsideWizardTask);

/// <summary>
/// Builds the speaker onboarding wizard view (REQUIREMENTS §28, design A). Reuses
/// the existing entitlement model (<see cref="FormEntitlementGate"/>) to include
/// only the steps a speaker is entitled to (Calendar email + Speaker Details are
/// always shown), in a fixed guided order (Calendar email → Speaker Details →
/// Hotel → Dinner → Swag → Lunch → Signal → Party → Accept), and detects
/// completion from each form's persisted data — reusing the existing form pages +
/// save logic untouched (lowest risk, resumable). Travel reimbursement + the two
/// presentation uploads were dropped from the wizard (operator 2026-06-27) — they
/// remain as deadline tasks, not onboarding steps.
/// </summary>
public sealed class SpeakerWizardService
{
    private readonly CommunityHubDbContext _db;
    private readonly SignalGroupsProvider? _signal;
    private readonly Core.Content.WelcomeCopyStore? _welcome;

    public SpeakerWizardService(
        CommunityHubDbContext db,
        SignalGroupsProvider? signal = null,
        // §680 — optional + last, the same pattern as _signal (see RoleWizardService).
        Core.Content.WelcomeCopyStore? welcome = null)
    {
        _db = db;
        _signal = signal;
        _welcome = welcome;
    }

    public async Task<SpeakerWizardView> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => new(ComposeSteps(await LoadFactsAsync(eventId, participantId, ct)));

    /// <summary>Resolve every fact for ONE speaker (the speaker's own wizard page).</summary>
    private async Task<SpeakerWizardFacts> LoadFactsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var entitled = await FormEntitlementGate.EffectiveItemsAsync(_db, eventId, participantId, ct);

        var profile = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId && p.ParticipantId == participantId)
            .Select(p => new { p.CalendarEmailSetAt, p.BioLastEditedBySpeakerAt })
            .FirstOrDefaultAsync(ct);

        // §410 — the closing "deadlines" step is offered only when this person actually has work
        // OUTSIDE Get Started; §173e mirrors every wizard step with a task, so without this the
        // step would list the speaker's own wizard back at them.
        var sourceKeys = await _db.Tasks.AsNoTracking()
            .Where(t => t.EventId == eventId && t.AssignedParticipantId == participantId)
            .Select(t => t.SourceKey)
            .ToListAsync(ct);

        return new SpeakerWizardFacts(
            Entitled: entitled,
            WelcomeConfigured: _welcome?.Exists(ParticipantRole.Speaker) == true,
            CalendarSet: profile?.CalendarEmailSetAt != null,
            DetailsEdited: profile?.BioLastEditedBySpeakerAt != null,
            HotelBooked: await _db.HotelBookings.AnyAsync(
                h => h.EventId == eventId && h.ParticipantId == participantId, ct),
            DinnerSignedUp: await _db.DinnerSignups.AnyAsync(
                d => d.EventId == eventId && d.ParticipantId == participantId, ct),
            SwagSaved: await _db.SwagPreferences.AnyAsync(
                s => s.EventId == eventId && s.ParticipantId == participantId, ct),
            LunchSignedUp: await _db.LunchSignups.AnyAsync(
                l => l.EventId == eventId && l.ParticipantId == participantId, ct),
            SignalInScope: _signal?.InScope(ParticipantRole.Speaker) == true,
            SignalTaskDone: await _db.Tasks.AnyAsync(
                t => t.EventId == eventId && t.AssignedParticipantId == participantId
                     && t.SourceKey == WizardStepTasks.Signal(participantId)
                     && t.State == TaskState.Done, ct),
            PartyRsvped: await _db.PartyRsvps.AnyAsync(
                r => r.EventId == eventId && r.ParticipantId == participantId, ct),
            PolicyAccepted: await _db.ParticipantPolicyAcceptances.AnyAsync(
                a => a.EventId == eventId && a.ParticipantId == participantId, ct),
            HasOutsideWizardTask: sourceKeys.Any(
                CommunityHub.Core.Participants.OutsideWizardTasks.IsOutsideWizard));
    }

    /// <summary>
    /// 🔒 THE ONE DEFINITION of the speaker's Get-Started step list — pure, so the speaker's own
    /// wizard and the organizer readiness roster (§784.9(c)) cannot drift apart. It must never read
    /// the database: everything it needs is in <see cref="SpeakerWizardFacts"/>.
    /// </summary>
    public static List<SpeakerWizardStep> ComposeSteps(SpeakerWizardFacts f)
    {
        var steps = new List<SpeakerWizardStep>();

        // 0. §680 — the WELCOME step, before everything else: thank the speaker, introduce the
        //    event, and name what they will find in the hub. Read-only, always Done (it asks
        //    nothing, so it must never hold the progress bar below 100%).
        if (f.WelcomeConfigured)
        {
            steps.Add(new(
                Core.Content.WelcomeCopyStore.StepKey, Core.Content.WelcomeCopyStore.StepRoute, true));
        }

        // 1. Calendar email (optional) — operator 2026-06-27. The FIRST step: an
        //    optional alternate address used for CALENDAR invites / notifications
        //    (some speakers don't use their Sessionize email for calendar). Done
        //    once the speaker has SAVED the step (CalendarEmailSetAt stamped), even
        //    if they left the field blank — the same "speaker acted" marker the
        //    details step uses, so an OPTIONAL step can still complete the wizard.
        steps.Add(new("calendar", "/Forms/CalendarEmail", f.CalendarSet));

        // 2. Speaker Details — always (the speaker's own profile). Done only once the
        //    SPEAKER has actually edited their own details (P13): a non-blank Biography
        //    can arrive from the Sessionize import before the speaker has touched
        //    anything, which used to mark this step done prematurely. The
        //    speaker-edit marker (BioLastEditedBySpeakerAt, stamped by MarkSpeakerEdited
        //    on the speaker-facing Details/Onboarding pages) is the true "speaker acted"
        //    signal — the import never sets it.
        steps.Add(new("details", "/Speaker/Details", f.DetailsEdited));

        // 3. Hotel — when entitled to a room.
        if (f.Entitled.Contains(OrderItem.Hotel))
        {
            steps.Add(new("hotel", "/Forms/Hotel", f.HotelBooked));
        }

        // 4. Appreciation dinner — when entitled to a seat.
        if (f.Entitled.Contains(OrderItem.AppreciationDinner))
        {
            steps.Add(new("dinner", "/Forms/Dinner", f.DinnerSignedUp));
        }

        // 5. Swag / gift — when entitled to swag or a polo.
        if (f.Entitled.Contains(OrderItem.Swag) || f.Entitled.Contains(OrderItem.Polo))
        {
            steps.Add(new("swag", "/Forms/Swag", f.SwagSaved));
        }

        // 6. Lunch — gated by ENTITLEMENT (LunchPreDay OR LunchMainDay), the SAME gate the
        //    nav (_Layout formEntitlementHidden) and the Lunch page itself apply, so all
        //    three agree: a speaker who lacks both lunch entitlements no longer sees a
        //    Lunch nav link, a Lunch wizard step AND a directly-rendered Lunch form.
        //    Completion reads from the speaker's persisted lunch signup.
        if (f.Entitled.Contains(OrderItem.LunchPreDay) || f.Entitled.Contains(OrderItem.LunchMainDay))
        {
            steps.Add(new("lunch", "/Forms/Lunch", f.LunchSignedUp));
        }

        // Travel reimbursement, "Upload preview presentation" and "Upload final
        // presentation" were REMOVED from the wizard (operator 2026-06-27, §141/§142):
        // they have deadlines months out, not onboarding actions. The two uploads
        // remain as §120 deadline TASKS (SpeakerDeadlineSeeder) on /Speaker/Tasks;
        // travel is replaced by the country-gated "Submit travel reimbursement" task
        // (§143) for non-Denmark speakers. None of them is a guided wizard step.
        // §314 (operator 2026-07-24): "Help to promote your session(s)" (§116) left the
        // wizard the same way — the session graphics are not ready at approval time, so
        // it is now a dated speaker deadline (due 2027-01-15, SpeakerDeadlineSeeder)
        // linking to the Help Promote page (/Speaker/Graphics), not an onboarding step.

        // 8. Join Signal groups (§109) — speakers get the Speakers chat + broadcast.
        //     Manual mark-done (joining is external), tracked on a signal: task. Gated
        //     on the signal-groups config being present + Speaker being in scope.
        if (f.SignalInScope)
        {
            steps.Add(new("signal", "/Forms/Signal", f.SignalTaskDone));
        }

        // 9. Party sign-up (§164) — every speaker RSVPs Yes/No to the pre-day party.
        //    Done once the speaker has a saved Party RSVP row (stamped with their id).
        //    The matching party-form: task + reminder is seeded by PartyTaskSeeder.
        steps.Add(new("party", "/Party", f.PartyRsvped));

        // 10. Accept Code of Conduct + Privacy (§119) — all roles, always last. Done
        //    once the speaker has a persisted acceptance row (who/when).
        steps.Add(new("accept", "/Forms/Accept", f.PolicyAccepted));

        // 11. §400 — closing summary of the DATED work that lives outside this wizard (slide
        //     upload, travel invoice, promotion). Speakers carry the most of it, and none of it was
        //     visible at the point they felt finished. Always Done: it asks nothing, so it must
        //     never keep a completed wizard below 100%.
        // §410 — offer the deadlines step ONLY when this person actually has something outside Get
        // Started. §173e mirrors every wizard STEP with a task, so a role whose tasks are ALL
        // mirrors (attendee, media, event partner, most volunteers) would otherwise get a step
        // listing their own wizard back at them (operator 2026-07-27).
        if (f.HasOutsideWizardTask)
        {
            steps.Add(new("deadlines", "/Tasks", true));
        }

        return steps;
    }
}
