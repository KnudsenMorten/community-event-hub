using System.Security.Claims;
using System.Text;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the volunteer unified "My schedule" page (REQUIREMENTS Top-8 #8):
/// the per-task .ics download handler returns a calendar file for the signed-in
/// volunteer's own assigned task and 404s for a task they aren't assigned to.
/// Drives the real <see cref="MyScheduleModel"/> over a fake HttpContext.
/// FAKE names only.
/// </summary>
public sealed class VolunteerMyScheduleTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-mysched-{Guid.NewGuid():N}")
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

    private static MyScheduleModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        http.Request.Host = new HostString("ceh.example.test");
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var structure = new VolunteerStructureService(db, TimeProvider.System);
        return new MyScheduleModel(
            db,
            accessor,
            new VolunteerScheduleBuilder(db, structure),
            structure,
            shifts: null!,                     // not reached by the .ics handler
            helpNotify: null!,                 // not reached by the .ics handler
            new CalendarFeedTokenService(db),
            new ParticipantCalendarBuilder(db),
            NullLogger<MyScheduleModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private sealed record Seed(Participant Volunteer, Participant Other, int TaskId);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            VenueName = "Test Venue", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
            CalendarSyncEnabled = true,
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
            DueDate = new DateOnly(2026, 9, 1),
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
    public async Task Own_assigned_task_returns_text_calendar_file()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        var result = await model.OnGetCalendarItemAsync(seed.TaskId, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.StartsWith("text/calendar", file.ContentType);
        var body = Encoding.UTF8.GetString(file.FileContents);
        Assert.StartsWith("BEGIN:VCALENDAR", body);
        Assert.Contains("Volunteer: Staff the desk", body);
    }

    [Fact]
    public async Task Task_not_assigned_to_me_returns_not_found()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        // Sign in as the OTHER volunteer (no assignment to the task).
        var http = new DefaultHttpContext { User = Session(seed.Other) };
        var model = NewModel(db, http);

        var result = await model.OnGetCalendarItemAsync(seed.TaskId, default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task OnGet_loads_schedule_for_the_signed_in_volunteer()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        var entry = Assert.Single(model.Schedule.Entries);
        Assert.Equal("Staff the desk", entry.Title);
        Assert.False(string.IsNullOrEmpty(model.CalendarWebcalUrl));
    }

    // ---------------------------------------------------------------------
    //  Volunteer status dropdown / server guard: Cancelled ("No longer
    //  needed") is a coordinator/supervisor-only state and must never be
    //  selectable by — nor accepted from — a volunteer's own surface.
    // ---------------------------------------------------------------------

    [Fact]
    public void Volunteer_selectable_statuses_exclude_Cancelled()
    {
        // The dropdown in BOTH volunteer views (MySchedule + MyTasks) is built
        // from these lists, so the rendered options can never offer Cancelled.
        Assert.DoesNotContain(VolunteerTaskStatus.Cancelled, MyScheduleModel.VolunteerSelectableStatuses);
        Assert.DoesNotContain(VolunteerTaskStatus.Cancelled, MyTasksModel.VolunteerSelectableStatuses);

        Assert.Equal(
            new[] { VolunteerTaskStatus.Open, VolunteerTaskStatus.InProgress, VolunteerTaskStatus.Done },
            MyScheduleModel.VolunteerSelectableStatuses);
        Assert.Equal(
            new[] { VolunteerTaskStatus.Open, VolunteerTaskStatus.InProgress, VolunteerTaskStatus.Done },
            MyTasksModel.VolunteerSelectableStatuses);
    }

    [Fact]
    public async Task Volunteer_cannot_set_Cancelled_via_post_handler_server_side()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        // A forged POST of Cancelled (e.g. crafted request, not from the dropdown)
        // must be rejected before the task is touched.
        var result = await model.OnPostSetStatusAsync(seed.TaskId, VolunteerTaskStatus.Cancelled, default);

        Assert.IsType<RedirectToPageResult>(result);
        // The task status is unchanged (still the seeded default Open).
        var task = await db.VolunteerTasks.AsNoTracking().FirstAsync(t => t.Id == seed.TaskId);
        Assert.Equal(VolunteerTaskStatus.Open, task.Status);
    }

    [Fact]
    public async Task Volunteer_can_still_set_an_allowed_status_via_post_handler()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        var result = await model.OnPostSetStatusAsync(seed.TaskId, VolunteerTaskStatus.Done, default);

        Assert.IsType<RedirectToPageResult>(result);
        var task = await db.VolunteerTasks.AsNoTracking().FirstAsync(t => t.Id == seed.TaskId);
        Assert.Equal(VolunteerTaskStatus.Done, task.Status);
    }
}
