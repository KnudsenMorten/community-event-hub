using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.2 — the BULK version lookup behind the organizer grid's "results stale" badge.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The whole point is that it agrees with <see cref="EvaluationReportBuilder.VersionAsync"/>
/// exactly.</b> The grid compares its answer against <c>PublishedReportVersion</c>, which the
/// publisher wrote from the single-session path. If the two ever diverged, the page would show a
/// "stale" no publish could clear, or call a superseded report current.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class EvaluationReportBulkVersionTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);

    private static async Task<CommunityHubDbContext> SeedAsync(
        params (int EvalSessionId, int CehSessionId, int Responses)[] sessions)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "ELDK 2027", Code = "ELDK27",
            IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });

        foreach (var (evalId, cehId, responses) in sessions)
        {
            db.Sessions.Add(new Session { Id = cehId, EventId = EventId, Title = $"Session {cehId}" });
            db.EvaluationSessions.Add(new EvaluationSession
            {
                Id = evalId, EventId = EventId, CehSessionId = cehId, Title = $"Session {cehId}",
                ScheduledStart = Start, ScheduledEnd = Start.AddHours(1),
                CreatedAt = Start.AddDays(-1), UpdatedAt = Start.AddDays(-1),
            });
            for (var i = 0; i < responses; i++)
            {
                db.EvaluationResponses.Add(new EvaluationResponse
                {
                    EventId = EventId, SessionId = evalId, Rating = 4,
                    CollectionTimestamp = Start.AddMinutes(i),
                    ReceivedTimestamp = Start.AddMinutes(i),
                    Source = EvaluationResponseSources.Device,
                });
            }
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static EvaluationReportBuilder Builder(CommunityHubDbContext db) =>
        new(db, new EvaluationScoreService(db));

    /// <summary>🔒 The contract: bulk == single, for every session.</summary>
    [Fact]
    public async Task The_bulk_version_equals_the_single_session_version()
    {
        using var db = await SeedAsync((900, 10, 12), (901, 11, 3));
        var builder = Builder(db);

        var bulk = await builder.VersionsAsync(EventId, [900, 901]);

        Assert.Equal(await builder.VersionAsync(EventId, 900), bulk[900]);
        Assert.Equal(await builder.VersionAsync(EventId, 901), bulk[901]);
    }

    /// <summary>
    /// A new response moves the version — which is exactly what makes a published report stale.
    /// </summary>
    [Fact]
    public async Task A_new_response_moves_the_version()
    {
        using var db = await SeedAsync((900, 10, 12));
        var builder = Builder(db);

        var before = (await builder.VersionsAsync(EventId, [900]))[900];

        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = 900, Rating = 5,
            CollectionTimestamp = Start.AddHours(2), ReceivedTimestamp = Start.AddHours(2),
            Source = EvaluationResponseSources.Device,
        });
        await db.SaveChangesAsync();

        var after = (await builder.VersionsAsync(EventId, [900]))[900];

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// ⚠️ A session NOBODY rated has no report, so it cannot have a stale one — it is absent from
    /// the result rather than carrying an empty version the grid would compare against.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_responses_has_no_version()
    {
        using var db = await SeedAsync((900, 10, 0));

        var bulk = await Builder(db).VersionsAsync(EventId, [900]);

        Assert.Empty(bulk);
    }

    [Fact]
    public async Task An_empty_request_asks_the_database_nothing()
    {
        using var db = await SeedAsync((900, 10, 12));

        Assert.Empty(await Builder(db).VersionsAsync(EventId, []));
    }

    /// <summary>Another edition's session is never returned — the same scoping the single path has.</summary>
    [Fact]
    public async Task Another_editions_session_is_not_returned()
    {
        using var db = await SeedAsync((900, 10, 12));

        var bulk = await Builder(db).VersionsAsync(eventId: 99, [900]);

        Assert.Empty(bulk);
    }
}
