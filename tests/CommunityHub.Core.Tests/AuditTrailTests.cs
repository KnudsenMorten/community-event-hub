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

    // §193: the per-user calendar FEED + its token service (CalendarFeedTokenService)
    // were removed, so the calendar-subscribe audit tests are gone with them.
}
