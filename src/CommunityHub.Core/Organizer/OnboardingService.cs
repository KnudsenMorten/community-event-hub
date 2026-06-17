using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Export;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The five mandatory onboarding wizard steps, each backed by a
/// <c>Participant.OnboardingCompleted_*</c> bit. The order is the wizard order.
/// </summary>
public enum OnboardingStep
{
    /// <summary>(a) Verify / update bio.</summary>
    Bio = 0,

    /// <summary>(b) Update / replace bio picture.</summary>
    Picture = 1,

    /// <summary>(c) Hotel form.</summary>
    Hotel = 2,

    /// <summary>(d) Appreciation form.</summary>
    Appreciation = 3,

    /// <summary>(e) Swag form.</summary>
    Swag = 4,
}

/// <summary>
/// The onboarding STAGE a person is at, for the organizer dashboard's
/// count-by-stage view. Derived (not stored) from the lifecycle state + the
/// persona's required-step progress:
///   <list type="bullet">
///   <item><b>Preselected</b> — lifecycle Preselected (shortlisted, not yet activated).</item>
///   <item><b>Invited</b> — activated (lifecycle Active) but zero required steps done.</item>
///   <item><b>InProgress</b> — some but not all required steps done.</item>
///   <item><b>Completed</b> — every required step for the persona done.</item>
///   </list>
/// (Lifecycle <c>Inactive</c> rows are still raw queue entries — not yet in the
/// onboarding pipeline — and are excluded from this dashboard.)
/// </summary>
public enum OnboardingStage
{
    Preselected = 0,
    Invited = 1,
    InProgress = 2,
    Completed = 3,
}

/// <summary>One participant's onboarding-completion line for the admin overview.</summary>
public sealed record OnboardingRow(
    int ParticipantId,
    string FullName,
    string Email,
    ParticipantRole Role,
    PersonaGroup Persona,
    OnboardingStage Stage,
    IReadOnlyList<OnboardingStep> RequiredSteps,
    bool Bio,
    bool Picture,
    bool Hotel,
    bool Appreciation,
    bool Swag)
{
    /// <summary>Is a step done (regardless of whether the persona requires it)?</summary>
    public bool DoneOf(OnboardingStep step) => step switch
    {
        OnboardingStep.Bio          => Bio,
        OnboardingStep.Picture      => Picture,
        OnboardingStep.Hotel        => Hotel,
        OnboardingStep.Appreciation => Appreciation,
        OnboardingStep.Swag         => Swag,
        _ => false,
    };

    /// <summary>Does this persona require the given step?</summary>
    public bool Requires(OnboardingStep step) => RequiredSteps.Contains(step);

    /// <summary>True when every REQUIRED step for the persona is done.</summary>
    public bool IsComplete => RequiredSteps.All(DoneOf);

    /// <summary>How many of the persona's REQUIRED steps are done.</summary>
    public int DoneCount => RequiredSteps.Count(DoneOf);

    /// <summary>Total required steps for the persona.</summary>
    public int RequiredCount => RequiredSteps.Count;

    /// <summary>Completion % over the persona's required steps.</summary>
    public int Percent =>
        RequiredCount == 0 ? 100 : (int)Math.Round(100.0 * DoneCount / RequiredCount);
}

/// <summary>
/// One line of the "who hasn't onboarded yet" export — a participant who is in
/// the onboarding pipeline but has NOT completed every step their persona
/// requires (stage Pre-selected / Invited / In-progress, i.e. NOT Completed).
/// Carries the per-persona progress plus the human list of the steps still
/// MISSING for that person, so an organizer can chase them down. A flat,
/// read-only projection — never a persisted entity.
/// </summary>
public sealed record PendingOnboardingRow(
    int ParticipantId,
    string FullName,
    string Email,
    ParticipantRole Role,
    PersonaGroup Persona,
    OnboardingStage Stage,
    int DoneCount,
    int RequiredCount,
    int Percent,
    IReadOnlyList<OnboardingStep> MissingSteps);

