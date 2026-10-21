using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Sponsors;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1352 — bought sponsor sessions become agenda sessions in their slot. Product names are the real
/// ELDK27 ones (read from PROD 2026-10-19). Operator: *"sponsorname: session tbd as initial name in the
/// agenda + room"* · *"the initial is only if they have not already submitted"* · *"only filter on real
/// purchases, not withdrawn / cancelled"*.
/// </summary>
public class SponsorAgendaSessionServiceTests
{
    private const int EventId = 1;
    private const string Long3 = "Sponsor Long 60 min Session - ELDK27 - timeslot: 3 (est. time: 13:30-14:30)";
    private const string Long1 = "Sponsor Long 60 min Session - ELDK27 - timeslot: 1 (est. time: 10:00-11:00)";
    private const string Expo2 = "Sponsor Expo Intro 20 min Session - ELDK27 - timeslot: 2 (est. time: 12:40-13:00)";
    private const string TestSlot = "Sponsor Expo Intro 20 min Session - ELDK27 - timeslot: 0 (TEST)";

    private static readonly SponsorAgendaSessionsConfig Config = new()
    {
        Rooms = new()
        {
            ["Long 60 min"] = "Room-17-Floor 1-Max 49",
            ["Expo Intro 20 min"] = "Expo-Stage",
        },
        FallbackTrack = "Security",
    };

