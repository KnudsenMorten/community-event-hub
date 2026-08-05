using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.10 / §768.10 D5 — a submitted travel claim freezes, and an organizer can reopen it.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The freeze and the reopen are one feature, not two.</b> Work-order §6.10 asked for
/// the lock and then named its consequence: a speaker who submits after uploading 3 of 5 receipts is
/// locked out <b>with no recovery path</b>. Shipping the freeze without the reopen would build that
/// trap deliberately — which is why every test here that pins the lock has a partner pinning the way
/// out of it.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class TravelClaimLockTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 18, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = Now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"claimlock-{Guid.NewGuid():N}")
            .Options);

    /// <summary>
    /// A lock with the freeze ENFORCED — which is not the shipped default (§769.7): the feature went
    /// to production switched off so it could be validated before it changed any speaker's
    /// experience. These tests are about the RULE, so they turn it on explicitly.
    /// </summary>
    private static TravelClaimLock NewLock(CommunityHubDbContext db, TimeProvider clock) =>
        new(db, clock, new TravelClaimLockOptions { FreezeEnabled = true });

    private static async Task<int> AddClaimAsync(
        CommunityHubDbContext db, string name, DateTimeOffset? submittedAt = null)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{Guid.NewGuid():N}@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.TravelReimbursements.Add(new TravelReimbursement
        {
            EventId = EventId, ParticipantId = p.Id,
            RequestReimbursement = true, ClaimAmountEur = 400,
            SubmittedAt = submittedAt,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task An_unsubmitted_claim_is_NOT_frozen()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Ada Lovelace");
        var sut = NewLock(db, new FixedClock());

        Assert.False(await sut.IsFrozenAsync(EventId, pid));
    }

    [Fact]
    public async Task Submitting_freezes_the_claim_and_stamps_WHEN()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Ada Lovelace");
        var sut = NewLock(db, new FixedClock());

        Assert.True(await sut.MarkSubmittedAsync(EventId, pid));
        Assert.True(await sut.IsFrozenAsync(EventId, pid));

        // 🔑 A timestamp, not a bool: "when did they submit?" is what an organizer asks when a
        // receipt is missing after the deadline, and a bool cannot answer it afterwards.
        var row = await db.TravelReimbursements.AsNoTracking().SingleAsync();
        Assert.Equal(Now, row.SubmittedAt);
    }

    [Fact]
    public async Task Submitting_twice_does_not_move_the_stamp()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Ada Lovelace");
        var clock = new FixedClock();
        var sut = NewLock(db, clock);

        await sut.MarkSubmittedAsync(EventId, pid);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.False(await sut.MarkSubmittedAsync(EventId, pid));   // no-op, reported honestly

        var row = await db.TravelReimbursements.AsNoTracking().SingleAsync();
        Assert.Equal(Now, row.SubmittedAt);                          // the ORIGINAL submission
    }

    /// <summary>
    /// 🔒 The recovery path. Without this, the freeze is the trap the work order warned about.
    /// </summary>
    [Fact]
    public async Task An_organizer_can_REOPEN_a_submitted_claim_and_it_is_audit_logged()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Ada Lovelace");
        var clock = new FixedClock();
        var sut = NewLock(db, clock);
        await sut.MarkSubmittedAsync(EventId, pid);

        clock.Advance(TimeSpan.FromHours(26));
        Assert.True(await sut.ReopenAsync(EventId, pid, "olivia@example.test"));

        // Genuinely open again — every write path asks ONE question, so this must be the same one.
        Assert.False(await sut.IsFrozenAsync(EventId, pid));

        var row = await db.TravelReimbursements.AsNoTracking().SingleAsync();
        Assert.Null(row.SubmittedAt);
        Assert.Equal(1, row.ReopenCount);
        Assert.Equal("olivia@example.test", row.ReopenedByEmail);

        var audit = await db.AuditEntries.AsNoTracking().SingleAsync();
        Assert.Equal("travel-claim.reopen", audit.Action);
        Assert.Equal("olivia@example.test", audit.ActorEmail);
        Assert.Equal(pid.ToString(), audit.TargetId);
        Assert.Contains("26", audit.Detail!);        // how long it sat submitted
    }

    [Fact]
    public async Task Reopening_something_that_was_never_submitted_changes_nothing()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Grace Hopper");
        var sut = NewLock(db, new FixedClock());

        Assert.False(await sut.ReopenAsync(EventId, pid, "olivia@example.test"));

        // No audit noise for a no-op: a trail full of events that did nothing hides the one that did.
        Assert.Empty(await db.AuditEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_reopened_claim_can_be_submitted_again_and_the_reopens_are_counted()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Ada Lovelace");
        var clock = new FixedClock();
        var sut = NewLock(db, clock);

        await sut.MarkSubmittedAsync(EventId, pid);
        await sut.ReopenAsync(EventId, pid, "olivia@example.test");
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await sut.MarkSubmittedAsync(EventId, pid));     // the whole point: they re-submit
        await sut.ReopenAsync(EventId, pid, "olivia@example.test");

        var row = await db.TravelReimbursements.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.ReopenCount);   // a claim reopened repeatedly is a pattern worth seeing
        Assert.False(await sut.IsFrozenAsync(EventId, pid));
    }

    /// <summary>
    /// 🔒 §769.7 — THE SHIPPED DEFAULT. The freeze went to production switched OFF, so it could be
    /// deployed alongside everything else without changing what any speaker can do until the
    /// operator had validated it.
    /// </summary>
    [Fact]
    public async Task With_the_switch_OFF_nobody_is_ever_frozen_but_the_submission_is_still_RECORDED()
    {
        using var db = NewDb();
        var pid = await AddClaimAsync(db, "Ada Lovelace");
        // The default options — exactly what production gets until TravelClaim:FreezeEnabled=true.
        var sut = new TravelClaimLock(db, new FixedClock());

        Assert.False(sut.Enforcing);
        Assert.True(await sut.MarkSubmittedAsync(EventId, pid));
        Assert.False(await sut.IsFrozenAsync(EventId, pid));      // nothing is locked

        // 🔑 The stamp is still written. WHEN somebody submitted is true either way and worth having
        // from day one; only the enforcement waits for the switch.
        var row = await db.TravelReimbursements.AsNoTracking().SingleAsync();
        Assert.Equal(Now, row.SubmittedAt);
    }

    [Fact]
    public async Task One_speakers_submission_never_freezes_anothers_claim()
    {
        using var db = NewDb();
        var ada = await AddClaimAsync(db, "Ada Lovelace");
        var grace = await AddClaimAsync(db, "Grace Hopper");
        var sut = NewLock(db, new FixedClock());

        await sut.MarkSubmittedAsync(EventId, ada);

        Assert.True(await sut.IsFrozenAsync(EventId, ada));
        Assert.False(await sut.IsFrozenAsync(EventId, grace));
    }
}
