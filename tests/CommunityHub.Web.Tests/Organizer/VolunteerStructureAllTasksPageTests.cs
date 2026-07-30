using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §199 web tests: the organizer Volunteer-structure page exposes a read-only
/// "All volunteer tasks" review listing EVERY defined task with its full detail.
/// Proves (a) the page model actually loads the defined tasks with their content
/// (so the section renders real data, not an empty shell), and (b) the Razor source
/// renders the detail fields. FAKE names only.
/// </summary>
public sealed class VolunteerStructureAllTasksPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vs-alltasks-{Guid.NewGuid():N}").Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static VolunteerStructureModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var structure = new VolunteerStructureService(db, TimeProvider.System, new HeuristicTaskGuidanceGenerator());
        return new VolunteerStructureModel(
            db, accessor, structure,
            new VolunteerTaskBulkOperationService(db),
            new FeatureGateService(db),
            new RingResolver(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<(int EventId, Participant Org)> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 9, 1), EndDate = new DateOnly(2027, 9, 2), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var org = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(org);

        var cat = new VolunteerCategory { EventId = evt.Id, Name = "Registration", CreatedAt = DateTimeOffset.UtcNow };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = evt.Id, CategoryId = cat.Id, Name = "Badge desk", CreatedAt = DateTimeOffset.UtcNow };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        db.VolunteerTasks.Add(new VolunteerTask
        {
            EventId = evt.Id, SubcategoryId = sub.Id,
            Title = "Staff badge desk",
            Description = "Hand out badges to arriving attendees.",
            Expectations = "Every attendee has a badge before the keynote.",
            ResponsibleTeam = "Front-of-house",
            Shift = "Day 1, 08:00", TimeEnd = "10:00",
            Criticality = VolunteerTaskCriticality.NeedToHave,
            ResourcesNeeded = 2,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (evt.Id, org);
    }

    [Fact]
    public async Task All_tasks_section_loads_every_defined_task_with_detail()
    {
        using var db = NewDb();
        var (_, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var model = NewModel(db, http);

        var get = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(get);
        Assert.False(model.AccessDenied);
        var task = Assert.Single(model.AllTasks);
        Assert.Equal("Staff badge desk", task.Title);
        Assert.Equal("Hand out badges to arriving attendees.", task.Description);
        Assert.Equal("Every attendee has a badge before the keynote.", task.Expectations);
        Assert.Equal("Front-of-house", task.ResponsibleTeam);
        // The owning category/subcategory is loaded so the section can show the path.
        Assert.Equal("Registration", task.Subcategory.Category.Name);
        Assert.Equal("Badge desk", task.Subcategory.Name);
    }

    [Fact]
    public void Razor_source_renders_the_all_tasks_section_with_the_detail_fields()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "CommunityHub", "Pages", "Organizer", "VolunteerStructure.cshtml");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(path);
        var html = File.ReadAllText(path!);

        Assert.Contains("All volunteer tasks", html);
        Assert.Contains("Model.AllTasks", html);
        Assert.Contains("Description", html);
        Assert.Contains("Expectations", html);
        Assert.Contains(".Instructions", html);
    }
}
