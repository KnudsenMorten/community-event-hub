using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the volunteer unified "My schedule" aggregation (REQUIREMENTS
/// Top-8 #8 / §20 Volunteer "My day"): the builder flattens a volunteer's
/// assigned tasks into ONE time-ordered list with the bucket go-to people, and
/// the per-user .ics feed carries those assigned dated tasks too. FAKE names only.
/// </summary>
public sealed class VolunteerScheduleBuilderTests
{
    private const int EventId = 42;
    private const string UidHost = "hub.example.test";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-sched-{Guid.NewGuid():N}")
            .Options);

    private sealed record Seed(int VolunteerId, int SupervisorId, int CategoryId, int SubId);

    /// <summary>
    /// Seeds an edition with a volunteer, a bucket (category) that has a multi-row
    /// supervisor + an ELDK lead, one subcategory, and N tasks the volunteer is
    /// assigned to (caller supplies the tasks). Returns the key ids.
    /// </summary>
    private static async Task<Seed> SeedAsync(
        CommunityHubDbContext db, IEnumerable<VolunteerTask> tasks, bool assignAll = true)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            VenueName = "Test Venue", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
            CalendarSyncEnabled = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var vol = new Participant
        {
            EventId = ev.Id, Email = "volunteer@example.test", FullName = "Vol Unteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        var sup = new Participant
        {
            EventId = ev.Id, Email = "supervisor@example.test", FullName = "Super Visor",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.AddRange(vol, sup);
        await db.SaveChangesAsync();

        var cat = new VolunteerCategory
        {
            EventId = ev.Id, Name = "Registration", EldkLeadName = "Lead Person",
        };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();

        db.VolunteerBucketSupervisors.Add(new VolunteerBucketSupervisor
        {
            EventId = ev.Id, CategoryId = cat.Id, ParticipantId = sup.Id,
        });

        var sub = new VolunteerSubcategory { EventId = ev.Id, CategoryId = cat.Id, Name = "Badge desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        var taskList = tasks.ToList();
        foreach (var t in taskList)
        {
            t.EventId = ev.Id;
            t.SubcategoryId = sub.Id;
        }
        db.VolunteerTasks.AddRange(taskList);
        await db.SaveChangesAsync();

        if (assignAll)
        {
            foreach (var t in taskList)
            {
                db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
                {
                    EventId = ev.Id, TaskId = t.Id, ParticipantId = vol.Id,
                });
            }
            await db.SaveChangesAsync();
        }

        return new Seed(vol.Id, sup.Id, cat.Id, sub.Id);
    }

    private static VolunteerScheduleBuilder NewBuilder(CommunityHubDbContext db) =>
        new(db, new VolunteerStructureService(db, TimeProvider.System));

    [Fact]
    public async Task Schedule_lists_all_assigned_tasks_with_supervisor_and_eldk_lead()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Open the desk", DueDate = new DateOnly(2026, 9, 1), Shift = "08:00" },
        });

        var schedule = await NewBuilder(db).BuildAsync(EventIdOf(db), seed.VolunteerId);

        var entry = Assert.Single(schedule.Entries);
        Assert.Equal("Open the desk", entry.Title);
        Assert.Equal("Registration", entry.Bucket);
        Assert.Equal("Badge desk", entry.Subcategory);
        Assert.Equal("Super Visor", entry.Supervisors);
        Assert.Equal("Lead Person", entry.EldkLeadName);
        Assert.Equal("08:00", entry.Shift);
    }

    [Fact]
    public async Task Schedule_is_time_ordered_dated_first_then_undated()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Late task", DueDate = new DateOnly(2026, 9, 2) },
            new VolunteerTask { Title = "Undated task" },
            new VolunteerTask { Title = "Early task", DueDate = new DateOnly(2026, 9, 1) },
        });

        var schedule = await NewBuilder(db).BuildAsync(EventIdOf(db), seed.VolunteerId);

        Assert.Equal(new[] { "Early task", "Late task", "Undated task" },
            schedule.Entries.Select(e => e.Title).ToArray());
    }

    [Fact]
    public async Task Schedule_only_includes_the_volunteers_own_assignments()
    {
        using var db = NewDb();
        // Seed two tasks but DO NOT auto-assign; assign only one to the volunteer.
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Mine", DueDate = new DateOnly(2026, 9, 1) },
            new VolunteerTask { Title = "Not mine", DueDate = new DateOnly(2026, 9, 1) },
        }, assignAll: false);

        var mine = await db.VolunteerTasks.FirstAsync(t => t.Title == "Mine");
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventIdOf(db), TaskId = mine.Id, ParticipantId = seed.VolunteerId,
        });
        await db.SaveChangesAsync();

        var schedule = await NewBuilder(db).BuildAsync(EventIdOf(db), seed.VolunteerId);

        var entry = Assert.Single(schedule.Entries);
        Assert.Equal("Mine", entry.Title);
    }

    [Fact]
    public async Task Empty_schedule_when_no_assignments()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, Array.Empty<VolunteerTask>());

        var schedule = await NewBuilder(db).BuildAsync(EventIdOf(db), seed.VolunteerId);

        Assert.True(schedule.IsEmpty);
        Assert.Empty(schedule.Entries);
    }

    // ---- Per-user .ics feed now carries assigned volunteer tasks --------------

    [Fact]
    public async Task Personal_feed_includes_assigned_volunteer_task_as_stable_vevent()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Staff the desk", DueDate = new DateOnly(2026, 9, 1), Shift = "08:00", TimeEnd = "10:00" },
        });

        var feed = await new ParticipantCalendarBuilder(db).BuildFeedAsync(seed.VolunteerId, UidHost);

        Assert.StartsWith("BEGIN:VCALENDAR", feed);
        Assert.Contains("Volunteer: Staff the desk", feed);
        Assert.Contains($"voltask:", feed);              // stable UID prefix
        Assert.Contains("08:00-10:00", feed);            // shift window in description
    }

    [Fact]
    public async Task Personal_feed_excludes_cancelled_volunteer_task()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Cancelled work", DueDate = new DateOnly(2026, 9, 1), Status = VolunteerTaskStatus.Cancelled },
        });

        var feed = await new ParticipantCalendarBuilder(db).BuildFeedAsync(seed.VolunteerId, UidHost);

        Assert.DoesNotContain("Cancelled work", feed);
    }

    [Fact]
    public async Task Single_volunteer_task_ics_matches_feed_uid_and_is_scoped()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Download me", DueDate = new DateOnly(2026, 9, 1) },
        });
        var task = await db.VolunteerTasks.FirstAsync();
        var builder = new ParticipantCalendarBuilder(db);

        var ics = await builder.BuildSingleVolunteerTaskAsync(seed.VolunteerId, task.Id, UidHost);

        Assert.NotNull(ics);
        Assert.Contains($"voltask:{task.Id}@{UidHost}", ics);

        // A different participant (the supervisor isn't assigned to this task) gets null.
        var other = await builder.BuildSingleVolunteerTaskAsync(seed.SupervisorId, task.Id, UidHost);
        Assert.Null(other);
    }

    [Fact]
    public async Task Single_volunteer_task_ics_null_when_no_due_date()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, new[]
        {
            new VolunteerTask { Title = "Undated" },
        });
        var task = await db.VolunteerTasks.FirstAsync();

        var ics = await new ParticipantCalendarBuilder(db)
            .BuildSingleVolunteerTaskAsync(seed.VolunteerId, task.Id, UidHost);

        Assert.Null(ics);
    }

    private static int EventIdOf(CommunityHubDbContext db) =>
        db.Events.AsNoTracking().Select(e => e.Id).First();
}
