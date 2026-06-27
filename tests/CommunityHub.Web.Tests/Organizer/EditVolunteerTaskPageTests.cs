using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §151 web tests for the shared task-definition editor (<see cref="EditVolunteerTaskModel"/>).
/// Drives the real page model over a fake HttpContext + an in-memory DB + a real
/// <see cref="VolunteerStructureService"/>. Proves the shared-edit contract: a SECOND
/// organizer can edit a task a DIFFERENT organizer created, and the detailed
/// Description is surfaced (renders) and auto-generated when left blank. FAKE names only.
/// </summary>
public sealed class EditVolunteerTaskPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"edit-task-{Guid.NewGuid():N}").Options);

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

    private static EditVolunteerTaskModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var structure = new VolunteerStructureService(
            db, TimeProvider.System, new HeuristicTaskGuidanceGenerator());
        return new EditVolunteerTaskModel(
            db, accessor, structure, new HeuristicTaskGuidanceGenerator(),
            NullLogger<EditVolunteerTaskModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private sealed record Seed(int EventId, Participant OrgA, Participant OrgB, int TaskId);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 9, 1), EndDate = new DateOnly(2027, 9, 2), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Participant Org(string name, string email) => new()
        {
            EventId = evt.Id, FullName = name, Email = email,
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        var orgA = Org("Olivia Organizer", "olivia@example.test");
        var orgB = Org("Otto Organizer", "otto@example.test");
        db.Participants.AddRange(orgA, orgB);

        var cat = new VolunteerCategory { EventId = evt.Id, Name = "Stage", CreatedAt = DateTimeOffset.UtcNow };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory
        {
            EventId = evt.Id, CategoryId = cat.Id, Name = "General", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        // Organizer A creates the task.
        var task = new VolunteerTask
        {
            EventId = evt.Id, SubcategoryId = sub.Id, Title = "Mount the stage banners",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        return new Seed(evt.Id, orgA, orgB, task.Id);
    }

    [Fact]
    public async Task Second_organizer_can_load_and_edit_a_task_another_organizer_created()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        // Organizer B (NOT the creator) opens the task.
        var http = new DefaultHttpContext { User = Session(seed.OrgB) };
        var model = NewModel(db, http);
        model.TaskId = seed.TaskId;

        var get = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(get);
        Assert.False(model.AccessDenied);
        Assert.NotNull(model.Editing);
        Assert.Equal("Mount the stage banners", model.Input.Title);

        // Organizer B saves an edit with a blank description → auto-generated.
        var saveModel = NewModel(db, http);
        saveModel.Input = new EditVolunteerTaskModel.InputModel
        {
            TaskId = seed.TaskId,
            Title = "Mount the main-stage banners",
            Description = null, // blank → auto-generated from the title
            ResourcesNeeded = 3,
        };
        var save = await saveModel.OnPostSaveAsync(default);

        Assert.IsType<RedirectToPageResult>(save);
        var saved = await db.VolunteerTasks.FirstAsync(t => t.Id == seed.TaskId);
        Assert.Equal("Mount the main-stage banners", saved.Title);
        Assert.Equal(3, saved.ResourcesNeeded);
        Assert.False(string.IsNullOrWhiteSpace(saved.Description)); // auto-filled by org B's edit
    }

    [Fact]
    public async Task Description_is_surfaced_for_rendering_when_the_task_is_opened()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(seed.OrgA) };

        // Save a supplied description first.
        var saveModel = NewModel(db, http);
        saveModel.Input = new EditVolunteerTaskModel.InputModel
        {
            TaskId = seed.TaskId,
            Title = "Mount the stage banners",
            Description = "Hang both banners on the truss before doors open.",
        };
        await saveModel.OnPostSaveAsync(default);

        // Re-open: the description is bound into the input the view renders.
        var getModel = NewModel(db, http);
        getModel.TaskId = seed.TaskId;
        await getModel.OnGetAsync(default);

        Assert.NotNull(getModel.Editing);
        Assert.Equal("Hang both banners on the truss before doors open.", getModel.Input.Description);
    }

    [Fact]
    public async Task Non_organizer_is_access_denied_on_get()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var attendee = new Participant
        {
            EventId = seed.EventId, FullName = "Anna Attendee", Email = "anna@example.test",
            Role = ParticipantRole.Attendee, IsActive = true,
        };
        db.Participants.Add(attendee);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(attendee) };
        var model = NewModel(db, http);
        model.TaskId = seed.TaskId;

        var get = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(get);
        Assert.True(model.AccessDenied);
        Assert.Null(model.Editing);
    }
}
