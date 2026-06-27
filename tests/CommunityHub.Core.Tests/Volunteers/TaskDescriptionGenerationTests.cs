using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §151 — the auto-description-from-title feature. Covers the heuristic generator
/// producing a non-empty detailed Description, and
/// <see cref="VolunteerStructureService.UpdateTaskContentAsync"/>: it auto-fills the
/// Description when blank, keeps a supplied Description, and is permitted for ANY
/// organizer (the shared task definition is NOT scoped to whoever created the task).
/// FAKE data only.
/// </summary>
public sealed class TaskDescriptionGenerationTests
{
    private const int EventId = 7;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T09:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"task-desc-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new HeuristicTaskGuidanceGenerator());

    private static VolunteerStructureService.ActorContext Organizer(int id) =>
        new(id, $"org{id}@example.test", ParticipantRole.Organizer, EventId);

    /// <summary>Seed an event + one bucket/subcategory and return the subcategory id.</summary>
    private static async Task<int> SeedTreeAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "C27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
        });
        var cat = new VolunteerCategory { EventId = EventId, Name = "Logistics", CreatedAt = DateTimeOffset.UtcNow };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory
        {
            EventId = EventId, CategoryId = cat.Id, Name = "General", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    [Fact]
    public async Task Heuristic_generator_returns_a_non_empty_description_from_a_title()
    {
        var gen = new HeuristicTaskGuidanceGenerator();

        // A keyword-matching title and a generic one both yield a description.
        var matched = await gen.GenerateAsync("Print badges for volunteers", "BadgeTeam", "BadgeTeam");
        Assert.False(string.IsNullOrWhiteSpace(matched.Description));
        Assert.Contains("Print badges for volunteers", matched.Description);

        var generic = await gen.GenerateAsync("Coordinate the after-party teardown crew");
        Assert.False(string.IsNullOrWhiteSpace(generic.Description));
    }

    [Fact]
    public async Task UpdateTaskContentAsync_auto_fills_description_when_blank()
    {
        using var db = NewDb();
        var subId = await SeedTreeAsync(db);
        var svc = NewSvc(db);

        var task = await svc.CreateTaskAsync(Organizer(1), subId, "Print attendee badges", description: null,
            due: null, shift: null);
        Assert.Null(task.Description); // created with no description

        var ok = await svc.UpdateTaskContentAsync(Organizer(1), task.Id,
            title: "Print attendee badges", description: null, resourcesNeeded: 2);

        Assert.True(ok);
        var reloaded = await db.VolunteerTasks.FirstAsync(t => t.Id == task.Id);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.Description));
        Assert.Equal(2, reloaded.ResourcesNeeded);
    }

    [Fact]
    public async Task UpdateTaskContentAsync_keeps_a_supplied_description()
    {
        using var db = NewDb();
        var subId = await SeedTreeAsync(db);
        var svc = NewSvc(db);

        var task = await svc.CreateTaskAsync(Organizer(1), subId, "Set up registration desk", description: null,
            due: null, shift: null);

        const string supplied = "Lay out the lanes, power the scanners, and brief the greeters.";
        await svc.UpdateTaskContentAsync(Organizer(1), task.Id,
            title: "Set up registration desk", description: supplied);

        var reloaded = await db.VolunteerTasks.FirstAsync(t => t.Id == task.Id);
        Assert.Equal(supplied, reloaded.Description);
    }

    [Fact]
    public async Task UpdateTaskContentAsync_is_permitted_for_any_organizer_not_creator_scoped()
    {
        using var db = NewDb();
        var subId = await SeedTreeAsync(db);
        var svc = NewSvc(db);

        // Organizer 1 creates the task; organizer 2 (a DIFFERENT organizer) edits it.
        var task = await svc.CreateTaskAsync(Organizer(1), subId, "Mount the stage banners", description: null,
            due: null, shift: null);

        var ok = await svc.UpdateTaskContentAsync(Organizer(2), task.Id,
            title: "Mount the main-stage banners", description: "Updated by a second organizer.");

        Assert.True(ok);
        var reloaded = await db.VolunteerTasks.FirstAsync(t => t.Id == task.Id);
        Assert.Equal("Mount the main-stage banners", reloaded.Title);
        Assert.Equal("Updated by a second organizer.", reloaded.Description);
    }

    [Fact]
    public async Task UpdateTaskContentAsync_rejects_a_non_organizer()
    {
        using var db = NewDb();
        var subId = await SeedTreeAsync(db);
        var svc = NewSvc(db);
        var task = await svc.CreateTaskAsync(Organizer(1), subId, "Charge the radios", description: null,
            due: null, shift: null);

        var attendee = new VolunteerStructureService.ActorContext(
            99, "att@example.test", ParticipantRole.Attendee, EventId);

        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(() =>
            svc.UpdateTaskContentAsync(attendee, task.Id, title: "Charge the radios"));
    }
}
