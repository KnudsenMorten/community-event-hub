using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="DataFreshnessService"/> — the read-only organizer
/// data-freshness panel (REQUIREMENTS §21 Organizer [M] "last synced at"). Uses
/// the EF Core InMemory provider so the real DbContext mapping + LINQ run (no
/// SQL), seeded so each feed has a known most-recent timestamp. A second
/// edition's rows are planted to prove every stamp is event-scoped.
/// </summary>
public sealed class DataFreshnessServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    // Fixed "now" so every age / staleness assertion is deterministic.
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"freshness-{Guid.NewGuid():N}")
            .Options);

    private static DataFreshnessService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    private static DateTimeOffset DaysAgo(double d) => Now.AddDays(-d);
    private static DateTimeOffset HoursAgo(double h) => Now.AddHours(-h);

    /// <summary>Seed one edition where each feed has a known most-recent stamp,
    /// plus a more-recent row in a SECOND edition that must never bleed in.</summary>
    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "FR27", CommunityName = "Freshness Test",
            DisplayName = "Freshness Test 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTHER", CommunityName = "Other",
            DisplayName = "Other 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2),
            IsActive = false,
        });

        // --- Email: newest at 1 day ago (fresh; window 3d). An older row +
        //     a newer row in the OTHER edition prove max + scoping.
        db.EmailLogs.Add(new EmailLog { EventId = EventId, ToEmail = "a@expertslive.dk", Subject = "x", Category = "test", SentAt = DaysAgo(5) });
        db.EmailLogs.Add(new EmailLog { EventId = EventId, ToEmail = "b@expertslive.dk", Subject = "y", Category = "test", SentAt = DaysAgo(1) });
        db.EmailLogs.Add(new EmailLog { EventId = OtherEventId, ToEmail = "c@expertslive.dk", Subject = "z", Category = "test", SentAt = HoursAgo(1) });

        // --- Attendee sync: newest at 5 days ago (STALE; window 36h).
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "att1@x.test", FirstName = "Att", LastName = "One", LastSyncedAt = DaysAgo(9) });
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "att2@x.test", FirstName = "Att", LastName = "Two", LastSyncedAt = DaysAgo(5) });

        // --- Sponsor leads: capture 9 days ago, last CRM sync 2 days ago →
        //     freshness = the LATER (2 days ago; fresh, window 7d).
        db.SponsorLeads.Add(new SponsorLead
        {
            EventId = EventId, SponsorCompanyId = "co1", FullName = "Lead One",
            CapturedAt = DaysAgo(9), LastSyncedAt = DaysAgo(2),
        });

        // --- Speaker import: newest at 2 days ago (fresh; window 7d). A speaker
        //     with NO import stamp must not lower the max.
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = 0, LastSessionizeImportAt = DaysAgo(2) });
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = 0, LastSessionizeImportAt = null });

        // --- Session import: newest at 2 days ago (fresh; window 7d).
        db.Sessions.Add(new Session { EventId = EventId, Title = "S1", LastSessionizeImportAt = DaysAgo(2) });
        db.Sessions.Add(new Session { EventId = EventId, Title = "Hub-added", IsHubAdded = true, LastSessionizeImportAt = null });

        // --- Session questions: newest at 4 hours ago (fresh; window 14d).
        db.SessionQuestions.Add(new SessionQuestion { EventId = EventId, SessionId = 0, QuestionText = "q?", CreatedAt = HoursAgo(4) });

        // --- Session evaluations: newest at 20 days ago (STALE; window 14d).
        db.SessionEvaluations.Add(new SessionEvaluation { EventId = EventId, SessionId = 0, Rating = 5, CreatedAt = DaysAgo(20) });

        // --- SoMe published: one QUEUED (no PublishedAtUtc) only → feed has no
        //     published data yet ("no data" state, not stale).
        db.SoMePosts.Add(new SoMePost
        {
            EventId = EventId, Type = SoMePostType.AdHoc, ScheduledAtUtc = Now.AddDays(2),
            Status = SoMePostStatus.Queued, PublishedAtUtc = null,
        });

        await db.SaveChangesAsync();
    }

    private static FreshnessRow Row(FreshnessSnapshot s, FreshnessFeed f) =>
        s.Feeds.Single(r => r.Feed == f);

    [Fact]
    public async Task Builds_one_row_per_feed_in_a_stable_order()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var snap = await NewSvc(db).BuildAsync(EventId);

        // Exactly one row per declared feed, no duplicates, no extras.
        var expected = Enum.GetValues<FreshnessFeed>().ToList();
        Assert.Equal(expected, snap.Feeds.Select(r => r.Feed).ToList());
        Assert.Equal(Now, snap.GeneratedAtUtc);
    }

    [Fact]
    public async Task Each_feed_reports_its_most_recent_timestamp()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(DaysAgo(1),  Row(snap, FreshnessFeed.Email).LastActivityUtc);
        Assert.Equal(DaysAgo(5),  Row(snap, FreshnessFeed.AttendeeSync).LastActivityUtc);
        Assert.Equal(DaysAgo(2),  Row(snap, FreshnessFeed.SponsorLeads).LastActivityUtc); // later of capture/sync
        Assert.Equal(DaysAgo(2),  Row(snap, FreshnessFeed.SpeakerImport).LastActivityUtc);
        Assert.Equal(DaysAgo(2),  Row(snap, FreshnessFeed.SessionImport).LastActivityUtc);
        Assert.Equal(HoursAgo(4), Row(snap, FreshnessFeed.SessionQuestions).LastActivityUtc);
        Assert.Equal(DaysAgo(20), Row(snap, FreshnessFeed.SessionEvaluations).LastActivityUtc);
    }

    [Fact]
    public async Task A_feed_with_no_data_has_null_timestamp_and_is_not_stale()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var snap = await NewSvc(db).BuildAsync(EventId);

        // Only a QUEUED SoMe post exists → no published timestamp.
        var some = Row(snap, FreshnessFeed.SoMePublished);
        Assert.Null(some.LastActivityUtc);
        Assert.False(some.HasData);
        Assert.Null(some.AgeFor(Now));
        // "No data yet" is NOT stale — it is a distinct state.
        Assert.False(some.IsStale(Now));
    }

    [Fact]
    public async Task Stale_flag_fires_only_past_the_window()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var snap = await NewSvc(db).BuildAsync(EventId);

        // Attendee sync (5d ago, window 36h) and evaluations (20d ago, window 14d)
        // are over their windows; everything else with data is within.
        Assert.True(Row(snap, FreshnessFeed.AttendeeSync).IsStale(Now));
        Assert.True(Row(snap, FreshnessFeed.SessionEvaluations).IsStale(Now));
        Assert.False(Row(snap, FreshnessFeed.Email).IsStale(Now));
        Assert.False(Row(snap, FreshnessFeed.SponsorLeads).IsStale(Now));

        // The snapshot rolls these up.
        Assert.True(snap.HasStaleFeeds);
        var stale = snap.StaleFeeds.Select(r => r.Feed).ToHashSet();
        Assert.Contains(FreshnessFeed.AttendeeSync, stale);
        Assert.Contains(FreshnessFeed.SessionEvaluations, stale);
        Assert.DoesNotContain(FreshnessFeed.Email, stale);
    }

    [Fact]
    public async Task Age_is_computed_relative_to_now_and_never_negative()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(TimeSpan.FromDays(1), Row(snap, FreshnessFeed.Email).AgeFor(Now));

        // A future stamp (clock skew) clamps to zero, never negative.
        var future = new FreshnessRow(FreshnessFeed.Email, Now.AddHours(1), DataFreshnessService.EmailStale);
        Assert.Equal(TimeSpan.Zero, future.AgeFor(Now));
        Assert.False(future.IsStale(Now));
    }

    [Fact]
    public async Task Everything_is_event_scoped()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        // The OTHER edition has a more-recent email (1h ago) than this edition (1d).
        var snap = await NewSvc(db).BuildAsync(EventId);
        Assert.Equal(DaysAgo(1), Row(snap, FreshnessFeed.Email).LastActivityUtc);

        // Querying the OTHER edition: only its email exists; every other feed empty.
        var other = await NewSvc(db).BuildAsync(OtherEventId);
        Assert.Equal(HoursAgo(1), Row(other, FreshnessFeed.Email).LastActivityUtc);
        Assert.False(Row(other, FreshnessFeed.AttendeeSync).HasData);
        Assert.False(Row(other, FreshnessFeed.SessionEvaluations).HasData);
    }

    [Fact]
    public async Task Empty_edition_yields_all_no_data_and_no_stale()
    {
        await using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = 99, Code = "EMPTY", CommunityName = "Empty", DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(99);

        Assert.All(snap.Feeds, r => Assert.False(r.HasData));
        Assert.False(snap.HasStaleFeeds);
        Assert.Empty(snap.StaleFeeds);
    }
}
