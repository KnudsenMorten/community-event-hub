using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>One step in a generic "Get started" wizard (REQUIREMENTS §43).</summary>
/// <param name="Key">Stable key → resx label/description (RoleWiz.Step.&lt;key&gt;[.Desc]).</param>
/// <param name="Route">The existing form/page this step opens (design A — pages untouched).</param>
/// <param name="Done">True when the participant has already completed this step (data exists).</param>
public sealed record RoleWizardStep(string Key, string Route, bool Done);

/// <summary>
/// A role's "Get started" progress (REQUIREMENTS §43), mirroring
/// <see cref="SpeakerWizardView"/>. An ordered list of the steps the participant is
/// ENTITLED to (§44a — gated by role + entitlement), each with done/not-done read
/// from existing hub data (§44b — a saved value = done), plus derived progress. A
/// resumable SHELL over the existing pages: it reads current data each time
/// (stateless), so a refresh / re-entry is always correct.
/// </summary>
public sealed record RoleWizardView(IReadOnlyList<RoleWizardStep> Steps)
{
    public int EntitledCount => Steps.Count;
    public int DoneCount => Steps.Count(s => s.Done);
    public bool AllDone => EntitledCount > 0 && DoneCount >= EntitledCount;
    public int Percent => EntitledCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / EntitledCount);

    /// <summary>The next incomplete step (the "Continue" target), or null when all done.</summary>
    public RoleWizardStep? NextStep => Steps.FirstOrDefault(s => !s.Done);

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
/// Builds the generic "Get started" wizard (REQUIREMENTS §43) for the roles that do
/// NOT already have a bespoke wizard — Volunteer, Organizer, Media, EventPartner
/// (speakers use <see cref="SpeakerWizardService"/> §28, sponsors use
/// <see cref="SponsorWizardService"/> §32). It mirrors the speaker pattern exactly:
/// a SHELL over the existing pages, reusing the SAME entitlement model
/// (<see cref="FormEntitlementGate"/>) so a step appears ONLY when the participant
/// is entitled to it (§44a), and detecting completion from each page's persisted
/// data (§44b — a saved row = done). No new storage, no migration.
///
/// <para>Per-role step plan (each logistics step is entitlement-gated, so a
/// participant only ever sees the items their role + overrides grant — e.g. a
/// volunteer who is also a supported speaker gets Hotel/Travel/Swag via the speaker
/// hat, while a plain volunteer does not):</para>
/// <list type="bullet">
///   <item>ALL roles: Profile (name + phone) first — the universal "tell us how to
///   reach you" step.</item>
///   <item>Volunteer: + Availability (per-day), then the entitlement logistics
///   (Dinner / Lunch / Swag — and Hotel/Travel only if entitled).</item>
///   <item>Organizer / Media / EventPartner: the entitlement logistics
///   (Hotel / Dinner / Lunch / Swag / Travel) — exactly what their hats grant.</item>
/// </list>
/// </summary>
public sealed class RoleWizardService
{
    private readonly CommunityHubDbContext _db;
    private readonly SignalGroupsProvider? _signal;

    public RoleWizardService(CommunityHubDbContext db, SignalGroupsProvider? signal = null)
    {
        _db = db;
        _signal = signal;
    }

    /// <summary>The roles this generic wizard serves (the others have bespoke wizards).</summary>
    public static bool Handles(ParticipantRole role) => role is
        ParticipantRole.Volunteer or ParticipantRole.Organizer
        or ParticipantRole.Media or ParticipantRole.EventPartner;

