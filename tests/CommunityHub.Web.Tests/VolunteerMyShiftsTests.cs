using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Volunteers;
using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the volunteer self-service "My shifts" page
/// (REQUIREMENTS §20 Volunteer "Shift availability + decline/swap + per-task
/// instructions"): the page model declines/confirms the SIGNED-IN volunteer's
/// own assigned shift, reflects it back in the schedule, and forbids touching a
/// shift the volunteer is not assigned to. Drives the real
/// <see cref="MyShiftsModel"/> over a fake HttpContext. FAKE names only.
/// </summary>
public sealed class VolunteerMyShiftsTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-myshifts-{Guid.NewGuid():N}")
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
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static MyShiftsModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        http.Request.Host = new HostString("ceh.example.test");
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var structure = new VolunteerStructureService(db, TimeProvider.System);
        var actions = new OrganizerActionItemService(db, TimeProvider.System);
        return new MyShiftsModel(
            accessor,
            new VolunteerScheduleBuilder(db, structure),
            new VolunteerShiftService(db, TimeProvider.System, actions))
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                http, new NullTempDataProvider()),
        };
    }

    private sealed class NullTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed record Seed(Participant Volunteer, Participant Other, int TaskId);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            VenueName = "Test Venue", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var eventId = ev.Id;

        var vol = new Participant
        {
            EventId = eventId, Email = "vol@example.test", FullName = "Vol Unteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        var other = new Participant
        {
            EventId = eventId, Email = "other@example.test", FullName = "Other Vol",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.AddRange(vol, other);
        await db.SaveChangesAsync();

        var cat = new VolunteerCategory { EventId = eventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = eventId, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        var task = new VolunteerTask
        {
            EventId = eventId, SubcategoryId = sub.Id, Title = "Staff the desk",
            DueDate = new DateOnly(2026, 9, 1), Instructions = "Hand out badges.",
        };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = eventId, TaskId = task.Id, ParticipantId = vol.Id,
        });
        await db.SaveChangesAsync();

        return new Seed(vol, other, task.Id);
    }

    [Fact]
    public async Task OnGet_loads_assigned_shift_with_instructions()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(seed.Volunteer) });

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        var entry = Assert.Single(model.Schedule.Entries);
        Assert.Equal("Staff the desk", entry.Title);
        Assert.Equal("Hand out badges.", entry.Instructions);
        Assert.Equal(ShiftDecisionStatus.None, entry.Decision);
    }

    [Fact]
    public async Task Decline_my_own_shift_persists_and_signals_coordinator()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(seed.Volunteer) });

        var result = await model.OnPostDeclineAsync(seed.TaskId, "Cannot make it", default);

        Assert.IsType<RedirectToPageResult>(result);
        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == seed.TaskId && x.ParticipantId == seed.Volunteer.Id);
        Assert.Equal(ShiftDecisionStatus.Declined, a.DecisionStatus);

        var open = await db.OrganizerActionItems.CountAsync(
            x => x.Type == OrganizerActionItemService.TypeVolunteerShiftReassign && x.ResolvedAt == null);
        Assert.Equal(1, open);
    }

    [Fact]
    public async Task Decline_a_shift_not_mine_is_forbidden()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        // Sign in as the OTHER volunteer (not assigned to the task).
        var model = NewModel(db, new DefaultHttpContext { User = Session(seed.Other) });

        var result = await model.OnPostDeclineAsync(seed.TaskId, "not mine", default);

        Assert.IsType<ForbidResult>(result);
        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == seed.TaskId && x.ParticipantId == seed.Volunteer.Id);
        Assert.Equal(ShiftDecisionStatus.None, a.DecisionStatus);
    }
}
