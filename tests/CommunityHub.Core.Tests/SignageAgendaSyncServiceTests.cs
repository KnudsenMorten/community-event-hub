using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Signage;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Signage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §754 deliverable 1 — the signage agenda mirror. These tests are mostly about what the sync
/// REFUSES to do: the screens must degrade to a stale agenda, never to an empty one.
/// </summary>
public sealed class SignageAgendaSyncServiceTests
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
            .UseInMemoryDatabase($"signage-{Guid.NewGuid():N}").Options);

    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new InvalidOperationException("The pull override must be used; no HTTP in these tests.");
    }

    private static SignageAgendaSyncService NewService(
        CommunityHubDbContext db,
        Func<CancellationToken, Task<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>> pull,
        bool zohoEnabled = true)
    {
        var options = new ZohoOptions
        {
            Enabled = zohoEnabled,
            ApiDomain = "https://zoho.test",
            BackstagePortalId = "P1",
            BackstageEventId = "E1",
        };
        var client = new ZohoClient(new HttpClient(new DeadHandler()), options, NullLogger<ZohoClient>.Instance);
        return new SignageAgendaSyncService(
            db, client, options, alerts: null, clock: new FixedClock(Now),
            log: null, pullOverride: pull);
    }

    private static ZohoClient.BackstageAgendaActivity Activity(
        string id, string title = "Talk", int hour = 9, int duration = 60,
        string? room = "Hall One", string? track = "Cloud", string? type = "PRESENTATION",
        string[]? speakers = null, int day = 1) =>
        new(id, title, new DateTimeOffset(2027, 2, 9, hour, 0, 0, TimeSpan.Zero), duration,
            room, track, type, speakers ?? new[] { "Ada Lovelace" }, day);

    private static async Task SeedAsync(CommunityHubDbContext db, params AgendaActivity[] rows)
    {
        db.AgendaActivities.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AgendaActivity Cached(string id, string title = "Old title") => new()
    {
        EventId = EventId,
        BackstageSessionId = id,
        Title = title,
        StartsAt = new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
        Room = "Hall One",
        Track = "Cloud",
        ActivityType = "PRESENTATION",
        Speakers = "Ada Lovelace",
        DayIndex = 1,
        LastSyncedAt = Now.AddHours(-2),
    };

    [Fact]
    public async Task Every_usable_activity_is_cached_with_a_derived_end_time()
    {
        using var db = NewDb();
        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[]
            {
                Activity("s1", "Opening Keynote", hour: 8, duration: 60, room: null, track: null, type: "KEYNOTE"),
                Activity("s2", "Identity Master Class", hour: 9, duration: 420),
                Activity("s3", "Lunch", hour: 12, duration: 60, room: "Foyer", track: null,
                    type: "BREAK", speakers: Array.Empty<string>()),
            }));

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ok);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.SkippedUnusable);

        var rows = await db.AgendaActivities.OrderBy(a => a.StartsAt).ToListAsync();
        Assert.Equal(3, rows.Count);

        // End time is start + duration — Backstage sends no end at all.
        var masterClass = rows.Single(a => a.BackstageSessionId == "s2");
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero), masterClass.EndsAt);
        Assert.Equal(Now, masterClass.LastSyncedAt);

        // A break is a first-class activity here — that is the whole reason this table exists.
        var lunch = rows.Single(a => a.BackstageSessionId == "s3");
        Assert.Equal("BREAK", lunch.ActivityType);
        Assert.Null(lunch.Speakers);      // no speakers ⇒ null, not an empty string
        Assert.Null(lunch.Track);
    }

    [Fact]
    public async Task A_changed_activity_updates_IN_PLACE_so_the_card_keeps_its_identity()
    {
        using var db = NewDb();
        await SeedAsync(db, Cached("s1"));
        var originalId = (await db.AgendaActivities.SingleAsync()).Id;

        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[] { Activity("s1", "New title", room: "Hall Two") }));

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);

        var row = await db.AgendaActivities.SingleAsync();
        // 🔒 The SAME row — an upsert on the Backstage id, not a delete-and-recreate. A new row
        // would re-sort and could bounce the card onto another page mid-rotation.
        Assert.Equal(originalId, row.Id);
        Assert.Equal("New title", row.Title);
        Assert.Equal("Hall Two", row.Room);
    }

    [Fact]
    public async Task An_unchanged_activity_is_not_counted_as_an_update_but_is_still_restamped()
    {
        using var db = NewDb();
        await SeedAsync(db, Cached("s1", "Talk"));

        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[] { Activity("s1") }));

        var result = await svc.RunAsync(EventId);

        // Otherwise the sync-health panel would report "1 updated" every 5 minutes for ever and
        // the number would stop meaning anything.
        Assert.Equal(0, result.Updated);
        Assert.False(result.ChangedAnything);
        // LastSyncedAt still moves: it answers "when did we last CONFIRM this against Zoho".
        Assert.Equal(Now, (await db.AgendaActivities.SingleAsync()).LastSyncedAt);
    }

    [Fact]
    public async Task An_activity_that_disappeared_from_zoho_is_removed()
    {
        using var db = NewDb();
        await SeedAsync(db, Cached("s1"), Cached("s2"));

        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[] { Activity("s1") }));

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Removed);
        Assert.Equal("s1", (await db.AgendaActivities.SingleAsync()).BackstageSessionId);
    }

    [Fact]
    public async Task An_activity_with_no_start_or_no_duration_is_skipped_never_guessed()
    {
        using var db = NewDb();
        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[]
            {
                Activity("ok"),
                new("no-start", "Missing start", null, 60, "Hall One", "Cloud", "PRESENTATION",
                    Array.Empty<string>(), 1),
                new("no-duration", "Missing duration",
                    new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero), null,
                    "Hall One", "Cloud", "PRESENTATION", Array.Empty<string>(), 1),
                new("zero-duration", "Zero duration",
                    new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero), 0,
                    "Hall One", "Cloud", "PRESENTATION", Array.Empty<string>(), 1),
            }));

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Added);
        // 🔑 Surfaced as a COUNT. A guessed end time would silently drop the activity out of the
        // hour slot it belongs to, which on a wall looks exactly like a cancellation.
        Assert.Equal(3, result.SkippedUnusable);
        Assert.Equal("ok", (await db.AgendaActivities.SingleAsync()).BackstageSessionId);
    }

    [Fact]
    public async Task An_EMPTY_pull_NEVER_empties_a_cached_agenda()
    {
        using var db = NewDb();
        await SeedAsync(db, Cached("s1"), Cached("s2"));

        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            Array.Empty<ZohoClient.BackstageAgendaActivity>()));

        var result = await svc.RunAsync(EventId);

        // 🔒 THE fail-safe. A failed read and a genuinely-cleared agenda are indistinguishable, and
        // the cost of guessing wrong is 15 blank screens in the middle of a conference.
        Assert.False(result.Ok);
        Assert.True(result.RefusedToEmpty);
        Assert.Equal(2, await db.AgendaActivities.CountAsync());
        Assert.Contains("EMPTY", result.FailureReason);
    }

    [Fact]
    public async Task An_empty_pull_against_an_empty_table_is_an_ordinary_success()
    {
        using var db = NewDb();

        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            Array.Empty<ZohoClient.BackstageAgendaActivity>()));

        var result = await svc.RunAsync(EventId);

        // Nothing cached, nothing pulled ⇒ nothing to protect. Reporting this as a failure would
        // alert on every tick of an edition whose agenda has not been built yet.
        Assert.True(result.Ok);
        Assert.False(result.RefusedToEmpty);
        Assert.Empty(await db.AgendaActivities.ToListAsync());
    }

    [Fact]
    public async Task A_FAILED_pull_writes_nothing_and_leaves_the_last_good_agenda_untouched()
    {
        using var db = NewDb();
        await SeedAsync(db, Cached("s1"));
        var before = await db.AgendaActivities.AsNoTracking().SingleAsync();

        var svc = NewService(db, _ =>
            Task.FromException<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
                new HttpRequestException("503 Service Unavailable")));

        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ok);
        Assert.Contains("503", result.FailureReason);

        var after = await db.AgendaActivities.AsNoTracking().SingleAsync();
        Assert.Equal(before.Title, after.Title);
        // 🔒 LastSyncedAt must NOT move on a failure — it is the operator's evidence of staleness,
        // and a sync that stamps itself on failure reports health it does not have.
        Assert.Equal(before.LastSyncedAt, after.LastSyncedAt);
    }

    [Fact]
    public async Task Zoho_switched_off_writes_nothing()
    {
        using var db = NewDb();
        await SeedAsync(db, Cached("s1"));

        var svc = NewService(db, _ => throw new InvalidOperationException("must not pull"),
            zohoEnabled: false);

        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ok);
        Assert.Single(await db.AgendaActivities.ToListAsync());
    }

    [Fact]
    public async Task Speaker_names_are_stored_joined_in_the_order_zoho_returned_them()
    {
        using var db = NewDb();
        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[] { Activity("s1", speakers: new[] { "Ada Lovelace", "Grace Hopper" }) }));

        await svc.RunAsync(EventId);

        // The card prints one speaker line; the join happens once, here, not on every render.
        Assert.Equal("Ada Lovelace, Grace Hopper", (await db.AgendaActivities.SingleAsync()).Speakers);
    }

    [Fact]
    public async Task Another_editions_activities_are_never_touched()
    {
        using var db = NewDb();
        var other = Cached("other-1");
        other.EventId = 99;
        await SeedAsync(db, Cached("s1"), other);

        var svc = NewService(db, _ => Task.FromResult<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>(
            new[] { Activity("s1") }));

        var result = await svc.RunAsync(EventId);

        // The removal pass is scoped to the edition — otherwise syncing one edition would wipe
        // every other edition's cached agenda, which is the classic un-scoped-delete bug.
        Assert.Equal(0, result.Removed);
        Assert.Single(await db.AgendaActivities.Where(a => a.EventId == 99).ToListAsync());
    }
}
