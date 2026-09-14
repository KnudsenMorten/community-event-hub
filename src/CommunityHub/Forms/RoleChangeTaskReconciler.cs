using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>
/// §253 G11 — an organizer ROLE CHANGE used to be a bare field write
/// (<c>p.Role = Role</c>): the OLD role's auto-seeded tasks (wizard-step mirrors +
/// dated <c>speakerdl:</c> deadlines) stayed Open forever — polluting My-Tasks and, for
/// an ex-speaker, still firing due-day reminders — while the NEW role's steps only
/// seeded lazily. This reconciler runs on the role-change save:
/// <list type="number">
///   <item>PRUNE: delete this participant's auto-seeded tasks whose owning step /
///   seeder does not apply to the NEW role (e.g. <c>speakerdl:</c> when no longer a
///   Speaker, <c>availability:</c> when no longer a Volunteer). Manual/organizer tasks
///   and company-scoped <c>sponsor:</c> tasks are never touched.</item>
///   <item>SEED: ensure the NEW role's wizard-step tasks exist
///   (<see cref="WizardStepTaskSeeder"/>) and sync their done-state from the data the
///   person already submitted (<see cref="FormTaskReconciler"/>).</item>
/// </list>
/// A new SPEAKER's <c>speakerdl:</c> deadline tasks are seeded by the existing
/// nightly/page-load <see cref="SpeakerDeadlineSeeder"/> pass (unchanged), and the
/// same seeder's ex-speaker sweep backstops role changes made outside this path.
/// Idempotent — re-running on an unchanged role does nothing.
/// </summary>
public sealed class RoleChangeTaskReconciler
{
    private readonly CommunityHubDbContext _db;
    private readonly WizardStepTaskSeeder _seeder;
    private readonly FormTaskReconciler _formReconciler;

    public RoleChangeTaskReconciler(
        CommunityHubDbContext db,
        WizardStepTaskSeeder seeder,
        FormTaskReconciler formReconciler)
    {
        _db = db;
        _seeder = seeder;
        _formReconciler = formReconciler;
    }

    /// <summary>
    /// Prune the tasks the NEW role does not have, then seed + reconcile the new
    /// role's steps. Call AFTER the role has been saved (the wizard services read
    /// the participant's persisted role/entitlements). Returns (removed, created).
    /// </summary>
    public async Task<(int Removed, int Created)> ReconcileAsync(
        int eventId, int participantId, ParticipantRole oldRole, ParticipantRole newRole,
        CancellationToken ct = default)
    {
        if (oldRole == newRole) return (0, 0);

        // --- 1. PRUNE the old role's auto-seeded tasks -----------------------
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.SourceKey != null)
            .ToListAsync(ct);

        var toRemove = tasks
            .Where(t => IsAutoSeededKey(t.SourceKey!, participantId)
                        && !KeyAppliesToRole(t.SourceKey!, participantId, newRole))
            .ToList();
        if (toRemove.Count > 0)
        {
            // 🔴 §1082 — RETIRED, NOT DELETED (operator 2026-08-13). A role change is the case where
            // deletion is most obviously wrong: the person may have COMPLETED several of the old
            // role's tasks, and erasing the rows erases that they did. Closing removes them from the
            // person's open list exactly as before, keeps the history, and is reversible if the role
            // was changed by mistake — the same shape as §502's leaver rule.
            var now = DateTimeOffset.UtcNow;
            foreach (var t in toRemove)
                CommunityHub.Core.Tasks.TaskClosure.Retire(
                    t, CommunityHub.Core.Domain.TaskClosedReason.RetiredFromCatalog, now);
            await _db.SaveChangesAsync(ct);
        }

        // --- 2. SEED the new role's wizard steps + sync done-state -----------
        var created = 0;
        if (WizardStepTaskSeeder.Handles(newRole))
        {
            created = await _seeder.EnsureForParticipantAsync(eventId, participantId, newRole, ct);
        }
        await _formReconciler.ReconcileAsync(eventId, participantId, ct);

