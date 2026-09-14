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

    /// <summary>
    /// §69 / §1052 — <b>everybody may see the PAGE; only an organizer may see "Top companies".</b>
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-10, settling it in his own words: <i>"everybody must be able to see the
    /// page, but nobody except organizers can see top companies"</i> — after a round trip through
    /// <i>"we cannot expose company names due to gdpr - this is public page (remove)"</i> and
    /// <i>"organizers is ok to see it, reverse"</i>. 🛑 The behaviour never needed to change; the
    /// organizer page's blurb claimed parity with the public page while showing this extra table,
    /// which made a correctly-gated feature look like a leak. Do not delete this aggregate.
    /// </remarks>
    [Fact]
    public async Task Top_companies_table_is_built_only_for_organizer_callers()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedMirrorAsync(db);

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

        // DEFENSE-IN-DEPTH (§69): never CONSTRUCTED for a public/sponsor caller, not merely hidden
        // at render — so no template bug, print stylesheet or PDF export can bring it back.
        var t = await NewService(db).GetAsync("all", isOrganizer: false);

        Assert.DoesNotContain(t!.Tables, x => x.Title == "Top companies");
        Assert.DoesNotContain(t.Tables, x => x.OrganizerOnly);
    }

    /// <summary>
    /// 🔴 §1052 — THE GUARANTEE NOBODY WAS TESTING: the two PUBLIC-FACING pages must pass
    /// <c>isOrganizer: false</c>.
    /// </summary>
    /// <remarks>
    /// <para>The service tests above prove the aggregate is withheld <i>when asked as a
    /// non-organizer</i>. Nothing proved the anonymous and sponsor PAGES actually ask that way — and
    /// that flag is the entire public guarantee. Flip either literal to <c>true</c>, or to something
    /// derived from the signed-in user, and company names reach a page anyone can open, with every
    /// existing test still green.</para>
    ///
    /// <para>⚠️ This is the §1041 lesson in a new place: a test of the present cases could not see
    /// the absent one. The operator's fear — <i>"i was worried that we exposed it out to the
    /// public"</i> — deserves a test that would actually catch it happening.</para>
    /// </remarks>
    [Theory]
    [InlineData("AttendeeTelemetry.cshtml.cs")]
    [InlineData(@"Sponsor\Telemetry.cshtml.cs")]
    public void The_public_facing_telemetry_pages_ask_as_a_non_organizer(string page)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? found = null;
        while (dir is not null && found is null)
        {
            var p = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", page);
            if (File.Exists(p)) found = File.ReadAllText(p);
            dir = dir.Parent;
        }

        Assert.NotNull(found);
        Assert.Contains("isOrganizer: false", found!, StringComparison.Ordinal);
        Assert.DoesNotContain("isOrganizer: true", found!, StringComparison.Ordinal);
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
    public async Task Headline_word_of_mouth_and_first_timer_are_percentages_of_all_attendees()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        // 4 active attendees: 1 heard via word of mouth, 2 are first-timers.
        db.Attendees.AddRange(
            WithCustom("w1", "w1@x.dk", hear: "Word of mouth", firstTime: "No, ELDK27 is my first"),
            WithCustom("w2", "w2@x.dk", hear: "LinkedIn", firstTime: "No, ELDK27 is my first"),
            WithCustom("w3", "w3@x.dk", hear: "Newsletter", firstTime: "Yes, I have attended before"),
            WithCustom("w4", "w4@x.dk", hear: "Newsletter", firstTime: "Yes, I have attended before"));
        await db.SaveChangesAsync();

        var t = await NewService(db).GetAsync("all");

        Assert.NotNull(t);
        Assert.Equal(4, t!.TotalAll);
        // §181: cards show a % of ALL attendees, not a raw count.
        Assert.Equal(1, t.WordOfMouthCount);
        Assert.Equal(25, t.WordOfMouthPct);   // 1 of 4
        Assert.Equal(2, t.FirstTimerCount);
        Assert.Equal(50, t.FirstTimerPct);    // 2 of 4
    }

    [Fact]
    public async Task Headline_percentages_guard_divide_by_zero_when_no_attendees()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        // No attendees seeded.

        var t = await NewService(db).GetAsync("all");

        Assert.NotNull(t);
        Assert.Equal(0, t!.TotalAll);
        Assert.Equal(0, t.WordOfMouthPct);    // no divide-by-zero
        Assert.Equal(0, t.FirstTimerPct);
        Assert.Equal(0, t.Pct2DayAll);
    }

    /// <summary>
    /// §1217 — each heading sits over the answers it names. The three were rotated one step on PROD:
    /// "Job role" showed Security/Intune/Azure, "Type of attendee" showed Modern Workplace Specialist,
    /// and "Attendee interest" showed Internal IT department. Asserted by ANSWER, which is how he
    /// reads the page.
    /// </summary>
    [Fact]
    public async Task Custom_field_headings_sit_over_the_answers_they_name()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var a = Att("q1", "q1@x.dk", "1-day Main Event");
        a.CustomFieldsJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["single_choice"]   = "Internal IT department",
            ["single_choice_1"] = "Intune",
            ["single_choice_2"] = "Modern Workplace Specialist (Intune, AVD, W365, etc.)",
        });
        db.Attendees.Add(a);
        await db.SaveChangesAsync();

        var t = await NewService(db).GetAsync("all");

        string TitleOver(string answer) =>
            t!.Tables.Single(x => x.Slices.Any(s => s.Label == answer)).Title;
        Assert.Equal("Type of attendee", TitleOver("Internal IT department"));
        Assert.Equal("Attendee interest / primary track", TitleOver("Intune"));
        Assert.Equal("Job role of attendees", TitleOver("Modern Workplace Specialist (Intune, AVD, W365, etc.)"));
    }

    private static Attendee WithCustom(string ticketId, string email, string hear, string firstTime)
    {
        var a = Att(ticketId, email, "1-day Main Event");
        // single_choice_3 = "how did you hear", multiple_choice = "attended before?" (2026-06-28 swap).
        a.CustomFieldsJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["single_choice_3"] = hear,
            ["multiple_choice"] = firstTime,
        });
        return a;
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
