using CommunityHub.Api;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §747 C8 — the outbound report pull, driven over a fake <see cref="HttpContext"/>.
/// </summary>
/// <remarks>
/// <para>What these tests protect is the contract an EXTERNAL system is written against. It is not
/// ours to change quietly, and its consumer is a program: a wrong status code is not a cosmetic
/// problem there, it is a consumer that archives the wrong bytes.</para>
///
/// <para>🔒 The single most valuable test here is
/// <see cref="No_credential_is_refused_and_never_serves_a_page"/> — the fail-closed fallback would
/// otherwise answer a machine client with an HTML login page at status 200.</para>
///
/// <para>FAKE names only; no network, no SQL, no secrets.</para>
/// </remarks>
public sealed class EvaluationReportEndpointTests
{
    private const int EventId = 7;
    private const int OtherEventId = 8;
    private const int SessionId = 21;

    private static readonly DateTimeOffset Now = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"eval-report-{Guid.NewGuid():N}")
            .Options);

    private sealed class Harness
    {
        public required CommunityHubDbContext Db { get; init; }
        public required EvaluationReportController Controller { get; init; }
        public required string Key { get; init; }
        public required DefaultHttpContext Http { get; init; }
    }

    private static async Task<Harness> SeedAsync(int responses = 12)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Events.Add(new Event
        {
            Id = OtherEventId, CommunityName = "C", DisplayName = "Another edition",
            Code = "ELDK28", IsActive = true,
            StartDate = new DateOnly(2028, 2, 9), EndDate = new DateOnly(2028, 2, 10),
        });

        db.EvaluationSessions.Add(new EvaluationSession
        {
            Id = SessionId, EventId = EventId, Title = "Keeping identity boring",
            ScheduledStart = Now.AddHours(-2), ScheduledEnd = Now.AddHours(-1),
            CollectionWindowOpensAt = Now.AddHours(-2), CollectionWindowClosesAt = Now,
            CreatedAt = Now.AddDays(-1), UpdatedAt = Now.AddDays(-1),
        });

        for (var i = 0; i < responses; i++)
        {
            db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, SessionId = SessionId, Rating = 4,
                CollectionTimestamp = Now.AddHours(-2).AddMinutes(i),
                ReceivedTimestamp = Now.AddHours(-1).AddMinutes(i),
                Source = EvaluationResponseSources.Device,
            });
        }

        await db.SaveChangesAsync();

        var clock = new FixedClock();
        var clients = new EvaluationApiClientService(db, clock);
        var (_, key) = await clients.IssueAsync(EventId, "Vendor report sync");

        var scores = new EvaluationScoreService(db);
        var controller = new EvaluationReportController(
            clients,
            new EvaluationReportBuilder(db, scores, clock),
            new EvaluationReportService(),
            NullLogger<EvaluationReportController>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return new Harness { Db = db, Controller = controller, Key = key, Http = http };
    }

    private static void Present(Harness h, string? key)
    {
        if (key is not null) h.Http.Request.Headers[EvaluationReportController.ApiKeyHeader] = key;
    }

    // ---- auth -------------------------------------------------------------------------------

    /// <summary>
    /// 🔒 A machine consumer must get a 401 it can act on — never a 200 carrying a login page, which
    /// it cannot distinguish from a report.
    /// </summary>
    [Fact]
    public async Task No_credential_is_refused_and_never_serves_a_page()
    {
        var h = await SeedAsync();

        var result = await h.Controller.GetReportAsync(EventId, SessionId, default);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Wrong_credential_is_refused()
    {
        var h = await SeedAsync();
        Present(h, EvaluationIngestService.NewKey());

        Assert.IsType<UnauthorizedObjectResult>(
            await h.Controller.GetReportAsync(EventId, SessionId, default));
    }

    /// <summary>
    /// 🔒 A valid key aimed at another edition is refused, and refused with the SAME status as a bad
    /// key — otherwise a key holder could enumerate which other events exist.
    /// </summary>
    [Fact]
    public async Task Credential_does_not_reach_another_event()
    {
        var h = await SeedAsync();
        Present(h, h.Key);

        Assert.IsType<UnauthorizedObjectResult>(
            await h.Controller.GetReportAsync(OtherEventId, SessionId, default));
    }

    /// <summary>A consumer's HTTP client usually supports one scheme or the other, rarely both.</summary>
    [Fact]
    public async Task Bearer_token_is_accepted_as_well_as_the_custom_header()
    {
        var h = await SeedAsync();
        h.Http.Request.Headers[HeaderNames.Authorization] = $"Bearer {h.Key}";

        Assert.IsType<FileContentResult>(
            await h.Controller.GetReportAsync(EventId, SessionId, default));
    }

    // ---- the document -----------------------------------------------------------------------

    [Fact]
    public async Task Valid_credential_returns_a_pdf()
    {
        var h = await SeedAsync();
        Present(h, h.Key);

        var file = Assert.IsType<FileContentResult>(
            await h.Controller.GetReportAsync(EventId, SessionId, default));

        Assert.Equal("application/pdf", file.ContentType);
        Assert.NotEmpty(file.FileContents);
        // A real PDF, not an error page rendered into the body.
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(file.FileContents, 0, 4));
    }

    [Fact]
    public async Task Unknown_session_is_not_found()
    {
        var h = await SeedAsync();
        Present(h, h.Key);

        Assert.IsType<NotFoundObjectResult>(
            await h.Controller.GetReportAsync(EventId, 999, default));
    }

    // ---- versioning -------------------------------------------------------------------------

    /// <summary>The version must be reachable both ways, so a consumer need not parse an ETag.</summary>
    [Fact]
    public async Task Response_carries_the_version_as_an_etag_and_a_header()
    {
        var h = await SeedAsync();
        Present(h, h.Key);

        await h.Controller.GetReportAsync(EventId, SessionId, default);

        var version = h.Http.Response.Headers[EvaluationReportController.VersionHeader].ToString();
        var etag = h.Http.Response.Headers[HeaderNames.ETag].ToString();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal($"\"{version}\"", etag);
    }

    /// <summary>
    /// 🔑 The whole point of C8's version: a consumer that already holds the current one is told so
    /// without a PDF being generated.
    /// </summary>
    [Fact]
    public async Task Matching_if_none_match_returns_304()
    {
        var h = await SeedAsync();
        Present(h, h.Key);

        await h.Controller.GetReportAsync(EventId, SessionId, default);
        var etag = h.Http.Response.Headers[HeaderNames.ETag].ToString();

        h.Http.Request.Headers[HeaderNames.IfNoneMatch] = etag;
        var second = await h.Controller.GetReportAsync(EventId, SessionId, default);

        var status = Assert.IsType<StatusCodeResult>(second);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    /// <summary>
    /// 🔒 The failure that matters most: late data arrives, the figures change, and a consumer
    /// holding the old version MUST be given the new document rather than a 304. This is exactly the
    /// supersession the brief asks consumers to be able to detect.
    /// </summary>
    [Fact]
    public async Task Late_data_supersedes_the_version_and_the_report_is_served_again()
    {
        var h = await SeedAsync();
        Present(h, h.Key);

        await h.Controller.GetReportAsync(EventId, SessionId, default);
        var before = h.Http.Response.Headers[HeaderNames.ETag].ToString();

        // A unit flushes a cached press collected during the session but received now.
        h.Db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = SessionId, Rating = 1,
            CollectionTimestamp = Now.AddHours(-2).AddMinutes(3),
            ReceivedTimestamp = Now.AddMinutes(5),
            Source = EvaluationResponseSources.Device,
        });
        await h.Db.SaveChangesAsync();

        h.Http.Request.Headers[HeaderNames.IfNoneMatch] = before;
        var after = await h.Controller.GetReportAsync(EventId, SessionId, default);

        Assert.IsType<FileContentResult>(after);   // NOT 304
        Assert.NotEqual(before, h.Http.Response.Headers[HeaderNames.ETag].ToString());
    }

    /// <summary>
    /// HEAD answers "is there a newer version?" without generating a PDF — otherwise the cheap check
    /// would be the expensive one and a consumer could not afford to poll.
    /// </summary>
    [Fact]
    public async Task Head_returns_the_version_without_a_body()
    {
        var h = await SeedAsync();
        Present(h, h.Key);
        h.Http.Request.Method = HttpMethods.Head;

        var result = await h.Controller.GetReportAsync(EventId, SessionId, default);

        Assert.IsNotType<FileContentResult>(result);
        Assert.False(string.IsNullOrWhiteSpace(
            h.Http.Response.Headers[EvaluationReportController.VersionHeader].ToString()));
    }

    /// <summary>An unauthenticated HEAD must be refused too — it is the same surface.</summary>
    [Fact]
    public async Task Head_still_requires_a_credential()
    {
        var h = await SeedAsync();
        h.Http.Request.Method = HttpMethods.Head;

        Assert.IsType<UnauthorizedObjectResult>(
            await h.Controller.GetReportAsync(EventId, SessionId, default));
    }
}
