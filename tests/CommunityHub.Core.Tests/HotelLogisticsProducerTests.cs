using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 / §3.5 — one rooming-list file per hotel.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Each file goes to a DIFFERENT external company.</b> The tests that matter here are
/// about what must never appear in the wrong file: one hotel's list must not contain another
/// hotel's guests, and a change at one hotel must not present as a change at the other (§6.4 mails
/// on change, per hotel).</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class HotelLogisticsProducerTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"hotel-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> AddHotelAsync(
        CommunityHubDbContext db, string name, string? contact = null)
    {
        var h = new Hotel { EventId = EventId, Name = name, ContactEmail = contact };
        db.Hotels.Add(h);
        await db.SaveChangesAsync();
        return h.Id;
    }

    private static async Task<int> AddGuestAsync(
        CommunityHubDbContext db, string name, int? hotelId, bool needsRoom = true,
        DateOnly? checkIn = null, DateOnly? checkOut = null, bool active = true,
        HotelConfirmationState state = HotelConfirmationState.NotConfirmed)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = ParticipantRole.Speaker, IsActive = active, HotelId = hotelId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = p.Id, NeedsRoom = needsRoom,
            CheckInDate = checkIn, CheckOutDate = checkOut, ConfirmationState = state,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<IReadOnlyList<HotelLogisticsFile>> BuildAsync(CommunityHubDbContext db) =>
        await new HotelLogisticsProducer(db).BuildAllAsync(EventId, "ELDK27");

    private static List<string> Names(GeneratedFile f)
    {
        using var wb = new XLWorkbook(new MemoryStream(f.Content));
        var ws = wb.Worksheet("Rooming list");
        var names = new List<string>();
        for (var r = 2; ; r++)
        {
            var v = ws.Cell(r, 1).GetString();
            if (string.IsNullOrWhiteSpace(v) || v.Contains("total guests")) break;
            names.Add(v);
        }
        return names;
    }

    [Fact]
    public async Task One_file_per_hotel_named_after_the_hotel()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        var comfort = await AddHotelAsync(db, "Comfort Hotel Vesterbro");
        await AddGuestAsync(db, "Ada Lovelace", scandic);
        await AddGuestAsync(db, "Grace Hopper", comfort);

        var files = await BuildAsync(db);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.File.FileName == "eldk27-hotel-scandic-copenhagen.xlsx");
        Assert.Contains(files, f => f.File.FileName == "eldk27-hotel-comfort-hotel-vesterbro.xlsx");
    }

    /// <summary>
    /// 🔒 THE PRIVACY BOUNDARY. Each file goes to a different company; one hotel must never see who
    /// is staying at another, and it cannot be unsent.
    /// </summary>
    [Fact]
    public async Task A_hotels_file_contains_ONLY_its_own_guests()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        var comfort = await AddHotelAsync(db, "Comfort Hotel Vesterbro");
        await AddGuestAsync(db, "Ada Lovelace", scandic);
        await AddGuestAsync(db, "Grace Hopper", comfort);

        var files = await BuildAsync(db);

        Assert.Equal(["Ada Lovelace"],
            Names(files.Single(f => f.HotelName == "Scandic Copenhagen").File));
        Assert.Equal(["Grace Hopper"],
            Names(files.Single(f => f.HotelName == "Comfort Hotel Vesterbro").File));
    }

    /// <summary>
    /// 🔒 §6.4 mails on change, PER HOTEL. A guest moving into one hotel must not make the other
    /// hotel's file look changed — that would mail a company about a list that did not move.
    /// </summary>
    [Fact]
    public async Task A_change_at_one_hotel_does_not_move_the_other_hotels_key()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        var comfort = await AddHotelAsync(db, "Comfort Hotel Vesterbro");
        await AddGuestAsync(db, "Ada Lovelace", scandic);
        await AddGuestAsync(db, "Grace Hopper", comfort);

        var before = await BuildAsync(db);
        await AddGuestAsync(db, "Alan Turing", scandic);
        var after = await BuildAsync(db);

        Assert.NotEqual(
            before.Single(f => f.HotelName == "Scandic Copenhagen").File.ContentKey,
            after.Single(f => f.HotelName == "Scandic Copenhagen").File.ContentKey);
        Assert.Equal(
            before.Single(f => f.HotelName == "Comfort Hotel Vesterbro").File.ContentKey,
            after.Single(f => f.HotelName == "Comfort Hotel Vesterbro").File.ContentKey);
    }

    /// <summary>
    /// ⚠️ Somebody who needs a room but has NOT been placed belongs to no hotel. Putting them in the
    /// first one would book a room somebody pays for.
    /// </summary>
    [Fact]
    public async Task An_UNPLACED_booking_is_in_nobodys_file_and_is_reported()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        await AddGuestAsync(db, "Ada Lovelace", scandic);
        await AddGuestAsync(db, "Grace Hopper", hotelId: null);

        var files = await BuildAsync(db);
        var unplaced = await new HotelLogisticsProducer(db).UnplacedAsync(EventId);

        Assert.Equal(["Ada Lovelace"], Names(files.Single().File));
        Assert.Equal(["Grace Hopper"], unplaced);
    }

    [Fact]
    public async Task Somebody_placed_who_no_longer_needs_a_room_is_not_on_the_list()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        await AddGuestAsync(db, "Ada Lovelace", scandic, needsRoom: false);

        Assert.Empty(await BuildAsync(db));      // no guests ⇒ no file for that hotel
    }

    [Fact]
    public async Task A_withdrawn_person_is_not_on_the_rooming_list()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        await AddGuestAsync(db, "Grace Hopper", scandic, active: false);

        Assert.Empty(await BuildAsync(db));
    }

    /// <summary>
    /// ⚠️ The hotel bills per NIGHT, so this number becomes money. A missing date must show as 0 and
    /// stand out, not quietly become a two-night stay.
    /// </summary>
    [Fact]
    public async Task Nights_are_counted_and_a_missing_date_is_zero_not_a_guess()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        await AddGuestAsync(db, "Ada Lovelace", scandic,
            checkIn: new DateOnly(2027, 2, 8), checkOut: new DateOnly(2027, 2, 10));
        await AddGuestAsync(db, "Grace Hopper", scandic, checkIn: new DateOnly(2027, 2, 8));

        var file = (await BuildAsync(db)).Single().File;
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheet("Rooming list");

        Assert.Equal(2, ws.Cell(2, 6).GetValue<int>());   // Ada: 8th → 10th
        Assert.Equal(0, ws.Cell(3, 6).GetValue<int>());   // Grace: no check-out recorded
    }

    [Fact]
    public async Task The_hotels_own_contact_travels_with_its_file()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen", "reservations@example.test");
        await AddGuestAsync(db, "Ada Lovelace", scandic);

        var file = (await BuildAsync(db)).Single();

        // §3.5: "hotel contacts already exist in CEH, use them."
        Assert.Equal("reservations@example.test", file.ContactEmail);
    }

    [Fact]
    public async Task A_hotel_with_no_contact_still_gets_a_FILE()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen", contact: null);
        await AddGuestAsync(db, "Ada Lovelace", scandic);

        var file = (await BuildAsync(db)).Single();

        // Produce the file, mail nobody — a missing address is not a reason to lose the rooming list.
        Assert.Null(file.ContactEmail);
        Assert.Equal(["Ada Lovelace"], Names(file.File));
    }

    [Fact]
    public async Task Two_runs_over_unchanged_data_produce_the_same_content_key()
    {
        using var db = NewDb();
        var scandic = await AddHotelAsync(db, "Scandic Copenhagen");
        await AddGuestAsync(db, "Ada Lovelace", scandic,
            checkIn: new DateOnly(2027, 2, 8), checkOut: new DateOnly(2027, 2, 10));

        var first = await BuildAsync(db);
        var second = await BuildAsync(db);

        Assert.Equal(first.Single().File.ContentKey, second.Single().File.ContentKey);
    }
}
