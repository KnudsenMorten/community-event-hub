using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §24 unified audit trail: the writer records append-only entries, and
/// the high-value engine event (calendar subscribe) is captured at its source — once,
/// on the first token mint, not on idempotent re-calls.
/// </summary>
public sealed class AuditTrailTests
{
    private const int EventId = 9;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-22T09:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"audit-{Guid.NewGuid():N}").Options);

    [Fact]
    public async Task RecordAsync_appends_an_entry_and_stamps_time_when_unset()
    {
        using var db = NewDb();
        var svc = new AuditTrailService(db, new FixedClock());

        await svc.RecordAsync(new AuditEntry
        {
            EventId = EventId,
            Category = AuditCategory.Admin,
            Action = "settings.change",
            ActorEmail = "org@x",
            Summary = "Changed a setting",
        });

        var row = await db.AuditEntries.SingleAsync();
        Assert.Equal(EventId, row.EventId);
        Assert.Equal(AuditCategory.Admin, row.Category);
        Assert.Equal("settings.change", row.Action);
        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Equal(DateTimeOffset.Parse("2026-06-22T09:00:00Z"), row.OccurredUtc);
    }

    [Fact]
    public async Task RecordAsync_never_throws_into_the_caller()
    {
        using var db = NewDb();
        db.Dispose();   // force the context unusable
        var svc = new AuditTrailService(db, new FixedClock());

        // Must swallow its own write error — auditing is observational.
        await svc.RecordAsync(new AuditEntry { EventId = EventId, Action = "x", ActorEmail = "a" });
    }

    [Fact]
    public async Task PurgeOlderThanAsync_deletes_only_entries_before_the_cutoff()
    {
        using var db = NewDb();
        var svc = new AuditTrailService(db, new FixedClock());
        await svc.RecordAsync(new AuditEntry { EventId = EventId, Action = "old", ActorEmail = "a",
            OccurredUtc = DateTimeOffset.Parse("2025-06-01T00:00:00Z") });   // older than the 6mo cutoff
        await svc.RecordAsync(new AuditEntry { EventId = EventId, Action = "recent", ActorEmail = "a",
            OccurredUtc = DateTimeOffset.Parse("2026-06-20T00:00:00Z") });   // within window

        var cutoff = DateTimeOffset.Parse("2026-06-22T09:00:00Z").AddMonths(-6); // 2025-12-22
        var removed = await svc.PurgeOlderThanAsync(cutoff);

        Assert.Equal(1, removed);
        var rows = await db.AuditEntries.Select(e => e.Action).ToListAsync();
        Assert.Equal(new[] { "recent" }, rows);
    }

    [Fact]
    public async Task Calendar_subscribe_is_audited_once_on_first_mint_only()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        var p = new Participant { EventId = EventId, Email = "spk@x", FullName = "Spk", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var audit = new AuditTrailService(db, new FixedClock());
        var svc = new CalendarFeedTokenService(db, audit);

        var t1 = await svc.EnsureTokenAsync(p.Id);
        var t2 = await svc.EnsureTokenAsync(p.Id);   // idempotent — no second audit row
        Assert.Equal(t1, t2);

        var subs = await db.AuditEntries
            .Where(e => e.Category == AuditCategory.CalendarSync && e.Action == AuditActions.CalendarSubscribe)
            .ToListAsync();
        Assert.Single(subs);
        Assert.Equal(p.Id, subs[0].ActorParticipantId);
        Assert.Equal("spk@x", subs[0].ActorEmail);

        // Regenerating (revoke + reissue) is audited as a token reset.
        await svc.RegenerateTokenAsync(p.Id);
        Assert.True(await db.AuditEntries.AnyAsync(e => e.Action == AuditActions.CalendarTokenReset));
    }

    [Fact]
    public async Task Calendar_token_service_works_without_an_audit_sink()
    {
        // The audit dependency is optional (legacy/test wiring) — minting still works.
        using var db = NewDb();
        var p = new Participant { EventId = EventId, Email = "v@x", FullName = "V", Role = ParticipantRole.Volunteer, IsActive = true };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var svc = new CalendarFeedTokenService(db);   // no audit sink
        var token = await svc.EnsureTokenAsync(p.Id);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.False(await db.AuditEntries.AnyAsync());
    }
}
