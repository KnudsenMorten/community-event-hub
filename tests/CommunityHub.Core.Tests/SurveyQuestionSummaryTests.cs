using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Surveys;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.5 — what the organizer sees from a post-event survey.
/// </summary>
/// <remarks>
/// 🔒 Two rules the summary must not break: an average is never shown without its distribution, and
/// free text is never re-ordered back into submission order.
/// </remarks>
public sealed class SurveyQuestionSummaryTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"qsum-{Guid.NewGuid():N}").Options);

    private static SurveyDefinition Definition() => new()
    {
        Slug = "post",
        Questions =
        [
            new SurveyQuestion { Id = "overall", Kind = SurveyQuestionKind.Rating, Prompt = "Overall?", ScaleMin = 1, ScaleMax = 5 },
            new SurveyQuestion
            {
                Id = "parts", Kind = SurveyQuestionKind.MultiChoice, Prompt = "Which parts?",
                Choices = [new SurveyChoice { Id = "expo", Label = "Expo" }, new SurveyChoice { Id = "party", Label = "Party" }],
            },
            new SurveyQuestion { Id = "one-change", Kind = SurveyQuestionKind.FreeText, Prompt = "One change?" },
        ],
    };

    private static async Task AddResponseAsync(
        CommunityHubDbContext db, int? rating = null, string? choices = null, string? text = null)
    {
        var r = new SurveyResponse { SurveySlug = "post", SelectedTrackId = string.Empty, SubmittedAt = DateTimeOffset.UtcNow };
        db.SurveyResponses.Add(r);
        await db.SaveChangesAsync();

        if (rating is not null)
            db.SurveyResponseAnswers.Add(new SurveyResponseAnswer { SurveyResponseId = r.Id, QuestionId = "overall", Kind = SurveyQuestionKind.Rating, Rating = rating });
        if (choices is not null)
            db.SurveyResponseAnswers.Add(new SurveyResponseAnswer { SurveyResponseId = r.Id, QuestionId = "parts", Kind = SurveyQuestionKind.MultiChoice, ChoiceIds = choices });
        if (text is not null)
            db.SurveyResponseAnswers.Add(new SurveyResponseAnswer { SurveyResponseId = r.Id, QuestionId = "one-change", Kind = SurveyQuestionKind.FreeText, Text = text });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 🔒 THE ONE THAT MATTERS. Two datasets with the SAME average and opposite meanings — the
    /// distribution is what tells them apart, so it must always be there.
    /// </summary>
    [Fact]
    public async Task The_distribution_distinguishes_lukewarm_from_polarised()
    {
        using var lukewarm = NewDb();
        await AddResponseAsync(lukewarm, rating: 3);
        await AddResponseAsync(lukewarm, rating: 3);

        using var polarised = NewDb();
        await AddResponseAsync(polarised, rating: 1);
        await AddResponseAsync(polarised, rating: 5);

        var a = await new SurveyQuestionSummaryService(lukewarm).BuildAsync(Definition());
        var b = await new SurveyQuestionSummaryService(polarised).BuildAsync(Definition());

        Assert.Equal(a.Ratings[0].Average, b.Ratings[0].Average);          // identical means
        Assert.NotEqual(
            a.Ratings[0].Distribution.Select(d => d.Count),
            b.Ratings[0].Distribution.Select(d => d.Count));               // different stories
    }

    [Fact]
    public async Task A_rating_distribution_covers_every_point_on_the_scale_even_the_unused_ones()
    {
        using var db = NewDb();
        await AddResponseAsync(db, rating: 5);

        var sum = await new SurveyQuestionSummaryService(db).BuildAsync(Definition());

        // A gap that is simply absent reads as "not asked" rather than "nobody chose it".
        Assert.Equal(5, sum.Ratings[0].Distribution.Count);
        Assert.Equal(0, sum.Ratings[0].Distribution.First(d => d.Value == 1).Count);
        Assert.Equal(1, sum.Ratings[0].Distribution.First(d => d.Value == 5).Count);
    }

    [Fact]
    public async Task A_multi_choice_counts_PEOPLE_who_answered_and_TICKS_per_option()
    {
        using var db = NewDb();
        await AddResponseAsync(db, choices: "expo,party");
        await AddResponseAsync(db, choices: "expo");

        var choice = (await new SurveyQuestionSummaryService(db).BuildAsync(Definition())).Choices[0];

        Assert.Equal(2, choice.Answered);                                   // two people
        Assert.Equal(2, choice.Options.First(o => o.Label == "Expo").Count); // two ticks
        Assert.Equal(1, choice.Options.First(o => o.Label == "Party").Count);
    }

    /// <summary>
    /// 🔒 Submission order can point at a person — read next to a session's end time or a mail's
    /// send time. Sorting by content breaks that link without losing a word.
    /// </summary>
    [Fact]
    public async Task Free_text_is_NOT_returned_in_submission_order()
    {
        using var db = NewDb();
        await AddResponseAsync(db, text: "zebra");     // submitted first
        await AddResponseAsync(db, text: "apple");     // submitted second

        var text = (await new SurveyQuestionSummaryService(db).BuildAsync(Definition())).FreeText[0];

        Assert.Equal(["apple", "zebra"], text.Answers);
    }

    [Fact]
    public async Task An_empty_survey_summarises_to_nothing_rather_than_throwing()
    {
        using var db = NewDb();

        var sum = await new SurveyQuestionSummaryService(db).BuildAsync(Definition());

        Assert.Equal(0, sum.Responses);
        Assert.Empty(sum.Ratings);
    }
}