    public async Task<RoleWizardView> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var role = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => (ParticipantRole?)p.Role)
            .FirstOrDefaultAsync(ct);

        var entitled = await FormEntitlementGate.EffectiveItemsAsync(_db, eventId, participantId, ct);
        var steps = new List<RoleWizardStep>();

        // 0. Profile — always, for every role. Done = the participant has filled in a
        //    phone number (the meaningful "I completed my contact basics"; name is
        //    pre-seeded from import, phone is the field people actually add here).
        var phoneDone = await _db.Participants.AnyAsync(
            p => p.Id == participantId && p.EventId == eventId
                 && p.Phone != null && p.Phone != "", ct);
        steps.Add(new("profile", "/Profile", phoneDone));

        // 1. Volunteer availability — volunteers only (their first scheduling input).
        //    Done = ≥1 saved per-day availability row.
        if (role == ParticipantRole.Volunteer)
        {
            var availDone = await _db.VolunteerDayAvailabilities.AnyAsync(
                a => a.EventId == eventId && a.ParticipantId == participantId, ct);
            steps.Add(new("availability", "/Volunteer/Availability", availDone));
        }

        // 2..n. Entitlement logistics — IDENTICAL data-backed completion + gating as
        //        the speaker wizard, so a person who wears several hats sees exactly
        //        the steps their effective entitlement set grants (§44a). Order:
        //        Hotel → Dinner → Lunch → Swag → Travel.
        if (entitled.Contains(OrderItem.Hotel))
        {
            var done = await _db.HotelBookings.AnyAsync(
                h => h.EventId == eventId && h.ParticipantId == participantId, ct);
            steps.Add(new("hotel", "/Forms/Hotel", done));
        }

        if (entitled.Contains(OrderItem.AppreciationDinner))
        {
            var done = await _db.DinnerSignups.AnyAsync(
                d => d.EventId == eventId && d.ParticipantId == participantId, ct);
            steps.Add(new("dinner", "/Forms/Dinner", done));
        }

        if (entitled.Contains(OrderItem.LunchPreDay) || entitled.Contains(OrderItem.LunchMainDay))
        {
            var done = await _db.LunchSignups.AnyAsync(
                l => l.EventId == eventId && l.ParticipantId == participantId, ct);
            steps.Add(new("lunch", "/Forms/Lunch", done));
        }

        if (entitled.Contains(OrderItem.Swag) || entitled.Contains(OrderItem.Polo))
        {
            var done = await _db.SwagPreferences.AnyAsync(
                s => s.EventId == eventId && s.ParticipantId == participantId, ct);
            steps.Add(new("swag", "/Forms/Swag", done));
        }

        if (entitled.Contains(OrderItem.TravelReimbursement))
        {
            var done = await _db.TravelReimbursements.AnyAsync(
                t => t.EventId == eventId && t.ParticipantId == participantId, ct);
            steps.Add(new("travel", "/Forms/Travel", done));
        }

        // n+1. Join Signal groups (§109) — only for roles in scope per the
        //      signal-groups config (Volunteers + Event Partners get chat+broadcast,
        //      Media gets broadcast only; Organizers are out of scope). Completion is
        //      a MANUAL mark-done (joining is external) tracked on a signal: task.
        if (role is { } r && _signal?.InScope(r) == true)
        {
            var done = await _db.Tasks.AnyAsync(
                t => t.EventId == eventId && t.AssignedParticipantId == participantId
                     && t.SourceKey == WizardStepTasks.Signal(participantId) && t.State == TaskState.Done, ct);
            steps.Add(new("signal", "/Forms/Signal", done));
        }

        // n+1b. Party sign-up (§164) — the staff roles that get a tracked party task
        //       (Volunteer / Organizer / Event Partner; NOT Media per the operator's
        //       list) RSVP Yes/No to the pre-day party. Done once a Party RSVP row
        //       stamped with this participant exists; the party-form: task + reminder is
        //       seeded by PartyTaskSeeder.
        if (role is { } pr && CommunityHub.Core.Config.PartyTaskSeeder.RoleGetsPartyTask(pr))
        {
            var partyDone = await _db.PartyRsvps.AnyAsync(
                r => r.EventId == eventId && r.ParticipantId == participantId, ct);
            steps.Add(new("party", "/Party", partyDone));
        }

        // n+2. Accept Code of Conduct + Privacy (§119) — ALL roles, always last. Done
        //      once the participant has a persisted acceptance row (who/when).
        var acceptDone = await _db.ParticipantPolicyAcceptances.AnyAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);
        steps.Add(new("accept", "/Forms/Accept", acceptDone));

        return new RoleWizardView(steps);
    }
}
