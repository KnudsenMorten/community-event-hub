using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CommunityHub.Core.Forms;
using CommunityHub.Core.Tasks.Definitions;

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
    private readonly ILogger<WizardStepTaskSeeder>? _log;

    public WizardStepTaskSeeder(
        CommunityHubDbContext db,
        SpeakerWizardService speakerWizard,
        RoleWizardService roleWizard,
        TimeProvider clock,
        ILogger<WizardStepTaskSeeder>? log = null)
    {
        _db = db;
        _speakerWizard = speakerWizard;
        _roleWizard = roleWizard;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// §708.13 — this participant's audience FACTS, read from the same sources the wizard reads.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Facts, not decisions.</b> This gathers what is TRUE about the person (entitled to
    /// a hotel room, in scope for Signal); the registry decides what that entitles them TO. That
    /// split is the whole point of §684.5 constraint 1 — before it, "who gets the swag task" was an
    /// <c>if</c> in the wizard service, a dedup rule here and a role check in a third seeder, and
    /// §708.3 showed what a rule stated in three places costs.</para>
    ///
    /// <para>🔒 <b>Read from <see cref="FormEntitlementGate"/>, which is what the FORMS themselves
    /// use.</b> Not re-derived: a second implementation of "is this person entitled" is how a task
    /// starts disagreeing with the form it points at.</para>
    /// </remarks>
    private async Task<TaskAudienceFacts> AudienceFactsAsync(
        int eventId, int participantId, ParticipantRole role,
        IReadOnlyList<Step> steps, CancellationToken ct)
    {
        var entitled = await FormEntitlementGate.EffectiveItemsAsync(_db, eventId, participantId, ct);
        var predicates = new List<TaskAudiencePredicate>();

        if (entitled.Contains(OrderItem.Hotel))
            predicates.Add(TaskAudiencePredicate.EntitledToHotel);
        if (entitled.Contains(OrderItem.AppreciationDinner))
            predicates.Add(TaskAudiencePredicate.EntitledToAppreciationDinner);
        // 🔒 The OR-pairs mirror the wizard's own gates EXACTLY (swag OR polo, pre-day OR main-day
        // lunch). Tightening either to a single item would withhold a task from someone who holds
        // only the other one — the §708 warning, repeated here because it is easy to "simplify".
        if (entitled.Contains(OrderItem.Swag) || entitled.Contains(OrderItem.Polo))
            predicates.Add(TaskAudiencePredicate.EntitledToSwag);
        if (entitled.Contains(OrderItem.LunchPreDay) || entitled.Contains(OrderItem.LunchMainDay))
            predicates.Add(TaskAudiencePredicate.EntitledToLunchPreDay);
        if (entitled.Contains(OrderItem.TravelReimbursement))
            predicates.Add(TaskAudiencePredicate.EntitledToTravelReimbursement);

        // §109 — Signal membership lives in CONFIG, and the WIZARD has already asked that config.
        //
        // 🔒 Read from the wizard's own steps, NOT by resolving SignalGroupsProvider again here. The
        // first attempt did the latter and three tests went red immediately: this seeder's provider
        // was null where the wizard's was not, so the gate withheld a task the wizard was offering.
        // That is precisely the bug this whole migration exists to stop — one rule, two readers,
        // quietly disagreeing. The wizard is the reader; this takes its answer.
        if (steps.Any(s => string.Equals(s.Key, "signal", StringComparison.Ordinal)))
            predicates.Add(TaskAudiencePredicate.IsSignalInScope);

        return TaskAudienceFacts.For(role, predicates.ToArray());
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

        // Load this participant's already-tagged tasks once so the ensure is idempotent without a
        // query per step (tracked — the backfills and the de-dup below mutate these rows).
        var allRows = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.SourceKey != null)
            .ToListAsync(ct);

        // 🔴 §708.12 — DE-DUPLICATE, don't just survive it. A SourceKey IS the idempotency key: two
        // rows sharing one for the same participant is never correct, it is a lost race between two
        // concurrent page loads (this seeder runs on EVERY hub and task page load, and the database
        // has no unique index to stop it). Observed on DEV: profile:1, accept:1 and party-form:1
        // each ×3 for participant 1, rendering the SAME task three times on /Tasks.
        //
        // 🔒 WHICH ROW SURVIVES MATTERS. Keep a DONE row over an open one — completion is the only
        // state here that cannot be reconstructed, and deleting it would re-open a task the person
        // has already finished and start chasing them for it again. Among equals, keep the oldest
        // (lowest Id): it owns the history, and any reminder ledger already points at it.
        var deduped = new Dictionary<string, ParticipantTask>(StringComparer.Ordinal);
        var duplicates = new List<ParticipantTask>();
        foreach (var group in allRows.GroupBy(t => t.SourceKey!, StringComparer.Ordinal))
        {
            var keep = group
                .OrderByDescending(t => t.State == TaskState.Done)
                .ThenBy(t => t.Id)
                .First();
            deduped[group.Key] = keep;
            duplicates.AddRange(group.Where(t => t.Id != keep.Id));
        }

        if (duplicates.Count > 0)
        {
            _db.Tasks.RemoveRange(duplicates);
            _log?.LogWarning(
                "Removed {Count} duplicate task row(s) for participant {Pid} — keys: {Keys}. A "
                + "duplicate SourceKey is a lost race between two concurrent seeds.",
                duplicates.Count, participantId,
                string.Join(", ", duplicates.Select(d => d.SourceKey).Distinct()));
        }

        var existingTasks = deduped;
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

        // §708.13 — what the REGISTRY owns, and what it allows THIS participant.
        var facts = await AudienceFactsAsync(eventId, participantId, role, steps, ct);
        // ⚠️ ONLY for the four GENERIC roles. ParticipantTaskDefinitions describes THEM; a SPEAKER
        // matches none of it, so applying the gate to a speaker made `allowed` empty and withheld
        // their profile, Code-of-Conduct and Signal tasks outright. Two tests caught it immediately.
        // A speaker's own tasks are the speakerdl: set, gated by SpeakerDeadlineSeeder.
        var registryStepKeys = ParticipantTaskDefinitions.GenericRoles.Contains(role)
            ? ParticipantTaskDefinitions.All.Select(StepKeyOf).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var allowedStepKeys = TaskDefinitionRegistry.Shipped.For(facts)
            .Where(d => d.Key.StartsWith("participant.", StringComparison.Ordinal))
            .Select(StepKeyOf).ToHashSet(StringComparer.Ordinal);

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

            // 🔒 §708.13 — THE AUDIENCE GATE, ENFORCED AT THE DEFINITION.
            //
            // Until now the definitions' Requires/Excludes were true only in the audience-matrix
            // tests: RoleWizardService's `if`s decided who actually got a row. This is the step that
            // makes the declared rule the OPERATING rule.
            //
            // ⚠️ Scoped to steps the registry actually owns. A step with no definition (travel,
            // speaker details, calendar, party) keeps its existing behaviour EXACTLY — this must not
            // become a silent rewrite of who gets what. Where both apply the two agree today, which
            // is why enabling this changes nothing visible; the value is that there is now ONE place
            // the rule lives, so the next change cannot be made in one of three and missed in two.
            //
            // A step that is registry-owned but NOT allowed is simply not added to `desired`, so the
            // prune below removes any row a person should no longer hold.
            if (registryStepKeys.Contains(step.Key) && !allowedStepKeys.Contains(step.Key))
            {
                continue;
            }

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
                existingTasks.TryGetValue(sourceKey, out var row);

                // BACKFILL: rows this seeder created undated (pre-fix) pre-empted the form
                // services' dated creation forever — stamp the deadline they should carry.
                if (dueDaysBeforeStart is int backfillDays
                    && row is not null
                    && row.DueDate is null
                    && startDate is not null)
                {
                    row.DueDate = startDate.Value.AddDays(-backfillDays);
                    created++;   // count as a change so SaveChanges runs
                }

                // §708.10 BACKFILL: every row this seeder owns must carry its step key, or the
                // shared /Tasks page cannot embed the form for a row seeded before §708.10 —
                // and an un-embedded row is exactly the "link out to a form" this replaces.
                if (row is not null && !string.Equals(row.FormStepKey, step.Key, StringComparison.Ordinal))
                {
                    row.FormStepKey = step.Key;
                    created++;
                }

                // §708.11 / §684.14 — a MIGRATED row stores NO rendered prose. Clearing it is what
                // makes the body file the single source of the copy; leaving both would show the
                // authored body AND the old description, which still carries the now-redundant
                // "Open the … form" button §708.2a says must not sit above an embedded form.
                if (row is not null
                    && !string.IsNullOrEmpty(row.Description)
                    && IsMigratedTitle(title))
                {
                    row.Description = null;
                    created++;
                }
                continue;  // idempotent
            }

            var done = step.Done;
            _db.Tasks.Add(new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = title,
                // §708.11 — null for a migrated step: its prose lives in its body file.
                Description = IsMigratedTitle(title) ? null : description,
                // Logistics steps carry the form service's own deadline; onboarding
                // steps (profile, accept, signal, …) are genuinely undated.
                DueDate = dueDaysBeforeStart is int days ? startDate?.AddDays(-days) : null,
                State = done ? TaskState.Done : TaskState.Open,
                CompletedAt = done ? now : null,
                IsMandatory = false,
                SourceKey = sourceKey,
                // §708.10 — the step this row mirrors, stated by the seeder that ALREADY HOLDS IT
                // rather than parsed back out of the SourceKey later. §708.4's lesson exactly: a
                // destination derived from a string is a destination that breaks on a rename.
                FormStepKey = step.Key,
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

        if (created > 0 || pruned > 0 || duplicates.Count > 0) await _db.SaveChangesAsync(ct);
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
    /// <summary>
    /// §708.11 — is this step's task now REGISTRY-BACKED, i.e. does a definition own its prose?
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Asked by TITLE, because that is the join <c>TaskBodyService.DefinitionFor</c>
    /// itself uses.</b> Deciding by step key instead would let the two disagree: a step could be
    /// stripped of its description here while the body service failed to match it to a definition,
    /// leaving a task with no prose from EITHER source — a blank task, on a green build. Asking the
    /// registry the same question it will be asked at render time makes that impossible.</para>
    ///
    /// <para>⚠️ Travel is deliberately absent from the registry (its title collides with the speaker
    /// definition's), so it answers false here and keeps its stored description — see
    /// <see cref="Core.Tasks.Definitions.ParticipantTaskDefinitions"/>.</para>
    /// </remarks>
    /// <summary>
    /// §708.13 — the WIZARD STEP KEY a participant definition governs.
    /// </summary>
    /// <remarks>
    /// Taken from <c>TaskCompletion.Form(stepKey)</c> where there is one — that field already IS the
    /// step key, and reusing it means the gate and the completion rule can never name different
    /// steps. Signal completes manually (joining happens outside the hub), so it falls back to the
    /// definition key's suffix, which is the same word by construction.
    /// </remarks>
    private static string StepKeyOf(TaskDefinition definition) =>
        definition.Completion is Core.Tasks.Definitions.TaskCompletion.Form form
            ? form.StepKey
            : definition.Key.Split('.')[^1];

    private static bool IsMigratedTitle(string title) =>
        Core.Tasks.Definitions.ParticipantTaskDefinitions.All.Any(
            d => string.Equals(d.Title, title, StringComparison.Ordinal));

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
            // 🔒 §733.1 — NO DUE DATE ON A WIZARD MIRROR. Operator 2026-07-31: *"get started wizard
            // gets remindes every 14 days. each of the entries dont have due dates. tasks lives
            // outside of this with due dates"*.
            //
            // These rows mirror a GET-STARTED STEP (§173e). §684.25 already settled that Get Started
            // and tasks are two different things; this applies that to reminders. The whole wizard
            // is chased ONCE, every 14 days, by `getstarted-digest` — which links to the step — so a
            // per-entry due date only produced a SECOND, per-row chase for the same work.
            //
            // 🔑 The due date IS the lever: `TaskReminderBuilder` selects `DueDate != null`, so
            // dropping it removes these from the per-task cadence by construction, with no second
            // rule to keep in step with this one. (§717 is what the old dates cost — speakers and
            // sponsors chased about a party "task still open" they experience as a wizard step.)
            //
            // ⚠️ `speakerdl:` and `sponsor:` are NOT touched: those are the genuinely-outside tasks
            // his model says SHOULD stay dated, and they are seeded elsewhere.
            "availability" => (WizardStepTaskKeys.Availability(pid), "Complete your day availability",
                "Tell us which days and times you can help so we can schedule you fairly.\n\n" + Open("Open the availability step"), false, null),
            "hotel"    => ($"{HotelFormService.HotelTaskKey}:{pid}", "Complete the Hotel form",
                "Tell us whether you need a room and your check-in / check-out dates.\n\n" + Open("Open the Hotel form"), speaker, null),
            "dinner"   => ($"{DinnerFormService.DinnerTaskKey}:{pid}", "Complete the Appreciation Dinner RSVP",
                "RSVP for the appreciation dinner and any dietary needs.\n\n" + Open("Open the Dinner form"), speaker, null),
            "lunch"    => ($"{LunchFormService.LunchTaskKey}:{pid}", "Complete the Lunch logistics form",
                "Choose your lunch and let us know any dietary requirements.\n\n" + Open("Open the Lunch form"), speaker, null),
            "swag"     => ($"{SwagFormService.SwagTaskKey}:{pid}", "Complete the Swag preferences form",
                "Pick your shirt size and gift preferences.\n\n" + Open("Open the Swag form"), speaker, null),
            "travel"   => ($"{TravelFormService.SubmitInvoiceTaskKey}:{pid}", "Submit travel reimbursement",
                "Submit your travel details for reimbursement.\n\n" + Open("Open the Travel form"), speaker, null),

            // --- owned by another seeder -----------------------------------------
            "party"    => null,                          // Core.Config.PartyTaskSeeder owns party-form:
            _ => null,
        };
    }
}
