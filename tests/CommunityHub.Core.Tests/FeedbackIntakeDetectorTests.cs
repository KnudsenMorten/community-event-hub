using CommunityHub.Core.Assistant;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The AiHelper INTAKE keyword detector (REQUIREMENTS §137). Pure, offline. Proves the
/// configurable keyword set classifies a message as Bug / Feature / neither, case-
/// insensitively and on WHOLE words only (no false positives like "debugger").
/// </summary>
public sealed class FeedbackIntakeDetectorTests
{
    private static readonly FeedbackIntakeDetector Detector = new(new FeedbackIntakeOptions());

    [Theory]
    [InlineData("There is a bug on the schedule page")]
    [InlineData("I got an ERROR when saving")]              // case-insensitive
    [InlineData("Der er en fejl i programmet")]             // Danish "fejl"
    public void Bug_keywords_detected_as_bug(string message)
    {
        Assert.Equal(FeedbackKind.Bug, Detector.Detect(message));
    }

    [Theory]
    [InlineData("Could you add a feature for dark mode?")]
    [InlineData("This is a feature request: export to PDF")]
    [InlineData("Jeg har et forslag til appen")]            // Danish "forslag"
    public void Feature_keywords_detected_as_feature(string message)
    {
        Assert.Equal(FeedbackKind.Feature, Detector.Detect(message));
    }

    [Theory]
    [InlineData("What time does the keynote start?")]
    [InlineData("Where is my hotel?")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ordinary_questions_are_not_detected(string? message)
    {
        Assert.Null(Detector.Detect(message));
    }

    [Theory]
    [InlineData("I am using the debugger to inspect")]      // "bug" inside "debugger" must NOT match
    [InlineData("The feature-rich agenda is great")]        // boundary handled
    public void Word_boundary_avoids_false_positives(string message)
    {
        // "debugger" contains "bug" but not as a whole word; \b prevents a match.
        Assert.NotEqual(FeedbackKind.Bug, Detector.Detect(message) ?? FeedbackKind.Question);
    }

    [Fact]
    public void Bug_takes_precedence_over_feature_when_both_present()
    {
        Assert.Equal(FeedbackKind.Bug, Detector.Detect("I want a new feature but there is a bug first"));
    }

    [Fact]
    public void Keyword_set_is_config_overridable()
    {
        var opts = new FeedbackIntakeOptions { BugKeywords = new[] { "kaboom" }, FeatureKeywords = Array.Empty<string>() };
        var custom = new FeedbackIntakeDetector(opts);

        Assert.Equal(FeedbackKind.Bug, custom.Detect("everything went kaboom"));
        // the default words no longer fire under the overridden set
        Assert.Null(custom.Detect("there is a bug"));
    }
}
