using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ImpersonationAuditService"/> — the acting-as
/// audit trail. Asserts records are written with the right actor/target/action,
/// returned newest-first, and scoped to the edition.
/// </summary>
public sealed class ImpersonationAuditServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"imp-audit-{Guid.NewGuid():N}")
            .Options);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Records_an_organizer_start_with_actor_and_target()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var svc = new ImpersonationAuditService(db, clock);

        await svc.RecordAsync(
            EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: 10, actorLabel: "Org Person (org@example.test)",
            targetParticipantId: 42, action: ImpersonationAuditService.ActionStart,
            detail: "switched in");

        var row = await db.ImpersonationAudits.SingleAsync();
        Assert.Equal(ImpersonationActorKind.Organizer, row.ActorKind);
        Assert.Equal(10, row.ActorParticipantId);
        Assert.Equal(42, row.TargetParticipantId);
        Assert.Equal(ImpersonationAuditService.ActionStart, row.Action);
    }

    [Fact]
    public async Task Records_a_secretary_use_with_no_actor_participant()
    {
        using var db = NewDb();
        var svc = new ImpersonationAuditService(db, new FakeClock());

        await svc.RecordAsync(
            EventId, ImpersonationActorKind.SecretaryToken,
            actorParticipantId: null, actorLabel: "Secretary for VP",
            targetParticipantId: 7, action: ImpersonationAuditService.ActionSecretaryUse);

        var row = await db.ImpersonationAudits.SingleAsync();
        Assert.Equal(ImpersonationActorKind.SecretaryToken, row.ActorKind);
        Assert.Null(row.ActorParticipantId);
        Assert.Equal(7, row.TargetParticipantId);
    }

    [Fact]
    public async Task Recent_is_newest_first_and_edition_scoped()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var svc = new ImpersonationAuditService(db, clock);

        await svc.RecordAsync(EventId, ImpersonationActorKind.Organizer, 1, "a", 5,
            ImpersonationAuditService.ActionStart);
        clock.Now = clock.Now.AddMinutes(5);
        await svc.RecordAsync(EventId, ImpersonationActorKind.Organizer, 1, "a", 5,
            ImpersonationAuditService.ActionReturn);
        // A different edition's entry must not appear.
        await svc.RecordAsync(OtherEventId, ImpersonationActorKind.Organizer, 9, "b", 6,
            ImpersonationAuditService.ActionStart);

        var recent = await svc.RecentAsync(EventId);
        Assert.Equal(2, recent.Count);
        Assert.Equal(ImpersonationAuditService.ActionReturn, recent[0].Action); // newest first
    }
}
