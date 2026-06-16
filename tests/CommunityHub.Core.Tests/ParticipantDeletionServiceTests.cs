using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ParticipantDeletionService"/> — the per-row
/// Delete the organizer grid offers (REQUIREMENTS §21). Asserts the safe
/// semantics: soft-delete (deactivate) is the default and idempotent; hard-delete
/// only happens when the row has no engagement and cleans its logistics links
/// first; a row WITH engagement is blocked from hard-delete and the caller is
/// told why. EF Core InMemory + a fixed clock; FAKE names only.
/// </summary>
public sealed class ParticipantDeletionServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"pdel-{Guid.NewGuid():N}")
            .Options);

    private static ParticipantDeletionService Sut(CommunityHubDbContext db) =>
        new(db, new FixedClock(T0));

    private static async Task<Participant> SeedPersonAsync(
        CommunityHubDbContext db, int eventId = EventId,
        string email = "alex@example.test", bool active = true)
    {
        var p = new Participant
        {
            EventId = eventId,
            Email = email,
            FullName = email.Split('@')[0],
            Role = ParticipantRole.Attendee,
            IsActive = active,
            LifecycleState = active
                ? ParticipantLifecycleState.Active
                : ParticipantLifecycleState.Inactive,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // ----- soft-delete (deactivate) -----------------------------------------

    [Fact]
    public async Task Deactivate_flips_active_row_to_inactive()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db);

        var r = await Sut(db).DeactivateAsync(EventId, p.Id);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.Deactivated, r.Status);
        var reloaded = await db.Participants.FindAsync(p.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsActive);
        Assert.Equal(ParticipantLifecycleState.Inactive, reloaded.LifecycleState);
    }

    [Fact]
    public async Task Deactivate_is_idempotent_on_already_inactive_row()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db, active: false);

        var r = await Sut(db).DeactivateAsync(EventId, p.Id);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.AlreadyInactive, r.Status);
    }

    [Fact]
    public async Task Deactivate_never_touches_a_row_in_another_event()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db, eventId: OtherEventId);

        var r = await Sut(db).DeactivateAsync(EventId, p.Id);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.NotFound, r.Status);
        var reloaded = await db.Participants.FindAsync(p.Id);
        Assert.True(reloaded!.IsActive); // untouched
    }

    // ----- hard-delete (safe) -----------------------------------------------

    [Fact]
    public async Task HardDelete_removes_an_unengaged_row_and_cleans_logistics()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db);
        // Pure-logistics dependents that must be cleaned, not block the delete.
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = p.Id, NeedsRoom = true });
        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = p.Id });
        db.LoginPins.Add(new LoginPin { ParticipantId = p.Id, PinHash = "x", ExpiresAt = T0 });
        await db.SaveChangesAsync();

        var r = await Sut(db).HardDeleteAsync(EventId, p.Id);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.HardDeleted, r.Status);
        Assert.Null(await db.Participants.FindAsync(p.Id));
        Assert.False(await db.HotelBookings.AnyAsync(x => x.ParticipantId == p.Id));
        Assert.False(await db.SwagPreferences.AnyAsync(x => x.ParticipantId == p.Id));
        Assert.False(await db.LoginPins.AnyAsync(x => x.ParticipantId == p.Id));
    }

    [Fact]
    public async Task HardDelete_nulls_the_hotel_placement_of_other_rows_untouched()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db);
        var hotel = new Hotel { EventId = EventId, Name = "Test Hotel" };
        db.Hotels.Add(hotel);
        await db.SaveChangesAsync();
        p.HotelId = hotel.Id;
        await db.SaveChangesAsync();

        var r = await Sut(db).HardDeleteAsync(EventId, p.Id);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.HardDeleted, r.Status);
        Assert.NotNull(await db.Hotels.FindAsync(hotel.Id)); // hotel survives
    }

    // ----- hard-delete blocked by engagement --------------------------------

    [Fact]
    public async Task HardDelete_is_blocked_when_the_row_speaks_a_session()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db);
        var session = new Session { EventId = EventId, Title = "A talk" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        var r = await Sut(db).HardDeleteAsync(EventId, p.Id);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.HardDeleteBlocked, r.Status);
        Assert.Contains("session(s) as speaker", r.BlockingDependencies);
        Assert.NotNull(await db.Participants.FindAsync(p.Id)); // left untouched
    }

    [Fact]
    public async Task GetHardDeleteBlockers_reports_empty_for_an_unengaged_row()
    {
        using var db = NewDb();
        var p = await SeedPersonAsync(db);

        var blockers = await Sut(db).GetHardDeleteBlockersAsync(EventId, p.Id);

        Assert.Empty(blockers);
    }

    [Fact]
    public async Task HardDelete_on_missing_row_returns_NotFound()
    {
        using var db = NewDb();

        var r = await Sut(db).HardDeleteAsync(EventId, 999);

        Assert.Equal(ParticipantDeletionService.DeletionStatus.NotFound, r.Status);
    }
}
