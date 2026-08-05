using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 / §3.5 — the appreciation-dinner and party files.
/// </summary>
/// <remarks>
/// <para>§3.5: <i>"Appreciation Dinner counts accompanying guests and lists speakers with allergies
/// by name, with their comments. Party counts from sign-ups."</i></para>
///
/// <para>🔒 The costly failures here are UNDER-counting (somebody arrives to no seat and no meal)
/// and losing an allergy (somebody is handed food they cannot eat). Both get a test.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class FoodLogisticsProducerTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"food-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> AddPersonAsync(
        CommunityHubDbContext db, string name, bool active = true, bool testUser = false)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = ParticipantRole.Speaker, IsActive = active, IsTestUser = testUser,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task DinnerAsync(
        CommunityHubDbContext db, int pid, bool attending = true,
        int plusOneCount = 0, bool plusOne = false, string? allergyNotes = null)
    {
        db.DinnerSignups.Add(new DinnerSignup
        {
            EventId = EventId, ParticipantId = pid, Attending = attending,
            PlusOne = plusOne, PlusOneCount = plusOneCount, AllergyNotes = allergyNotes,
        });
        await db.SaveChangesAsync();
    }

    private static async Task PartyAsync(
        CommunityHubDbContext db, string name, int? headCount, int? pid = null, bool attending = true)
    {
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = EventId, Name = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Attending = attending, HeadCount = headCount, ParticipantId = pid,
        });
        await db.SaveChangesAsync();
    }

    private static IXLWorksheet Sheet(GeneratedFile file, string name) =>
        new XLWorkbook(new MemoryStream(file.Content)).Worksheet(name);

    private static async Task<IReadOnlyList<GeneratedFile>> BuildAsync(CommunityHubDbContext db) =>
        await new FoodLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

    // ---- dinner -----------------------------------------------------------------------------

    [Fact]
    public async Task Accompanying_guests_are_COUNTED_as_seats()
    {
        using var db = NewDb();
        var ada = await AddPersonAsync(db, "Ada Lovelace");
        await DinnerAsync(db, ada, plusOneCount: 2);

        var dinner = (await BuildAsync(db))
            .Single(f => f.FileName == "eldk27-appreciationdinner-preday.xlsx");
        var ws = Sheet(dinner, "Dinner attendees");

        Assert.Equal(2, ws.Cell(2, 4).GetValue<int>());   // guests
        Assert.Equal(3, ws.Cell(2, 5).GetValue<int>());   // the person + 2 guests
    }

    /// <summary>
    /// ⚠️ A legacy tick with no number still means ONE guest. Reading it as zero would seat one
    /// person short per row that predates the counted field.
    /// </summary>
    [Fact]
    public async Task A_plus_one_TICK_without_a_number_still_counts_as_one_guest()
    {
        using var db = NewDb();
        var ada = await AddPersonAsync(db, "Ada Lovelace");
        await DinnerAsync(db, ada, plusOne: true, plusOneCount: 0);

        var ws = Sheet((await BuildAsync(db))
            .Single(f => f.FileName == "eldk27-appreciationdinner-preday.xlsx"), "Dinner attendees");

        Assert.Equal(1, ws.Cell(2, 4).GetValue<int>());
        Assert.Equal(2, ws.Cell(2, 5).GetValue<int>());
    }

    /// <summary>
    /// 🔒 §3.5 asks for the allergy people BY NAME. The kitchen works from that sheet, so it must
    /// not be a filter somebody has to remember to apply to the big list.
    /// </summary>
    [Fact]
    public async Task People_with_allergies_get_their_own_sheet_BY_NAME_with_their_comments()
    {
        using var db = NewDb();
        var ada = await AddPersonAsync(db, "Ada Lovelace");
        var grace = await AddPersonAsync(db, "Grace Hopper");
        await DinnerAsync(db, ada, allergyNotes: "Severe nut allergy — no cross-contamination");
        await DinnerAsync(db, grace);

        var ws = Sheet((await BuildAsync(db))
            .Single(f => f.FileName == "eldk27-appreciationdinner-preday.xlsx"), "Allergies by name");

        Assert.Equal("Ada Lovelace", ws.Cell(2, 1).GetString());
        Assert.Contains("nut allergy", ws.Cell(2, 4).GetString(), StringComparison.OrdinalIgnoreCase);
        // Grace has nothing to accommodate and must NOT be on the kitchen's sheet.
        Assert.True(string.IsNullOrWhiteSpace(ws.Cell(3, 1).GetString()));
    }

    [Fact]
    public async Task Someone_who_declined_the_dinner_is_not_seated()
    {
        using var db = NewDb();
        var ada = await AddPersonAsync(db, "Ada Lovelace");
        await DinnerAsync(db, ada, attending: false);

        var ws = Sheet((await BuildAsync(db))
            .Single(f => f.FileName == "eldk27-appreciationdinner-preday.xlsx"), "Dinner attendees");

        Assert.True(string.IsNullOrWhiteSpace(ws.Cell(2, 1).GetString()));
    }

    [Fact]
    public async Task A_withdrawn_person_is_not_seated_even_though_their_signup_survives()
    {
        using var db = NewDb();
        var gone = await AddPersonAsync(db, "Grace Hopper", active: false);
        await DinnerAsync(db, gone, plusOneCount: 1);

        var ws = Sheet((await BuildAsync(db))
            .Single(f => f.FileName == "eldk27-appreciationdinner-preday.xlsx"), "Dinner attendees");

        // Operator: "in general inactive is not counted" — and a dinner seat is bought.
        Assert.True(string.IsNullOrWhiteSpace(ws.Cell(2, 1).GetString()));
    }

    // ---- party ------------------------------------------------------------------------------

    /// <summary>
    /// 🔒 THE UNDER-CATERING TRAP. An RSVP that says "yes" with no number is still a person. Reading
    /// a blank head-count as zero silently seats nobody for everyone who skipped that field.
    /// </summary>
    [Fact]
    public async Task An_RSVP_with_NO_head_count_still_counts_as_one_seat()
    {
        using var db = NewDb();
        await PartyAsync(db, "Ada Lovelace", headCount: null);

        var ws = Sheet((await BuildAsync(db)).Single(f => f.FileName == "eldk27-party-preday.xlsx"),
            "Party sign-ups");

        Assert.Equal(1, ws.Cell(2, 3).GetValue<int>());
    }

    /// <summary>
    /// ⚠️ A party RSVP can be ANONYMOUS — a public sign-up with no participant row at all. Those
    /// people are coming and must be fed; there is no active flag to test, so they count on their
    /// own, and the sheet says where each row came from.
    /// </summary>
    [Fact]
    public async Task An_anonymous_public_sign_up_counts_and_is_labelled_as_such()
    {
        using var db = NewDb();
        await PartyAsync(db, "Ada Lovelace", headCount: 2);            // no participant link

        var ws = Sheet((await BuildAsync(db)).Single(f => f.FileName == "eldk27-party-preday.xlsx"),
            "Party sign-ups");

        Assert.Equal(2, ws.Cell(2, 3).GetValue<int>());
        Assert.Equal("Public sign-up", ws.Cell(2, 4).GetString());
    }

    [Fact]
    public async Task A_LINKED_rsvp_from_a_withdrawn_participant_does_not_count()
    {
        using var db = NewDb();
        var gone = await AddPersonAsync(db, "Grace Hopper", active: false);
        await PartyAsync(db, "Grace Hopper", headCount: 3, pid: gone);

        var ws = Sheet((await BuildAsync(db)).Single(f => f.FileName == "eldk27-party-preday.xlsx"),
            "Party sign-ups");

        Assert.True(string.IsNullOrWhiteSpace(ws.Cell(2, 1).GetString()));
    }

    // ---- the property the mail rule needs ---------------------------------------------------

    [Fact]
    public async Task Two_runs_over_unchanged_data_produce_the_same_content_key()
    {
        using var db = NewDb();
        var ada = await AddPersonAsync(db, "Ada Lovelace");
        await DinnerAsync(db, ada, plusOneCount: 1, allergyNotes: "Gluten");
        await PartyAsync(db, "Ada Lovelace", headCount: 2, pid: ada);

        var first = await BuildAsync(db);
        var second = await BuildAsync(db);

        foreach (var (a, b) in first.Zip(second))
            Assert.Equal(a.ContentKey, b.ContentKey);
    }
}
