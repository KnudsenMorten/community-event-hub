using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the organizer SyncQueue page (<see cref="SyncQueueModel"/>, §59 delta-
/// approval queue). Drives the real page model over a fake HttpContext + an in-memory DB.
/// Proves: an organizer sees the pending list; approve applies + moves it to decided; reject
/// keeps it; a non-organizer is access-denied. FAKE names only.
/// </summary>
public sealed class SyncQueuePageTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"syncq-{Guid.NewGuid():N}")
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

    private static SyncQueueModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var queue = new SyncDeltaQueueService(db);
        return new SyncQueueModel(accessor, queue)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<Participant> SeedOrganizerAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Id = EventId, Code = "SQ27", CommunityName = "C", DisplayName = "SQ 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var organizer = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(organizer);
        await db.SaveChangesAsync();
        return organizer;
    }

    private static async Task<int> SeedSessionUpdateDeltaAsync(CommunityHubDbContext db)
    {
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var session = new Session
        {
            EventId = EventId, SessionizeId = "sz-1", Title = "Demo Session",
            BackstageSessionId = "bs-1",
            BackstageStartsAt = start, BackstageEndsAt = start.AddHours(1), BackstageRoom = "Room A",
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var queue = new SyncDeltaQueueService(db);
        var delta = await queue.EnqueueSessionUpdateAsync(
            EventId, session.Id, "Demo Session", SessionSyncDirection.ZohoToCeh,
            SyncDeltaQueueService.BuildSessionChanges(
                start, start.AddHours(1), "Room A",
                start.AddHours(2), start.AddHours(3), "Room A"));
        return delta.Id;
    }

    [Fact]
    public async Task OnGet_lists_pending_for_an_organizer()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        await SeedSessionUpdateDeltaAsync(db);

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        var d = Assert.Single(model.Pending);
        Assert.Equal("Demo Session", d.EntityLabel);
    }

    [Fact]
    public async Task Non_organizer_is_access_denied()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        organizer.Role = ParticipantRole.Attendee;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        Assert.Empty(model.Pending);
    }

    [Fact]
    public async Task Approve_applies_and_moves_to_decided()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        var id = await SeedSessionUpdateDeltaAsync(db);

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostApproveAsync(id, default);

        Assert.IsType<RedirectToPageResult>(result);
        var delta = await new SyncDeltaQueueService(db).GetAsync(id);
        Assert.Equal(SyncDeltaStatus.Applied, delta!.Status);
        // The session's stored Backstage start advanced to the approved value.
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 12, 0, 0, TimeSpan.Zero),
            db.Sessions.Single().BackstageStartsAt);
    }

    [Fact]
    public async Task Reject_keeps_the_value()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        var id = await SeedSessionUpdateDeltaAsync(db);

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostRejectAsync(id, "Not real", default);

        Assert.IsType<RedirectToPageResult>(result);
        var delta = await new SyncDeltaQueueService(db).GetAsync(id);
        Assert.Equal(SyncDeltaStatus.Rejected, delta!.Status);
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
            db.Sessions.Single().BackstageStartsAt); // untouched
    }
}
