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
/// only the steps a speaker is entitled to (Speaker Details is always shown), in a
/// fixed guided order, and detects completion from each form's persisted data —
/// reusing the existing form pages + save logic untouched (lowest risk, resumable).
/// </summary>
public sealed class SpeakerWizardService
{
    private readonly CommunityHubDbContext _db;

    public SpeakerWizardService(CommunityHubDbContext db) => _db = db;

    public async Task<SpeakerWizardView> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var entitled = await FormEntitlementGate.EffectiveItemsAsync(_db, eventId, participantId, ct);
        var steps = new List<SpeakerWizardStep>();

        // 1. Speaker Details — always (the speaker's own profile). Done = a profile
        //    row exists with a non-blank biography (the meaningful "filled it in").
        var detailsDone = await _db.SpeakerProfiles.AnyAsync(
            p => p.EventId == eventId && p.ParticipantId == participantId
                 && p.Biography != null && p.Biography != "", ct);
        steps.Add(new("details", "/Speaker/Details", detailsDone));

        // 2. Hotel — when entitled to a room.
        if (entitled.Contains(OrderItem.Hotel))
        {
            var done = await _db.HotelBookings.AnyAsync(
                h => h.EventId == eventId && h.ParticipantId == participantId, ct);
            steps.Add(new("hotel", "/Forms/Hotel", done));
        }

        // 3. Appreciation dinner — when entitled to a seat.
        if (entitled.Contains(OrderItem.AppreciationDinner))
        {
            var done = await _db.DinnerSignups.AnyAsync(
                d => d.EventId == eventId && d.ParticipantId == participantId, ct);
            steps.Add(new("dinner", "/Forms/Dinner", done));
        }

        // 4. Lunch — when entitled to either day.
        if (entitled.Contains(OrderItem.LunchPreDay) || entitled.Contains(OrderItem.LunchMainDay))
        {
            var done = await _db.LunchSignups.AnyAsync(
                l => l.EventId == eventId && l.ParticipantId == participantId, ct);
            steps.Add(new("lunch", "/Forms/Lunch", done));
        }

        // 5. Swag / gift — when entitled to swag or a polo.
        if (entitled.Contains(OrderItem.Swag) || entitled.Contains(OrderItem.Polo))
        {
            var done = await _db.SwagPreferences.AnyAsync(
                s => s.EventId == eventId && s.ParticipantId == participantId, ct);
            steps.Add(new("swag", "/Forms/Swag", done));
        }

        // 6. Travel reimbursement — when entitled.
        if (entitled.Contains(OrderItem.TravelReimbursement))
        {
            var done = await _db.TravelReimbursements.AnyAsync(
                t => t.EventId == eventId && t.ParticipantId == participantId, ct);
            steps.Add(new("travel", "/Forms/Travel", done));
        }

        return new SpeakerWizardView(steps);
    }
}
