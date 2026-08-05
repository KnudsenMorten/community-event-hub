namespace CommunityHub.Core.Surveys;

/// <summary>What a survey question asks for.</summary>
public enum SurveyQuestionKind
{
    /// <summary>A number on a scale — "how would you rate …, 1 to 5".</summary>
    Rating = 0,

    /// <summary>Free text. The thing people actually tell you something in.</summary>
    FreeText = 1,

    /// <summary>Pick exactly one of a fixed list.</summary>
    SingleChoice = 2,

    /// <summary>Pick any number of a fixed list.</summary>
    MultiChoice = 3,
}

/// <summary>One option in a choice question.</summary>
public sealed class SurveyChoice
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// §6.5 — one question in a post-event survey.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This EXTENDS the existing anonymous survey engine; it does not replace it.</b> The
/// live preliminary survey (<c>App_Data/Surveys/eldk27-topics.json</c>) is a fixed three-step
/// wizard — pick a track, rank three topics, choose a level each — and it is running in production.
/// A definition that declares no <see cref="SurveyDefinition.Questions"/> behaves exactly as before,
/// so the post-event surveys can arrive without touching the one people are answering today.</para>
///
/// <para>🔑 <b>Why the definition stays JSON.</b> Work-order §6.5: <i>"Draft the question sets
/// yourself — placeholder quality is expected and the owner will refine them. Store them in editable
/// configuration or seed data, never hardcoded."</i> The existing engine already loads its catalogue
/// from <c>App_Data/Surveys/{slug}.json</c> with no code change and no migration; these questions ride
/// the same file, so refining a wording is an edit and not a deploy.</para>
/// </remarks>
public sealed class SurveyQuestion
{
    /// <summary>Stable id — the key an ANSWER is stored under, so wording can change freely.</summary>
    /// <remarks>
    /// ⚠️ Renaming an id after responses exist orphans them. The id is a data key, not a label:
    /// change <see cref="Prompt"/> as often as you like, never this.
    /// </remarks>
    public string Id { get; set; } = string.Empty;

    public SurveyQuestionKind Kind { get; set; } = SurveyQuestionKind.Rating;

    /// <summary>The question as the respondent reads it.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Optional clarification under the prompt.</summary>
    public string? Help { get; set; }

    /// <summary>
    /// Must be answered before the survey can be submitted.
    /// </summary>
    /// <remarks>
    /// ⚠️ Use sparingly on an ANONYMOUS survey: nobody is chasing a half-finished response, so a
    /// required question that people do not want to answer costs the whole response, not just that
    /// answer.
    /// </remarks>
    public bool Required { get; set; }

    // ---- Rating ------------------------------------------------------------------------------

    /// <summary>Lowest value on the scale (default 1).</summary>
    public int ScaleMin { get; set; } = 1;

    /// <summary>Highest value on the scale (default 5).</summary>
    public int ScaleMax { get; set; } = 5;

    /// <summary>What the bottom of the scale means, e.g. "Poor".</summary>
    public string? ScaleMinLabel { get; set; }

    /// <summary>What the top means, e.g. "Excellent".</summary>
    public string? ScaleMaxLabel { get; set; }

    // ---- Free text ---------------------------------------------------------------------------

    /// <summary>Maximum characters accepted (default 2000).</summary>
    public int MaxLength { get; set; } = 2000;

    /// <summary>Placeholder shown in an empty box.</summary>
    public string? Placeholder { get; set; }

    // ---- Choice ------------------------------------------------------------------------------

    public List<SurveyChoice> Choices { get; set; } = new();

