using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using UglyToad.PdfPig;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1278 — the two follow-up questions (what was good / what could be better): the catalog, and the
/// counts in the speaker's report.
/// </summary>
/// <remarks>
/// Operator 2026-09-21: *"lets make 2 questions: one positive and one improvement point. it will be
/// motivating for a person"* · *"it must be buttons easy to press"*. Ingest and QR storage are pinned
/// in their own suites; the page flows are browser behaviour (by hand).
/// </remarks>
public sealed class EvaluationFollowUpTests
{
    // ---- the catalog ---------------------------------------------------------------------------

    [Fact]
    public void Codes_are_unique_within_each_question_and_fit_the_column()
    {
        foreach (var list in new[] { EvaluationFollowUps.Positive, EvaluationFollowUps.Improvement })
        {
            Assert.Equal(list.Count, list.Select(c => c.Code).Distinct().Count());
            Assert.All(list, c =>
            {
                Assert.True(c.Code.Length <= EvaluationFollowUps.MaxCodeLength);
                Assert.Equal(c.Code, c.Code.Trim().ToLowerInvariant());
                Assert.False(string.IsNullOrWhiteSpace(c.Label));
            });
        }
    }

    [Theory]
    [InlineData("great-demos", "great-demos")]
    [InlineData("  Great-Demos ", "great-demos")]
    [InlineData("speak-slower", null)]        // an IMPROVEMENT code is not a positive one
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("<script>", null)]
    public void Only_known_codes_survive_normalisation(string? input, string? expected) =>
        Assert.Equal(expected, EvaluationFollowUps.NormalizePositive(input));

    [Fact]
    public void The_lists_are_the_ones_agreed()
    {
        // §1279 — the operator's final wording (2026-09-21), in his order.
        Assert.Equal(new[] { "Awesome speaker", "Well structured session", "Great demos",
                             "Good energy and engagement", "Great interaction with audience" },
            EvaluationFollowUps.Positive.Select(c => c.Label));
        Assert.Equal(new[] { "More interaction with audience", "Clearer session structure",
                             "Fewer slides, more demos", "More time for questions", "Speak slower",
                             "More energy and engagement", "Deeper, more advanced content", "Better slides",
                             "Better time management", "More rehearsal and stage confidence" },
            EvaluationFollowUps.Improvement.Select(c => c.Label));
    }

    // ---- the report ----------------------------------------------------------------------------

    private const int EventId = 1, SessionId = 900;
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start.AddHours(3);
    }

    private static async Task<CommunityHubDbContext> SeedAsync(params (string? Pos, string? Imp)[] answers)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.EvaluationSessions.Add(new EvaluationSession
        {
            Id = SessionId, EventId = EventId, CehSessionId = 55, Title = "Keeping identity boring",
            ScheduledStart = Start, ScheduledEnd = Start.AddHours(1),
            CollectionWindowOpensAt = Start, CollectionWindowClosesAt = Start.AddMinutes(90),
            CreatedAt = Start, UpdatedAt = Start,
        });
        var i = 0;
        foreach (var (pos, imp) in answers)
        {
            var t = Start.AddMinutes(50 + i++);
            db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, SessionId = SessionId, Rating = 4,
                CollectionTimestamp = t, ReceivedTimestamp = t,
                Source = EvaluationResponseSources.Kiosk, PositivePoint = pos, ImprovementPoint = imp,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task The_report_counts_each_answer_most_picked_first()
    {
        using var db = await SeedAsync(
            ("great-demos", "speak-slower"),
            ("great-demos", null),
            ("awesome-speaker", "speak-slower"),
            (null, "time-for-questions"),
            (null, null));

        var report = await new EvaluationReportBuilder(db, new EvaluationScoreService(db), new FixedClock())
            .BuildAsync(EventId, SessionId);

        var good = report!.Data.PositivePoints!;
        Assert.Equal(new[] { ("Great demos", 2), ("Awesome speaker", 1) }, good.Select(p => (p.Label, p.Count)));
        var better = report.Data.ImprovementPoints!;
        Assert.Equal(new[] { ("Speak slower", 2), ("More time for questions", 1) }, better.Select(p => (p.Label, p.Count)));
    }

    [Fact]
    public async Task The_PDF_prints_the_section_when_answers_exist()
    {
        using var db = await SeedAsync(("great-demos", "speak-slower"), ("good-energy", null));
        var report = await new EvaluationReportBuilder(db, new EvaluationScoreService(db), new FixedClock())
            .BuildAsync(EventId, SessionId);

        var pdf = new EvaluationReportService().Render(report!.Data);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Join(" ", doc.GetPages().Select(p => p.Text));
        Assert.Contains("What people picked", text);
        Assert.Contains("Great demos", text);
        // §1288 — the share beside each count in "How people answered" (both seeded presses are 4s).
        Assert.Contains("(100%)", text);
        Assert.Contains("Speak slower", text);
    }

    [Fact]
    public async Task A_session_nobody_answered_the_follow_ups_for_has_no_such_section()
    {
        using var db = await SeedAsync((null, null), (null, null));
        var report = await new EvaluationReportBuilder(db, new EvaluationScoreService(db), new FixedClock())
            .BuildAsync(EventId, SessionId);

        var pdf = new EvaluationReportService().Render(report!.Data);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Join(" ", doc.GetPages().Select(p => p.Text));
        Assert.DoesNotContain("What people picked", text);
    }
}
