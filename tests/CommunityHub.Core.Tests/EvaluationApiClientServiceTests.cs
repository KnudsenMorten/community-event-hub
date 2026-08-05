using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §747 C8 — the SERVICE credentials external systems present to pull reports.
/// </summary>
/// <remarks>
/// These reports quote verbatim attendee comments, which the brief calls the most sensitive data in
/// the system. Every test here pins a property that keeps a key from reaching further than intended.
/// </remarks>
public sealed class EvaluationApiClientServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DateTimeOffset Now = new(2027, 2, 9, 9, 30, 0, TimeSpan.Zero);

    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
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
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Issued_key_authenticates()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));

        var (client, key) = await svc.IssueAsync(EventId, "Vendor report sync");

        var authed = await svc.AuthenticateAsync(EventId, key);
        Assert.NotNull(authed);
        Assert.Equal(client.Id, authed!.Id);
    }

    /// <summary>🔒 The key itself is never stored — a database leak must yield no working credential.</summary>
    [Fact]
    public async Task Plaintext_key_is_never_stored()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));

        var (client, key) = await svc.IssueAsync(EventId, "Vendor report sync");

        Assert.DoesNotContain(key, client.KeyHash, StringComparison.Ordinal);
        Assert.Contains(':', client.KeyHash);   // salt:hash
    }

    [Fact]
    public async Task Wrong_key_is_refused()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));
        await svc.IssueAsync(EventId, "Vendor report sync");

        Assert.Null(await svc.AuthenticateAsync(EventId, EvaluationIngestService.NewKey()));
        Assert.Null(await svc.AuthenticateAsync(EventId, ""));
        Assert.Null(await svc.AuthenticateAsync(EventId, null));
    }

    /// <summary>
    /// 🔒 The brief's rule: authorise PER EVENT even while there is one event. A valid key aimed at
    /// another edition is refused — this is the constraint that keeps the extraction viable.
    /// </summary>
    [Fact]
    public async Task Key_does_not_work_across_events()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));
        var (_, key) = await svc.IssueAsync(EventId, "Vendor report sync");

        Assert.NotNull(await svc.AuthenticateAsync(EventId, key));
        Assert.Null(await svc.AuthenticateAsync(OtherEventId, key));
    }

    /// <summary>Revocation must work without a deploy, and must take effect immediately.</summary>
    [Fact]
    public async Task Revoked_credential_is_refused()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));
        var (client, key) = await svc.IssueAsync(EventId, "Vendor report sync");

        Assert.True(await svc.SetActiveAsync(EventId, client.Id, false));
        Assert.Null(await svc.AuthenticateAsync(EventId, key));

        Assert.True(await svc.SetActiveAsync(EventId, client.Id, true));
        Assert.NotNull(await svc.AuthenticateAsync(EventId, key));
    }

    /// <summary>
    /// 🔒 Rotation must not cut the consumer off mid-flight: the OLD key keeps working until it is
    /// explicitly retired. A consumer is usually someone else's scheduled job, and we cannot make
    /// both sides switch in the same second.
    /// </summary>
    [Fact]
    public async Task Rotation_keeps_the_old_key_working_until_retired()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));
        var (client, oldKey) = await svc.IssueAsync(EventId, "Vendor report sync");

        var newKey = await svc.RotateAsync(EventId, client.Id);
        Assert.NotNull(newKey);
        Assert.NotEqual(oldKey, newKey);

        Assert.NotNull(await svc.AuthenticateAsync(EventId, newKey));
        Assert.NotNull(await svc.AuthenticateAsync(EventId, oldKey));   // still accepted

        Assert.True(await svc.RetirePreviousAsync(EventId, client.Id));

        Assert.NotNull(await svc.AuthenticateAsync(EventId, newKey));
        Assert.Null(await svc.AuthenticateAsync(EventId, oldKey));      // now closed
    }

    /// <summary>
    /// 🔑 The only way to answer "is anything still using this?" before revoking it. Recorded on
    /// success only — a refused attempt must not make a dead credential look alive.
    /// </summary>
    [Fact]
    public async Task LastUsedAt_is_stamped_on_success_only()
    {
        var db = await SeedAsync();
        var clock = new FixedClock(Now);
        var svc = new EvaluationApiClientService(db, clock);
        var (client, key) = await svc.IssueAsync(EventId, "Vendor report sync");

        Assert.Null(client.LastUsedAt);

        await svc.AuthenticateAsync(EventId, EvaluationIngestService.NewKey());
        Assert.Null(client.LastUsedAt);

        var used = Now.AddHours(3);
        clock.Set(used);
        await svc.AuthenticateAsync(EventId, key);
        Assert.Equal(used, client.LastUsedAt);
    }

    /// <summary>Rotating or revoking another event's credential must not be possible by id alone.</summary>
    [Fact]
    public async Task Management_is_scoped_to_the_event()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));
        var (client, key) = await svc.IssueAsync(EventId, "Vendor report sync");

        Assert.Null(await svc.RotateAsync(OtherEventId, client.Id));
        Assert.False(await svc.RetirePreviousAsync(OtherEventId, client.Id));
        Assert.False(await svc.SetActiveAsync(OtherEventId, client.Id, false));

        // Untouched by all three.
        Assert.NotNull(await svc.AuthenticateAsync(EventId, key));
    }

    [Fact]
    public async Task List_returns_only_this_events_credentials()
    {
        var db = await SeedAsync();
        var svc = new EvaluationApiClientService(db, new FixedClock(Now));
        await svc.IssueAsync(EventId, "Vendor report sync");
        await svc.IssueAsync(OtherEventId, "Someone else");

        var mine = await svc.ListAsync(EventId);
        Assert.Single(mine);
        Assert.Equal("Vendor report sync", mine[0].Name);
    }
}
