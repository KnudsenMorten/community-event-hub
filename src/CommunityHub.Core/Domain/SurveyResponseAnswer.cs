using CommunityHub.Core.Surveys;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §6.5 — one answer to one <see cref="SurveyQuestion"/> on a submitted survey response.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Keyed on <see cref="QuestionId"/>, never on the question's wording.</b> The prompts
/// are in editable JSON precisely so the owner can refine them (§6.5), and a stored answer must
/// survive that edit. The id is the data key; the prompt is a label.</para>
///
/// <para>⚠️ <b>Still anonymous.</b> This row hangs off <see cref="SurveyResponse"/>, which carries no
/// participant — the survey is reached by a public link with no login. That is what makes people
/// answer honestly, and it is why the §3.4 summaries are aggregate by construction rather than by a
/// rule somebody has to remember to apply.</para>
///
/// <para>One row per answered question. A MULTI-choice answer stores its selections in
/// <see cref="ChoiceIds"/> rather than as several rows: the answer to "which of these applied?" is
/// one answer, and splitting it would make "how many people answered this question" a DISTINCT
/// count that somebody will eventually forget to write.</para>
/// </remarks>
public class SurveyResponseAnswer
{
    public int Id { get; set; }

    public int SurveyResponseId { get; set; }
    public SurveyResponse Response { get; set; } = null!;

    /// <summary>The question's stable id from the definition JSON.</summary>
    public string QuestionId { get; set; } = string.Empty;

    /// <summary>What kind of answer this is — stored so a reader need not re-open the definition.</summary>
    public SurveyQuestionKind Kind { get; set; }

    /// <summary>The number, for a rating question.</summary>
    public int? Rating { get; set; }

    /// <summary>The typed text, for a free-text question.</summary>
    public string? Text { get; set; }

    /// <summary>Selected option ids, comma-separated, for a choice question.</summary>
    public string? ChoiceIds { get; set; }
}
