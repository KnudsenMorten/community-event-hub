using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for <see cref="SurveySummaryService"/> — the shared survey aggregation +
/// organizer admin state (open/close, reset). Covers REQUIREMENTS §24:
///   • reset deletes ONLY the target slug's responses (+ their picks);
///   • close blocks public submission (the IsOpen gate the public page reads);
///   • the summary math matches the public dashboard (weighted demand).
/// </summary>
public sealed class SurveySummaryServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 18, 9, 0, 0, TimeSpan.Zero);

    private static SurveySummaryService NewService(CommunityHubDbContext db) =>
        new(db, new FixedClock(T0), NullLogger<SurveySummaryService>.Instance);

    private static SurveyResponse Resp(string slug, string track, params (int rank, string topic, SurveyLevel lvl)[] picks) =>
        new()
        {
            SurveySlug = slug,
            SelectedTrackId = track,
            SubmittedAt = T0,
            Picks = picks.Select(p => new SurveyResponsePick
            {
                Rank = p.rank,
                TopicId = p.topic,
                DesiredLevel = p.lvl,
            }).ToList(),
        };

    // --- Catalog helper (matches what the JSON definition projects to) -------
    private static IReadOnlyList<SurveySummaryService.CatalogTrack> Catalog() => new[]
    {
        new SurveySummaryService.CatalogTrack("sec", "Security", new[]
        {
            new SurveySummaryService.CatalogTopic("intune", "Intune", "Endpoint"),
            new SurveySummaryService.CatalogTopic("defender", "Defender", "Endpoint"),
        }),
        new SurveySummaryService.CatalogTrack("dev", "Dev", new[]
        {
            new SurveySummaryService.CatalogTopic("ci", "CI/CD", "DevOps"),
        }),
    };

    [Fact]
    public async Task Reset_deletes_only_the_target_slugs_responses_and_picks()
    {
        using var db = TestDb.New();
        // Target survey: 2 responses (4 picks total).
        db.SurveyResponses.Add(Resp("alpha", "sec", (1, "intune", SurveyLevel.Advanced), (2, "defender", SurveyLevel.Expert)));
        db.SurveyResponses.Add(Resp("alpha", "dev", (1, "ci", SurveyLevel.BlackBelt)));
        // Other survey that must SURVIVE the reset.
        db.SurveyResponses.Add(Resp("beta", "sec", (1, "intune", SurveyLevel.Advanced)));
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var deleted = await svc.ResetResponsesAsync("alpha", "org@x.test", default);

        Assert.Equal(2, deleted);
        // alpha fully gone (responses + picks).
        Assert.Empty(db.SurveyResponses.Where(r => r.SurveySlug == "alpha"));
        Assert.Empty(db.SurveyResponsePicks.Where(p => p.Response.SurveySlug == "alpha"));
        // beta untouched.
        Assert.Single(db.SurveyResponses.Where(r => r.SurveySlug == "beta"));
        Assert.Single(db.SurveyResponsePicks.Where(p => p.Response.SurveySlug == "beta"));
    }

    [Fact]
    public async Task Reset_on_empty_slug_deletes_nothing_and_returns_zero()
    {
        using var db = TestDb.New();
        db.SurveyResponses.Add(Resp("beta", "sec", (1, "intune", SurveyLevel.Advanced)));
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var deleted = await svc.ResetResponsesAsync("alpha", "org@x.test", default);

        Assert.Equal(0, deleted);
        Assert.Single(db.SurveyResponses); // beta intact
    }

    [Fact]
    public async Task A_survey_with_no_state_row_is_open_by_default()
    {
        using var db = TestDb.New();
        var svc = NewService(db);

        Assert.True(await svc.IsOpenAsync("alpha", default));
    }

    [Fact]
    public async Task Close_blocks_public_submission_then_activate_reopens()
    {
        using var db = TestDb.New();
        var svc = NewService(db);

        // Closing flips IsOpen -> false (the gate the public page checks).
        await svc.SetOpenAsync("alpha", false, "org@x.test", default);
        Assert.False(await svc.IsOpenAsync("alpha", default));

        // Re-activating flips it back.
        await svc.SetOpenAsync("alpha", true, "org@x.test", default);
        Assert.True(await svc.IsOpenAsync("alpha", default));
    }

    [Fact]
    public async Task SetOpen_upserts_a_single_state_row_and_records_audit_fields()
    {
        using var db = TestDb.New();
        var svc = NewService(db);

        await svc.SetOpenAsync("alpha", false, "org@x.test", default);
        await svc.SetOpenAsync("alpha", true, "other@x.test", default);

        var rows = db.SurveyStates.Where(s => s.SurveySlug == "alpha").ToList();
        Assert.Single(rows); // upsert, not a second row
        Assert.True(rows[0].IsOpen);
        Assert.Equal("other@x.test", rows[0].UpdatedByEmail);
        Assert.Equal(T0, rows[0].UpdatedAt);
    }

    [Fact]
    public async Task GetOpenStates_defaults_missing_slugs_to_open()
    {
        using var db = TestDb.New();
        var svc = NewService(db);
        await svc.SetOpenAsync("closed-one", false, null, default);

        var map = await svc.GetOpenStatesAsync(new[] { "closed-one", "never-touched" }, default);

        Assert.False(map["closed-one"]);
        Assert.True(map["never-touched"]);
    }

    [Fact]
    public async Task Summary_computes_weighted_demand_and_track_distribution()
    {
        using var db = TestDb.New();
        // 2 responses on "sec", 1 on "dev".
        db.SurveyResponses.Add(Resp("alpha", "sec",
            (1, "intune", SurveyLevel.Advanced), (2, "defender", SurveyLevel.Expert)));
        db.SurveyResponses.Add(Resp("alpha", "sec",
            (1, "intune", SurveyLevel.Expert), (2, "defender", SurveyLevel.Advanced)));
        db.SurveyResponses.Add(Resp("alpha", "dev",
            (1, "ci", SurveyLevel.BlackBelt)));
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var sum = await svc.BuildSummaryAsync("alpha", Catalog(), default);

        Assert.Equal(3, sum.TotalResponses);

        // intune: two rank-1 picks => weighted 3+3 = 6, pickCount 2.
        var intune = sum.TopTopicsOverall.First(t => t.TopicId == "intune");
        Assert.Equal(2, intune.PickCount);
        Assert.Equal(6, intune.WeightedScore);
        // defender: two rank-2 picks => weighted 2+2 = 4.
        var defender = sum.TopTopicsOverall.First(t => t.TopicId == "defender");
        Assert.Equal(4, defender.WeightedScore);

        // Track distribution: sec 2/3 (67%), dev 1/3 (33%).
        var sec = sum.TrackStats.First(t => t.TrackId == "sec");
        Assert.Equal(2, sec.ResponseCount);
        Assert.Equal(67, sec.Percent);

        // Level totals across all picks: Advanced 2, Expert 2, BlackBelt 1.
        Assert.Equal(2, sum.LevelTotals[SurveyLevel.Advanced]);
        Assert.Equal(2, sum.LevelTotals[SurveyLevel.Expert]);
        Assert.Equal(1, sum.LevelTotals[SurveyLevel.BlackBelt]);
    }

    [Fact]
    public async Task CountResponses_is_scoped_to_the_slug()
    {
        using var db = TestDb.New();
        db.SurveyResponses.Add(Resp("alpha", "sec", (1, "intune", SurveyLevel.Advanced)));
        db.SurveyResponses.Add(Resp("alpha", "dev", (1, "ci", SurveyLevel.Expert)));
        db.SurveyResponses.Add(Resp("beta", "sec", (1, "intune", SurveyLevel.Advanced)));
        await db.SaveChangesAsync();

        var svc = NewService(db);
        Assert.Equal(2, await svc.CountResponsesAsync("alpha", default));
        Assert.Equal(1, await svc.CountResponsesAsync("beta", default));
    }
}
