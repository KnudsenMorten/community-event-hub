using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the PUBLIC day-by-day agenda read service (<see cref="PublicAgendaService"/>)
/// and its pure grouping core (<see cref="PublicAgendaBuilder"/>) — REQUIREMENTS §21
/// Public site "agenda/grid view". The service backs the anonymous <c>/Agenda</c>
/// page: it resolves the active edition and groups its SCHEDULED, non-service talks
/// into per-day timetables in start-time order. Proves:
///  - talks are <b>grouped by venue-local day</b> and the days come back chronological,
///  - within a day, talks are <b>start-time ordered</b> (ties → room, then title),
///  - <b>unscheduled</b> talks are excluded from the grid but counted,
///  - <b>service sessions</b> (breaks) are excluded entirely,
///  - <b>speaker linkage</b> joins + orders each talk's speaker name(s),
///  - <b>empty-state</b>: no active event → null; an edition with nothing scheduled → empty view,
///  - <b>event scoping</b>: another edition's sessions never leak in.
///
/// In-memory DbContext; synthetic ids + example.test — no real data.
/// </summary>
public sealed class PublicAgendaServiceTests
{
    // Day 1 = 9 Feb 2027, 09:00 (fixed offset so the venue-local date is stable).
    private static readonly DateTimeOffset Day1At9 =
        new(2027, 2, 9, 9, 0, 0, TimeSpan.FromHours(1));

    private static async Task<int> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "AGN27",
            CommunityName = "Agenda Community",
            DisplayName = "Agenda Community 2027",
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Participant Spk(string name, string email)
        {
            var p = new Participant
            {
                EventId = evt.Id, FullName = name, Email = email,
                Role = ParticipantRole.Speaker, IsActive = true,
            };
            db.Participants.Add(p);
            return p;
        }

        var alice = Spk("Alice Adams", "alice@example.test");
        var bob = Spk("Bob Brown", "bob@example.test");
        await db.SaveChangesAsync();

        Session Sess(string id, string title, SessionType type, SessionLength len,
            string? room, DateTimeOffset? start, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Abstract = $"About {title}.", Type = type, Length = len,
                Room = room, StartsAt = start,
                EndsAt = start?.AddMinutes((int)len == 0 ? 480 : (int)len),
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }

        // Day 1: two talks at the SAME time in different rooms (room tie-break), plus a
        // later one. Day 2: one talk. Deliberately seeded OUT of chronological order.
        Sess("d2-keynote", "Day Two Keynote",
            SessionType.CommunityTechSession, SessionLength.SixtyMin, "Room A",
            Day1At9.AddDays(1));                                           // Day 2, 09:00

        Sess("d1-late", "Closing Panel",
            SessionType.CommunityTechSession, SessionLength.FiftyMin, "Room A",
            Day1At9.AddHours(6), bob);                                     // Day 1, 15:00

        Sess("d1-roomB", "Bicep Basics",
            SessionType.CommunityTechSession, SessionLength.FiftyMin, "Room B",
            Day1At9, alice);                                              // Day 1, 09:00 (Room B)

        Sess("d1-roomA", "Kubernetes Workshop",
            SessionType.CommunityMasterClass, SessionLength.FullDay, "Room A",
            Day1At9, alice, bob);                                         // Day 1, 09:00 (Room A)

        // Unscheduled talk — counted, but NOT on the grid.
        Sess("tba", "To Be Announced",
            SessionType.CommunityTechSession, SessionLength.SixtyMin, null, null);

        // Service session (break) — excluded entirely.
        Sess("brk", "Coffee Break",
            SessionType.CommunityTechSession, SessionLength.TwentyMin, "Foyer",
            Day1At9.AddHours(2)).IsServiceSession = true;

