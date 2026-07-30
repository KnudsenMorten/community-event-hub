using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>
/// §207/§208 — the ATTENDEE "Get started" wizard (main-menu stepper, like §161). Unlike the
/// crew wizard (<see cref="RoleWizardService"/>) an attendee's steps are driven by their TICKET
/// class, read live from the synced <see cref="Attendee"/> mirror:
/// <list type="bullet">
///   <item><b>2-day</b> (<see cref="TicketStatus.TwoDay"/>): TWO steps — Master Class selection
///   (<c>/Attendee</c>; done once a CONFIRMED Master Class signup exists) then Party signup
///   (<c>/Party</c>; done once a PartyRsvp row stamped with this participant exists).</item>
///   <item><b>1-day</b> (any other ticket): ONE step — Party signup.</item>
/// </list>
/// It returns the SAME <see cref="RoleWizardView"/> record the crew wizard uses, so the shared
/// <c>_WizardStepper</c> renders it identically (progress bar + editable Edit/Open cards). Pure
/// read over existing data — no new storage, no migration.
/// </summary>
public sealed class AttendeeWizardService
{
    private readonly CommunityHubDbContext _db;

    public AttendeeWizardService(CommunityHubDbContext db) => _db = db;

    public async Task<RoleWizardView> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var email = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => p.Email)
            .FirstOrDefaultAsync(ct);
        var norm = (email ?? string.Empty).Trim().ToLowerInvariant();

        var isTwoDay = !string.IsNullOrEmpty(norm) && await _db.Attendees.AnyAsync(
            a => a.EventId == eventId
                 && a.TicketStatus == TicketStatus.TwoDay
                 && a.Email.ToLower() == norm, ct);

        var steps = new List<RoleWizardStep>();

        // 1. Master Class selection — 2-day attendees only. Done once a CONFIRMED Master Class
        //    signup exists for the attendee row matching this participant's email.
        if (isTwoDay)
        {
            var mcDone = !string.IsNullOrEmpty(norm) && await _db.MasterClassSignups.AnyAsync(
                s => s.EventId == eventId
                     && s.Status == MasterClassSignupStatus.Confirmed
                     && s.Attendee.Email.ToLower() == norm, ct);
            // Razor page NAME (Url.Page) — "/Attendee" alone resolves to null (the page
            // is Pages/Attendee/Index.cshtml) and silently killed the step link + the
            // "Continue" CTA for 2-day attendees.
            steps.Add(new("masterclass", "/Attendee/Index", mcDone));

            // 1b. §384 — WAITLIST selection, its own step (operator 2026-07-26: "step 1 master class
            //     selection and step 2 is waitlist selection for only full classes"). Booking a seat
            //     and queueing for a different class are not mutually exclusive, but one radio group
            //     could only express one of them — so the two actions are now two steps.
            //
            //     Only offered when something is ACTUALLY full: an empty waitlist step in every
            //     attendee's wizard is noise. Counted here rather than in the handler because the
            //     step LIST is what the progress bar and "step X of N" are built from.
            var anyFull = await _db.Sessions
                .Where(s => s.EventId == eventId
                            && s.Type == SessionType.MasterClass
                            && s.MasterClassCapacity != null)
                .AnyAsync(s => _db.MasterClassSignups.Count(
                    x => x.EventId == eventId
                         && x.SessionId == s.Id
                         && x.Status == MasterClassSignupStatus.Confirmed) >= s.MasterClassCapacity, ct);

            if (anyFull)
            {
                // DONE = they already hold a waitlist place or a held offer. Deliberately NOT
                // "they answered": a waitlist place is optional, so the step must never be able to
                // block wizard completion — the form service returns Advance on an empty choice.
                var waitDone = !string.IsNullOrEmpty(norm) && await _db.MasterClassSignups.AnyAsync(
                    s => s.EventId == eventId
                         && (s.Status == MasterClassSignupStatus.Waitlisted
                             || s.Status == MasterClassSignupStatus.Offered)
                         && s.Attendee.Email.ToLower() == norm, ct);
                steps.Add(new("masterclass-waitlist", "/Attendee/Waitlist", waitDone));
            }
        }

        // 2. Party signup — every attendee. Done once a Party RSVP row (Yes or No) stamped with
        //    this participant exists.
        var partyDone = await _db.PartyRsvps.AnyAsync(
            r => r.EventId == eventId && r.ParticipantId == participantId, ct);
        steps.Add(new("party", "/Party", partyDone));

        // 3. §400 — closing summary of anything dated that lives outside this wizard. Attendees
        //    usually have little or none, and the step says so plainly rather than being hidden:
        //    "nothing outstanding" is itself the answer someone wants at the end of onboarding.
        //    Always Done — it asks nothing and must never keep the wizard below 100%.
        // §410 — offer the deadlines step ONLY when this person actually has something outside Get
        // Started. §173e mirrors every wizard STEP with a task, so a role whose tasks are ALL
        // mirrors (attendee, media, event partner, most volunteers) would otherwise get a step
        // listing their own wizard back at them (operator 2026-07-27).
        var hasOutside = await _db.Tasks.AsNoTracking()
            .Where(t => t.EventId == eventId && t.AssignedParticipantId == participantId)
            .Select(t => t.SourceKey)
            .ToListAsync(ct);
        if (hasOutside.Any(CommunityHub.Core.Participants.OutsideWizardTasks.IsOutsideWizard))
        {
            steps.Add(new("deadlines", "/Tasks", true));
        }

        return new RoleWizardView(steps);
    }
}