/// <summary>Per-step completion totals for the admin dashboard tiles.</summary>
public sealed record OnboardingStepStat(OnboardingStep Step, int Done, int Total)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
}

/// <summary>Count + completion-% for one onboarding stage in the dashboard.</summary>
public sealed record OnboardingStageStat(OnboardingStage Stage, int Count);

/// <summary>The onboarding admin overview snapshot for one edition.</summary>
public sealed class OnboardingOverview
{
    /// <summary>The persona filter this snapshot was built for (null = all personas).</summary>
    public PersonaGroup? PersonaFilter { get; set; }

    public int TotalParticipants { get; set; }
    public int FullyOnboarded { get; set; }

    /// <summary>Overall completion % across the (filtered) participants.</summary>
    public int OverallPercent =>
        TotalParticipants == 0
            ? 0
            : (int)Math.Round(100.0 * FullyOnboarded / TotalParticipants);

    /// <summary>Count by onboarding stage (Preselected / Invited / In-progress / Completed).</summary>
    public List<OnboardingStageStat> StageStats { get; set; } = new();

    public List<OnboardingStepStat> StepStats { get; set; } = new();
    public List<OnboardingRow> Rows { get; set; } = new();

    /// <summary>Rows in a given stage (for the dashboard's by-stage lists).</summary>
    public IEnumerable<OnboardingRow> InStage(OnboardingStage stage) =>
        Rows.Where(r => r.Stage == stage);
}

/// <summary>
/// The onboarding-lifecycle service: the per-step completion flags that the
/// wizard sets, the admin overview that reads them, and the organizer
/// "flip a flag back to 0" hook that re-opens a step and HANDS OFF to the email
/// system (it raises an <see cref="OrganizerActionItem"/> of type
/// <see cref="OrganizerActionItemService.TypeOnboardingStepReset"/> — the actual
/// reminder send is the email system's job, this service only exposes the hook).
///
/// Everything is edition-scoped and idempotent: marking a step done that is
/// already done changes nothing; flipping a flag already 0 raises no new
/// reminder.
/// </summary>
public sealed class OnboardingService
{
    private readonly CommunityHubDbContext _db;
    private readonly OrganizerActionItemService _actions;
    private readonly TimeProvider _clock;

    public OnboardingService(
        CommunityHubDbContext db,
        OrganizerActionItemService actions,
        TimeProvider clock)
    {
        _db = db;
        _actions = actions;
        _clock = clock;
    }

    /// <summary>Human label for a step (used in the wizard + admin + email hook).</summary>
    public static string LabelFor(OnboardingStep step) => step switch
    {
        OnboardingStep.Bio          => "Verify your bio",
        OnboardingStep.Picture      => "Bio picture",
        OnboardingStep.Hotel        => "Hotel",
        OnboardingStep.Appreciation => "Appreciation",
        OnboardingStep.Swag         => "Swag",
        _ => step.ToString(),
    };

    private static bool Get(Participant p, OnboardingStep step) =>
        OnboardingStepSets.IsStepDone(p, step);

    /// <summary>
    /// Set a step's completion bit AND its <c>*At</c> timestamp together: when a
    /// step is marked done the timestamp is stamped (to <paramref name="at"/>);
    /// when it is re-opened the timestamp is cleared to null. The bit and the
    /// timestamp are therefore always consistent (done ⇔ has a timestamp).
    /// </summary>
    private static void Set(Participant p, OnboardingStep step, bool value, DateTimeOffset? at)
    {
        var stamp = value ? at : null;
        switch (step)
        {
            case OnboardingStep.Bio:
                p.OnboardingCompleted_Bio = value; p.OnboardingCompleted_BioAt = stamp; break;
            case OnboardingStep.Picture:
                p.OnboardingCompleted_Picture = value; p.OnboardingCompleted_PictureAt = stamp; break;
            case OnboardingStep.Hotel:
                p.OnboardingCompleted_Hotel = value; p.OnboardingCompleted_HotelAt = stamp; break;
            case OnboardingStep.Appreciation:
                p.OnboardingCompleted_Appreciation = value; p.OnboardingCompleted_AppreciationAt = stamp; break;
            case OnboardingStep.Swag:
                p.OnboardingCompleted_Swag = value; p.OnboardingCompleted_SwagAt = stamp; break;
        }
    }

