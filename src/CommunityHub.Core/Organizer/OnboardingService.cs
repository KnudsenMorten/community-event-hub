using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
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
