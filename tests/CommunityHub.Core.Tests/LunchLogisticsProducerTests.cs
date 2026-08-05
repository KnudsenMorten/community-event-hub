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

    /// <summary>🔒 THE RULE: the sign-up decides, not the role.</summary>
    [Fact]
    public async Task Only_people_who_SIGNED_UP_for_the_pre_day_lunch_are_on_the_sheet()
    {
        using var db = NewDb();
        var ada = await AddAsync(db, "Ada Lovelace", ParticipantRole.Organizer);
        var grace = await AddAsync(db, "Grace Hopper", ParticipantRole.Organizer);
        await SignUpAsync(db, ada, preDay: true);
        await SignUpAsync(db, grace, preDay: false);     // same role, said no

        var names = Names(await BuildAsync(db));

        Assert.Equal(["Ada Lovelace"], names);
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

    private static async Task AddAsync(
        CommunityHubDbContext db, string name, bool active = true, bool lunchPreDay = false)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = ParticipantRole.Organizer, IsActive = active,
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
