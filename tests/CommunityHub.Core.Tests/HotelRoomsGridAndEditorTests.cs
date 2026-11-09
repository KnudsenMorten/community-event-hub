using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1386 follow-ups — rooms not people (roommates), the one reservation editor, the guest grid, problems,
/// readiness and the sent-to-hotel marker. In-memory DB, fake names.
/// </summary>
public sealed class HotelRoomsGridAndEditorTests
{
    private const int EventId = 1;
    private static readonly DateOnly N8 = new(2027, 2, 8);
    private static readonly DateOnly N9 = new(2027, 2, 9);
    private static readonly DateOnly N10 = new(2027, 2, 10);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-08T10:00:00Z");
    }

    private sealed record W(CommunityHubDbContext Db, HotelOverviewService Overview, HotelReservationEditor Editor,
        HotelManagementService Hotels, HotelAgreementSheet Sheet, int MainId);

    private static async Task<W> NewAsync()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"h1386b-{Guid.NewGuid():N}").Options);
        var clock = new Clock();
        var log = new HotelChangeLogService(db, clock);
        db.Events.Add(new Event { Id = EventId, Code = "H", CommunityName = "H", DisplayName = "H 2027",
            StartDate = N9, EndDate = N10, IsActive = true });
        var main = new Hotel { EventId = EventId, Name = "Hotel Alpha", Priority = 1 };
        db.Hotels.Add(main);
        await db.SaveChangesAsync();
        db.HotelAllotments.AddRange(
            new HotelAllotment { EventId = EventId, HotelId = main.Id, Night = N8, RoomsAllotted = 5 },
            new HotelAllotment { EventId = EventId, HotelId = main.Id, Night = N9, RoomsAllotted = 5 });
        await db.SaveChangesAsync();
        return new W(db, new HotelOverviewService(db), new HotelReservationEditor(db, clock, log),
            new HotelManagementService(db, clock, log), new HotelAgreementSheet(db, new HotelCoverageService(db, clock)), main.Id);
    }

    private static async Task<Participant> PersonAsync(W w, string name, ParticipantRole role = ParticipantRole.Volunteer,
        bool? needs = true, DateOnly? inD = null, DateOnly? outD = null, int? hotel = null, string? share = null)
    {
        var p = new Participant { EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.').ToLowerInvariant()}@guest.example",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active, HotelId = hotel };
        w.Db.Participants.Add(p);
        await w.Db.SaveChangesAsync();
        if (needs is bool n)
        {
            w.Db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = p.Id, NeedsRoom = n,
                CheckInDate = n ? inD ?? N8 : null, CheckOutDate = n ? outD ?? N10 : null, RoomShareWith = share });
            await w.Db.SaveChangesAsync();
        }
        return p;
    }

    private static HotelReservationInput Input(int? hotel, int? mate = null, int? guests = null, bool needs = true,
        HotelConfirmationState state = HotelConfirmationState.NotConfirmed, DateOnly? inD = null, DateOnly? outD = null) =>
        new(needs, needs ? inD ?? N8 : null, needs ? outD ?? N10 : null, hotel, mate, guests, null, state, null, null);

    // ---- rooms, not people ----------------------------------------------------------------------

    [Fact]
    public void Two_roommates_at_the_same_hotel_are_one_room_and_one_alone_is_one()
    {
        var a = new HotelSleeper(1, 2, 7, false);
        var b = new HotelSleeper(2, null, 7, true);   // the back-link is enough: either side may name the other
        var c = new HotelSleeper(3, null, 7, true);
        Assert.Equal((2, 1), HotelRoomMath.Count(new[] { a, b, c }));     // {1,2} reserved (one not confirmed) + {3} confirmed
        Assert.Equal((1, 0), HotelRoomMath.Count(new[] { a }));           // roommate not there that night: still a room
        Assert.Equal((2, 1), HotelRoomMath.Count(new[] { a, b with { HotelId = 8 } }));   // different hotels: two rooms (b confirmed)
    }

    [Fact]
    public async Task Linking_roommates_in_the_editor_is_symmetric_and_halves_the_rooms_on_the_board_and_sheet()
    {
        var w = await NewAsync();
        var a = await PersonAsync(w, "Ann Able", hotel: w.MainId);
        var b = await PersonAsync(w, "Bo Brave", hotel: w.MainId);

        Assert.Null(await w.Editor.SaveAsync(EventId, a.Id, Input(w.MainId, mate: b.Id), "org@crew.example"));

        Assert.Equal(a.Id, (await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == b.Id)).RoommateParticipantId);
        var ov = await w.Overview.BuildAsync(EventId, includeNotAnswered: false);
        Assert.Equal(1, ov.Nights.Single(n => n.Night == N8).Reserved);   // 2 people, 1 room
        var rows = await w.Sheet.RowsAsync(EventId, w.MainId);
        Assert.Equal(1, rows.Sum(r => r.Rooms));
        Assert.All(rows, r => Assert.Equal(2, r.GuestsInRoom));
        var board = await new HotelAllotmentService(w.Db, new Clock()).BuildAsync(EventId);
        Assert.Equal(1, board.Rows.Single(r => r.HotelId == w.MainId).Cells.Single(c => c.Night == N8).Demand);
    }

    [Fact]
    public async Task Guests_in_room_set_by_an_organizer_goes_on_the_sheet_and_in_the_log()
    {
        var w = await NewAsync();
        var a = await PersonAsync(w, "Ann Able", hotel: w.MainId);

        Assert.Null(await w.Editor.SaveAsync(EventId, a.Id, Input(w.MainId, guests: 2), "org@crew.example"));

        Assert.Equal(2, Assert.Single(await w.Sheet.RowsAsync(EventId, w.MainId)).GuestsInRoom);
        Assert.Contains(await w.Db.HotelChangeLog.ToListAsync(), e => e.Summary.Contains("guests in room") && e.AffectsGuestList);
    }

    [Fact]
    public async Task The_editor_turns_a_placed_guest_who_needs_no_room_into_a_cancellation_and_validates_dates()
    {
        var w = await NewAsync();
        var a = await PersonAsync(w, "Ann Able", hotel: w.MainId);

        Assert.NotNull(await w.Editor.SaveAsync(EventId, a.Id, Input(w.MainId, inD: N10, outD: N8), "org@crew.example"));
        Assert.Null(await w.Editor.SaveAsync(EventId, a.Id, Input(w.MainId, needs: false), "org@crew.example"));

        var b = await w.Db.HotelBookings.SingleAsync();
        Assert.Equal(HotelConfirmationState.Cancelled, b.ConfirmationState);
        Assert.Equal(w.MainId, b.CancelledHotelId);
    }

    [Fact]
    public async Task The_editor_creates_a_reservation_for_someone_who_never_answered()
    {
        var w = await NewAsync();
        var a = await PersonAsync(w, "Ann Able", needs: null);

        Assert.Null(await w.Editor.SaveAsync(EventId, a.Id, Input(w.MainId), "org@crew.example"));

        Assert.True((await w.Db.HotelBookings.SingleAsync()).NeedsRoom);
        Assert.Equal(w.MainId, (await w.Db.Participants.FindAsync(a.Id))!.HotelId);
    }

    // ---- grid -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_grid_lists_every_crew_member_with_a_flag_per_night_and_filters()
    {
        var w = await NewAsync();
        await PersonAsync(w, "Ann Able", hotel: w.MainId);                              // 8→10
        await PersonAsync(w, "Cy Late", inD: N9, outD: N10, hotel: w.MainId);           // 9→10
        await PersonAsync(w, "Ned No", needs: false);
        await PersonAsync(w, "Nat None", ParticipantRole.Speaker, needs: null);
        await PersonAsync(w, "Sue Sponsor", ParticipantRole.Sponsor, needs: null);
        await PersonAsync(w, "Ari Attendee", ParticipantRole.Attendee, needs: null);

        var all = await w.Overview.GuestGridAsync(EventId, new HotelGridFilter());
        Assert.Equal(4, all.Total);                                  // sponsors + attendees excluded
        Assert.Equal(new[] { N8, N9 }, all.NightColumns);
        Assert.Contains(N8, all.Rows.Single(r => r.FullName == "Ann Able").Nights);

        var on8 = await w.Overview.GuestGridAsync(EventId, new HotelGridFilter(Night: N8));
        Assert.Equal("Ann Able", Assert.Single(on8.Rows).FullName);
        var notAnswered = await w.Overview.GuestGridAsync(EventId, new HotelGridFilter(Answer: HotelAnswer.NotAnswered));
        Assert.Equal("Nat None", Assert.Single(notAnswered.Rows).FullName);   // every speaker, entitled or not
        var no = await w.Overview.GuestGridAsync(EventId, new HotelGridFilter(Answer: HotelAnswer.NoRoom));
        Assert.Equal("Ned No", Assert.Single(no.Rows).FullName);
    }

    /// <summary>
    /// Operator 2026-11-08: "you are including sponsor speaker category in the hotel form, they must not be
    /// included" · "use the speaker category to differ". A Sponsor-category speaker has no hotel entitlement
    /// (the sponsor pays) and never gets the form; a Guest speaker IS entitled (individual agreement, up to 6
    /// nights) and stays in. The grid, the not-answered list and readiness all use the one definition.
    /// </summary>
    [Fact]
    public async Task Sponsor_category_speakers_are_left_out_and_guest_speakers_stay_in()
    {
        var w = await NewAsync();
        var sponsorSpk = await PersonAsync(w, "Sam Sponsorspeaker", ParticipantRole.Speaker, needs: null);
        var guestSpk = await PersonAsync(w, "Gil Guest", ParticipantRole.Speaker, needs: null);
        var communitySpk = await PersonAsync(w, "Cam Community", ParticipantRole.Speaker, needs: null);
        w.Db.SpeakerProfiles.AddRange(
            new SpeakerProfile { EventId = EventId, ParticipantId = sponsorSpk.Id, Category = SpeakerCategory.Sponsor },
            new SpeakerProfile { EventId = EventId, ParticipantId = guestSpk.Id, Category = SpeakerCategory.Guest },
            new SpeakerProfile { EventId = EventId, ParticipantId = communitySpk.Id, Category = SpeakerCategory.Community });
        await w.Db.SaveChangesAsync();

        var grid = await w.Overview.GuestGridAsync(EventId, new HotelGridFilter(Answer: HotelAnswer.NotAnswered));
        Assert.Equal(new[] { "Cam Community", "Gil Guest" }, grid.Rows.Select(r => r.FullName).ToArray());
        Assert.Equal(2, grid.Total);

        var ov = await w.Overview.BuildAsync(EventId, includeNotAnswered: true);
        Assert.DoesNotContain(ov.NotAnswered, r => r.FullName == "Sam Sponsorspeaker");
        Assert.Equal(2, ov.NotAnswered.Count);
    }

    // ---- problems + readiness -------------------------------------------------------------------------

    [Fact]
    public async Task Problems_flag_nights_outside_the_contract_share_with_text_and_one_sided_roommates()
    {
        var w = await NewAsync();
        var a = await PersonAsync(w, "Ann Able", outD: new DateOnly(2027, 2, 11), hotel: w.MainId);   // 10th not contracted
        await PersonAsync(w, "Bo Brave", hotel: w.MainId, share: "my wife");
        var c = await PersonAsync(w, "Cy Gone", needs: false);
        var ab = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == a.Id);
        ab.RoommateParticipantId = c.Id;
        await w.Db.SaveChangesAsync();

        var problems = await w.Overview.ProblemsAsync(EventId);

        Assert.Contains(problems, p => p.Name == "Ann Able" && p.Problem.Contains("10 Feb"));
        Assert.Contains(problems, p => p.Name == "Ann Able" && p.Problem.Contains("roommate no longer"));
        Assert.Contains(problems, p => p.Name == "Bo Brave" && p.Problem.Contains("sharing with my wife"));
        var rd = await w.Overview.ReadinessAsync(EventId);
        Assert.False(rd.Ready);
        Assert.True(rd.Problems >= 3);
    }

    [Fact]
    public async Task Ready_when_everyone_answered_is_placed_and_nothing_is_open()
    {
        var w = await NewAsync();
        await PersonAsync(w, "Ann Able", hotel: w.MainId);
        await PersonAsync(w, "Ned No", needs: false);

        Assert.True((await w.Overview.ReadinessAsync(EventId)).Ready);

        await PersonAsync(w, "Nat None", needs: null);
        var rd = await w.Overview.ReadinessAsync(EventId);
        Assert.False(rd.Ready);
        Assert.Equal(1, rd.NotAnswered);
    }

    // ---- sent to hotel ------------------------------------------------------------------------------

    [Fact]
    public async Task Changes_since_sent_count_only_guest_list_changes_after_the_marker()
    {
        var w = await NewAsync();
        var a = await PersonAsync(w, "Ann Able", hotel: w.MainId);
        await w.Editor.SaveAsync(EventId, a.Id, Input(w.MainId, guests: 2), "org@crew.example");
        Assert.Single(await w.Hotels.ChangesSinceSentAsync(EventId, w.MainId));

        await w.Hotels.MarkSentToHotelAsync(EventId, w.MainId, "org@crew.example");
        Assert.Empty(await w.Hotels.ChangesSinceSentAsync(EventId, w.MainId));
    }
}
