using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>
/// §173e — make My-Tasks MIRROR the Get-Started journey for every per-participant role.
/// For EVERY step a role's Get-Started wizard shows, this ensures a matching
/// <see cref="ParticipantTask"/> EXISTS (idempotent, per participant), reusing the SAME
/// SourceKeys + deep-links the rest of the hub already uses, so the step's card and its
/// My-Tasks row tell one story. The task's done-state is then kept in sync — both ways —
/// by <see cref="FormTaskReconciler"/> (step done ⇒ task Done; step un-answered ⇒ reopen),
/// which the checklist builder runs on every surface.
///
/// <para>It drives off the REAL wizard services (<see cref="SpeakerWizardService"/> /
/// <see cref="RoleWizardService"/>) so the task set can never drift from the cards — same
/// entitlement gating, same order, same routes. Dedup rules keep it to ONE task per step:</para>
/// <list type="bullet">
///   <item>Logistics steps (hotel / dinner / lunch / swag / travel) for a SPEAKER are owned by
///   the <c>speakerdl:</c> deadline tasks (<see cref="Core.Config.SpeakerDeadlineSeeder"/>) — not
///   re-created here. For the generic roles they map to the per-form auto-tasks
///   (<c>hotel-form:</c> …) so the same row the form page would create shows up proactively.</item>
///   <item>The Party step is owned by <see cref="Core.Config.PartyTaskSeeder"/> (<c>party-form:</c>)
///   — skipped here so its reminder due-date isn't clobbered.</item>
///   <item>Signal (§109) + Promote (§116) reuse their existing <c>signal:</c> / <c>promote:</c>
///   manual mark-done tasks.</item>
///   <item>The four steps that had NO task before §173e — Calendar email, Speaker details,
///   Profile, Code of Conduct — get NEW tasks keyed by <see cref="WizardStepTaskKeys"/>.</item>
/// </list>
/// Idempotent: re-runs create nothing (keyed on SourceKey). Safe to call on every hub /
/// tasks page-load.
/// </summary>
public sealed class WizardStepTaskSeeder
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerWizardService _speakerWizard;
    private readonly RoleWizardService _roleWizard;
    private readonly TimeProvider _clock;

    public WizardStepTaskSeeder(
        CommunityHubDbContext db,
        SpeakerWizardService speakerWizard,
        RoleWizardService roleWizard,
        TimeProvider clock)
    {
        _db = db;
        _speakerWizard = speakerWizard;
        _roleWizard = roleWizard;
        _clock = clock;
    }

    /// <summary>The roles whose Get-Started steps this seeder mirrors into tasks — the
    /// per-participant roles. Sponsors are excluded (their Get-Started steps are
    /// COMPANY-scoped sections of the Company Details page, already surfaced through the
    /// sponsor deliverables/company tasks, not per-participant); attendees have no
    /// Get-Started wizard (their one tracked item, the party, is seeded by
    /// <see cref="Core.Config.PartyTaskSeeder"/>).</summary>
    public static bool Handles(ParticipantRole role) =>
        role == ParticipantRole.Speaker || RoleWizardService.Handles(role);

    /// <summary>One step flattened from either wizard view (same shape, different record types).</summary>
    private readonly record struct Step(string Key, string Route, bool Done);

    /// <summary>
    /// Ensure a task exists for every Get-Started step this participant has. No-op for a
    /// role without a per-participant wizard. Returns the count created.
    /// </summary>
    public async Task<int> EnsureForParticipantAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        var steps = await BuildStepsAsync(eventId, participantId, role, ct);
        if (steps.Count == 0) return 0;

        // Load this participant's already-tagged tasks once so the ensure is idempotent
        // without a query per step (tracked — the due-date backfill below may update rows).
        var existingTasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.SourceKey != null)
            .ToDictionaryAsync(t => t.SourceKey!, t => t, StringComparer.Ordinal, ct);
        var existing = existingTasks.Keys.ToHashSet(StringComparer.Ordinal);

        // §234 (7c): a SPEAKER's logistics steps are owned by the speakerdl: deadline tasks
        // (SpeakerDeadlineSeeder) — this seeder never creates them, whether or not the
        // speakerdl rows exist yet (see the unconditional skip in the loop below).

        var now = _clock.GetUtcNow();
        var created = 0;

        // §253 (G17 hygiene): the SourceKeys the participant's CURRENT wizard wants.
        // Steps this seeder does not own (party) and a speaker's logistics (owned by
        // speakerdl:, §234 7c) are excluded — anything seeder-owned that is no longer
        // wanted (an entitlement override-exclude shrank the wizard) is pruned below.
        var desired = new HashSet<string>(StringComparer.Ordinal);

        // The LOGISTICS steps reuse the per-form auto-task SourceKeys, and those form
        // services create their task DATED (Event.StartDate − N days). Because this seeder
        // usually runs FIRST (hub page-load), it must stamp the SAME due date — an undated
        // row here would pre-empt the dated one forever (no overdue badge, no Add-Reminder,
        // no TaskReminderBuilder email). The pure onboarding steps stay undated.
        var startDate = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate)
            .FirstOrDefaultAsync(ct);
        if (startDate == default(DateOnly)) startDate = null;   // unset date ⇒ undated tasks

        // §234 (7b): a speaker/entitled role who deliberately OPTED OUT of travel
        // reimbursement (a TravelReimbursement row with RequestReimbursement = false) has
        // completed that decision — never re-create the travel task they closed/deleted.
        // Computed lazily (one query, only when a travel step is present).
        bool? travelOptedOut = null;

        foreach (var step in steps)
        {
            var spec = MapStep(step.Key, participantId, role, step.Route);
            if (spec is null) continue;                  // step owned elsewhere (party) — skip
            var (sourceKey, title, description, isSpeakerLogistics, dueDaysBeforeStart) = spec.Value;

            // §234 (7c): SPEAKER logistics steps are owned by the speakerdl: deadline tasks
            // (SpeakerDeadlineSeeder) — skip them here UNCONDITIONALLY. Seeding the
            // form-owned key (hotel-form: …) before the speakerdl rows existed produced a
            // DUPLICATE pair once SpeakerDeadlineSeeder ran; when the speakerdl rows exist
            // the old dedup already skipped, so the only correct behaviour is: never seed
            // speaker logistics from this seeder at all.
            if (isSpeakerLogistics) continue;

            desired.Add(sourceKey);

            // §234 (7b): don't resurrect the travel task after a deliberate opt-out.
            if (step.Key == "travel")
            {
                travelOptedOut ??= await _db.TravelReimbursements.AnyAsync(
                    t => t.EventId == eventId && t.ParticipantId == participantId
                         && !t.RequestReimbursement, ct);
                if (travelOptedOut.Value && !existing.Contains(sourceKey)) continue;
            }

            if (existing.Contains(sourceKey))
            {
                // BACKFILL: rows this seeder created undated (pre-fix) pre-empted the form
                // services' dated creation forever — stamp the deadline they should carry.
                if (dueDaysBeforeStart is int backfillDays
                    && existingTasks.TryGetValue(sourceKey, out var row)
                    && row.DueDate is null
                    && startDate is not null)
                {
                    row.DueDate = startDate.Value.AddDays(-backfillDays);
                    created++;   // count as a change so SaveChanges runs
                }
                continue;  // idempotent
            }

            var done = step.Done;
            _db.Tasks.Add(new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = title,
                Description = description,
                // Logistics steps carry the form service's own deadline; onboarding
                // steps (profile, accept, signal, …) are genuinely undated.
                DueDate = dueDaysBeforeStart is int days ? startDate?.AddDays(-days) : null,
                State = done ? TaskState.Done : TaskState.Open,
                CompletedAt = done ? now : null,
                IsMandatory = false,
                SourceKey = sourceKey,
                CreatedAt = now,
            });
            existing.Add(sourceKey);
            created++;
        }

        // §253 (G17 hygiene): PRUNE seeder-owned tasks whose step vanished from the
        // wizard — an entitlement override-exclude used to leave the old task Open
        // forever (this seeder was creation-only). Only OPEN rows go: a completed
        // form's Done task keeps its audit trail. Role changes are handled by
        // RoleChangeTaskReconciler; this catches same-role entitlement shrink.
        var pruned = 0;
        foreach (var (key, row) in existingTasks)
        {
            if (row.State == TaskState.Done) continue;
            if (!IsSeederOwnedKey(key, participantId)) continue;
            if (desired.Contains(key)) continue;
            _db.Tasks.Remove(row);
            pruned++;
        }

        if (created > 0 || pruned > 0) await _db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>The keys THIS seeder is allowed to prune — exactly the keys
    /// <see cref="MapStep"/> can produce. Keys owned by other seeders
    /// (<c>party-form:</c>, <c>masterclass-form:</c>, <c>speakerdl:</c>) are never
    /// touched here.</summary>
    private static bool IsSeederOwnedKey(string key, int pid) =>
        key == WizardStepTaskKeys.Calendar(pid)
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

    private async Task<IReadOnlyList<Step>> BuildStepsAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        if (role == ParticipantRole.Speaker)
        {
            var v = await _speakerWizard.BuildAsync(eventId, participantId, ct);
            return v.Steps.Select(s => new Step(s.Key, s.Route, s.Done)).ToList();
        }
        if (RoleWizardService.Handles(role))
        {
            var v = await _roleWizard.BuildAsync(eventId, participantId, ct);
            return v.Steps.Select(s => new Step(s.Key, s.Route, s.Done)).ToList();
        }
        return System.Array.Empty<Step>();
    }

    /// <summary>
    /// Map a wizard step key to the task it should ensure: its stable SourceKey (reusing the
    /// existing scheme), the human title (matching the step's Get-Started card / the existing
    /// form-task wording), and whether it is a SPEAKER logistics step (owned by a speakerdl
    /// deadline, so skipped when one exists). Returns null for steps owned by another seeder
    /// (the party step).
    /// </summary>
    private static (string SourceKey, string Title, string Description, bool IsSpeakerLogistics, int? DueDaysBeforeStart)? MapStep(
        string key, int pid, ParticipantRole role, string route)
    {
        var speaker = role == ParticipantRole.Speaker;
        // Markdown "Open" button (TaskTextLinkifier renders [label](url) as a button) so the
        // task row links to the SAME page the Get-Started card opens.
        string Open(string label) => $"[{label}]({route})";
        return key switch
        {
            // --- the data-signal steps that had no task before §173e -------------
            // §313 (operator 2026-07-24): the Calendar-email step is OPTIONAL — it must
            // NEVER surface as a pending task. The wizard step stays; no task is seeded
            // (and, via IsSeederOwnedKey + the prune loop, any existing open one goes).
            "calendar" => null,
            "details"  => (WizardStepTaskKeys.SpeakerDetails(pid), "Speaker details",
                "Add your bio, photo, tagline, links and session preferences.\n\n" + Open("Open Speaker details"), false, null),
            "profile"  => (WizardStepTaskKeys.Profile(pid), "Your profile",
                "Check your name and add a phone number so we can reach you.\n\n" + Open("Open your profile"), false, null),
            "accept"   => (WizardStepTaskKeys.Accept(pid), "Code of Conduct & Privacy",
                "Review and accept our Code of Conduct and Privacy Policy.\n\n" + Open("Review & accept"), false, null),

            // --- manual mark-done steps (reuse existing keys + their existing wording) ------
            // §314: "promote" is no longer a wizard step — Help Promote is a dated speaker
            // deadline (SpeakerDeadlineSeeder, due 2027-01-15). Promote stays in
            // IsSeederOwnedKey so leftover open promote: rows are pruned.
            "signal"   => (WizardStepTasks.Signal(pid), "Join Signal groups",
                "Join the ELDK27 Signal group(s), then mark this done.\n\n" + Open("Open the Signal step"), false, null),

            // --- logistics steps (reuse the per-form auto-task keys + titles + DUE DATES:
            //     the same StartDate − N offsets the owning form services stamp, so it
            //     doesn't matter which side seeds the row first) -----------------------
            // §234 (7a): the per-day availability step gets its OWN key (availability:{pid});
            // it previously reused volunteer-form:{pid}, which belongs to the SHIFTS wizard —
            // cross-wiring two different live forms' tasks/completion signals.
            "availability" => (WizardStepTaskKeys.Availability(pid), "Complete your day availability",
                "Tell us which days and times you can help so we can schedule you fairly.\n\n" + Open("Open the availability step"), false, 30),
            "hotel"    => ($"{HotelFormService.HotelTaskKey}:{pid}", "Complete the Hotel form",
                "Tell us whether you need a room and your check-in / check-out dates.\n\n" + Open("Open the Hotel form"), speaker, 30),
            "dinner"   => ($"{DinnerFormService.DinnerTaskKey}:{pid}", "Complete the Appreciation Dinner form",
                "RSVP for the appreciation dinner and any dietary needs.\n\n" + Open("Open the Dinner form"), speaker, 21),
            "lunch"    => ($"{LunchFormService.LunchTaskKey}:{pid}", "Complete the Lunch form",
                "Choose your lunch and let us know any dietary requirements.\n\n" + Open("Open the Lunch form"), speaker, 21),
            "swag"     => ($"{SwagFormService.SwagTaskKey}:{pid}", "Complete the Swag form",
                "Pick your shirt size and gift preferences.\n\n" + Open("Open the Swag form"), speaker, 21),
            "travel"   => ($"{TravelFormService.SubmitInvoiceTaskKey}:{pid}", "Submit travel reimbursement",
                "Submit your travel details for reimbursement.\n\n" + Open("Open the Travel form"), speaker, 30),

            // --- owned by another seeder -----------------------------------------
            "party"    => null,                          // Core.Config.PartyTaskSeeder owns party-form:
            _ => null,
        };
    }
}
