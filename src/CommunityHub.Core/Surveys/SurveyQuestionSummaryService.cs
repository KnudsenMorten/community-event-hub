using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Surveys;

/// <summary>One rating question, summarised.</summary>
/// <param name="Distribution">How many people gave each value, lowest score first.</param>
public sealed record RatingSummary(
    string QuestionId, string Prompt, int Answered, double Average,
    IReadOnlyList<(int Value, int Count)> Distribution);

/// <summary>One choice question, summarised.</summary>
public sealed record ChoiceSummary(
    string QuestionId, string Prompt, int Answered,
    IReadOnlyList<(string Label, int Count)> Options);

/// <summary>One free-text question and everything people wrote in it.</summary>
public sealed record FreeTextSummary(
    string QuestionId, string Prompt, IReadOnlyList<string> Answers);

/// <summary>Everything a question survey has collected.</summary>
public sealed record QuestionSurveySummary(
    int Responses,
    IReadOnlyList<RatingSummary> Ratings,
    IReadOnlyList<ChoiceSummary> Choices,
    IReadOnlyList<FreeTextSummary> FreeText);

/// <summary>
/// §6.5 — aggregates the RATING / CHOICE / FREE-TEXT answers of a post-event survey.
/// </summary>
/// <remarks>
/// <para>The existing <c>SurveySummaryService</c> aggregates the preliminary survey's tracks and
/// topic picks; those questions do not exist here and these do not exist there, so this is a
/// sibling rather than a change to it.</para>
///
/// <para>🔒 <b>The average is reported WITH the distribution, never alone.</b> A mean of 3.0 is what
/// "everybody was lukewarm" and "half loved it, half hated it" both look like, and those are
/// opposite problems. The distribution is the only version that tells the organizer which one
/// happened.</para>
///
/// <para>⚠️ <b>Free text is listed in full and never summarised.</b> It is the part people actually
/// say something in, and a count of comments is worth nothing. Answers are shuffled out of
/// submission order deliberately — see <see cref="BuildAsync"/>.</para>
/// </remarks>
public sealed class SurveyQuestionSummaryService
{
    private readonly CommunityHubDbContext _db;

    public SurveyQuestionSummaryService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Summarise one question survey.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Free-text answers are returned ordered by their TEXT, not by when they arrived.</b> The
    /// survey is anonymous, but submission order is not: read next to a session's end time or a
    /// mail's send time it can point at a person, and the whole value of the form is that it cannot.
    /// Sorting by content breaks that link without losing a word.
    /// </remarks>
    public async Task<QuestionSurveySummary> BuildAsync(
        SurveyDefinition definition, CancellationToken ct = default)
    {
        var responseIds = await _db.SurveyResponses
            .Where(r => r.SurveySlug == definition.Slug)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (responseIds.Count == 0)
            return new QuestionSurveySummary(0, [], [], []);

        var answers = await _db.SurveyResponseAnswers
            .Where(a => responseIds.Contains(a.SurveyResponseId))
            .ToListAsync(ct);

        var byQuestion = answers
            .GroupBy(a => a.QuestionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var ratings = new List<RatingSummary>();
        var choices = new List<ChoiceSummary>();
        var freeText = new List<FreeTextSummary>();

        // Definition order, not database order: the organizer reads the summary against the form
        // they sent out.
        foreach (var q in definition.Questions)
        {
            byQuestion.TryGetValue(q.Id, out var rows);
            rows ??= [];

            switch (q.Kind)
            {
                case SurveyQuestionKind.Rating:
                {
                    var values = rows.Where(r => r.Rating is not null).Select(r => r.Rating!.Value).ToList();
                    var distribution = Enumerable.Range(q.ScaleMin, Math.Max(1, q.ScaleMax - q.ScaleMin + 1))
                        .Select(v => (Value: v, Count: values.Count(x => x == v)))
                        .ToList();

                    ratings.Add(new RatingSummary(
                        q.Id, q.Prompt, values.Count,
                        values.Count == 0 ? 0 : Math.Round(values.Average(), 2),
                        distribution));
                    break;
                }

                case SurveyQuestionKind.SingleChoice:
                case SurveyQuestionKind.MultiChoice:
                {
                    var picked = rows
                        .SelectMany(r => (r.ChoiceIds ?? string.Empty)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .ToList();

                    choices.Add(new ChoiceSummary(
                        q.Id, q.Prompt,
                        // 🔑 How many PEOPLE answered, not how many boxes were ticked: on a
                        // multi-choice those differ, and the percentage everybody wants is per person.
                        rows.Count(r => !string.IsNullOrWhiteSpace(r.ChoiceIds)),
                        q.Choices
                            .Select(c => (
                                c.Label,
                                Count: picked.Count(p => string.Equals(p, c.Id, StringComparison.OrdinalIgnoreCase))))
                            .OrderByDescending(x => x.Count)
                            .ThenBy(x => x.Label, StringComparer.Ordinal)
                            .ToList()));
                    break;
                }

                case SurveyQuestionKind.FreeText:
                {
                    freeText.Add(new FreeTextSummary(
                        q.Id, q.Prompt,
                        rows.Select(r => r.Text)
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Select(t => t!.Trim())
                            .OrderBy(t => t, StringComparer.Ordinal)   // never submission order
                            .ToList()));
                    break;
                }
            }
        }

        return new QuestionSurveySummary(responseIds.Count, ratings, choices, freeText);
    }
}
