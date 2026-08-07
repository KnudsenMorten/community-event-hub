using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Forms;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>One step in a generic "Get started" wizard (REQUIREMENTS §43).</summary>
/// <param name="Key">Stable key → resx label/description (RoleWiz.Step.&lt;key&gt;[.Desc]).</param>
/// <param name="Route">The existing form/page this step opens (design A — pages untouched).</param>
/// <param name="Done">True when the participant has already completed this step (data exists).</param>
/// <param name="MissingFields">
/// §949 — WHICH field(s) would complete this step, when the step can name them. Empty/null means
/// "this step just has not been answered yet", which its description already says.
/// </param>
/// <remarks>
/// <para>🔴 <b>Why this exists</b> (operator 2026-08-07, reported TWICE): <i>"i still dont see that
/// 'User Profile' is completed in the get started wizard. i reported this earlier."</i> The rule was
/// right — his phone was blank — but the screen showed a filled-in name, a filled-in e-mail, and a
/// chip with no tick. The phone field was below the fold. <b>"9 of 10" is a scoreboard, not a
/// diagnosis</b>, and a correct answer with no reason attached costs as much time as a wrong one.</para>
///
/// <para>🔑 <b>FIELD KEYS, NOT SENTENCES.</b> These are stable identifiers the VIEW localises
/// (<c>RoleWiz.Field.&lt;key&gt;</c>), because this assembly has no business composing English —
/// the hub ships en + da-DK and a hardcoded sentence here would be untranslatable.</para>
/// </remarks>
public sealed record RoleWizardStep(
    string Key, string Route, bool Done, IReadOnlyList<string>? MissingFields = null);

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
    private readonly Core.Content.WelcomeCopyStore? _welcome;

    public RoleWizardService(
        CommunityHubDbContext db,
        SignalGroupsProvider? signal = null,
        // §680 — optional + last, the same pattern as _signal: unit tests that construct this
        // service directly keep compiling and simply get no welcome step; DI always supplies it.
        Core.Content.WelcomeCopyStore? welcome = null)
    {
        _db = db;
        _signal = signal;
        _welcome = welcome;
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

        // -1. §680 — the WELCOME step, first for every role that has copy (Volunteer / Media /
        //     Event Partner here; Organizer has none and gets none). Read-only: it thanks the
        //     person, introduces the event and names what they will find in the hub.
        //     Route = the wizard itself: the step has no standalone page to open.
        if (role is { } welcomeRole && _welcome?.Exists(welcomeRole) == true)
        {
            steps.Add(new(
                Core.Content.WelcomeCopyStore.StepKey, Core.Content.WelcomeCopyStore.StepRoute, true));
        }

        // 0. Profile — always, for every role. §945 (operator 2026-08-07): done = FULL NAME +
        //    EMAIL + PHONE are all filled in. The rule itself lives in ProfileCompletion because
        //    ProfileFormService.IsDoneAsync answers the same question elsewhere, and two copies of
        //    "am I finished?" is the §939 defect (a finished volunteer told 90%).
        //
        //    §949 — and when it is NOT done, say WHICH of the three is blank. One projection
        //    fetches the fields; completeness is then decided by the SAME shared rule
        //    (ProfileCompletion.IsCompleteFor), never by re-testing the fields here — re-deriving
        //    "am I complete?" beside the reason is exactly how §945's two copies drifted apart.
        var profile = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new { p.FullName, p.Email, p.Phone, p.Role })
            .FirstOrDefaultAsync(ct);

        var profileDone = profile is not null
            && ProfileCompletion.IsCompleteFor(
                profile.FullName, profile.Email, profile.Phone, profile.Role);

        List<string>? profileMissing = null;
        if (!profileDone)
        {
            profileMissing = new List<string>();
            if (string.IsNullOrWhiteSpace(profile?.FullName)) profileMissing.Add("FullName");
            if (string.IsNullOrWhiteSpace(profile?.Email)) profileMissing.Add("Email");
            // 🔒 §945a — only name a phone when this role actually needs one. Telling a speaker to
            // "add your phone number to finish this step" when phone is optional for them would be
            // the §949 defect inverted: a reason that is worse than no reason, because it sends
            // somebody to fill in a field that will not change their status.
            if (profile is not null
                && ProfileCompletion.PhoneRequiredFor(profile.Role)
                && string.IsNullOrWhiteSpace(profile.Phone)) profileMissing.Add("Phone");
        }
        steps.Add(new("profile", "/Profile", profileDone, profileMissing));

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

        // n+1b. Party sign-up (§164/§206) — the crew roles that get a tracked party task
        //       (Volunteer / Organizer / Event Partner / Media — §206 added Media) RSVP
        //       Yes/No to the pre-day party. Done once a Party RSVP row stamped with this
        //       participant exists; the party-form: task + reminder is seeded by PartyTaskSeeder.
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

        // n+3. §400 — the closing SUMMARY of everything with a deadline that lives OUTSIDE this
        //      wizard (slide uploads, travel invoice, promotion …). Placed last on purpose: the
        //      moment someone believes they are finished is exactly when they need to see what is
        //      still ahead of them.
        //
        //      Done: TRUE, always. It asks nothing and stores nothing, so it must never hold the
        //      progress bar below 100% nor be the step the wizard lands you on — you walk INTO it
        //      (AdvanceFrom moves in sequence, not to the first incomplete step) and can return via
        //      the rail, but it can never make a completed wizard look unfinished.
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
