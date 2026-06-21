using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the organizer multi-hotel management page
/// (<see cref="HotelsModel"/>, REQUIREMENTS §3 hotels). Drives the real page
/// model over a fake HttpContext. Proves:
///  - a signed-in organizer's edition hotels (incl. the RoomBlockSize column)
///    load on GET without throwing,
///  - a non-organizer role is access-denied (friendly, not the content),
///  - DEFECT GUARD: when the data layer fails (e.g. a schema-lagged DB after a
///    deploy applied code before the RoomBlockSize migration), OnGet does NOT
///    throw an unhandled exception — it returns a Page result with an honest
///    error banner. This is the regression-proof for the iPhone-SE post-deploy
///    validation that caught /Organizer/Hotels returning HTTP 500.
/// FAKE names only.
/// </summary>
public sealed class HotelsPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"hotels-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static HotelsModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new HotelsModel(
            accessor,
            new HotelManagementService(db, TimeProvider.System),
            new HotelBulkOperationService(db));
    }

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "HOT27", CommunityName = "C", DisplayName = "HOT 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var organizer = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(organizer);

        // Two hotels, one with a room block set, one without — exercises the
        // RoomBlockSize column the grid renders.
        db.Hotels.Add(new Hotel
        {
            EventId = evt.Id, Name = "Central Plaza", Address = "1 Main St",
            ContactEmail = "stay@example.test", RoomBlockSize = 25,
        });
        db.Hotels.Add(new Hotel
        {
            EventId = evt.Id, Name = "Riverside Inn", RoomBlockSize = null,
        });
        await db.SaveChangesAsync();

        return organizer;
    }

    [Fact]
    public async Task OnGet_loads_edition_hotels_including_room_block_column()
    {
        using var db = NewDb();
        var organizer = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        Assert.Null(model.Error);
        Assert.Equal(2, model.Hotels.Count);
        Assert.Contains(model.Hotels, h => h.Name == "Central Plaza" && h.RoomBlockSize == 25);
        Assert.Contains(model.Hotels, h => h.Name == "Riverside Inn" && h.RoomBlockSize == null);
    }

    [Fact]
    public async Task Non_organizer_role_is_access_denied()
    {
        using var db = NewDb();
        var organizer = await SeedAsync(db);
        organizer.Role = ParticipantRole.Attendee;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        Assert.Empty(model.Hotels);
    }

    [Fact]
    public async Task OnGet_does_not_throw_500_when_the_data_layer_fails()
    {
        // Regression guard for the iPhone-SE post-deploy validation: a
        // schema-lagged / unavailable DB must degrade to an honest error banner
        // on a 200 page, NOT crash with an unhandled exception (HTTP 500).
        // Simulate a failing data layer by disposing the context before the
        // query runs — the page must catch it, not rethrow.
        var db = NewDb();
        var organizer = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);
        await db.DisposeAsync(); // any subsequent query throws ObjectDisposedException

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);        // a real page, never an unhandled 500
        Assert.False(model.AccessDenied);
        Assert.Empty(model.Hotels);               // no half-built grid
        Assert.False(string.IsNullOrEmpty(model.Error)); // honest banner instead
    }
}
