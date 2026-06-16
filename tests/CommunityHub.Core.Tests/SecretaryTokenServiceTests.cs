using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SecretaryTokenService"/> — the write-scoped,
/// time-bound, revocable, single-person secretary grant. Asserts: a freshly
/// issued grant resolves to exactly its participant; revoke + expiry kill the
/// link; a withdrawn / not-yet-activated participant's link never resolves;
/// cross-edition issuance is rejected.
/// </summary>
public sealed class SecretaryTokenServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sectok-{Guid.NewGuid():N}")
            .Options);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Participant Active(int eventId, string email) => new()
    {
        EventId = eventId,
        Email = email,
        FullName = email.Split('@')[0],
        Role = ParticipantRole.Speaker,
        IsActive = true,
        LifecycleState = ParticipantLifecycleState.Active,
    };

    [Fact]
    public async Task Issued_grant_resolves_to_its_one_participant()
    {
        using var db = NewDb();
        var p = Active(EventId, "vp@example.test");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var clock = new FakeClock();
        var svc = new SecretaryTokenService(db, clock);

        var grant = await svc.IssueAsync(EventId, p.Id, "VP assistant", "org@example.test");
        Assert.NotNull(grant);
        Assert.False(string.IsNullOrWhiteSpace(grant!.Token));

        var resolved = await svc.ResolveAsync(grant.Token);
        Assert.NotNull(resolved);
        Assert.Equal(p.Id, resolved!.ParticipantId);     // single-person scope
        Assert.NotNull(resolved.LastUsedAt);             // use is stamped
    }

    [Fact]
    public async Task Revoked_grant_stops_resolving()
    {
        using var db = NewDb();
        var p = Active(EventId, "vp@example.test");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var clock = new FakeClock();
        var svc = new SecretaryTokenService(db, clock);
        var grant = await svc.IssueAsync(EventId, p.Id, null, null);

        Assert.NotNull(await svc.ResolveAsync(grant!.Token));   // valid before revoke
        var revoked = await svc.RevokeAsync(EventId, grant.Id);
        Assert.True(revoked);
        Assert.Null(await svc.ResolveAsync(grant.Token));       // dead after revoke
    }

    [Fact]
    public async Task Revoke_is_scoped_to_the_edition()
    {
        using var db = NewDb();
        var p = Active(EventId, "vp@example.test");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var svc = new SecretaryTokenService(db, new FakeClock());
        var grant = await svc.IssueAsync(EventId, p.Id, null, null);

        // Another edition cannot revoke this grant.
        Assert.False(await svc.RevokeAsync(OtherEventId, grant!.Id));
        Assert.NotNull(await svc.ResolveAsync(grant.Token));    // still valid
    }

    [Fact]
    public async Task Expired_grant_stops_resolving()
    {
        using var db = NewDb();
        var p = Active(EventId, "vp@example.test");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var clock = new FakeClock();
        var svc = new SecretaryTokenService(db, clock);
        var grant = await svc.IssueAsync(EventId, p.Id, null, null, TimeSpan.FromDays(1));

        Assert.NotNull(await svc.ResolveAsync(grant!.Token));   // within window

        clock.Now = clock.Now.AddDays(2);                       // past expiry
        Assert.Null(await svc.ResolveAsync(grant.Token));
    }

    [Fact]
    public async Task Withdrawn_or_not_yet_active_participant_link_never_resolves()
    {
        using var db = NewDb();
        var withdrawn = Active(EventId, "gone@example.test");
        withdrawn.IsActive = false;                             // cancelled
        var queued = Active(EventId, "queued@example.test");
        queued.LifecycleState = ParticipantLifecycleState.Preselected; // not activated
        db.Participants.AddRange(withdrawn, queued);
        await db.SaveChangesAsync();

        var svc = new SecretaryTokenService(db, new FakeClock());
        var g1 = await svc.IssueAsync(EventId, withdrawn.Id, null, null);
        var g2 = await svc.IssueAsync(EventId, queued.Id, null, null);

        Assert.Null(await svc.ResolveAsync(g1!.Token));         // IsActive=false gate
        Assert.Null(await svc.ResolveAsync(g2!.Token));         // LifecycleState gate
    }

    [Fact]
    public async Task Issue_rejects_a_participant_from_another_edition()
    {
        using var db = NewDb();
        var theirs = Active(OtherEventId, "theirs@example.test");
        db.Participants.Add(theirs);
        await db.SaveChangesAsync();

        var svc = new SecretaryTokenService(db, new FakeClock());
        var grant = await svc.IssueAsync(EventId, theirs.Id, null, null);
        Assert.Null(grant);                                    // not in this edition
    }

    [Fact]
    public async Task Unknown_or_blank_token_resolves_to_null()
    {
        using var db = NewDb();
        var svc = new SecretaryTokenService(db, new FakeClock());
        Assert.Null(await svc.ResolveAsync(null));
        Assert.Null(await svc.ResolveAsync("   "));
        Assert.Null(await svc.ResolveAsync("not-a-real-token"));
    }
}
