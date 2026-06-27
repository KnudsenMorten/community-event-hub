namespace CommunityHub.Core.Sponsors;

/// <summary>
/// One resolved sponsor-deliverable SIGNAL fed to <see cref="SponsorDeliverablesCalculator"/>
/// — a single lifecycle stage with its current done state and (optional) deadline. This is
/// the PURE input shape: the data layer (<see cref="SponsorDeliverablesService"/>) resolves
/// these from EXISTING tables (SponsorInfo / uploads / booth members / ParticipantTask) and
/// the calculator only reduces them to a completion score + the overdue/at-risk split.
/// Keeping the input pure (no DB types) makes the calculator trivially testable.
/// </summary>
/// <param name="Key">Stable stage key (e.g. <c>onboarding</c>, <c>logo</c>) — for tests / UI keys.</param>
/// <param name="Label">Human label shown on the board / checklist ("Logo uploaded").</param>
/// <param name="Applicable">
/// Whether this stage applies to THIS company. A non-applicable stage (e.g. booth materials
/// for a digital-only sponsor with no booth) is dropped entirely — it never counts for or
/// against the score, exactly like the §134 readiness model.
/// </param>
/// <param name="Done">True when the company has satisfied this stage.</param>
/// <param name="Deadline">
/// The stage's due date, or null when the stage has no dated deadline (e.g. the logo upload,
/// which is collected on Company Details without a deadline-bearing task). Drives the
/// calculator's overdue flag; a null deadline is never overdue.
/// </param>
/// <param name="FixLink">Deep-link to the page that completes this stage, or null.</param>
public sealed record SponsorDeliverableSignal(
    string Key,
    string Label,
    bool Applicable,
    bool Done,
    DateOnly? Deadline,
    string? FixLink);

/// <summary>One applicable deliverable stage, flattened for display.</summary>
/// <param name="Key">Stable stage key.</param>
/// <param name="Label">Human label.</param>
/// <param name="Done">True when satisfied.</param>
/// <param name="Deadline">The stage's due date, or null when undated.</param>
/// <param name="Overdue">
/// True when the stage is NOT done AND its <see cref="Deadline"/> is in the past (computed
/// by the calculator against "today"). A done stage is never overdue; an undated stage is
/// never overdue.
/// </param>
/// <param name="FixLink">Deep-link to fix it (only meaningful while not <see cref="Done"/>).</param>
public sealed record SponsorDeliverableStage(
    string Key,
    string Label,
    bool Done,
    DateOnly? Deadline,
    bool Overdue,
    string? FixLink);

/// <summary>
/// One sponsor company's deliverables rollup (REQUIREMENTS §135): the per-stage
/// done/overdue items plus the derived completion score. Computed PURELY from EXISTING data
/// (no new source of truth) by <see cref="SponsorDeliverablesCalculator"/>; the same shape
/// feeds the sponsor's own /Sponsor/Deliverables checklist and the organizer board
/// /Organizer/SponsorDeliverables.
/// </summary>
/// <param name="CompanyId">The Company Manager / WooCommerce company id.</param>
/// <param name="CompanyName">Display name (organizer board); falls back to the id.</param>
/// <param name="IsExhibitor">True when the company has a physical booth (booth stages apply).</param>
/// <param name="Stages">Every APPLICABLE stage, in the calculator's input (lifecycle) order.</param>
public sealed record SponsorDeliverables(
    string CompanyId,
    string CompanyName,
    bool IsExhibitor,
    IReadOnlyList<SponsorDeliverableStage> Stages)
{
    /// <summary>Number of applicable stages (the score denominator).</summary>
    public int ApplicableCount => Stages.Count;

    /// <summary>Number of applicable stages that are done (the score numerator).</summary>
    public int DoneCount => Stages.Count(s => s.Done);

    /// <summary>Number of applicable stages that are overdue (drives the at-risk highlight).</summary>
    public int OverdueCount => Stages.Count(s => s.Overdue);

    /// <summary>The still-incomplete stages, in order (the "what's left" checklist).</summary>
    public IReadOnlyList<SponsorDeliverableStage> MissingStages =>
        Stages.Where(s => !s.Done).ToList();

    /// <summary>The completed stages, in order.</summary>
    public IReadOnlyList<SponsorDeliverableStage> DoneStages =>
        Stages.Where(s => s.Done).ToList();

    /// <summary>The overdue stages, in order (for the at-risk detail).</summary>
    public IReadOnlyList<SponsorDeliverableStage> OverdueStages =>
        Stages.Where(s => s.Overdue).ToList();

    /// <summary>
    /// Completion as a 0–100 percentage (rounded). 0 when there are no applicable stages,
    /// so a brand-new company with nothing on file never reads as a misleading 100%.
    /// </summary>
    public int Percent =>
        ApplicableCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / ApplicableCount);

    /// <summary>
    /// True only when there is at least one applicable stage AND all of them are done — an
    /// empty stage set is NOT "complete".
    /// </summary>
    public bool IsComplete => ApplicableCount > 0 && DoneCount >= ApplicableCount;

    /// <summary>
    /// True when the company has at least one overdue stage — the organizer board highlights
    /// these as "at risk" so the operator sees who needs chasing.
    /// </summary>
    public bool AtRisk => OverdueCount > 0;

    /// <summary>A short "3 of 5 done" summary for the UI.</summary>
    public string Summary => $"{DoneCount} of {ApplicableCount} done";
}

/// <summary>
/// PURE sponsor-deliverables calculator (REQUIREMENTS §135). Reduces a company's resolved
/// <see cref="SponsorDeliverableSignal"/> list to a <see cref="SponsorDeliverables"/>: it
/// keeps only the APPLICABLE signals (in input order), derives each stage's overdue flag
/// against "today", and the record derives the completion score + at-risk state. No DB, no
/// I/O — so full / partial / overdue / empty cases are unit-testable directly.
/// <see cref="SponsorDeliverablesService"/> builds the signals from existing tables and
/// calls this.
/// </summary>
public static class SponsorDeliverablesCalculator
{
    /// <summary>
    /// Compute a company's deliverables from its resolved signals. Non-applicable signals are
    /// dropped (they never affect the score). A stage is OVERDUE when it is not done and its
    /// deadline is strictly before <paramref name="today"/>. Input order is preserved so the
    /// checklist reads in the intended lifecycle order.
    /// </summary>
    public static SponsorDeliverables Compute(
        string companyId,
        string companyName,
        bool isExhibitor,
        DateOnly today,
        IEnumerable<SponsorDeliverableSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var stages = signals
            .Where(s => s.Applicable)
            .Select(s => new SponsorDeliverableStage(
                s.Key,
                s.Label,
                s.Done,
                s.Deadline,
                Overdue: !s.Done && s.Deadline is { } d && d < today,
                s.FixLink))
            .ToList();

        return new SponsorDeliverables(companyId, companyName, isExhibitor, stages);
    }
}
