using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §334 — proof that the typed confirmation on irreversible organizer actions is enforced by
/// the SERVER, not by the browser.
///
/// This is the whole point of the change: the previous guard was
/// <c>onsubmit="return confirm(...)"</c>, so posting the handler directly — JavaScript off, a
/// replayed form, a scripted request — executed the destructive action with no confirmation at
/// all. These tests drive the real page handlers with NO phrase and assert that the data is
/// still there afterwards. They are written against a REAL organizer session, so the only
/// thing that can stop the write is the phrase check.
/// </summary>
public sealed class TypedConfirmGuardTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"typed-confirm-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-26T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    /// <summary>A genuine (not acting-as) organizer session — the role gate must pass.</summary>
    private static DefaultHttpContext RealOrganizer() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ClaimTypes.Email, "real.organizer@example.test"),
            new(ClaimTypes.Name, "Real Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
            new("EventId", EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme)),
    };

    private static ICurrentParticipantAccessor Accessor(HttpContext http) =>
        new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));

    private static async Task<Event> SeedEventAsync(CommunityHubDbContext db)
    {
        var e = new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    // -------------------------------------------------------------------------
    //  Hotel delete — un-assigns everyone placed in the hotel.
    // -------------------------------------------------------------------------

    private static HotelsModel NewHotels(CommunityHubDbContext db, HttpContext http)
    {
        var clock = new FixedClock();
        // The collaborators the GUARDED path never reaches are passed as null! on purpose: if
        // the phrase check ever stops running, these tests fail with a NullReferenceException
        // instead of quietly passing.
        return new HotelsModel(
            Accessor(http),
            new CommunityHub.Core.Organizer.HotelManagementService(db, clock),
            new CommunityHub.Core.Organizer.HotelBulkOperationService(db),
            db,
            null!,
            new CommunityHub.Core.Settings.FeatureGateService(db),
            new CommunityHub.Core.Settings.RingResolver(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HotelsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task Hotel_delete_without_the_phrase_changes_nothing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Hotels.Add(new Hotel { EventId = EventId, Name = "Block Hotel", RoomBlockSize = 10 });
        await db.SaveChangesAsync();
        var hotelId = await db.Hotels.Select(h => h.Id).SingleAsync();

        var model = NewHotels(db, RealOrganizer());
        await model.OnPostDeleteAsync(hotelId, confirmPhrase: null, CancellationToken.None);

        Assert.True(await db.Hotels.AnyAsync(h => h.Id == hotelId));   // still there
        Assert.NotNull(model.Error);
        Assert.Contains(TypedConfirmation.DeletePhrase, model.Error);
    }

    [Fact]
    public async Task Hotel_delete_with_the_phrase_goes_through()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Hotels.Add(new Hotel { EventId = EventId, Name = "Block Hotel", RoomBlockSize = 10 });
        await db.SaveChangesAsync();
        var hotelId = await db.Hotels.Select(h => h.Id).SingleAsync();

        var model = NewHotels(db, RealOrganizer());
        await model.OnPostDeleteAsync(hotelId, TypedConfirmation.DeletePhrase, CancellationToken.None);

        Assert.False(await db.Hotels.AnyAsync(h => h.Id == hotelId));
    }

    /// <summary>A wrong phrase is not "close enough" — it is a refusal.</summary>
    [Fact]
    public async Task Hotel_delete_with_the_wrong_phrase_changes_nothing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Hotels.Add(new Hotel { EventId = EventId, Name = "Block Hotel", RoomBlockSize = 10 });
        await db.SaveChangesAsync();
        var hotelId = await db.Hotels.Select(h => h.Id).SingleAsync();

        var model = NewHotels(db, RealOrganizer());
        await model.OnPostDeleteAsync(hotelId, "yes", CancellationToken.None);

        Assert.True(await db.Hotels.AnyAsync(h => h.Id == hotelId));
    }

    // -------------------------------------------------------------------------
    //  Test-data cleanup — a bulk delete/deactivate over every test row.
    // -------------------------------------------------------------------------

    private static TestDataCleanupModel NewCleanup(CommunityHubDbContext db, HttpContext http)
    {
        var clock = new FixedClock();
        return new TestDataCleanupModel(
            Accessor(http),
            new TestDataCleanupService(
                db,
                new ParticipantDeletionService(db, clock,
                    new ParticipantDeactivationService(db, clock, new AuditTrailService(db, clock)))))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task Test_data_cleanup_without_the_phrase_deletes_nothing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "temp@example.test", FullName = "Temp Tester",
            Role = ParticipantRole.Volunteer, IsActive = true, IsTestUser = true,
        });
        await db.SaveChangesAsync();

        var model = NewCleanup(db, RealOrganizer());
        var result = await model.OnPostCleanupAsync(confirmPhrase: null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.Error);
        // The row is untouched — still present AND still active.
        var row = await db.Participants.SingleAsync();
        Assert.True(row.IsActive);
    }

    [Fact]
    public async Task Test_data_cleanup_with_the_phrase_runs()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "temp@example.test", FullName = "Temp Tester",
            Role = ParticipantRole.Volunteer, IsActive = true, IsTestUser = true,
        });
        await db.SaveChangesAsync();

        var model = NewCleanup(db, RealOrganizer());
        await model.OnPostCleanupAsync(TypedConfirmation.ConfirmPhrase, CancellationToken.None);

        Assert.Null(model.Error);
        Assert.NotNull(model.DoneMessage);
    }

    /// <summary>The DELETE phrase must not arm a CONFIRM-gated action, or vice versa.</summary>
    [Fact]
    public async Task The_other_phrase_does_not_arm_the_cleanup()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "temp@example.test", FullName = "Temp Tester",
            Role = ParticipantRole.Volunteer, IsActive = true, IsTestUser = true,
        });
        await db.SaveChangesAsync();

        var model = NewCleanup(db, RealOrganizer());
        await model.OnPostCleanupAsync(TypedConfirmation.DeletePhrase, CancellationToken.None);

        Assert.NotNull(model.Error);
        Assert.True((await db.Participants.SingleAsync()).IsActive);
    }
}
