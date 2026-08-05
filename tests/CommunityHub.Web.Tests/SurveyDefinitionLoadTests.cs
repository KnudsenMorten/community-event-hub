using CommunityHub.Core.Surveys;
using CommunityHub.Surveys;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §6.5 — the drafted post-event surveys load through the PRODUCT'S OWN loader.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This test exists because its absence shipped a 404.</b> The first version lived in the
/// Core tests and deserialized the JSON with options it supplied ITSELF — including a
/// <c>JsonStringEnumConverter</c>. It passed. Production then answered 404 for all three surveys,
/// because <see cref="SurveyDefinitionProvider"/> had no such converter, so <c>"kind": "Rating"</c>
/// threw, the provider logged and returned null, and the page could not find the survey.</para>
///
/// <para>🔒 <b>The lesson, and it is general: a test that re-implements the thing it is testing
/// proves nothing.</b> It validated MY parsing of the file, not the product's. This one goes through
/// the real provider, reading the real files from the real content root.</para>
/// </remarks>
public sealed class SurveyDefinitionLoadTests
{
    private static SurveyDefinitionProvider Provider()
    {
        var contentRoot = Path.Combine(RepoRoot(), "src", "CommunityHub");
        var env = new StubEnv { ContentRootPath = contentRoot };
        return new SurveyDefinitionProvider(env, NullLogger<SurveyDefinitionProvider>.Instance);
    }

    private sealed class StubEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "CommunityHub";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Theory]
    [InlineData("eldk27-post-attendee")]
    [InlineData("eldk27-post-speaker")]
    [InlineData("eldk27-post-sponsor")]
    public void The_post_event_surveys_LOAD_and_are_question_surveys(string slug)
    {
        var def = Provider().TryGet(slug);

        // 🔴 A null here is exactly the production 404: the provider swallows the parse failure.
        Assert.NotNull(def);
        Assert.True(def!.IsQuestionSurvey, $"{slug} loaded but carries no questions.");
        Assert.Empty(def.ValidateQuestions());

        // §6.5 — the derived close rule has to be IN THE REAL FILE, read by the REAL loader. A rule
        // that only exists in a hand-built test object is a rule production does not have, which is
        // the §773.1 lesson stated once more: assert against what ships.
        Assert.Equal(1, def.ClosesMonthsAfterEventEnd);

        // Derived from the event's end date, never a literal. ELDK27 ends 10 Feb 2027, so the
        // survey stays open through 10 March and shuts at the start of the 11th.
        var closesAt = def.AutoCloseAt(new DateOnly(2027, 2, 10));
        Assert.Equal(new DateTimeOffset(2027, 3, 11, 0, 0, 0, TimeSpan.Zero), closesAt);
    }

    /// <summary>
    /// The enum arrives as a WORD from the file and must survive the real loader — the specific
    /// thing that broke.
    /// </summary>
    [Fact]
    public void Question_kinds_written_as_words_survive_the_real_loader()
    {
        var def = Provider().TryGet("eldk27-post-attendee");

        Assert.NotNull(def);
        Assert.Contains(def!.Questions, q => q.Kind == SurveyQuestionKind.Rating);
        Assert.Contains(def.Questions, q => q.Kind == SurveyQuestionKind.FreeText);
        Assert.Contains(def.Questions, q => q.Kind == SurveyQuestionKind.MultiChoice);
    }

    /// <summary>🔒 The LIVE preliminary survey must still load, and still be the track wizard.</summary>
    [Fact]
    public void The_existing_preliminary_survey_is_unaffected()
    {
        var def = Provider().TryGet("eldk27-topics");

        Assert.NotNull(def);
        Assert.False(def!.IsQuestionSurvey);
        Assert.NotEmpty(def.Tracks);
    }
}
