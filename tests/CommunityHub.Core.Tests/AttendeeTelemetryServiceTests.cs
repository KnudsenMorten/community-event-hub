using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §127: <see cref="AttendeeTelemetryService"/> aggregates from the CEH SQL
/// mirror (Attendee + Order), NOT live Zoho. Soft-cancelled rows
/// (<see cref="MirrorState.Cancelled"/>) are excluded so totals match Zoho's ACTIVE set;
/// the 2-day segment uses the unified <see cref="MasterClassTicketPolicy"/>; the "Updated
/// &lt;t&gt;" footer reads the last-successful-sync stamp (<see cref="SyncRun"/>); and the
/// §69 "Top companies" OrganizerOnly aggregate is built ONLY for organizer callers
/// (defense-in-depth — never assembled for public/sponsor, not just hidden at render).
/// EF in-memory.
/// </summary>
public sealed class AttendeeTelemetryServiceTests
{
    private const int EventId = 31;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"telemetry-{Guid.NewGuid():N}").Options);

    private static AttendeeTelemetryService NewService(CommunityHubDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AttendeeTelemetryService>.Instance);

    private static async Task SeedEventAsync(CommunityHubDbContext db, bool active = true)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = active,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
    }

    private static Attendee Att(
        string ticketId, string email, string ticketClass, MirrorState state = MirrorState.Active,
        string? company = null, string? country = "Denmark", string? code = "DK", string? job = null)
        => new()
        {
            EventId = EventId,
            BackstageTicketId = ticketId,
            OrderId = "ord-" + ticketId,
            Email = email,
            FirstName = "F",
            LastName = "L",
            TicketClassName = ticketClass,
            TicketStatus = MasterClassTicketPolicy.IncludesMasterClass(ticketClass)
                ? TicketStatus.TwoDay : TicketStatus.Other,
            MirrorState = state,
            CompanyName = company,
            Country = country,
            CountryCode = code,
            JobTitle = job,
        };

    /// <summary>Two active DK 2-day, one active intl 1-day, one CANCELLED 2-day.</summary>
    private static async Task SeedMirrorAsync(CommunityHubDbContext db)
    {
        db.Attendees.AddRange(
            Att("t1", "a@x.dk", "2-day Pre-day + Main Event", company: "ACME", job: "CTO"),
            Att("t2", "b@x.dk", "2-day Pre-day + Main Event", company: "ACME", job: "Developer"),
            Att("t3", "c@x.se", "1-day Main Event", country: "Sweden", code: "SE", company: "Globex"),
            // Soft-cancelled 2-day holder — must NOT count toward the active set (§128).
            Att("t4", "d@x.dk", "2-day Pre-day + Main Event", state: MirrorState.Cancelled, company: "ACME"));
        await db.SaveChangesAsync();
    }

    private static async Task SeedSyncMarkerAsync(CommunityHubDbContext db, DateTimeOffset at)
    {
        db.SyncRuns.Add(new SyncRun
        {
            EventId = EventId, Key = SyncRun.AttendeeBackstageKey, LastSuccessAt = at,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Total_counts_only_active_mirror_rows_excluding_cancelled()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        var t = await NewService(db).GetAsync("all");

        Assert.NotNull(t);
        // 3 active rows; the cancelled 2-day holder (t4) is excluded.
        Assert.Equal(3, t!.TotalAll);
        Assert.Equal(3, t.SegmentCount);
    }

    [Fact]
    public async Task TwoDay_segment_uses_master_class_policy_and_excludes_cancelled()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        var t = await NewService(db).GetAsync("twoday");

        Assert.NotNull(t);
        // t1 + t2 match the "2-day" marker; t4 matches the marker too but is cancelled,
        // so the ACTIVE 2-day segment is exactly 2 (not 3).
        Assert.Equal(2, t!.SegmentCount);
        Assert.Equal(100, t.Pct2DayInSegment);
        // 2 of 3 active tickets are 2-day ⇒ 67%.
        Assert.Equal(67, t.PctOfTotal);
    }

    [Fact]
    public async Task All_segment_two_day_share_matches_active_set()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        var t = await NewService(db).GetAsync("all");

        // 2 of 3 active are 2-day ⇒ 67% on a 2-day ticket.
        Assert.Equal(67, t!.Pct2DayInSegment);
    }

    [Fact]
    public async Task Top_companies_table_is_built_only_for_organizer_callers()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        // Organizer surface (isOrganizer: true) — the OrganizerOnly aggregate is assembled.
        var t = await NewService(db).GetAsync("all", isOrganizer: true);

        var companies = t!.Tables.SingleOrDefault(x => x.Title == "Top companies");
        Assert.NotNull(companies);
        Assert.True(companies!.OrganizerOnly);
    }

    [Fact]
    public async Task Top_companies_table_is_never_assembled_for_non_organizer_callers()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        // Public / sponsor surface (isOrganizer: false, the default) — DEFENSE-IN-DEPTH (§69):
        // the sensitive "Top companies" aggregate is never CONSTRUCTED, not merely hidden at
        // render. No OrganizerOnly table should exist on the returned data at all.
        var t = await NewService(db).GetAsync("all", isOrganizer: false);

        Assert.DoesNotContain(t!.Tables, x => x.Title == "Top companies");
        Assert.DoesNotContain(t.Tables, x => x.OrganizerOnly);
    }

    [Fact]
    public async Task Footer_uses_last_successful_sync_timestamp_not_now()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);
        var syncedAt = new DateTimeOffset(2026, 6, 20, 6, 0, 0, TimeSpan.Zero);
        await SeedSyncMarkerAsync(db, syncedAt);

        var t = await NewService(db).GetAsync("all");

        Assert.Equal(syncedAt, t!.LastSyncAtUtc);
        // GeneratedAtUtc is the render wall-clock and is distinct from the sync stamp.
        Assert.True(t.GeneratedAtUtc > syncedAt);
    }

    [Fact]
    public async Task No_sync_marker_leaves_last_sync_null()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        var t = await NewService(db).GetAsync("all");

        Assert.Null(t!.LastSyncAtUtc);
    }

    [Fact]
    public async Task No_active_event_returns_null()
    {
        using var db = NewDb();
        await SeedEventAsync(db, active: false);
        await SeedMirrorAsync(db);

        var t = await NewService(db).GetAsync("all");

        Assert.Null(t);
    }

    [Fact]
    public async Task Country_filter_dimension_built_from_active_rows()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

        var t = await NewService(db).GetAsync("all");

        var country = t!.FilterDimensions!.Single(d => d.Key == "country");
        // Denmark (t1,t2) + Sweden (t3); the cancelled DK row (t4) does not appear.
        Assert.Contains("Denmark", country.Values);
        Assert.Contains("Sweden", country.Values);
    }
}
