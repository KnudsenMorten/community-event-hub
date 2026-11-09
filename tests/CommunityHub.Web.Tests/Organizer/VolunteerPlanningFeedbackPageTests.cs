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
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1380 — Leth's volunteer feedback (2026-11-01/02): an assigned volunteer can be removed again, a task made on
/// Work structure is a plannable volunteer task, supervisors see and remove assignees, and the "worker roster"
/// wording is gone from the pages. FAKE names only.
/// </summary>
public sealed class VolunteerPlanningFeedbackPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vs-1380-{Guid.NewGuid():N}").Options);

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

    private static VolunteerStructureService Structure(CommunityHubDbContext db) =>
        new(db, TimeProvider.System, VolunteerAssignmentGate.For(db), new HeuristicTaskGuidanceGenerator());

    private static VolunteerStructureModel NewModel(CommunityHubDbContext db, Participant who)
    {
        var http = new DefaultHttpContext { User = Session(who) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new VolunteerStructureModel(
            db, accessor, Structure(db),
            new VolunteerTaskBulkOperationService(db),
            new FeatureGateService(db),
            new RingResolver(db),
            new VolunteerPlanningRosterService(db, TimeProvider.System))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<(Participant Org, Participant Vol, VolunteerCategory Cat, VolunteerTask Task)> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event { Code = "C27", CommunityName = "C", DisplayName = "C 2027", IsActive = true };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var org = new Participant { EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test", Role = ParticipantRole.Organizer, IsActive = true };
        var vol = new Participant { EventId = evt.Id, FullName = "Vera Volunteer", Email = "vera@example.test", Role = ParticipantRole.Volunteer, IsActive = true };
        db.Participants.AddRange(org, vol);
        var cat = new VolunteerCategory { EventId = evt.Id, Name = "Registration", CreatedAt = DateTimeOffset.UtcNow };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var task = new VolunteerTask
        {
            EventId = evt.Id, CategoryId = cat.Id, Title = "Badge desk", ResourcesNeeded = 2,
            ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();
        return (org, vol, cat, task);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "src", "CommunityHub", "Pages" }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    [Fact]
    public async Task A_queued_volunteer_can_be_removed_from_the_task()
    {
        using var db = NewDb();
        var (org, vol, _, task) = await SeedAsync(db);

        await NewModel(db, org).OnPostAssignAsync(task.Id, vol.Id, CancellationToken.None);
        Assert.Single(db.TaskAllocationDrafts);

        await NewModel(db, org).OnPostUnassignAsync(task.Id, vol.Id, CancellationToken.None);
        Assert.Empty(db.TaskAllocationDrafts);
    }

    [Fact]
    public async Task A_published_volunteer_can_be_removed_from_the_task()
    {
        using var db = NewDb();
        var (org, vol, _, task) = await SeedAsync(db);
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment { EventId = task.EventId, TaskId = task.Id, ParticipantId = vol.Id });
        await db.SaveChangesAsync();

        await NewModel(db, org).OnPostUnassignAsync(task.Id, vol.Id, CancellationToken.None);
        Assert.Empty(db.VolunteerTaskAssignments);
    }

    [Fact]
    public void Every_assignee_pill_carries_a_remove_button()
    {
        var html = Source("Organizer", "VolunteerStructure.cshtml");
        // Two Unassign forms: one in the published loop, one in the queued loop; the published one confirms.
        Assert.Equal(2, html.Split("asp-page-handler=\"Unassign\"").Length - 1);
        Assert.Contains("data-confirm", html);
        Assert.Contains("have already seen it", html);
    }

    [Fact]
    public async Task A_task_made_on_work_structure_is_a_plannable_volunteer_task()
    {
        using var db = NewDb();
        var (org, _, cat, _) = await SeedAsync(db);
        var day = new DateOnly(2027, 2, 10);

        await NewModel(db, org).OnPostCreateTaskAsync(
            null, null, "Cloakroom", "Take coats", day, "08:00", CancellationToken.None,
            timeEnd: "11:30", resourcesNeeded: 3, target: $"c:{cat.Id}");

        var t = db.VolunteerTasks.Single(x => x.Title == "Cloakroom");
        Assert.Equal(cat.Id, t.CategoryId);
        Assert.Equal(3, t.ResourcesNeeded);
        Assert.Equal(AllocationQueueKind.Volunteer, ResponsibleTeamRouter.Route(t.ResponsibleTeam));
        Assert.Equal(new TimeOnly(8, 0), t.StartTime);     // typed times are synced on create
        Assert.Equal(new TimeOnly(11, 30), t.EndTime);
    }

    [Fact]
    public async Task A_quick_added_task_defaults_to_one_volunteer()
    {
        using var db = NewDb();
        var (org, _, cat, _) = await SeedAsync(db);

        await NewModel(db, org).OnPostCreateTaskAsync(null, cat.Id, "Stack chairs", null, null, null, CancellationToken.None);

        var t = db.VolunteerTasks.Single(x => x.Title == "Stack chairs");
        Assert.Equal(1, t.ResourcesNeeded);
        Assert.Equal(ResponsibleTeamRouter.VolunteerTeam, t.ResponsibleTeam);
    }

    [Fact]
    public void Supervisor_page_lists_assignees_with_a_remove_button()
    {
        var html = Source("Volunteer", "Supervisor.cshtml");
        Assert.Contains("Model.Assignees", html);
        Assert.Contains("asp-page-handler=\"Unassign\"", html);
    }

    [Fact]
    public void The_pages_no_longer_say_worker_roster()
    {
        foreach (var page in new[] { new[] { "Organizer", "VolunteerWorkerRoster.cshtml" }, new[] { "Organizer", "BucketAllocation.cshtml" }, new[] { "Organizer", "Volunteers.cshtml" } })
        {
            var html = Source(page);
            // Comments may cite the old name; rendered text must not.
            var rendered = System.Text.RegularExpressions.Regex.Replace(html, @"@\*.*?\*@|//[^\n]*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.DoesNotContain("worker roster", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Plan for <strong>every</strong>", rendered);
        }
        Assert.Contains("Full-day workers", Source("Organizer", "VolunteerWorkerRoster.cshtml"));
    }
}