        await db.SaveChangesAsync();
        return evt.Id;
    }

    [Fact]
    public async Task No_active_event_returns_null()
    {
        using var db = TestDb.New();
        var svc = new PublicAgendaService(db);

        Assert.Null(await svc.BuildAsync());
    }

    [Fact]
    public async Task Groups_by_day_in_chronological_order()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicAgendaService(db);

        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Equal("Agenda Community 2027", view!.EventDisplayName);
        Assert.False(view.IsEmpty);
        Assert.Equal(2, view.Days.Count);
        // Days are chronological.
        Assert.Equal(new DateOnly(2027, 2, 9), view.Days[0].Date);
        Assert.Equal(new DateOnly(2027, 2, 10), view.Days[1].Date);
        // 4 scheduled talks (the unscheduled + the break are excluded from the grid).
        Assert.Equal(4, view.ScheduledCount);
        Assert.Equal(1, view.UnscheduledCount);   // the TBA talk
    }

    [Fact]
    public async Task Within_a_day_talks_are_start_then_room_then_title_ordered()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicAgendaService(db);

        var view = await svc.BuildAsync();

        var day1 = view!.Days[0].Items;
        Assert.Equal(3, day1.Count);
        // 09:00 Room A (Kubernetes) before 09:00 Room B (Bicep) — room tie-break,
        // then 15:00 Closing Panel last.
        Assert.Equal(new[] { "Kubernetes Workshop", "Bicep Basics", "Closing Panel" },
            day1.Select(i => i.Title).ToArray());
        Assert.Equal("Room A", day1[0].Room);
        Assert.Equal("Room B", day1[1].Room);
    }

    [Fact]
    public async Task Excludes_service_sessions_and_unscheduled_from_the_grid()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicAgendaService(db);

        var view = await svc.BuildAsync();

        var allTitles = view!.Days.SelectMany(d => d.Items).Select(i => i.Title).ToList();
        Assert.DoesNotContain("Coffee Break", allTitles);     // service session
        Assert.DoesNotContain("To Be Announced", allTitles);  // unscheduled
    }

    [Fact]
    public async Task Joins_and_orders_speaker_names_per_talk()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicAgendaService(db);

        var view = await svc.BuildAsync();

        var mc = view!.Days.SelectMany(d => d.Items).Single(i => i.Title == "Kubernetes Workshop");
        Assert.Equal("Alice Adams, Bob Brown", mc.Speakers);   // ordered by name

        var bicep = view.Days.SelectMany(d => d.Items).Single(i => i.Title == "Bicep Basics");
        Assert.Equal("Alice Adams", bicep.Speakers);
    }

    [Fact]
    public async Task Active_edition_only_other_editions_never_leak()
    {
        using var db = TestDb.New();
        await SeedAsync(db);

        var other = new Event
        {
            Code = "OLD26", CommunityName = "Old", DisplayName = "Old 2026",
            StartDate = new DateOnly(2026, 2, 1), EndDate = new DateOnly(2026, 2, 2),
            IsActive = false,
        };
        db.Events.Add(other);
        await db.SaveChangesAsync();
        db.Sessions.Add(new Session
        {
            EventId = other.Id, SessionizeId = "old", Title = "Last Year Talk",
            Type = SessionType.CommunityTechSession, Length = SessionLength.SixtyMin,
            StartsAt = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.FromHours(1)),
        });
        await db.SaveChangesAsync();

        var view = await new PublicAgendaService(db).BuildAsync();

        Assert.Equal("Agenda Community 2027", view!.EventDisplayName);
        Assert.DoesNotContain(view.Days.SelectMany(d => d.Items),
            i => i.Title == "Last Year Talk");
    }

    [Fact]
    public async Task Edition_with_no_scheduled_talks_returns_empty_view()
    {
        using var db = TestDb.New();
        var evt = new Event
        {
            Code = "EMPTY27", CommunityName = "Empty", DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        // An UNSCHEDULED talk exists, but nothing is on the grid.
        db.Sessions.Add(new Session
        {
            EventId = evt.Id, SessionizeId = "tba", Title = "Unscheduled",
            Type = SessionType.CommunityTechSession, Length = SessionLength.SixtyMin,
            StartsAt = null,
        });
        await db.SaveChangesAsync();

        var view = await new PublicAgendaService(db).BuildAsync();

        Assert.NotNull(view);          // active event exists → not null
        Assert.True(view!.IsEmpty);    // nothing scheduled
        Assert.Empty(view.Days);
        Assert.Equal(0, view.ScheduledCount);
        Assert.Equal(1, view.UnscheduledCount);
    }

    // --- Pure builder (no DbContext) ---------------------------------------

    [Fact]
    public void Builder_groups_orders_and_drops_unscheduled()
    {
        var d1 = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.FromHours(1));
        var rows = new List<RawAgendaSession>
        {
            new(2, "Second", SessionType.CommunityTechSession, SessionLength.FiftyMin,
                "R1", null, d1.AddHours(2), d1.AddHours(3), new[] { "Zoe" }),
            new(1, "First", SessionType.CommunityTechSession, SessionLength.FiftyMin,
                "R1", null, d1, d1.AddHours(1), new[] { "Bob", "Ann" }),
            new(3, "NoTime", SessionType.CommunityTechSession, SessionLength.FiftyMin,
                null, null, null, null, Array.Empty<string>()),
        };

        var view = PublicAgendaBuilder.Build("Test 2027", rows);

        Assert.Single(view.Days);
        Assert.Equal(2, view.ScheduledCount);
        Assert.Equal(1, view.UnscheduledCount);
        Assert.Equal(new[] { "First", "Second" },
            view.Days[0].Items.Select(i => i.Title).ToArray());
        // Speaker names are joined + alphabetised.
        Assert.Equal("Ann, Bob", view.Days[0].Items[0].Speakers);
    }

    [Fact]
    public void Builder_empty_input_is_empty_view()
    {
        var view = PublicAgendaBuilder.Build("Test 2027", Array.Empty<RawAgendaSession>());

        Assert.True(view.IsEmpty);
        Assert.Empty(view.Days);
        Assert.Equal(0, view.ScheduledCount);
        Assert.Equal(0, view.UnscheduledCount);
        Assert.Equal("Test 2027", view.EventDisplayName);
    }
}
