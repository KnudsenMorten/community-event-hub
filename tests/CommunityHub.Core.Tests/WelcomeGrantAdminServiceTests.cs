using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Organizer admin + housekeeping for welcome auto-login grants
/// (<see cref="WelcomeGrantAdminService"/>): list with computed state, edition-
/// scoped idempotent revoke, and retention-window prune of dead grants. Offline:
/// EF in-memory, a fixed clock passed as the <c>now</c> argument.
/// </summary>
public class WelcomeGrantAdminServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    private static async Task<int> SeedEventWithParticipantAsync(
        CommunityHubDbContext db, string name = "Test Person")
    {
        var ev = new Event
        {
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            Code = "TC27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id,
            Email = "person@example.com",
            FullName = name,
            Role = ParticipantRole.Speaker,
            IsActive = true,
            IsTestUser = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static MagicLinkGrant Grant(
        int eventId, int participantId, DateTimeOffset created, DateTimeOffset expires,
        DateTimeOffset? consumed = null, DateTimeOffset? revoked = null,
        string email = "person@example.com") => new()
    {
        EventId = eventId,
        ParticipantId = participantId,
        Role = ParticipantRole.Speaker,
        Purpose = WelcomeAutoLoginTokenService.PurposeName,
        TokenIdHash = WelcomeAutoLoginTokenService.HashTokenId(
            $"tok-{created.Ticks}-{expires.Ticks}-{consumed?.Ticks}-{revoked?.Ticks}"),
        RecipientEmail = email,
        CreatedAt = created,
        ExpiresAt = expires,
        ConsumedAt = consumed,
        RevokedAt = revoked,
    };

    [Fact]
    public async Task List_returns_newest_first_with_each_state()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventWithParticipantAsync(db);
        var pid = db.Participants.First().Id;

        db.MagicLinkGrants.AddRange(
            Grant(ev, pid, Now.AddDays(-1), Now.AddDays(2)),                       // active
            Grant(ev, pid, Now.AddDays(-2), Now.AddDays(1), consumed: Now.AddDays(-1)), // consumed
            Grant(ev, pid, Now.AddDays(-3), Now.AddDays(1), revoked: Now.AddDays(-2)),  // revoked
            Grant(ev, pid, Now.AddDays(-4), Now.AddDays(-1)));                     // expired
        await db.SaveChangesAsync();

        var svc = new WelcomeGrantAdminService(db);
        var rows = await svc.ListAsync(ev, Now);

        Assert.Equal(4, rows.Count);
        // Newest first.
        Assert.True(rows[0].CreatedAt >= rows[1].CreatedAt);
        Assert.Contains(rows, r => r.State == WelcomeGrantAdminService.GrantState.Active);
        Assert.Contains(rows, r => r.State == WelcomeGrantAdminService.GrantState.Consumed);
        Assert.Contains(rows, r => r.State == WelcomeGrantAdminService.GrantState.Revoked);
        Assert.Contains(rows, r => r.State == WelcomeGrantAdminService.GrantState.Expired);
        // Only the active grant is revocable.
        Assert.Single(rows, r => r.CanRevoke);
    }

    [Fact]
    public async Task List_activeOnly_returns_just_redeemable_grants()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventWithParticipantAsync(db);
        var pid = db.Participants.First().Id;
        db.MagicLinkGrants.AddRange(
            Grant(ev, pid, Now.AddDays(-1), Now.AddDays(2)),                  // active
            Grant(ev, pid, Now.AddDays(-2), Now.AddDays(-1)));               // expired
        await db.SaveChangesAsync();

        var svc = new WelcomeGrantAdminService(db);
        var rows = await svc.ListAsync(ev, Now, activeOnly: true);

        Assert.Single(rows);
        Assert.Equal(WelcomeGrantAdminService.GrantState.Active, rows[0].State);
    }

    [Fact]
    public async Task Revoke_stamps_revoked_and_is_idempotent_and_scoped()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventWithParticipantAsync(db);
        var pid = db.Participants.First().Id;
        var g = Grant(ev, pid, Now.AddDays(-1), Now.AddDays(2));
        db.MagicLinkGrants.Add(g);
        await db.SaveChangesAsync();

        var svc = new WelcomeGrantAdminService(db);

        Assert.True(await svc.RevokeAsync(ev, g.Id, Now));      // first revoke succeeds
        Assert.NotNull((await db.MagicLinkGrants.FindAsync(g.Id))!.RevokedAt);
        Assert.False(await svc.RevokeAsync(ev, g.Id, Now));     // already revoked -> no-op
        Assert.False(await svc.RevokeAsync(ev + 999, g.Id, Now)); // wrong edition -> no-op
    }

    [Fact]
    public async Task Revoke_refuses_an_already_consumed_grant()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventWithParticipantAsync(db);
        var pid = db.Participants.First().Id;
        var g = Grant(ev, pid, Now.AddDays(-1), Now.AddDays(2), consumed: Now.AddHours(-1));
        db.MagicLinkGrants.Add(g);
        await db.SaveChangesAsync();

        var svc = new WelcomeGrantAdminService(db);
        Assert.False(await svc.RevokeAsync(ev, g.Id, Now));   // a used link is never "un-consumed"
        Assert.Null((await db.MagicLinkGrants.FindAsync(g.Id))!.RevokedAt);
    }

    [Fact]
    public async Task Prune_removes_dead_old_grants_but_keeps_active_and_recent()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventWithParticipantAsync(db);
        var pid = db.Participants.First().Id;
        var old = Now.AddDays(-40); // older than the 30-day default retention

        var activeOld   = Grant(ev, pid, old, Now.AddDays(5));                       // active -> KEEP
        var deadOld     = Grant(ev, pid, old, old.AddDays(1), consumed: old.AddDays(1)); // dead+old -> PRUNE
        var expiredOld  = Grant(ev, pid, old, old.AddDays(1));                       // expired+old -> PRUNE
        var deadRecent  = Grant(ev, pid, Now.AddDays(-2), Now.AddDays(-1), revoked: Now.AddDays(-1)); // dead but recent -> KEEP
        db.MagicLinkGrants.AddRange(activeOld, deadOld, expiredOld, deadRecent);
        await db.SaveChangesAsync();

        var svc = new WelcomeGrantAdminService(db);
        var removed = await svc.PruneAsync(Now);

        Assert.Equal(2, removed);
        var remaining = db.MagicLinkGrants.Select(g => g.Id).ToHashSet();
        Assert.Contains(activeOld.Id, remaining);
        Assert.Contains(deadRecent.Id, remaining);
        Assert.DoesNotContain(deadOld.Id, remaining);
        Assert.DoesNotContain(expiredOld.Id, remaining);
    }
}
