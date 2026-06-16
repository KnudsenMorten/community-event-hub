using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ModifyOnBehalfService"/>. The defining property:
/// an organizer's on-behalf change writes the SAME HotelBooking / SwagPreference
/// rows the participant's own pages read, so it shows up on that person's view.
/// Also asserts edition-scoping and the late-change action-queue hook.
/// </summary>
public sealed class ModifyOnBehalfServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"modbehalf-{Guid.NewGuid():N}")
            .Options);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ModifyOnBehalfService NewSvc(CommunityHubDbContext db, FakeClock clock)
        => new(db, clock, new OrganizerActionItemService(db, clock));

    private static async Task<Participant> SeedParticipantAsync(
        CommunityHubDbContext db, int eventId, string email)
    {
        // An Event row is needed for the late-change lock-date lookup.
        if (!await db.Events.AnyAsync(e => e.Id == eventId))
        {
            db.Events.Add(new Event
            {
                Id = eventId,
                Code = $"EV{eventId}",
                CommunityName = "Test Community",
                DisplayName = "Test",
                StartDate = new DateOnly(2027, 2, 9),
                LockDate = new DateOnly(2027, 1, 15),
                IsActive = true,
            });
        }
        var p = new Participant
        {
            EventId = eventId,
            Email = email,
            FullName = email.Split('@')[0],
            Role = ParticipantRole.Speaker,
            IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Hotel_change_writes_the_row_the_participant_reads()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        var svc = NewSvc(db, clock);

        var (result, summary) = await svc.SetHotelNeededAsync(EventId, p.Id, needsRoom: true);
        Assert.Equal(ModifyOnBehalfService.ModifyResult.Ok, result);
        Assert.Contains("room needed", summary);

        // The participant's OWN hotel booking row now reflects the change.
        var booking = await db.HotelBookings.SingleAsync(
            h => h.EventId == EventId && h.ParticipantId == p.Id);
        Assert.True(booking.NeedsRoom);
    }

    [Fact]
    public async Task Clearing_hotel_need_wipes_dates_for_a_consistent_user_view()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = p.Id, NeedsRoom = true,
            CheckInDate = new DateOnly(2027, 2, 8), CheckOutDate = new DateOnly(2027, 2, 10),
            RoomShareWith = "someone", CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var svc = NewSvc(db, clock);
        await svc.SetHotelNeededAsync(EventId, p.Id, needsRoom: false);

        var booking = await db.HotelBookings.SingleAsync(h => h.ParticipantId == p.Id);
        Assert.False(booking.NeedsRoom);
        Assert.Null(booking.CheckInDate);
        Assert.Null(booking.CheckOutDate);
        Assert.Null(booking.RoomShareWith);
    }

    [Fact]
    public async Task Swag_polo_change_writes_the_row_the_participant_reads()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        var svc = NewSvc(db, clock);

        var size = SwagOptions.PoloSizes[2]; // "M (men)"
        var (result, summary) = await svc.SetPoloSizeAsync(
            EventId, p.Id, size, SwagOptions.PoloSizes, SwagOptions.NoPoloLabel);

        Assert.Equal(ModifyOnBehalfService.ModifyResult.Ok, result);
        var pref = await db.SwagPreferences.SingleAsync(s => s.ParticipantId == p.Id);
        Assert.True(pref.WantsPolo);
        Assert.Equal(size, pref.PoloSize);
        Assert.Contains(size, summary);
    }

    [Fact]
    public async Task Swag_no_polo_clears_the_polo_want()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        var svc = NewSvc(db, clock);

        await svc.SetPoloSizeAsync(EventId, p.Id, SwagOptions.NoPoloLabel,
            SwagOptions.PoloSizes, SwagOptions.NoPoloLabel);

        var pref = await db.SwagPreferences.SingleAsync(s => s.ParticipantId == p.Id);
        Assert.False(pref.WantsPolo);
        Assert.Null(pref.PoloSize);
    }

    [Fact]
    public async Task Unknown_polo_size_is_rejected_not_persisted()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        var svc = NewSvc(db, clock);

        var (result, _) = await svc.SetPoloSizeAsync(
            EventId, p.Id, "NOT-A-SIZE", SwagOptions.PoloSizes, SwagOptions.NoPoloLabel);
        Assert.Equal(ModifyOnBehalfService.ModifyResult.NotFound, result);
    }

    [Fact]
    public async Task Cross_edition_participant_is_rejected()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var theirs = await SeedParticipantAsync(db, OtherEventId, "theirs@example.test");
        var svc = NewSvc(db, clock);

        var (hotel, _) = await svc.SetHotelNeededAsync(EventId, theirs.Id, true);
        Assert.Equal(ModifyOnBehalfService.ModifyResult.NotFound, hotel);
        Assert.False(await db.HotelBookings.AnyAsync()); // nothing written
    }

    [Fact]
    public async Task Late_change_to_existing_booking_raises_an_action_item()
    {
        using var db = NewDb();
        // "today" is inside the 14-day window before the lock date (2027-01-15).
        var clock = new FakeClock { Now = DateTimeOffset.Parse("2027-01-10T12:00:00Z") };
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = p.Id, NeedsRoom = true,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var svc = NewSvc(db, clock);
        await svc.SetHotelNeededAsync(EventId, p.Id, needsRoom: false);

        var items = await db.OrganizerActionItems
            .Where(a => a.EventId == EventId
                        && a.Type == OrganizerActionItemService.TypeHotelChanged
                        && a.ResolvedAt == null)
            .ToListAsync();
        Assert.Single(items);
        Assert.Contains("on the participant's behalf", items[0].Summary);
    }

    [Fact]
    public async Task First_time_write_does_not_raise_an_action_item()
    {
        using var db = NewDb();
        var clock = new FakeClock { Now = DateTimeOffset.Parse("2027-01-10T12:00:00Z") };
        var p = await SeedParticipantAsync(db, EventId, "a@example.test");
        var svc = NewSvc(db, clock);

        // No existing booking -> first submission -> stays quiet.
        await svc.SetHotelNeededAsync(EventId, p.Id, needsRoom: true);

        Assert.False(await db.OrganizerActionItems.AnyAsync());
    }
}
