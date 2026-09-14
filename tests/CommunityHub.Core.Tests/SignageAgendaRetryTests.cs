using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Signage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1141 — the signage sync retries the agenda read before it mails the operator.
///
/// <para>Operator 2026-08-27, after three "[PROD] Signage agenda sync failed" mails in one day:
/// <i>"make retries before throwing errors in my face"</i>.</para>
///
/// <para>⚠️ Every one of those mails was a <b>single transient 401 on the first call of the pass</b>
/// — `halls` twice, `sessions?day=2` once — with successful passes on either side. One rejected
/// read became one alert, because the pass had no second attempt at all.</para>
///
/// <para>🔒 Retrying costs nothing here: the screens hold the last good agenda throughout, so a
/// slower failure is invisible and a recovered one saves him a mail. See
/// <see cref="ZohoTokenReauthTests"/> for the half that makes the retry able to succeed — without
/// re-authentication a retry just re-presents the rejected token, which is what
/// <c>SessionBackstagePushService</c> was already doing three times over.</para>
/// </summary>
public sealed class SignageAgendaRetryTests
{
    private const int EventId = 1;

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DateTimeOffset Now = new(2027, 2, 9, 7, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"signage-retry-{Guid.NewGuid():N}").Options);

    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new InvalidOperationException("The pull override must be used; no HTTP in these tests.");
    }

    private static SignageAgendaSyncService NewService(
        CommunityHubDbContext db,
        Func<CancellationToken, Task<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>> pull)
    {
        var options = new ZohoOptions
        {
            Enabled = true,
            ApiDomain = "https://zoho.test",
            BackstagePortalId = "P1",
            BackstageEventId = "E1",
        };
        var client = new ZohoClient(new HttpClient(new DeadHandler()), options, NullLogger<ZohoClient>.Instance);
        return new SignageAgendaSyncService(
            db, client, options, alerts: null, clock: new FixedClock(Now), log: null, pullOverride: pull,
            retryDelay: _ => TimeSpan.Zero);
    }

    private static ZohoClient.BackstageAgendaActivity Activity(string id) =>
        new(id, "Talk", new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero), 60,
            "Hall One", "Cloud", "PRESENTATION", new[] { "Ada Lovelace" }, 1);

    /// <summary>The 401 the operator actually received, in the shape the strict pager throws it.</summary>
    private static HttpRequestException Zoho401() =>
        new("Zoho GET sessions?day=2 page 1 failed: HTTP 401 Unauthorized");

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_pass_succeeds()
    {
        using var db = NewDb();
        var calls = 0;

        var svc = NewService(db, _ =>
        {
            calls++;
            if (calls == 1) throw Zoho401();
            return Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
                new[] { Activity("S1") });
        });

        var result = await svc.RunAsync(EventId);

        // 🔴 Before §1141 this was a failed pass AND a mail, off one unlucky call.
        Assert.True(result.Ok);
        Assert.Equal(2, calls);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task It_keeps_trying_to_the_third_attempt()
    {
        using var db = NewDb();
        var calls = 0;

        var svc = NewService(db, _ =>
        {
            calls++;
            if (calls < 3) throw Zoho401();
            return Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
                new[] { Activity("S1") });
        });

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ok);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_sustained_outage_still_fails_and_SAYS_how_many_times_it_tried()
    {
        using var db = NewDb();
        var calls = 0;

        var svc = NewService(db, _ => { calls++; throw Zoho401(); });

        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ok);
        Assert.Equal(3, calls);

        // 🔑 A mail that DOES arrive now carries evidence of a real outage rather than of one
        // unlucky call — the attempt count is the difference between the two.
        Assert.Contains("after 3 attempts", result.FailureReason);
        Assert.Contains("HTTP 401", result.FailureReason);
    }

    [Fact]
    public async Task The_screens_are_never_emptied_while_it_retries()
    {
        using var db = NewDb();
        db.AgendaActivities.Add(new CommunityHub.Core.Domain.Signage.AgendaActivity
        {
            EventId = EventId,
            BackstageSessionId = "OLD",
            Title = "Cached talk",
            StartsAt = new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
            Room = "Hall One",
            DayIndex = 1,
            LastSyncedAt = Now.AddHours(-2),
        });
        await db.SaveChangesAsync();

        var svc = NewService(db, _ => throw Zoho401());
        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ok);

        // 🔒 The fail-safe is unchanged by the retry: a failed read never clears the cache, so the
        // screens keep showing the last good agenda for the whole time we are retrying.
        Assert.Equal(1, await db.AgendaActivities.CountAsync(a => a.EventId == EventId));
    }

    [Fact]
    public async Task A_missing_token_is_NOT_retried()
    {
        // 🔒 Deliberate asymmetry. "No access token" means the §783.12 cooldown is in force —
        // Zoho's budget is 10 token requests per 10 minutes and it has already been spent. Asking
        // again is what turns a throttle into a sustained outage, so this path fails immediately.
        using var db = NewDb();
        var options = new ZohoOptions
        {
            Enabled = true,
            ApiDomain = "https://zoho.test",
            BackstagePortalId = "P1",
            BackstageEventId = "E1",
            ReadsAllowed = false,          // the host may not reach Zoho ⇒ no token, no HTTP
        };
        var client = new ZohoClient(
            new HttpClient(new DeadHandler()), options, NullLogger<ZohoClient>.Instance);
        var svc = new SignageAgendaSyncService(
            db, client, options, alerts: null, clock: new FixedClock(Now), log: null);

        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ok);
        Assert.Contains("No Zoho access token", result.FailureReason);
        Assert.DoesNotContain("attempts", result.FailureReason);
    }
}
