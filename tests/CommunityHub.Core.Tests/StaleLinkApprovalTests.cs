using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §559 — the APPROVE action for a stale Backstage link, the one thing this queue does that cannot
/// be undone.
/// </summary>
/// <remarks>
/// <para>Operator: <i>"you can use this queue for this purpose"</i>, after <i>"no clue where to find
/// the place to approve"</i>. Until this existed, the §555 detection could only send a mail that
/// admitted <b>an approve button is not built yet</b> — so 9 sessions sat neither updatable nor
/// re-creatable, and he was clearing ids by hand: <i>"reset zoho field for the deleted sessions so
/// it recreates again, as the sync approver is still not build"</i>.</para>
///
/// <para>🔒 <b>Approving IS a create.</b> Clearing the id is what makes the next push create the
/// session, and the Backstage sessions API has no delete — so a wrong clear duplicates the live
/// agenda permanently. The two guards below are the whole reason this is safe to put behind a
/// button.</para>
/// </remarks>
public sealed class StaleLinkApprovalTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 2, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SyncDeltaQueueService NewService(CommunityHubDbContext db) =>
        new(db, clock: new FixedClock(Now));

    private static async Task<int> SeedAsync(CommunityHubDbContext db, string? backstageId)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();

        var s = new Session
        {
            EventId = EventId, SessionizeId = "sz-1", Title = "Securing Azure",
            BackstageSessionId = backstageId,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    // ---------- the happy path he could not reach before ----------

    [Fact]
    public async Task Approving_CLEARS_the_dead_link_so_the_next_push_re_creates_the_session()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        await svc.EnqueueStaleLinkAsync(
            EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
            "bs-dead", SessionSyncDirection.CehToZoho);

        var pending = Assert.Single(await svc.ListPendingAsync(EventId));
        Assert.Equal(SyncDeltaChangeKind.StaleLink, pending.ChangeKind);

        var result = await svc.ApproveAsync(pending.Id, "mok@expertslive.dk");

        Assert.True(result.Applied);
        Assert.Null((await db.Sessions.FindAsync(sid))!.BackstageSessionId);
    }

    [Fact]
    public async Task An_approved_stale_link_is_recorded_as_APPLIED_not_merely_acknowledged()
    {
        // A real write happened. Showing it as a bare acknowledgement in the audit section would
        // misdescribe the one irreversible action in this queue.
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        var d = await svc.EnqueueStaleLinkAsync(
            EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
            "bs-dead", SessionSyncDirection.CehToZoho);
        await svc.ApproveAsync(d.Id, "mok@expertslive.dk");

        Assert.Equal(SyncDeltaStatus.Applied, (await svc.GetAsync(d.Id))!.Status);
    }

    [Fact]
    public async Task Rejecting_leaves_the_link_exactly_as_it_was()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        var d = await svc.EnqueueStaleLinkAsync(
            EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
            "bs-dead", SessionSyncDirection.CehToZoho);
        await svc.RejectAsync(d.Id, "mok@expertslive.dk", "I will fix it in Zoho instead");

        Assert.Equal("bs-dead", (await db.Sessions.FindAsync(sid))!.BackstageSessionId);
    }

    // ---------- 🔒 the two guards that make approving safe ----------

    /// <summary>
    /// GUARD 1. Time passes between detection and approval. If the session has been re-linked to a
    /// DIFFERENT id since, that id is LIVE — erasing it would duplicate a session that is perfectly
    /// fine, permanently. §553's lesson exactly: a stale read must never drive a write.
    /// </summary>
    [Fact]
    public async Task A_session_RE_LINKED_since_detection_is_NOT_cleared()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        var d = await svc.EnqueueStaleLinkAsync(
            EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
            "bs-dead", SessionSyncDirection.CehToZoho);

        // Meanwhile the push re-created it and stored a NEW, live id.
        var session = await db.Sessions.FindAsync(sid);
        session!.BackstageSessionId = "bs-alive";
        await db.SaveChangesAsync();

        var result = await svc.ApproveAsync(d.Id, "mok@expertslive.dk");

        Assert.False(result.Applied);
        Assert.Equal("bs-alive", (await db.Sessions.FindAsync(sid))!.BackstageSessionId);
        Assert.Contains("could duplicate", result.Message);
    }

    /// <summary>
    /// GUARD 2. He has done this by hand more than once. An already-cleared link IS the desired end
    /// state, so the row must close cleanly rather than fail and leave him wondering whether the
    /// button or his own edit took effect.
    /// </summary>
    [Fact]
    public async Task An_ALREADY_cleared_link_closes_cleanly_instead_of_failing()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        var d = await svc.EnqueueStaleLinkAsync(
            EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
            "bs-dead", SessionSyncDirection.CehToZoho);

        var session = await db.Sessions.FindAsync(sid);
        session!.BackstageSessionId = null;   // cleared by hand
        await db.SaveChangesAsync();

        var result = await svc.ApproveAsync(d.Id, "mok@expertslive.dk");

        Assert.True(result.Applied);
        Assert.Contains("already cleared", result.Message);
    }

    /// <summary>
    /// The detection runs every 5 minutes. Without dedupe, 9 stale links would become thousands of
    /// queue rows in a day — the §302 "70-mail night" as a UI instead of an inbox.
    /// </summary>
    [Fact]
    public async Task Re_detecting_the_same_stale_link_updates_ONE_row_instead_of_stacking()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        for (var pass = 0; pass < 12; pass++)
        {
            await svc.EnqueueStaleLinkAsync(
                EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
                "bs-dead", SessionSyncDirection.CehToZoho);
        }

        Assert.Single(await svc.ListPendingAsync(EventId));
    }

    /// <summary>
    /// The queue row must SHOW what approving will erase — it is the only chance to notice that the
    /// wrong id is about to be cleared.
    /// </summary>
    [Fact]
    public async Task The_row_carries_the_dead_id_so_the_page_can_show_it()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedAsync(db, "bs-dead");
        var svc = NewService(db);

        var d = await svc.EnqueueStaleLinkAsync(
            EventId, SyncDeltaEntityType.Session, sid.ToString(), "Securing Azure",
            "bs-dead", SessionSyncDirection.CehToZoho);

        var change = Assert.Single(d.Changes);
        Assert.Equal(SyncDeltaQueueService.FieldBackstageId, change.Field);
        Assert.Equal("bs-dead", change.OldValue);
    }
}
