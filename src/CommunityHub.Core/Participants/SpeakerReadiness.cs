namespace CommunityHub.Core.Participants;

/// <summary>
/// One resolved readiness SIGNAL fed to <see cref="SpeakerReadinessCalculator"/> — a
/// single "am I ready?" check with its current done/not-done state. This is the PURE
/// input shape: the data layer (<see cref="SpeakerReadinessService"/>) resolves these
/// from existing tables, the calculator only reduces them to a score + a what's-missing
/// split. Keeping the input pure (no DB types) makes the calculator trivially testable.
/// </summary>
/// <param name="Key">Stable signal key (e.g. <c>details</c>, <c>hotel</c>) — for tests / UI keys.</param>
/// <param name="Label">Human label shown in the checklist ("Headshot photo").</param>
/// <param name="Applicable">
/// Whether this signal applies to THIS speaker. A non-applicable signal (e.g. Hotel for a
/// speaker not entitled to a room, or Master Class prep for a speaker not on the pre-day)
/// is dropped entirely — it never counts for or against the score.
/// </param>
/// <param name="Done">True when the speaker has satisfied this signal.</param>
/// <param name="FixLink">Deep-link to the page that completes this signal, or null.</param>
public sealed record ReadinessSignal(
    string Key,
    string Label,
    bool Applicable,
    bool Done,
    string? FixLink);

/// <summary>One applicable readiness item, flattened for display (done or missing).</summary>
/// <param name="Key">Stable signal key.</param>
/// <param name="Label">Human label.</param>
/// <param name="Done">True when satisfied.</param>
/// <param name="FixLink">Deep-link to fix it (only meaningful while not <see cref="Done"/>).</param>
public sealed record ReadinessItem(string Key, string Label, bool Done, string? FixLink);

/// <summary>
/// A single speaker's "am I ready?" rollup (REQUIREMENTS §134): the per-signal
/// done/missing items plus the derived score. Computed purely from EXISTING data
/// (no new source of truth) by <see cref="SpeakerReadinessCalculator"/>; the same
/// shape feeds the speaker's own /Speaker/Readiness view and the organizer roster
/// /Organizer/SpeakerReadiness.
/// </summary>
/// <param name="ParticipantId">The speaker's participant id.</param>
/// <param name="FullName">Display name (organizer roster).</param>
/// <param name="Email">Email (organizer roster).</param>
/// <param name="Items">Every APPLICABLE item, in the calculator's input order.</param>
public sealed record SpeakerReadiness(
    int ParticipantId,
    string FullName,
    string Email,
    IReadOnlyList<ReadinessItem> Items)
{
    /// <summary>Number of applicable items (the score denominator).</summary>
    public int ApplicableCount => Items.Count;

    /// <summary>Number of applicable items that are done (the score numerator).</summary>
    public int DoneCount => Items.Count(i => i.Done);

    /// <summary>The still-missing items, in order (the "what's missing" checklist).</summary>
    public IReadOnlyList<ReadinessItem> MissingItems =>
        Items.Where(i => !i.Done).ToList();

    /// <summary>The completed items, in order.</summary>
    public IReadOnlyList<ReadinessItem> DoneItems =>
        Items.Where(i => i.Done).ToList();

    /// <summary>
    /// Readiness as a 0–100 percentage (rounded). 0 when there are no applicable
    /// items, so a brand-new speaker with nothing to do never reads as a misleading
    /// 100%.
    /// </summary>
    public int Percent =>
        ApplicableCount == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / ApplicableCount);

    /// <summary>
    /// True only when there is at least one applicable item AND all of them are done
    /// (mirrors <c>SpeakerWizardView.AllDone</c>): an empty item set is NOT "ready".
    /// </summary>
    public bool IsReady => ApplicableCount > 0 && DoneCount >= ApplicableCount;

    /// <summary>A short "4 of 7 done" summary for the UI.</summary>
    public string Summary => $"{DoneCount} of {ApplicableCount} done";
}

/// <summary>
/// PURE readiness calculator (REQUIREMENTS §134). Reduces a speaker's resolved
/// <see cref="ReadinessSignal"/> list to a <see cref="SpeakerReadiness"/>: it keeps
/// only the APPLICABLE signals (in input order), flattens them to done/missing items,
/// and the record derives the score. No DB, no I/O — so full / partial / empty cases
/// are unit-testable directly. <see cref="SpeakerReadinessService"/> builds the
/// signals from existing tables and calls this.
/// </summary>
public static class SpeakerReadinessCalculator
{
    /// <summary>
    /// Compute a speaker's readiness from their resolved signals. Non-applicable
    /// signals are dropped (they never affect the score). Input order is preserved so
    /// the checklist reads in the intended journey order.
    /// </summary>
    public static SpeakerReadiness Compute(
        int participantId,
        string fullName,
        string email,
        IEnumerable<ReadinessSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var items = signals
            .Where(s => s.Applicable)
            .Select(s => new ReadinessItem(s.Key, s.Label, s.Done, s.FixLink))
            .ToList();

        return new SpeakerReadiness(participantId, fullName, email, items);
    }
}
