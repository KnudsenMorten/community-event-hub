using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 / §3.5 — the swag files, and the two properties the whole mail schedule rests on.
/// </summary>
/// <remarks>
/// 🔒 <b>DETERMINISM is not a nicety here.</b> §6.4 rebuilds these daily and mails weekly, with the
/// hotel file mailed ON CHANGE. If an unchanged edition produced different bytes twice, every file
/// would report "changed" every day and the change-triggered mail would become a daily mail to an
/// external contact. These tests pin byte-identity.
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class SwagLogisticsProducerTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase(name ?? $"swagprod-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> AddAsync(
        CommunityHubDbContext db, string name, ParticipantRole role,
        bool polo = false, string? size = null, bool gift = false, bool credly = false,
        bool active = true, bool testUser = false)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = role, IsActive = active, IsTestUser = testUser,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.SwagPreferences.Add(new SwagPreference
        {
            EventId = EventId, ParticipantId = p.Id,
            WantsPolo = polo, PoloSize = size, WantsGift = gift, WantsCredlyBadge = credly,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task The_award_and_polo_files_are_named_from_the_event_short_name()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, polo: true, size: "M", gift: true);

        var files = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        Assert.Contains(files, f => f.FileName == "eldk27-award.xlsx");
        Assert.Contains(files, f => f.FileName == "eldk27-polo.xlsx");
    }

    /// <summary>
    /// 🔒 THE PROPERTY THE MAIL RULE DEPENDS ON. Same data twice ⇒ the same bytes.
    /// </summary>
    [Fact]
    public async Task Two_runs_over_UNCHANGED_data_produce_the_SAME_CONTENT_KEY()
    {
        var dbName = $"swagdet-{Guid.NewGuid():N}";
        using var db = NewDb(dbName);
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, polo: true, size: "M", gift: true, credly: true);
        await AddAsync(db, "Grace Hopper", ParticipantRole.Organizer, polo: true, size: "L", gift: true);

        var first = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        // A DIFFERENT context over the same data — the row order EF returns must not decide the bytes.
        using var db2 = NewDb(dbName);
        var second = await new SwagLogisticsProducer(db2).BuildAllAsync(EventId, "ELDK27");

        Assert.Equal(first.Count, second.Count);
        foreach (var (a, b) in first.Zip(second))
        {
            Assert.Equal(a.FileName, b.FileName);

            // 🔒 The CONTENT KEY is what must be stable, not the bytes. An .xlsx is a ZIP whose
            // document properties and per-entry timestamps are not reproducible — this assertion
            // was written against the bytes first, and it FAILED, which is how that was found.
            // Comparing bytes would have reported "changed" daily and mailed the venue daily.
            Assert.Equal(a.ContentKey, b.ContentKey);
        }
    }

    [Fact]
    public async Task A_CHANGE_in_the_data_changes_the_content_key()
    {
        var dbName = $"swagchg-{Guid.NewGuid():N}";
        using var db = NewDb(dbName);
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, polo: true, size: "M");
        var before = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        await AddAsync(db, "Grace Hopper", ParticipantRole.Volunteer, polo: true, size: "L");
        var after = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        var b1 = before.Single(f => f.FileName == "eldk27-polo.xlsx").ContentKey;
        var b2 = after.Single(f => f.FileName == "eldk27-polo.xlsx").ContentKey;
        Assert.NotEqual(b1, b2);
    }

    /// <summary>
    /// 🔒 Operator 2026-08-02: <i>"in general inactive is not counted"</i>. These sheets order
    /// physical goods — a withdrawn person is a shirt printed and paid for.
    /// </summary>
    [Fact]
    public async Task An_INACTIVE_person_is_not_counted()
    {
        var dbName = $"swaginact-{Guid.NewGuid():N}";
        using var db = NewDb(dbName);
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, polo: true, size: "M");
        var withOnlyAda = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        await AddAsync(db, "Grace Hopper", ParticipantRole.Volunteer, polo: true, size: "M", active: false);
        var withWithdrawnGrace = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        // A withdrawn person's swag row still EXISTS (the form result is kept); it must not count.
        Assert.Equal(
            withOnlyAda.Single(f => f.FileName == "eldk27-polo.xlsx").ContentKey,
            withWithdrawnGrace.Single(f => f.FileName == "eldk27-polo.xlsx").ContentKey);
    }

    /// <summary>
    /// ⚠️ A CHANGE from the old download, and a deliberate one: a test persona's shirt is a real
    /// shirt. The old page filtered on IsActive alone.
    /// </summary>
    [Fact]
    public async Task A_TEST_persona_is_not_counted_either()
    {
        var dbName = $"swagtest-{Guid.NewGuid():N}";
        using var db = NewDb(dbName);
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, polo: true, size: "M");
        var real = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        await AddAsync(db, "Test Persona", ParticipantRole.Volunteer, polo: true, size: "M", testUser: true);
        var withTest = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        Assert.Equal(
            real.Single(f => f.FileName == "eldk27-polo.xlsx").ContentKey,
            withTest.Single(f => f.FileName == "eldk27-polo.xlsx").ContentKey);
    }

    /// <summary>§3.5: one Credly PAIR per CEH role — and no file for a role nobody is in.</summary>
    [Fact]
    public async Task Credly_produces_one_xlsx_and_one_csv_PER_ROLE_and_none_for_empty_roles()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, credly: true);
        await AddAsync(db, "Grace Hopper", ParticipantRole.Speaker, credly: true);
        await AddAsync(db, "Nobody Badge", ParticipantRole.Attendee);   // wants no badge

        var files = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

        Assert.Contains(files, f => f.FileName == "eldk27-credly-volunteer.xlsx");
        Assert.Contains(files, f => f.FileName == "eldk27-credly-volunteer.csv");
        Assert.Contains(files, f => f.FileName == "eldk27-credly-speaker.xlsx");
        Assert.Contains(files, f => f.FileName == "eldk27-credly-speaker.csv");
        // An empty workbook per unused role is noise in a folder a human reads.
        Assert.DoesNotContain(files, f => f.FileName.Contains("attendee"));
    }

    [Fact]
    public async Task The_Credly_csv_carries_name_and_email_which_is_what_gets_imported()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, credly: true);

        var files = await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");
        var csv = System.Text.Encoding.UTF8.GetString(
            files.Single(f => f.FileName == "eldk27-credly-volunteer.csv").Content);

        Assert.StartsWith("Name,Email\n", csv);
        Assert.Contains("Ada Lovelace,Ada.Lovelace@example.test", csv);
    }

    /// <summary>§299 OPEN-14 — every organizer is a flat ×5, whether or not they also speak.</summary>
    [Fact]
    public async Task An_organizer_counts_as_five_polos()
    {
        using var db = NewDb();
        await AddAsync(db, "Grace Hopper", ParticipantRole.Organizer, polo: true, size: "L");

        var polo = (await new SwagLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27"))
            .Single(f => f.FileName == "eldk27-polo.xlsx");

        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(polo.Content));
        var ws = wb.Worksheet("Polo per person");
        Assert.Equal(5, ws.Cell(2, 5).GetValue<int>());
    }
}