    private static async Task<CommunityHub.Core.Data.CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        // 🔴 PROD's real shape: the edition STARTS with the pre-day (StartDate = PreDayDate = 9 Feb); the
        // main day is 10 Feb. A fixture with StartDate = main day hid the first-release bug.
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), PreDayDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.SponsorInfos.AddRange(
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "19", CompanyName = "Patch My PC" },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "10", CompanyName = "Robopack" },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "104", CompanyName = "LENOVO" });
        await db.SaveChangesAsync();
        return db;
    }

    private static WooOrder Order(long id, string companyId, params string[] products) =>
        new(id, "completed", "buyer@example.test", "Buyer ApS", companyId, DateTimeOffset.UtcNow,
            products.Select((p, i) => new WooLineItem(i + 1, p, "Sponsor Sessions")).ToList());

    private static async Task<Participant> SpeakerAsync(CommunityHub.Core.Data.CommunityHubDbContext db, string email)
    {
        var p = new Participant { EventId = EventId, Email = email, FullName = "Sam Speaker", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task SubmitAsync(CommunityHub.Core.Data.CommunityHubDbContext db, string companyId, string title, int speakerId, string? track = null)
    {
        var s = new SponsorSession { EventId = EventId, SponsorCompanyId = companyId, Title = title, Abstract = "What we will show.", Track = track };
        s.Speakers.Add(new SponsorSessionSpeaker { ParticipantId = speakerId, Name = "Sam Speaker", Email = "sam@example.test" });
        db.SponsorSessions.Add(s);
        await db.SaveChangesAsync();
    }

    private static Task<SponsorAgendaSessionService.Result> SyncAsync(CommunityHub.Core.Data.CommunityHubDbContext db, params WooOrder[] orders) =>
        new SponsorAgendaSessionService(db).SyncAsync(EventId, orders, Config);

    // ---- the slot in the product name ---------------------------------------------------------

    [Fact]
    public void The_slot_is_read_from_the_product_name_and_a_test_slot_without_a_time_is_not_a_slot()
    {
        Assert.Equal(new SponsorAgendaSessionService.Slot("Long 60 min", 3, new TimeOnly(13, 30), new TimeOnly(14, 30)),
                     SponsorAgendaSessionService.ParseSlot(Long3));
        Assert.Equal(new SponsorAgendaSessionService.Slot("Expo Intro 20 min", 2, new TimeOnly(12, 40), new TimeOnly(13, 0)),
                     SponsorAgendaSessionService.ParseSlot(Expo2));
        Assert.Null(SponsorAgendaSessionService.ParseSlot(TestSlot));
        Assert.Null(SponsorAgendaSessionService.ParseSlot("Booth: Chair, Tolix Stool Outdoor, black"));
    }

    [Theory]
    [InlineData("2027-02-09", "2027-02-09", "2027-02-10")]   // ELDK27: starts with the pre-day
    [InlineData("2027-02-09", "2027-02-08", "2027-02-09")]   // a pre-day before the start
    [InlineData("2027-02-10", null, "2027-02-10")]           // no pre-day
    public void The_main_day_is_the_first_day_after_the_pre_day(string start, string? preDay, string expected) =>
        Assert.Equal(DateOnly.Parse(expected),
            SponsorAgendaSessionService.MainDay(DateOnly.Parse(start), preDay is null ? null : DateOnly.Parse(preDay)));

    // ---- create -------------------------------------------------------------------------------

    [Fact]
    public async Task A_sponsor_that_has_not_submitted_gets_a_TBD_session_in_its_room_at_its_Danish_time()
    {
        using var db = await SeedAsync();

        var result = await SyncAsync(db, Order(10832, "10", Long1), Order(10855, "104", Expo2));

        Assert.Equal(2, result.Created);
        var robopack = await db.Sessions.SingleAsync(s => s.SponsorCompanyId == "10");
        Assert.Equal("Robopack: Session TBD", robopack.Title);
        Assert.Equal("Room-17-Floor 1-Max 49", robopack.Room);
        // 10:00 in Copenhagen in February (UTC+1) is 09:00 UTC — never a UTC-stamped wall time.
        Assert.Equal(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero), robopack.StartsAt);
        Assert.Equal(60, robopack.LengthMinutes);
        Assert.Equal("Security", robopack.Track);   // Zoho refuses a session without a track
        Assert.True(robopack.IsHubAdded);
        Assert.True(robopack.ExcludeFromSoMeAnnouncements);   // "Session TBD" must never be posted
        Assert.Empty(await db.SessionSpeakers.Where(l => l.SessionId == robopack.Id).ToListAsync());

        var lenovo = await db.Sessions.SingleAsync(s => s.SponsorCompanyId == "104");
        Assert.Equal("Expo-Stage", lenovo.Room);
        Assert.Equal(20, lenovo.LengthMinutes);
    }

    [Fact]
    public async Task A_sponsor_that_has_submitted_gets_its_real_title_abstract_track_and_speaker()
    {
        using var db = await SeedAsync();
        var speaker = await SpeakerAsync(db, "sam@example.test");
        await SubmitAsync(db, "19", "The Game Has Changed: Endpoint Resilience in the Age of AI", speaker.Id, track: "Intune");

        await SyncAsync(db, Order(10760, "19", Long3));

        var s = await db.Sessions.Include(x => x.SessionSpeakers).SingleAsync();
        Assert.Equal("The Game Has Changed: Endpoint Resilience in the Age of AI", s.Title);
        Assert.Equal("What we will show.", s.Abstract);
        Assert.Equal("Intune", s.Track);
        Assert.Equal(new[] { speaker.Id }, s.SessionSpeakers.Select(l => l.ParticipantId));
    }

    // ---- only real purchases ------------------------------------------------------------------

    [Fact]
    public async Task Only_the_orders_passed_in_count_so_a_cancelled_test_purchase_and_a_test_sponsor_make_nothing()
    {
        using var db = await SeedAsync();
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "77", CompanyName = "Test Co", IsTestData = true });
        await db.SaveChangesAsync();

        // The caller passes completed + signed orders only; a TEST slot has no time; a test sponsor is skipped.
        var result = await SyncAsync(db, Order(10811, "12", TestSlot), Order(1, "77", Long1));

        Assert.Equal(0, result.Created);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    [Fact]
    public async Task Without_agenda_config_nothing_is_created()
    {
        using var db = await SeedAsync();

        var result = await new SponsorAgendaSessionService(db).SyncAsync(EventId, new[] { Order(10832, "10", Long1) }, config: null);

        Assert.Equal(0, result.Created);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    // ---- keep in step -------------------------------------------------------------------------

    [Fact]
    public async Task A_second_run_changes_nothing_and_a_later_submission_replaces_TBD_in_the_same_session()
    {
        using var db = await SeedAsync();
        await SyncAsync(db, Order(10832, "10", Long1));
        var id = (await db.Sessions.SingleAsync()).Id;

        var again = await SyncAsync(db, Order(10832, "10", Long1));
        Assert.Equal(0, again.Created);
        Assert.Equal(0, again.Updated);

        var speaker = await SpeakerAsync(db, "rob@example.test");
        await SubmitAsync(db, "10", "Packaging without pain", speaker.Id);
        var later = await SyncAsync(db, Order(10832, "10", Long1));

        Assert.Equal(1, later.Updated);
        var s = await db.Sessions.Include(x => x.SessionSpeakers).SingleAsync();
        Assert.Equal(id, s.Id);   // the SAME session — so its Backstage link and the §1002 difference mail follow it
        Assert.Equal("Packaging without pain", s.Title);
        Assert.Equal("Security", s.Track);   // no track chosen → still the fallback
        Assert.Equal(new[] { speaker.Id }, s.SessionSpeakers.Select(l => l.ParticipantId));
    }

    [Fact]
    public async Task A_track_set_by_an_organizer_is_kept_until_the_sponsor_picks_one()
    {
        // Operator 2026-10-19: *"tracks chg in ceh"* — set by hand on the Sessions page; the first version
        // reset them to the fallback on its next run.
        using var db = await SeedAsync();
        await SyncAsync(db, Order(10832, "10", Long1));
        var s = await db.Sessions.SingleAsync();
        Assert.Equal("Security", s.Track);   // fallback on create

        s.Track = "Intune";   // the organizer's choice
        await db.SaveChangesAsync();
        var again = await SyncAsync(db, Order(10832, "10", Long1));
        Assert.Equal(0, again.Updated);
        Assert.Equal("Intune", (await db.Sessions.SingleAsync()).Track);

        // The sponsor submits WITHOUT a track: still the organizer's; then WITH one: the sponsor's.
        var speaker = await SpeakerAsync(db, "rob@example.test");
        await SubmitAsync(db, "10", "Packaging without pain", speaker.Id);
        await SyncAsync(db, Order(10832, "10", Long1));
        Assert.Equal("Intune", (await db.Sessions.SingleAsync()).Track);

        (await db.SponsorSessions.SingleAsync()).Track = "Azure";
        await db.SaveChangesAsync();
        await SyncAsync(db, Order(10832, "10", Long1));
        Assert.Equal("Azure", (await db.Sessions.SingleAsync()).Track);
    }

    [Fact]
    public async Task A_slot_no_longer_bought_is_reported_but_never_deleted()
    {
        using var db = await SeedAsync();
        await SyncAsync(db, Order(10832, "10", Long1));

        var result = await SyncAsync(db);   // the order is gone (cancelled)

        Assert.Single(result.Orphans);
        Assert.Contains("Robopack: Session TBD", result.Orphans[0]);
        Assert.Single(await db.Sessions.ToListAsync());
    }
}