        return (toRemove.Count, created);
    }

    /// <summary>
    /// True when <paramref name="key"/> is one of THIS participant's auto-seeded
    /// per-participant task keys (wizard-step mirrors, per-form auto-tasks, party /
    /// master-class seeds, speaker deadlines). Company-scoped <c>sponsor:</c> tasks and
    /// free-form organizer tasks are NOT auto-seeded and never pruned.
    /// </summary>
    internal static bool IsAutoSeededKey(string key, int pid) =>
        key.StartsWith($"speakerdl:{pid}:", StringComparison.Ordinal)
        || key == PartyTaskSeeder.SourceKeyFor(pid)
        || key == AttendeeMasterClassTaskSeeder.SourceKeyFor(pid)
        || key == WizardStepTaskKeys.Calendar(pid)
        || key == WizardStepTaskKeys.SpeakerDetails(pid)
        || key == WizardStepTaskKeys.Profile(pid)
        || key == WizardStepTaskKeys.Accept(pid)
        || key == WizardStepTaskKeys.Availability(pid)
        || key == WizardStepTasks.Signal(pid)
        || key == WizardStepTasks.Promote(pid)
        || key == $"{HotelFormService.HotelTaskKey}:{pid}"
        || key == $"{DinnerFormService.DinnerTaskKey}:{pid}"
        || key == $"{LunchFormService.LunchTaskKey}:{pid}"
        || key == $"{SwagFormService.SwagTaskKey}:{pid}"
        || key == $"{TravelFormService.SubmitInvoiceTaskKey}:{pid}";

    /// <summary>
    /// Whether an auto-seeded key still applies once the participant holds
    /// <paramref name="role"/>. Mirrors the owning seeders' role rules:
    /// <list type="bullet">
    ///   <item><c>party-form:</c> — every role gets the party task (PartyTaskSeeder).</item>
    ///   <item><c>speakerdl:</c> / <c>calendar:</c> / <c>speaker-details:</c> /
    ///   <c>promote:</c> — Speaker only.</item>
    ///   <item><c>masterclass-form:</c> — Attendee only.</item>
    ///   <item><c>availability:</c> — Volunteer only (§148).</item>
    ///   <item><c>profile:</c> — the generic-role wizard's roles.</item>
    ///   <item><c>accept:</c> / <c>signal:</c> — any wizard-bearing role.</item>
    ///   <item>per-form auto-tasks (<c>hotel-form:</c> …) — generic-role wizards only;
    ///   a SPEAKER's logistics are owned by <c>speakerdl:</c> (§234 7c), and
    ///   Attendee/Sponsor have no per-participant logistics forms.</item>
    /// </list>
    /// </summary>
    internal static bool KeyAppliesToRole(string key, int pid, ParticipantRole role)
    {
        if (key == PartyTaskSeeder.SourceKeyFor(pid)) return true;

        if (key.StartsWith($"speakerdl:{pid}:", StringComparison.Ordinal)
            || key == WizardStepTaskKeys.Calendar(pid)
            || key == WizardStepTaskKeys.SpeakerDetails(pid)
            || key == WizardStepTasks.Promote(pid))
            return role == ParticipantRole.Speaker;

        if (key == AttendeeMasterClassTaskSeeder.SourceKeyFor(pid))
            return role == ParticipantRole.Attendee;

        if (key == WizardStepTaskKeys.Availability(pid))
            return role == ParticipantRole.Volunteer;

        if (key == WizardStepTaskKeys.Profile(pid))
            return RoleWizardService.Handles(role);

        if (key == WizardStepTaskKeys.Accept(pid) || key == WizardStepTasks.Signal(pid))
            return role == ParticipantRole.Speaker || RoleWizardService.Handles(role);

        // Per-form auto-tasks: the generic-role wizards own them; the speaker's
        // logistics ride speakerdl: instead (avoiding the §234 7c duplicate pair).
        return RoleWizardService.Handles(role);
    }
}
