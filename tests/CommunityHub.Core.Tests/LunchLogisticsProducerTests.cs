using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 / §3.5 — the lunch file, built from the SIGN-UPS rather than from a rule about who is
/// probably there.
/// </summary>
/// <remarks>
/// 🔒 Operator 2026-08-02: <i>"data is in the table for the different sign up. so calculation
/// service must not add another logic … we have data already in ceh"</i>. The version this replaced
/// derived lunch from roles and ticket classes; these tests pin that the sign-up decides.
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class LunchLogisticsProducerTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"lunch-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> AddAsync(
        CommunityHubDbContext db, string name, ParticipantRole role, bool active = true)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = role, IsActive = active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task SignUpAsync(
        CommunityHubDbContext db, int pid, bool preDay, string? notes = null)
    {
        db.LunchSignups.Add(new LunchSignup
        {
            EventId = EventId, ParticipantId = pid, LunchPreDay = preDay, Notes = notes,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<GeneratedFile> BuildAsync(CommunityHubDbContext db) =>
        (await new LunchLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27"))
        .Single(f => f.FileName == "eldk27-lunch-day1-preday.xlsx");

    private static List<string> Names(GeneratedFile file)
    {
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheets.First();
        var names = new List<string>();
        for (var r = 2; !string.IsNullOrWhiteSpace(ws.Cell(r, 1).GetString()); r++)
            names.Add(ws.Cell(r, 1).GetString());
        return names;
    }

    private static int SummaryValue(GeneratedFile file, string label)
    {
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheet("Catering summary");
        for (var r = 2; r < 40; r++)
            if (ws.Cell(r, 1).GetString() == label) return ws.Cell(r, 2).GetValue<int>();
        return -1;
    }

    /// <summary>
    /// 🔒 THE RULE: for the roles that ARE asked, the sign-up decides — not a guess from the role.
    /// </summary>
    /// <remarks>
    /// ⚠️ §1086 — this test used two ORGANIZERS, and that was a bad fixture rather than a bad rule:
    /// an organizer is never SHOWN the pre-day checkbox (<c>PreDayAutoCountedRole</c>), so
    /// "same role, said no" was a state the product cannot produce. It now uses volunteers, who are
    /// genuinely asked. The auto-counted crew have their own test below.
    /// </remarks>
    [Fact]
    public async Task Only_people_who_SIGNED_UP_for_the_pre_day_lunch_are_on_the_sheet()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer);
        var grace = await AddAsync(db, "Grace Hopper", ParticipantRole.Volunteer);
        await SignUpAsync(db, ada, preDay: true);
        await SignUpAsync(db, grace, preDay: false);     // same role, said no

        var names = Names(await BuildAsync(db));

        Assert.Equal(["Ada Lovelace"], names);
    }

    /// <summary>
    /// 🔴 §1086 — THE CREW ARE ON THE SHEET WITHOUT ANSWERING ANYTHING. Organizers, media and
    /// event partners are never shown a pre-day checkbox, so their stored <c>false</c> means "never
    /// asked". The file used to read it as "not eating" and left every one of them out of the
    /// venue's order.
    /// </summary>
    [Fact]
    public async Task Always_on_site_crew_are_counted_without_a_sign_up()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);       // no LunchSignup at all
        var media = await AddAsync(db, "Grace Hopper", ParticipantRole.Media);
        await SignUpAsync(db, media, preDay: false);                          // stored false = unasked

        var names = Names(await BuildAsync(db));

        Assert.Equal(["Ada Lovelace", "Grace Hopper"], names.Order());
    }

    /// <summary>
    /// 🔴 §1086 — A SPONSOR WHO TICKED WAS COUNTED TWICE: once as a named cover, and again inside
    /// their company's booth-member count. Their pre-day heads are company-level by design (§298).
    /// </summary>
    [Fact]
    public async Task A_sponsor_who_ticked_the_box_is_not_double_counted_against_their_booth_team()
    {
        using var db = NewDb();
        var contact = await AddAsync(db, "Sponsor Contact", ParticipantRole.Sponsor);
        await SignUpAsync(db, contact, preDay: true);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "c1",
            BoothCheckInSlot = BoothCheckInSlots.S0900, BoothCheckInMemberCount = 3,
        });
        await db.SaveChangesAsync();

        var file = await BuildAsync(db);

        Assert.Empty(Names(file));                                     // not named…
        Assert.Equal(3, SummaryValue(file, "Sponsor booth members (from booth check-in)"));
        Assert.Equal(3, SummaryValue(file, "Total covers"));           // …and counted once
    }

    [Fact]
    public async Task Somebody_who_never_answered_the_form_is_not_counted()
    {
        using var db = NewDb();
        await AddAsync(db, "Grace Hopper", ParticipantRole.Speaker);   // no LunchSignup row at all

        Assert.Empty(Names(await BuildAsync(db)));
    }

    [Fact]
    public async Task A_withdrawn_person_who_signed_up_is_not_counted()
    {
        using var db = NewDb();
        var gone = await AddAsync(db, "Grace Hopper", ParticipantRole.Volunteer, active: false);
        await SignUpAsync(db, gone, preDay: true);

        // "In general inactive is not counted" — the sign-up row survives the withdrawal (§209).
        Assert.Empty(Names(await BuildAsync(db)));
    }

    /// <summary>
    /// 🔒 §298, verbatim: the booth check-in <i>"feeds the organizer pre-day LUNCH headcount (each
    /// checked-in booth member eats the pre-day lunch)"</i>. It is a COUNT, not named people.
    /// </summary>
    [Fact]
    public async Task Sponsor_booth_members_are_added_from_the_BOOTH_CHECK_IN_count()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);
        await SignUpAsync(db, ada, preDay: true);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "c1",
            BoothCheckInSlot = BoothCheckInSlots.S0900, BoothCheckInMemberCount = 4,
        });
        await db.SaveChangesAsync();

        var file = await BuildAsync(db);

        Assert.Equal(1, SummaryValue(file, "Signed-up covers (named)"));
        Assert.Equal(4, SummaryValue(file, "Sponsor booth members (from booth check-in)"));
        Assert.Equal(5, SummaryValue(file, "Total covers"));
    }

    /// <summary>
    /// ⚠️ A sponsor who ticked "we are not participating on the pre-day" must not be catered for.
    /// This is the case the earlier derived version got wrong for every sponsor at once.
    /// </summary>
    [Fact]
    public async Task A_sponsor_who_opted_OUT_of_the_pre_day_contributes_nobody()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "c1",
            BoothCheckInSlot = BoothCheckInSlots.NotParticipating, BoothCheckInMemberCount = 6,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, SummaryValue(await BuildAsync(db), "Sponsor booth members (from booth check-in)"));
    }

    [Fact]
    public async Task A_sponsor_who_has_not_answered_the_check_in_contributes_nobody()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "c1" });
        await db.SaveChangesAsync();

        Assert.Equal(0, SummaryValue(await BuildAsync(db), "Sponsor booth members (from booth check-in)"));
    }

    [Fact]
    public async Task Two_runs_over_unchanged_data_produce_the_same_content_key()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);
        await SignUpAsync(db, ada, preDay: true, notes: "arrives late");

        var first = await BuildAsync(db);
        var second = await BuildAsync(db);

        Assert.Equal(first.ContentKey, second.ContentKey);
    }

    // ---- §1086 — the Master Class audience eats the pre-day lunch --------------------------

    private static async Task AddAttendeeAsync(
        CommunityHubDbContext db, string name, TicketStatus ticket,
        MirrorState mirror = MirrorState.Active)
    {
        db.Attendees.Add(new Attendee
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            TicketStatus = ticket, MirrorState = mirror,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 🔴 THE REPORTED BUG (operator 2026-08-14): <i>"logistics lunch preday must contain any
    /// attendees that booked a 2-day ticket. i think it is missing in the calculation and excel
    /// export"</i>. The pre-day IS the Master Class day — every 2-day holder is in the room.
    /// </summary>
    [Fact]
    public async Task Two_day_ticket_holders_are_counted_on_the_pre_day()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);
        await SignUpAsync(db, ada, preDay: true);
        await AddAttendeeAsync(db, "Two Day One", TicketStatus.TwoDay);
        await AddAttendeeAsync(db, "Two Day Two", TicketStatus.TwoDay);

        var file = await BuildAsync(db);

        Assert.Equal(1, SummaryValue(file, "Signed-up covers (named)"));
        Assert.Equal(2, SummaryValue(file, "Attendees with a 2-day ticket (Master Class)"));
        Assert.Equal(3, SummaryValue(file, "Total covers"));
    }

    /// <summary>
    /// ⚠️ A 1-day ticket does NOT admit you to the Master Class, so that person is not in the
    /// building on the pre-day and must not be catered for.
    /// </summary>
    [Fact]
    public async Task A_one_day_ticket_holder_is_NOT_counted_on_the_pre_day()
    {
        using var db = NewDb();
        await AddAttendeeAsync(db, "One Day", TicketStatus.Other);
        await AddAttendeeAsync(db, "No Ticket", TicketStatus.None);

        Assert.Equal(0, SummaryValue(await BuildAsync(db), "Attendees with a 2-day ticket (Master Class)"));
    }

    /// <summary>§326as — a cancelled ticket keeps its row for audit and stops being a cover.</summary>
    [Fact]
    public async Task A_cancelled_two_day_ticket_is_not_a_cover()
    {
        using var db = NewDb();
        await AddAttendeeAsync(db, "Gone Away", TicketStatus.TwoDay, MirrorState.Cancelled);

        Assert.Equal(0, SummaryValue(await BuildAsync(db), "Attendees with a 2-day ticket (Master Class)"));
    }

    /// <summary>
    /// 🔑 The count is part of the CHANGE KEY: a ticket sold today must make the file "changed"
    /// today, or the venue keeps yesterday's order.
    /// </summary>
    [Fact]
    public async Task Selling_a_two_day_ticket_changes_the_content_key()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);
        await SignUpAsync(db, ada, preDay: true);
        var before = await BuildAsync(db);

        await AddAttendeeAsync(db, "Late Buyer", TicketStatus.TwoDay);

        Assert.NotEqual(before.ContentKey, (await BuildAsync(db)).ContentKey);
    }

    // ---- §1086b — the MAIN-DAY lunch file, and no diets on a lunch sheet -------------------

    private static async Task<GeneratedFile> BuildMainDayAsync(CommunityHubDbContext db) =>
        (await new LunchLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27"))
        .Single(f => f.FileName == "eldk27-lunch-day2-mainday.xlsx");

    /// <summary>
    /// 🔴 Operator 2026-08-14: <i>"make the main day lunch excel with named crew"</i>. The file name
    /// has been supported since §3.5 and nothing ever produced it, so the event's biggest catering
    /// order lived only on an organizer screen.
    /// </summary>
    [Fact]
    public async Task The_main_day_lunch_file_names_the_crew_and_counts_the_attendees()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);
        await AddAsync(db, "Grace Hopper", ParticipantRole.Speaker);
        await AddAsync(db, "Test Account", ParticipantRole.Volunteer);   // real person for this test
        await AddAttendeeAsync(db, "Two Day", TicketStatus.TwoDay);
        await AddAttendeeAsync(db, "One Day", TicketStatus.Other);
        await AddAttendeeAsync(db, "Cancelled", TicketStatus.TwoDay, MirrorState.Cancelled);

        var file = await BuildMainDayAsync(db);

        // Named crew — no sign-up needed: main-day lunch is ordered for everyone (§326h).
        Assert.Equal(["Ada Lovelace", "Grace Hopper", "Test Account"], Names(file).Order());
        Assert.Equal(3, SummaryValue(file, "Crew, speakers, volunteers and sponsors (named)"));
        // Both ticket classes eat on the main day; the cancelled one does not.
        Assert.Equal(2, SummaryValue(file, "Attendees (every live ticket, 1-day and 2-day)"));
        Assert.Equal(5, SummaryValue(file, "Total covers"));
    }

    /// <summary>
    /// 🔒 §1086b — <b>NO DIET ON A LUNCH SHEET.</b> Operator 2026-08-14: <i>"we do not support diets
    /// in the onboarding regarding lunch … we must not include this in the dialog in ceh except for
    /// the appreciation dinner"</i>.
    /// <para>⚠️ The column that was there read a dietary surface NOTHING in CEH writes, so the venue
    /// received an always-empty "Diet / allergies" column — which reads as "nobody has a dietary
    /// need", not as "we do not collect this".</para>
    /// </summary>
    [Fact]
    public async Task No_lunch_sheet_carries_a_diet_column()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer);
        await SignUpAsync(db, ada, preDay: true);

        foreach (var file in await new LunchLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27"))
        {
            if (!file.FileName.Contains("lunch")) continue;
            using var wb = new XLWorkbook(new MemoryStream(file.Content));
            var headers = wb.Worksheets.First().Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            Assert.DoesNotContain(headers, h => h.Contains("Diet", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(headers, h => h.Contains("allerg", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// …and the sheet SAYS why, rather than leaving the venue to wonder. An absent column is
    /// ambiguous; a sentence naming the real process is not.
    /// </summary>
    [Fact]
    public async Task The_lunch_summary_states_that_diets_are_agreed_with_the_venue()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);

        var file = await BuildMainDayAsync(db);
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var summary = wb.Worksheet("Catering summary");
        var text = string.Join(" ", Enumerable.Range(1, 12).Select(r => summary.Cell(r, 1).GetString()));

        Assert.Contains("ordering process", text);
        Assert.Contains("Appreciation Dinner", text);
    }

    /// <summary>
    /// 🔒 The file and <c>/Organizer/Lunch</c> must give ONE answer — the defect being fixed was
    /// that the page counted these people and the spreadsheet did not. Both now read
    /// <see cref="LogisticsAudience.PreDayAttendees"/>; this pins the rule itself.
    /// </summary>
    [Fact]
    public async Task The_shared_rule_selects_exactly_the_active_two_day_holders()
    {
        using var db = NewDb();
        await AddAttendeeAsync(db, "Keeps Ticket", TicketStatus.TwoDay);
        await AddAttendeeAsync(db, "Cancelled", TicketStatus.TwoDay, MirrorState.Cancelled);
        await AddAttendeeAsync(db, "One Day", TicketStatus.Other);

        var preDay = await LogisticsAudience.PreDayAttendees(db.Attendees, EventId).ToListAsync();
        var anyTicket = await LogisticsAudience.CountableAttendees(db.Attendees, EventId).ToListAsync();

        Assert.Equal(["Keeps Ticket"], preDay.Select(a => a.FullName));
        // Main day admits every ticket class — the cancelled row is still out.
        Assert.Equal(["Keeps Ticket", "One Day"], anyTicket.Select(a => a.FullName).Order());
    }
}

/// <summary>
/// §770.8 — the breakfast files. There is no breakfast sign-up in CEH, so these are an ESTIMATE.
/// </summary>
/// <remarks>
/// 🔒 Operator 2026-08-02: <i>"come up with breakfast calculation formula. use 75% of people as …
/// eat breakfast"</i>, because <i>"some [sleep] at hotels"</i> and eat breakfast there.
/// </remarks>
public sealed class BreakfastLogisticsTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"breakfast-{Guid.NewGuid():N}")
            .Options);

    /// <remarks>
    /// ⚠️ §1086 — VOLUNTEERS, not organizers. These fixtures are about the breakfast ESTIMATE
    /// riding on the lunch head-count, and an organizer is auto-counted for the pre-day whatever
    /// they answer (they are never asked), which would make "did not sign up for the pre-day" an
    /// impossible state to seed. The auto-count rule has its own tests.
    /// </remarks>
    private static async Task AddAsync(
        CommunityHubDbContext db, string name, bool active = true, bool lunchPreDay = false)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = ParticipantRole.Volunteer, IsActive = active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        if (lunchPreDay)
        {
            db.LunchSignups.Add(new LunchSignup
            {
                EventId = EventId, ParticipantId = p.Id, LunchPreDay = true,
            });
            await db.SaveChangesAsync();
        }
    }

    private static int Covers(GeneratedFile file)
    {
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheets.First();
        return ws.Cell(4, 2).GetValue<int>();
    }

    private static async Task<IReadOnlyList<GeneratedFile>> BuildAsync(CommunityHubDbContext db) =>
        await new LunchLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

    [Fact]
    public async Task The_MAIN_day_breakfast_is_75_percent_of_everybody_coming()
    {
        using var db = NewDb();
        for (var i = 0; i < 8; i++) await AddAsync(db, $"Person {i}");

        var file = (await BuildAsync(db)).Single(f => f.FileName == "eldk27-breakfast-day2-mainday.xlsx");

        Assert.Equal(6, Covers(file));       // 8 × 0.75
    }

    /// <summary>
    /// ⚠️ ROUNDS UP. A fractional cover is a real person: 3 × 0.75 = 2.25, and catering for two
    /// leaves somebody without breakfast. Over-catering by one costs one breakfast.
    /// </summary>
    [Fact]
    public async Task A_fractional_cover_rounds_UP_because_it_is_a_person()
    {
        using var db = NewDb();
        for (var i = 0; i < 3; i++) await AddAsync(db, $"Person {i}");

        var file = (await BuildAsync(db)).Single(f => f.FileName == "eldk27-breakfast-day2-mainday.xlsx");

        Assert.Equal(3, Covers(file));       // ceil(2.25)
    }

    [Fact]
    public async Task The_PRE_day_breakfast_counts_the_lunch_signups_not_everybody()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", lunchPreDay: true);
        await AddAsync(db, "Grace Hopper", lunchPreDay: true);
        // Four more people who are coming to the main day but did not sign up for the pre-day.
        for (var i = 0; i < 4; i++) await AddAsync(db, $"Person {i}");

        var files = await BuildAsync(db);

        Assert.Equal(2, Covers(files.Single(f => f.FileName == "eldk27-breakfast-day1-preday.xlsx")));
        Assert.Equal(5, Covers(files.Single(f => f.FileName == "eldk27-breakfast-day2-mainday.xlsx")));
    }

    [Fact]
    public async Task A_withdrawn_person_is_not_in_the_head_count()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace");
        await AddAsync(db, "Grace Hopper", active: false);

        var file = (await BuildAsync(db)).Single(f => f.FileName == "eldk27-breakfast-day2-mainday.xlsx");

        Assert.Equal(1, Covers(file));       // ceil(1 × 0.75)
    }

    /// <summary>
    /// 🔴 §1086 — the breakfast head-counts ride on the lunch ones, so the missing attendees were
    /// missing here too: the MAIN day counts every ticket class, the PRE day only the 2-day holders
    /// who are admitted to the Master Class.
    /// </summary>
    [Fact]
    public async Task Attendees_are_in_both_breakfast_head_counts_by_the_right_rule()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", lunchPreDay: true);       // 1 crew, pre-day + main day
        db.Attendees.AddRange(
            new Attendee { EventId = EventId, FullName = "Two Day", Email = "t@example.test",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active },
            new Attendee { EventId = EventId, FullName = "One Day", Email = "o@example.test",
                TicketStatus = TicketStatus.Other, MirrorState = MirrorState.Active },
            new Attendee { EventId = EventId, FullName = "Cancelled", Email = "c@example.test",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled });
        await db.SaveChangesAsync();

        var files = await BuildAsync(db);

        // Pre-day: 1 crew sign-up + 1 two-day attendee = 2 heads → ceil(1.5) = 2.
        Assert.Equal(2, Covers(files.Single(f => f.FileName == "eldk27-breakfast-day1-preday.xlsx")));
        // Main day: 1 crew + 2 live attendees (any class) = 3 heads → ceil(2.25) = 3.
        Assert.Equal(3, Covers(files.Single(f => f.FileName == "eldk27-breakfast-day2-mainday.xlsx")));
    }

    /// <summary>
    /// 🔒 No names on the breakfast sheet, deliberately: nobody was asked, so a per-person list
    /// would look like a register CEH does not have. Publishing invented names to a venue is worse
    /// than publishing an honest number.
    /// </summary>
    [Fact]
    public async Task The_breakfast_sheet_states_that_it_is_an_estimate_and_names_nobody()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace");

        var file = (await BuildAsync(db)).Single(f => f.FileName == "eldk27-breakfast-day2-mainday.xlsx");
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheets.First();

        var text = string.Join(" ", Enumerable.Range(1, 10).Select(r => ws.Cell(r, 1).GetString()));
        Assert.Contains("ESTIMATE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada Lovelace", text);
    }
}