    /// <summary>
    /// Is this question's definition usable? A malformed question is a question nobody can answer.
    /// </summary>
    /// <remarks>
    /// 🔒 Checked when the definition LOADS, not when somebody submits: a survey whose link has been
    /// mailed out is the worst possible moment to discover that a scale runs from 5 to 1.
    /// </remarks>
    public string? Validate() =>
        string.IsNullOrWhiteSpace(Id) ? "A question is missing its id."
        : string.IsNullOrWhiteSpace(Prompt) ? $"Question '{Id}' has no prompt."
        : Kind == SurveyQuestionKind.Rating && ScaleMax <= ScaleMin
            ? $"Question '{Id}' has a scale that does not ascend ({ScaleMin}–{ScaleMax})."
        : Kind == SurveyQuestionKind.Rating && ScaleMax - ScaleMin > 10
            // A 1–100 slider produces a number nobody can act on and that no two people mean the
            // same thing by.
            ? $"Question '{Id}' has a scale wider than 10 points."
        : Kind is SurveyQuestionKind.SingleChoice or SurveyQuestionKind.MultiChoice
          && Choices.Count < 2
            ? $"Question '{Id}' is a choice question with fewer than two options."
        : Kind == SurveyQuestionKind.FreeText && MaxLength is < 1 or > 10000
            ? $"Question '{Id}' has an unusable maximum length ({MaxLength})."
        : Choices.Any(c => string.IsNullOrWhiteSpace(c.Id))
            ? $"Question '{Id}' has an option with no id."
        : Choices.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Choices.Count
            ? $"Question '{Id}' has two options with the same id."
        : null;
}

/// <summary>One respondent's answer to one <see cref="SurveyQuestion"/>, before it is persisted.</summary>
/// <param name="Rating">The chosen number, for a rating question.</param>
/// <param name="Text">The typed text, for a free-text question.</param>
/// <param name="ChoiceIds">The selected option ids, for a choice question.</param>
public sealed record SurveyAnswerInput(
    string QuestionId,
    int? Rating = null,
    string? Text = null,
    IReadOnlyList<string>? ChoiceIds = null);

/// <summary>
/// §6.5 — validates a submitted answer set against the definition, before anything is stored.
/// </summary>
/// <remarks>
/// 🔒 <b>Server-side, because the survey is ANONYMOUS and public.</b> There is no login to lean on
/// and the page is reachable by anybody with the link, so "the form only offered 1–5" is not a
/// constraint — it is a suggestion to whoever is posting.
/// </remarks>
public static class SurveyAnswerValidator
{
    /// <summary>Returns the problems found; empty means the answer set may be stored.</summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<SurveyQuestion> questions, IReadOnlyList<SurveyAnswerInput> answers)
    {
        var problems = new List<string>();
        var byId = answers
            .GroupBy(a => a.QuestionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var q in questions)
        {
            byId.TryGetValue(q.Id, out var a);

            if (a is null || IsBlank(q, a))
            {
                if (q.Required) problems.Add($"'{q.Prompt}' is required.");
                continue;
            }

            switch (q.Kind)
            {
                case SurveyQuestionKind.Rating when a.Rating is not { } r:
                    problems.Add($"'{q.Prompt}' needs a rating.");
                    break;
                case SurveyQuestionKind.Rating when a.Rating < q.ScaleMin || a.Rating > q.ScaleMax:
                    problems.Add($"'{q.Prompt}' must be between {q.ScaleMin} and {q.ScaleMax}.");
                    break;

                case SurveyQuestionKind.FreeText when (a.Text?.Length ?? 0) > q.MaxLength:
                    problems.Add($"'{q.Prompt}' is longer than {q.MaxLength} characters.");
                    break;

                case SurveyQuestionKind.SingleChoice when (a.ChoiceIds?.Count ?? 0) > 1:
                    problems.Add($"'{q.Prompt}' takes a single answer.");
                    break;

                case SurveyQuestionKind.SingleChoice or SurveyQuestionKind.MultiChoice:
                    foreach (var id in a.ChoiceIds ?? [])
                    {
                        if (!q.Choices.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
                            problems.Add($"'{q.Prompt}' has no option '{id}'.");
                    }
                    break;
            }
        }

        // 🔑 An answer to a question that does not exist is REFUSED, not ignored. On an anonymous
        // endpoint it means either a stale form or somebody posting by hand, and silently dropping
        // it would let a stale page look like it worked.
        foreach (var a in answers)
        {
            if (!questions.Any(q => string.Equals(q.Id, a.QuestionId, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"There is no question '{a.QuestionId}' in this survey.");
        }

        return problems;
    }

    private static bool IsBlank(SurveyQuestion q, SurveyAnswerInput a) => q.Kind switch
    {
        SurveyQuestionKind.Rating => a.Rating is null,
        SurveyQuestionKind.FreeText => string.IsNullOrWhiteSpace(a.Text),
        _ => a.ChoiceIds is null || a.ChoiceIds.Count == 0,
    };
}
