namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1278 — the two follow-up questions asked after a rating: one POSITIVE point and one IMPROVEMENT
/// point, each answered with a single tap (or skipped).
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-21: *"can you include a second question depending on what they chose …
/// it must be buttons easy to press"*, then *"lets make 2 questions: one positive and one improvement
/// point. it will be motivating for a person"*. Everybody gets both, whatever they rated: a speaker
/// hears what worked from the unhappy and what to improve from the happy.</para>
///
/// <para>🔒 <b>Stored as a CODE, rendered from here.</b> The same rule as the ratings
/// (<see cref="EvaluationRatingLabels"/>): the stored value is stable and the words are presentation,
/// so rewording a button later never invalidates what was collected. A code not in this list is
/// dropped at the door (<see cref="NormalizePositive"/> / <see cref="NormalizeImprovement"/>) rather
/// than stored as free text — the follow-ups are counted, and an unknown code cannot be counted.</para>
///
/// <para>🔒 <b>Optional, always.</b> A rating is complete without them; skipping costs nothing.</para>
/// </remarks>
public static class EvaluationFollowUps
{
    public sealed record Choice(string Code, string Label);

    /// <summary>"What was good?" — in the order the buttons appear.</summary>
    // §1279 — reworded by the operator the same day (2026-09-21), every improvement point now phrased
    // as something to DO rather than a fault. Codes kept wherever the meaning is unchanged, so an
    // answer already collected is counted under its new words; only "more-energy" is new.
    public static readonly IReadOnlyList<Choice> Positive = new[]
    {
        new Choice("awesome-speaker", "Awesome speaker"),
        new Choice("well-structured", "Well structured session"),
        new Choice("great-demos", "Great demos"),
        new Choice("good-energy", "Good energy and engagement"),
        new Choice("audience-interaction", "Great interaction with audience"),
    };

    /// <summary>"What could be better?" — in the order the buttons appear.</summary>
    public static readonly IReadOnlyList<Choice> Improvement = new[]
    {
        new Choice("audience-interaction", "More interaction with audience"),
        new Choice("structure", "Clearer session structure"),
        new Choice("more-demos", "Fewer slides, more demos"),
        new Choice("time-for-questions", "More time for questions"),
        new Choice("speak-slower", "Speak slower"),
        new Choice("more-energy", "More energy and engagement"),
        new Choice("level-too-basic", "Deeper, more advanced content"),
        new Choice("better-slides", "Better slides"),
        new Choice("time-management", "Better time management"),
        new Choice("more-experience", "More rehearsal and stage confidence"),
    };

    public const string PositiveQuestion = "What was good?";
    public const string ImprovementQuestion = "What could be better?";

    /// <summary>
    /// §1280 — shown under each question (operator 2026-09-21: *"add info on the 2 questions to select
    /// top priority / select only one option"*). One answer per question is the design, and saying so
    /// stops people looking for a second button to press.
    /// </summary>
    public const string PickOneHint = "Pick the one that matters most — one choice only";

    /// <summary>The longest code — the database column is sized from this, with headroom.</summary>
    public const int MaxCodeLength = 40;

    /// <summary>A known positive code, or null for anything else (including blank).</summary>
    public static string? NormalizePositive(string? code) => Normalize(code, Positive);

    /// <summary>A known improvement code, or null for anything else (including blank).</summary>
    public static string? NormalizeImprovement(string? code) => Normalize(code, Improvement);

    public static string LabelOfPositive(string code) => Label(code, Positive);
    public static string LabelOfImprovement(string code) => Label(code, Improvement);

    private static string? Normalize(string? code, IReadOnlyList<Choice> list)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        return list.Any(x => x.Code == c) ? c : null;
    }

    private static string Label(string code, IReadOnlyList<Choice> list) =>
        list.FirstOrDefault(x => x.Code == code)?.Label ?? code;
}
