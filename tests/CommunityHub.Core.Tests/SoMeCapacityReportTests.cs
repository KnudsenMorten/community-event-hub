using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1199 — CAPACITY vs. DEMAND. You cannot sell the same seat twice.
///
/// <para>Operator 2026-09-12: <i>"i need to have a overview whre I can see the calculation between
/// capacity (available some post timeslot vs. planned /scheduled) … we cannot sell the same seat
/// twice like an airplane company"</i> · <i>"i can setup predictions until we know the final count -
/// 42 sponsors, 66 sessions (incl. 9 sponsor sessions)"</i>.</para>
///
/// <para>🔑 The planner already knows when it runs out — it reports "no room 2" and names the
/// subjects. That says it failed, not by how much, not where, and not what would fix it.</para>
/// </summary>
public sealed class SoMeCapacityReportTests
{
    private const int EventId = 1;
    private static readonly DateOnly From = new(2026, 9, 14);
    private static readonly DateOnly EventDay = new(2027, 2, 9);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-capacity-{Guid.NewGuid():N}").Options);

    private static async Task<CommunityHubDbContext> SeedAsync(int maxPerDay = 2)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", DisplayName = "ELDK 2027",
            StartDate = EventDay, EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SoMeSettings.Add(new SoMeSettings { EventId = EventId, MaxPostsPerDay = maxPerDay });
        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>
    /// 🔑 A seat is a weekday × a posting time — weekends and the holiday blackout are not seats,
    /// because the planner will not use them.
    /// </summary>
    [Fact]
    public async Task Weekends_and_the_holiday_break_are_not_seats()
    {
        using var db = await SeedAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(EventId, From);

        var calendarDays = EventDay.DayNumber - From.DayNumber;
        Assert.True(r.PostingDays < calendarDays,
            "every calendar day was counted as a posting day — weekends are not seats");
        Assert.Equal(r.PostingDays * r.PostsPerDay, r.Seats);

        // Not one of the blackout days may be counted.
        Assert.True(r.Months.All(m => m.PostingDays > 0));
        var december = r.Months.Single(m => m is { Year: 2026, Month: 12 });
        Assert.True(december.PostingDays <= 16,
            $"December counted {december.PostingDays} posting days — the 23 Dec–3 Jan break is not seats");
    }

    /// <summary>
    /// 🔴 A SEAT ALREADY TAKEN CANNOT BE SOLD AGAIN — including one already flown. §1144 treats a
    /// published post as an obstacle for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_published_post_still_holds_its_seat()
    {
        using var db = await SeedAsync();
        db.SoMePosts.Add(new SoMePost
        {
            Id = 1, EventId = EventId, TemplateKind = SoMeTemplateKind.Sponsor,
            SubjectKey = "sponsor:1", Occurrence = 1,
            ScheduledAtUtc = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero),
            Status = SoMePostStatus.Published, PublishedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(EventId, From);

        Assert.Equal(1, r.Taken);
        Assert.Equal(r.Seats - 1, r.Free);
    }

    /// <summary>
    /// 🔴 HIS NUMBERS: 42 sponsors and 66 sessions including 9 sponsor sessions. The forecast has to
    /// reach the demand table, or the page answers a question he did not ask.
    /// </summary>
    [Fact]
    public async Task The_forecast_counts_drive_the_demand()
    {
        using var db = await SeedAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(
            EventId, From,
            new SoMeCapacityReport.Forecast(
                Sponsors: 42, TechnicalSessions: 57, SponsorSessions: 9, MasterClasses: 0));

        var sponsors = r.Demand.Single(d => d.Category == SoMeAnnouncementCategory.Sponsors);
        Assert.Equal(42, sponsors.Subjects);
        // Sponsors are announced twice (contractual, §842.5).
        Assert.Equal(84, sponsors.Wanted);

        var sponsorSessions = r.Demand.Single(
            d => d.Category == SoMeAnnouncementCategory.SponsorSpeakerSessions);
        Assert.Equal(9, sponsorSessions.Subjects);
        Assert.Equal(18, sponsorSessions.Wanted);
    }

    /// <summary>An omitted forecast falls back to what the edition actually holds.</summary>
    [Fact]
    public async Task An_omitted_forecast_uses_todays_counts()
    {
        using var db = await SeedAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(EventId, From);

        Assert.All(r.Demand, d => Assert.Equal(0, d.Subjects));
    }

    /// <summary>
    /// 🔑 THE ANSWER IS A NUMBER, not "add more". If it does not fit, say what posts-per-day would.
    /// </summary>
    [Fact]
    public async Task It_says_what_posts_per_day_would_fit()
    {
        using var db = await SeedAsync(maxPerDay: 1);

        var r = await new SoMeCapacityReport(db).BuildAsync(
            EventId, From, new SoMeCapacityReport.Forecast(Sponsors: 42, TechnicalSessions: 57));

        if (!r.Fits)
        {
            Assert.True(r.PostsPerDayNeeded > r.PostsPerDay);
            Assert.True(r.PostsPerDayNeeded <= SoMeSchedulePlanner.PreferredTimes.Length);
        }
    }

    /// <summary>
    /// ⚠️ And it admits when NO setting can fix it. §842.7 caps the day at the four posting times;
    /// beyond that a post would share a minute or break the 08:00–16:00 rule, so promising a bigger
    /// number would be promising something the planner refuses to do.
    /// </summary>
    [Fact]
    public async Task It_admits_when_more_slots_per_day_cannot_fix_it()
    {
        using var db = await SeedAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(
            EventId, From,
            new SoMeCapacityReport.Forecast(Sponsors: 400, TechnicalSessions: 400, Tracks: 50));

        Assert.False(r.Fits);
        Assert.True(r.NeedsMoreThanSlotsAllow);
        Assert.True(r.PostsPerDayNeeded <= SoMeSchedulePlanner.PreferredTimes.Length);
    }

    /// <summary>A comfortable campaign reads as fitting, or the warning means nothing.</summary>
    [Fact]
    public async Task A_campaign_that_fits_says_so()
    {
        using var db = await SeedAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(
            EventId, From, new SoMeCapacityReport.Forecast(Sponsors: 5, TechnicalSessions: 5));

        Assert.True(r.Fits);
        Assert.Equal(0, r.Shortfall);
    }

    /// <summary>Every month between now and the event is accounted for — no silent gaps.</summary>
    [Fact]
    public async Task Every_month_up_to_the_event_is_listed()
    {
        using var db = await SeedAsync();

        var r = await new SoMeCapacityReport(db).BuildAsync(EventId, From);

        Assert.Equal(new[] { (2026, 9), (2026, 10), (2026, 11), (2026, 12), (2027, 1), (2027, 2) },
            r.Months.Select(m => (m.Year, m.Month)));
    }
}