    /// <summary>
    /// Mark an onboarding step complete for a participant (the wizard calls this
    /// when the participant finishes the step). Idempotent + edition-scoped.
    /// Returns true if the flag actually moved 0 → 1.
    /// </summary>
    public async Task<bool> MarkStepCompleteAsync(
        int eventId, int participantId, OnboardingStep step,
        CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null || Get(p, step)) return false;

        Set(p, step, true, _clock.GetUtcNow());
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Organizer "flip a flag back to 0" — re-opens a previously completed step
    /// (e.g. a speaker phones in wanting a hotel after all). On a real 1 → 0
    /// flip this HANDS OFF to the email system by raising an organizer action
    /// item asking the person to complete that wizard step. Idempotent + scoped.
    /// Returns true if the flag actually moved 1 → 0.
    /// </summary>
    public async Task<bool> ResetStepAsync(
        int eventId, int participantId, OnboardingStep step,
        CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null || !Get(p, step)) return false;

        Set(p, step, false, null);   // re-open: clear the bit AND its timestamp
        await _db.SaveChangesAsync(ct);

        // Hand off to the email system: one open action per (participant, step)
        // re-open. The actual reminder send is the email system's job.
        await _actions.UpsertOpenAsync(
            eventId,
            OrganizerActionItemService.TypeOnboardingStepReset,
            participantId,
            $"Onboarding step re-opened: {LabelFor(step)} — please remind {p.FullName} to complete it.",
            ct);
        return true;
    }

    /// <summary>
    /// The outcome of a per-persona bulk step re-open: how many people in the
    /// persona currently HAD the step done (the candidates) versus how many were
    /// actually re-opened (a 1 → 0 flip that raised a remind hand-off). They are
    /// equal in practice — a candidate is by definition currently done — but the
    /// pair makes the organizer banner honest ("Re-opened X of Y") and lets a
    /// no-op (nobody had it done) report distinctly.
    /// </summary>
    public sealed record PersonaResetResult(int Candidates, int Reopened)
    {
        /// <summary>True when nobody in the persona had that step done (no change).</summary>
        public bool IsNoOp => Reopened == 0;
    }

    /// <summary>
    /// Organizer "re-open this step for everyone in a persona" — the bulk sibling
    /// of <see cref="ResetStepAsync"/>. Re-opens the given step for EVERY active or
    /// pre-selected participant of <paramref name="persona"/> who currently has it
    /// done AND whose persona actually requires it, raising the same per-person
    /// <see cref="OrganizerActionItemService.TypeOnboardingStepReset"/> email
    /// hand-off for each (one open item per person, deduped by the upsert). People
    /// who never finished the step, or whose persona does not require it, are
    /// untouched. Edition-scoped + idempotent: a re-run after everyone is already
    /// re-opened flips nothing and raises nothing (returns a no-op result).
    ///
    /// Use case: a catering or swag deadline moves and the organizer wants the
    /// whole persona to re-confirm that step. Returns the candidate / re-opened
    /// counts for an honest confirmation banner.
    /// </summary>
    public async Task<PersonaResetResult> ResetStepForPersonaAsync(
        int eventId, PersonaGroup persona, OnboardingStep step,
        CancellationToken ct = default)
    {
        // The step only applies if the persona requires it — otherwise no row is a
        // candidate and this is a clean no-op (never re-opens an irrelevant step).
        if (!OnboardingStepSets.Requires(persona, step))
            return new PersonaResetResult(0, 0);

        // Resolve the candidate ids up front (SQL-translatable: the persona match
        // is a role-set the DB can filter; the step-done bit is a column). We then
        // re-open each via the single-row path so the email hand-off + timestamp
        // clearing stay in ONE place (no duplicated reset logic).
        var roles = RolesForPersona(persona);
        var inPipeline = await _db.Participants
            .Where(p => p.EventId == eventId
                        && roles.Contains(p.Role)
                        && (p.LifecycleState == ParticipantLifecycleState.Active
                            || p.LifecycleState == ParticipantLifecycleState.Preselected))
            .Select(p => p.Id)
            .ToListAsync(ct);

        int candidates = 0;
        int reopened = 0;
        foreach (var id in inPipeline)
        {
            // ResetStepAsync is a no-op (returns false) when the step is not done,
            // so it self-filters to the people who actually had it complete.
            var flipped = await ResetStepAsync(eventId, id, step, ct);
            if (flipped) { candidates++; reopened++; }
        }

        return new PersonaResetResult(candidates, reopened);
    }

    /// <summary>The participant roles that map to a persona group (the inverse of
    /// <see cref="OnboardingEmailSets.PersonaFor"/>), resolved once so the bulk
    /// query filters on a concrete role set the database can translate.</summary>
    private static IReadOnlyList<ParticipantRole> RolesForPersona(PersonaGroup persona) =>
        Enum.GetValues<ParticipantRole>()
            .Where(r => OnboardingEmailSets.PersonaFor(r) == persona)
            .ToList();

    /// <summary>
    /// Build the onboarding admin overview for an edition: counts by STAGE
    /// (Preselected / Invited / In-progress / Completed) with completion %,
    /// per-step done/total stats, and a per-participant grid — optionally
    /// filtered to one persona group. Read-only aggregation, never writes.
    ///
    /// Includes both <c>Preselected</c> rows (the shortlist queue) and
    /// <c>Active</c> rows (in/through onboarding); raw <c>Inactive</c> queue
    /// entries are excluded (they are not yet in the onboarding pipeline).
    /// Completion is PERSONA-AWARE: a row is "complete" when every step its
    /// persona requires (<see cref="OnboardingStepSets"/>) is done, and the
    /// per-step stats only count people for whom that step is required.
    /// </summary>
    public async Task<OnboardingOverview> BuildOverviewAsync(
        int eventId, PersonaGroup? persona = null, CancellationToken ct = default)
    {
        var entities = await _db.Participants
            .Where(p => p.EventId == eventId
                        && (p.LifecycleState == ParticipantLifecycleState.Active
                            || p.LifecycleState == ParticipantLifecycleState.Preselected))
            .OrderBy(p => p.Role)
            .ThenBy(p => p.FullName)
            .ToListAsync(ct);

        var people = entities
            .Where(p => persona is null
                        || OnboardingEmailSets.PersonaFor(p.Role) == persona.Value)
            .Select(ToRow)
            .ToList();

        var overview = new OnboardingOverview
        {
            PersonaFilter = persona,
            TotalParticipants = people.Count,
            FullyOnboarded = people.Count(r => r.IsComplete),
            Rows = people,
        };

        foreach (OnboardingStage stage in Enum.GetValues<OnboardingStage>())
        {
            overview.StageStats.Add(
                new OnboardingStageStat(stage, people.Count(r => r.Stage == stage)));
        }

        // Per-step stat counts only people whose persona REQUIRES that step.
        foreach (OnboardingStep step in Enum.GetValues<OnboardingStep>())
        {
            var requiring = people.Where(r => r.Requires(step)).ToList();
            int done = requiring.Count(r => r.DoneOf(step));
            overview.StepStats.Add(new OnboardingStepStat(step, done, requiring.Count));
        }

        return overview;
    }

    /// <summary>
    /// The "who hasn't onboarded yet" list for an edition: every participant in
    /// the onboarding pipeline whose persona-required steps are NOT all done
    /// (stage != Completed), with the steps still missing. Optionally filtered to
    /// one persona group. Read-only — reuses the same persona-aware projection as
    /// <see cref="BuildOverviewAsync"/> so the export matches the dashboard.
    ///
    /// Ordered by persona, then by least-complete first (fewest steps done), then
    /// by name — so the people needing the most chasing surface at the top.
    /// </summary>
    public async Task<IReadOnlyList<PendingOnboardingRow>> BuildPendingAsync(
        int eventId, PersonaGroup? persona = null, CancellationToken ct = default)
    {
        var overview = await BuildOverviewAsync(eventId, persona, ct);

        return overview.Rows
            .Where(r => r.Stage != OnboardingStage.Completed)
            .OrderBy(r => r.Persona)
            .ThenBy(r => r.DoneCount)
            .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PendingOnboardingRow(
                r.ParticipantId, r.FullName, r.Email, r.Role, r.Persona, r.Stage,
                r.DoneCount, r.RequiredCount, r.Percent,
                r.RequiredSteps.Where(step => !r.DoneOf(step)).ToList()))
            .ToList();
    }

    /// <summary>
    /// CSV of the "who hasn't onboarded yet" list (UTF-8 written by the caller).
    /// Columns: Name, Email, Persona, Stage, Done, Required, Percent, MissingSteps
    /// (the missing-steps cell is a "; "-joined list of human step labels).
    /// </summary>
    public async Task<string> BuildPendingCsvAsync(
        int eventId, PersonaGroup? persona = null, CancellationToken ct = default)
    {
        var rows = await BuildPendingAsync(eventId, persona, ct);
        return CsvWriter.Write(
            new[] { "Name", "Email", "Persona", "Stage", "Done", "Required", "Percent", "MissingSteps" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                string.IsNullOrWhiteSpace(r.FullName) ? r.Email : r.FullName,
                r.Email,
                r.Persona.ToString(),
                LabelFor(r.Stage),
                r.DoneCount.ToString(),
                r.RequiredCount.ToString(),
                r.Percent + "%",
                string.Join("; ", r.MissingSteps.Select(LabelFor)),
            }));
    }

    /// <summary>
    /// Map a participant to a dashboard row, deriving its persona-aware stage.
    /// </summary>
    private static OnboardingRow ToRow(Participant p)
    {
        var persona = OnboardingEmailSets.PersonaFor(p.Role);
        var required = OnboardingStepSets.For(persona);
        var stage = DeriveStage(p, required);
        return new OnboardingRow(
            p.Id, p.FullName, p.Email, p.Role, persona, stage, required,
            p.OnboardingCompleted_Bio,
            p.OnboardingCompleted_Picture,
            p.OnboardingCompleted_Hotel,
            p.OnboardingCompleted_Appreciation,
            p.OnboardingCompleted_Swag);
    }

    /// <summary>
    /// Stage = Preselected (lifecycle Preselected) · else, once Active:
    /// Completed (all required done) · InProgress (some done) · Invited (none done).
    /// </summary>
    private static OnboardingStage DeriveStage(
        Participant p, IReadOnlyList<OnboardingStep> required)
    {
        if (p.LifecycleState == ParticipantLifecycleState.Preselected)
            return OnboardingStage.Preselected;

        int done = required.Count(step => OnboardingStepSets.IsStepDone(p, step));
        if (required.Count > 0 && done == required.Count) return OnboardingStage.Completed;
        if (required.Count == 0) return OnboardingStage.Completed; // nothing required ⇒ done
        return done == 0 ? OnboardingStage.Invited : OnboardingStage.InProgress;
    }

    /// <summary>Human label for a dashboard stage.</summary>
    public static string LabelFor(OnboardingStage stage) => stage switch
    {
        OnboardingStage.Preselected => "Pre-selected",
        OnboardingStage.Invited     => "Invited",
        OnboardingStage.InProgress  => "In progress",
        OnboardingStage.Completed   => "Completed",
        _ => stage.ToString(),
    };
}
