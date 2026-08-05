using System.Text.Json;
using CommunityHub.Core.Surveys;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.5 — the RATING / FREE-TEXT / CHOICE question model, and the three drafted question sets.
/// </summary>
/// <remarks>
/// 🔒 <b>The survey is ANONYMOUS and public.</b> There is no login to lean on and the page is
/// reachable by anybody with the link, so every rule here is enforced server-side: "the form only
/// offered 1–5" is a suggestion to whoever is posting, not a constraint.
/// </remarks>
public sealed class SurveyQuestionModelTests
{
    private static SurveyQuestion Rating(string id, bool required = false) => new()
    {
        Id = id, Kind = SurveyQuestionKind.Rating, Prompt = $"How was {id}?",
        Required = required, ScaleMin = 1, ScaleMax = 5,
    };

    private static SurveyQuestion Text(string id, int max = 2000) => new()
    {
        Id = id, Kind = SurveyQuestionKind.FreeText, Prompt = $"Tell us about {id}", MaxLength = max,
    };

    private static SurveyQuestion Choice(string id, bool multi = false) => new()
    {
        Id = id,
        Kind = multi ? SurveyQuestionKind.MultiChoice : SurveyQuestionKind.SingleChoice,
        Prompt = $"Which {id}?",
        Choices =
        [
            new SurveyChoice { Id = "a", Label = "A" },
            new SurveyChoice { Id = "b", Label = "B" },
        ],
    };

    // ---- definition validation, at LOAD -----------------------------------------------------

    [Theory]
    [InlineData(5, 1)]      // does not ascend
    [InlineData(1, 100)]    // wider than 10 points: a number nobody can act on
    public void A_broken_rating_scale_is_refused_when_the_definition_loads(int min, int max)
    {
        var q = Rating("overall");
        q.ScaleMin = min;
        q.ScaleMax = max;

        Assert.NotNull(q.Validate());
    }

    [Fact]
    public void A_choice_question_with_fewer_than_two_options_is_refused()
    {
        var q = Choice("traffic");
        q.Choices.RemoveAt(1);

        Assert.NotNull(q.Validate());
    }

    [Fact]
    public void Two_questions_sharing_an_id_are_refused_because_answers_would_overwrite()
    {
        var def = new SurveyDefinition { Questions = [Rating("overall"), Text("overall")] };

        Assert.Contains(def.ValidateQuestions(), p => p.Contains("share the id"));
    }

    /// <summary>🔒 The compatibility contract: the live preliminary survey is untouched.</summary>
    [Fact]
    public void A_definition_with_no_questions_is_still_the_TRACK_wizard()
    {
        var def = new SurveyDefinition { Tracks = [new SurveyTrack { Id = "t", Name = "T" }] };

        Assert.False(def.IsQuestionSurvey);
        Assert.Empty(def.ValidateQuestions());
    }

    // ---- answer validation, at SUBMIT --------------------------------------------------------

    [Fact]
    public void A_rating_outside_the_scale_is_refused()
    {
        var qs = new[] { Rating("overall") };

        Assert.NotEmpty(SurveyAnswerValidator.Validate(qs, [new SurveyAnswerInput("overall", Rating: 9)]));
        Assert.Empty(SurveyAnswerValidator.Validate(qs, [new SurveyAnswerInput("overall", Rating: 4)]));
    }

    [Fact]
    public void A_required_question_left_blank_is_refused()
    {
        var qs = new[] { Rating("overall", required: true) };

        Assert.NotEmpty(SurveyAnswerValidator.Validate(qs, []));
        Assert.NotEmpty(SurveyAnswerValidator.Validate(qs, [new SurveyAnswerInput("overall")]));
    }

    [Fact]
    public void An_OPTIONAL_question_left_blank_is_fine()
    {
        // ⚠️ On an anonymous survey a blank optional answer is the normal case — nobody is chasing
        // a half-finished response, so refusing it would cost the whole submission.
        var qs = new[] { Rating("venue"), Text("anything-else") };

        Assert.Empty(SurveyAnswerValidator.Validate(qs, []));
    }

    [Fact]
    public void Free_text_longer_than_its_limit_is_refused()
    {
        var qs = new[] { Text("comment", max: 10) };

        Assert.NotEmpty(SurveyAnswerValidator.Validate(
            qs, [new SurveyAnswerInput("comment", Text: new string('x', 11))]));
    }

    [Fact]
    public void A_single_choice_question_refuses_two_answers()
    {
        var qs = new[] { Choice("traffic") };

        Assert.NotEmpty(SurveyAnswerValidator.Validate(
            qs, [new SurveyAnswerInput("traffic", ChoiceIds: ["a", "b"])]));
        Assert.Empty(SurveyAnswerValidator.Validate(
            qs, [new SurveyAnswerInput("traffic", ChoiceIds: ["a"])]));
    }

    [Fact]
    public void A_multi_choice_question_accepts_several_but_not_an_unknown_option()
    {
        var qs = new[] { Choice("wants", multi: true) };

        Assert.Empty(SurveyAnswerValidator.Validate(
            qs, [new SurveyAnswerInput("wants", ChoiceIds: ["a", "b"])]));
        Assert.NotEmpty(SurveyAnswerValidator.Validate(
            qs, [new SurveyAnswerInput("wants", ChoiceIds: ["a", "smuggled"])]));
    }

    /// <summary>
    /// 🔑 An answer to a question that does not exist is REFUSED, not ignored — on an anonymous
    /// endpoint that means a stale form or a hand-rolled post, and dropping it silently would let a
    /// stale page look like it worked.
    /// </summary>
    [Fact]
    public void An_answer_to_a_question_that_does_not_exist_is_refused()
    {
        var qs = new[] { Rating("overall") };

        Assert.Contains(
            SurveyAnswerValidator.Validate(qs, [new SurveyAnswerInput("ghost", Rating: 3)]),
            p => p.Contains("ghost"));
    }

}
