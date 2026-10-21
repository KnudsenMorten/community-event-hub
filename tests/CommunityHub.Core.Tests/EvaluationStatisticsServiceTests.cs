using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1273 — the feedback breakdown per day / session type / track, by channel, with a calculated-at.
/// </summary>
/// <remarks>
/// Operator 2026-09-21: *"improve this statistics so i can see stats per day, type of session (master
/// class, technical session), track status, number of responses: total, kiosk, qr code - when
/// calculation was made"*.
/// </remarks>
public sealed class EvaluationStatisticsServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Day1 = new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);   // 10:00 Danish
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);
    private static readonly DateTimeOffset CalcAt = new(2027, 2, 10, 18, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => CalcAt;
    }

    private static async Task<Data.CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Sessions.AddRange(
            new Session { Id = 1, EventId = EventId, SessionizeId = "s1", Title = "MC", Type = SessionType.MasterClass },
            new Session { Id = 2, EventId = EventId, SessionizeId = "s2", Title = "Tech A", Type = SessionType.TechnicalSession },
            new Session { Id = 3, EventId = EventId, SessionizeId = "s3", Title = "Tech B", Type = SessionType.TechnicalSession });
        EvaluationSession Eval(int id, int ceh, string? track, DateTimeOffset start) => new()
        {
            Id = id, EventId = EventId, CehSessionId = ceh, Title = $"S{id}", TrackName = track,
            ScheduledStart = start, ScheduledEnd = start.AddHours(1),
            CollectionWindowOpensAt = start, CollectionWindowClosesAt = start.AddMinutes(90),
            CreatedAt = start, UpdatedAt = start,
        };
        db.EvaluationSessions.AddRange(
            Eval(101, 1, "Security", Day1),     // master class, day 1
            Eval(102, 2, "Security", Day1),     // technical, day 1
            Eval(103, 3, null, Day2));          // technical, day 2, no track

        void Press(int? session, int rating, string source) => db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = session, Rating = rating, Source = source,
            CollectionTimestamp = Day1, ReceivedTimestamp = Day1,
        });
        Press(101, 4, EvaluationResponseSources.Device);
        Press(101, 4, EvaluationResponseSources.Kiosk);
        Press(102, 3, EvaluationResponseSources.Qr);
        Press(103, 1, EvaluationResponseSources.Kiosk);
        Press(null, 2, EvaluationResponseSources.Kiosk);   // unattributed: counts for the event only
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<EvaluationStatisticsService.Breakdown> BuildAsync(Data.CommunityHubDbContext db) =>
        new EvaluationStatisticsService(db, new Clock()).BuildAsync(EventId, t => t?.ToString() ?? "Unknown type");

    [Fact]
    public async Task The_event_line_counts_every_response_by_channel_and_says_when()
    {
        using var db = await SeedAsync();
        var s = await BuildAsync(db);

        Assert.Equal(new EvaluationStatisticsService.Channels(Total: 5, Box: 1, Kiosk: 3, Qr: 1), s.Event);
        Assert.Equal(CalcAt, s.CalculatedAt);
    }

    [Fact]
    public async Task Per_day_is_by_the_sessions_Danish_date_and_in_date_order()
    {
        using var db = await SeedAsync();
        var s = await BuildAsync(db);

        Assert.Equal(new[] { "Tue 9 Feb 2027", "Wed 10 Feb 2027" }, s.ByDay.Select(r => r.Label));
        Assert.Equal(3, s.ByDay[0].Channels.Total);          // unattributed press excluded from groups
        Assert.Equal(2, s.ByDay[0].Sessions);
        Assert.Equal(1, s.ByDay[1].Channels.Kiosk);
    }

    [Fact]
    public async Task Per_type_uses_the_CEH_session_type_most_responses_first()
    {
        using var db = await SeedAsync();
        var s = await BuildAsync(db);

        var mc = Assert.Single(s.ByType, r => r.Label == nameof(SessionType.MasterClass));
        Assert.Equal(2, mc.Channels.Total);
        Assert.Equal(100d, mc.Score.Score);                  // two "very happy": pooled, no floor on a group
        var tech = Assert.Single(s.ByType, r => r.Label == nameof(SessionType.TechnicalSession));
        Assert.Equal(2, tech.Sessions);
        Assert.Equal(new EvaluationStatisticsService.Channels(2, 0, 1, 1), tech.Channels);
    }

    [Fact]
    public async Task Per_track_lists_no_track_last()
    {
        using var db = await SeedAsync();
        var s = await BuildAsync(db);

        Assert.Equal(new[] { "Security", EvaluationStatisticsService.NoTrack }, s.ByTrack.Select(r => r.Label));
        Assert.Equal(3, s.ByTrack[0].Channels.Total);
    }
}
