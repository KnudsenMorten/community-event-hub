using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reporting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1086 — <b>ONE NUMBER, WHEREVER IT IS READ.</b>
/// </summary>
/// <remarks>
/// <para>🔴 Operator 2026-08-14, across four messages in one sitting: <i>"logistics lunch preday
/// must contain any attendees that booked a 2-day ticket … missing in the calculation and excel
/// export"</i> · <i>"lunch count here must use same calculation engine"</i> · <i>"it shows 100, but
/// we have 97"</i> · <b><i>"i need to trust numbers and have logic consistent across"</i></b>.</para>
///
/// <para>🔑 <b>Fixing the three surfaces was not enough</b> — they had each been individually
/// correct-looking for months. What was missing was anything that FAILS when a fourth reader invents
/// a fourth answer. These tests are that. They assert agreement between surfaces rather than
/// re-asserting each surface's arithmetic, because agreement is the property that was lost.</para>
///
/// <para>FAKE names only.</para>
/// </remarks>
public sealed class LunchAndAttendeeCountConsistencyTests
{
    private const int EventId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"counts-{Guid.NewGuid():N}")
            .Options);

    /// <summary>
    /// One edition with every shape that has ever been counted wrongly: crew who never answer,
    /// somebody who declined, a test account, a sponsor who ticked the box AND has booth members,
    /// a 1-day and a 2-day attendee, and a cancelled ticket.
    /// </summary>
    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "Test Edition",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        });
        await db.SaveChangesAsync();

        async Task<int> Person(string name, ParticipantRole role, bool active = true, bool test = false)
        {
            var p = new Participant
            {
                EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
                Role = role, IsActive = active, IsTestUser = test,
                LifecycleState = ParticipantLifecycleState.Active,
            };
            db.Participants.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        }

        // Crew: no pre-day checkbox exists for them, so they are auto-counted.
        await Person("Ada Lovelace", ParticipantRole.Organizer);
        await Person("Media Person", ParticipantRole.Media);
        // A speaker who ticked, and a volunteer who did not.
        var speaker = await Person("Grace Hopper", ParticipantRole.Speaker);
        var volunteer = await Person("Volunteer One", ParticipantRole.Volunteer);
        // ⚠️ A test account and a withdrawn person: neither is a meal.
        var tester = await Person("Test Account", ParticipantRole.Volunteer, test: true);
        var gone = await Person("Gone Away", ParticipantRole.Volunteer, active: false);
        // A sponsor contact who ticked the pre-day box — their company also declares booth members.
        var sponsor = await Person("Sponsor Contact", ParticipantRole.Sponsor);

        db.LunchSignups.AddRange(
            new LunchSignup { EventId = EventId, ParticipantId = speaker, LunchPreDay = true, LunchSetupDay = true },
            new LunchSignup { EventId = EventId, ParticipantId = volunteer, LunchPreDay = false },
            new LunchSignup { EventId = EventId, ParticipantId = tester, LunchPreDay = true },
            new LunchSignup { EventId = EventId, ParticipantId = gone, LunchPreDay = true },
            new LunchSignup { EventId = EventId, ParticipantId = sponsor, LunchPreDay = true });

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "c1",
            BoothCheckInSlot = BoothCheckInSlots.S0900, BoothCheckInMemberCount = 3,
        });

        db.Attendees.AddRange(
            new Attendee { EventId = EventId, FullName = "Two Day", Email = "two@example.test",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active },
            new Attendee { EventId = EventId, FullName = "One Day", Email = "one@example.test",
                TicketStatus = TicketStatus.Other, MirrorState = MirrorState.Active },
            new Attendee { EventId = EventId, FullName = "Cancelled", Email = "gone@example.test",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled });
        await db.SaveChangesAsync();
    }

    // ---- the engine itself ------------------------------------------------------------------

    /// <summary>
    /// 🔑 The whole pre-day rule in one assertion: 2 crew (auto) + 1 speaker who ticked
    /// + 3 booth members + 1 two-day attendee = 7.
    /// <para>NOT counted: the volunteer who said no, the test account, the withdrawn person, the
    /// SPONSOR who ticked (they are inside the booth count), the 1-day attendee (not admitted to the
    /// Master Class) and the cancelled ticket.</para>
    /// </summary>
    [Fact]
    public async Task The_pre_day_head_count_is_crew_plus_declared_plus_booth_plus_two_day_attendees()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var c = await new LunchHeadcountService(db).ComputeAsync(EventId);

        Assert.Equal(2, c.PreDayCrewAutoCounted);
        Assert.Equal(1, c.PreDayDeclared);
        Assert.Equal(3, c.PreDaySponsorBoothMembers);
        Assert.Equal(1, c.PreDayTwoDayAttendees);
        Assert.Equal(7, c.PreDay);
    }

    /// <summary>Main day: everyone on site (§326h) — 5 countable crew + 2 live attendees.</summary>
    [Fact]
    public async Task The_main_day_head_count_is_everybody_on_site()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var c = await new LunchHeadcountService(db).ComputeAsync(EventId);

        Assert.Equal(5, c.MainDayCrew);        // 2 crew + speaker + volunteer + sponsor contact
        Assert.Equal(2, c.MainDayAttendees);   // the cancelled ticket is not a meal
        Assert.Equal(7, c.MainDay);
    }

    // ---- the surfaces agree -----------------------------------------------------------------

    /// <summary>
    /// 🔴 THE CONTRACT THE OPERATOR ASKED FOR. The venue's spreadsheet total and the engine the
    /// organizer pages read must be the same number — they were not, in three different ways.
    /// </summary>
    [Fact]
    public async Task The_venue_spreadsheet_total_equals_the_engine()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var expected = (await new LunchHeadcountService(db).ComputeAsync(EventId)).PreDay;
        var file = (await new LunchLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27"))
            .Single(f => f.FileName == "eldk27-lunch-day1-preday.xlsx");

        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var summary = wb.Worksheet("Catering summary");
        var total = -1;
        for (var r = 2; r < 40; r++)
            if (summary.Cell(r, 1).GetString() == "Total covers") total = summary.Cell(r, 2).GetValue<int>();

        Assert.Equal(expected, total);
        // …and the headline the organizer sees on the logistics page says the same.
        Assert.Contains(expected.ToString(), file.Headline);
    }

    /// <summary>
    /// The NAMED rows and the counted heads must reconcile: a number an organizer cannot check
    /// against a list is a number they will not trust (§326bv).
    /// </summary>
    [Fact]
    public async Task Every_counted_participant_head_appears_as_a_named_row()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var c = await new LunchHeadcountService(db).ComputeAsync(EventId);
        var file = (await new LunchLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27"))
            .Single(f => f.FileName == "eldk27-lunch-day1-preday.xlsx");

        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheets.First();
        var names = new List<string>();
        for (var r = 2; !string.IsNullOrWhiteSpace(ws.Cell(r, 1).GetString()); r++)
            names.Add(ws.Cell(r, 1).GetString());

        Assert.Equal(c.PreDayNamed, names.Count);
        Assert.Contains("Ada Lovelace", names);        // auto-counted crew ARE listed
        Assert.Contains("Grace Hopper", names);        // and the speaker who ticked
        Assert.DoesNotContain("Sponsor Contact", names); // counted once, via the booth count
        Assert.DoesNotContain("Test Account", names);
        Assert.DoesNotContain("Gone Away", names);
    }

    /// <summary>
    /// 🔴 <i>"it shows 100, but we have 97"</i> — every headline attendee total must exclude the
    /// cancelled rows the attendee table keeps as history (§326as).
    /// </summary>
    [Fact]
    public async Task Every_attendee_total_counts_live_tickets_only()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var clock = new FixedClock();

        var overview = await new OrganizerOverviewService(db, clock).BuildAsync(EventId);
        var report = await new ReportingService(db, clock).BuildAsync(EventId);
        var lunch = await new LunchHeadcountService(db).ComputeAsync(EventId);

        // 2 live tickets, 1 cancelled — every surface says 2.
        Assert.Equal(2, overview.AttendeeTotal);
        Assert.Equal(2, report.AttendeeTotal);
        Assert.Equal(2, lunch.MainDayAttendees);
    }

    // ---- the structural guard ---------------------------------------------------------------

    /// <summary>
    /// 🔒 <b>The rule that keeps the rule.</b> Counting <c>Attendees</c> without saying which rows
    /// are live is how all three defects above happened, and every one of them looked correct in
    /// review. This fails on the next one.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ Deliberately narrow: it only inspects COUNT/SUM aggregates, which is where a
    /// head-count is produced. A query that LISTS attendees has its own reasons (the sync services
    /// must see cancelled rows to un-cancel them; the audit surfaces exist to show history).</para>
    ///
    /// <para>🔑 It reads the whole STATEMENT, not the text before the call. The first version
    /// stopped at <c>.CountAsync(</c> and reported five false positives — the filter lives INSIDE
    /// the call, in the lambda. A guard that cries wolf gets an allowlist bolted onto it and then
    /// stops guarding anything.</para>
    /// </remarks>
    [Fact]
    public void No_attendee_HEAD_COUNT_is_written_without_the_live_ticket_rule()
    {
        var markers = new[]
        {
            "MirrorState", ".LiveIn(", ".Live()", "CountableAttendees", "PreDayAttendees",
        };

        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            // Statements, so the aggregate and the predicate it was given are read together.
            foreach (var statement in File.ReadAllText(file).Split(';'))
            {
                if (!statement.Contains(".Attendees")) continue;
                if (!statement.Contains(".CountAsync(") && !statement.Contains(".SumAsync(")) continue;
                if (markers.Any(statement.Contains)) continue;
                offenders.Add($"{Path.GetFileName(file)}: {Collapse(statement)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These count attendees without stating which tickets are live — a cancelled ticket is "
            + "not a person who is coming (§1086). Use AttendeeScope.LiveIn(eventId):\n  "
            + string.Join("\n  ", offenders));
    }

    private static string Collapse(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim() is var one && one.Length > 120 ? one[..120] + "…" : one;

    private static IEnumerable<string> SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Directory
            .EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
    }
}
