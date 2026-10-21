using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1297 — the WORKER ROSTER end to end: who the planner draws from, and the interval matching
/// as it behaves against a real <see cref="CommunityHubDbContext"/> rather than the pure core.
/// </summary>
public sealed class VolunteerPlannerRosterScopeTests
{
    private const int EventId = 7;
    private const int OrganizerId = 1;
    private static readonly DateOnly MainDay = new(2027, 2, 10);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-22T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"roster-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static CommunityHubDbContext Seed()
    {
        var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        db.SaveChanges();
        return db;
    }

    private static int AddVolunteer(CommunityHubDbContext db, string email, string slot)
    {
        var p = new Participant { EventId = EventId, Email = email, FullName = email, Role = ParticipantRole.Volunteer, IsActive = true };
        db.Participants.Add(p);
        db.SaveChanges();

        // The stored shape the real form writes: the slot tag in Note, level alongside it.
        var option = VolunteerDayOptions.For(MainDay).Single(o => o.Slot == slot);
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = p.Id, Day = MainDay,
            Level = option.Level, Note = VolunteerDayOptions.ComposeNote(slot, null),
        });
        db.SaveChanges();
        return p.Id;
    }

    private static VolunteerTask AddTask(CommunityHubDbContext db, string title, string start, string end, int needed)
    {
        var t = new VolunteerTask
        {
            EventId = EventId, Title = title, ResourcesNeeded = needed,
            ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam,
            DueDate = MainDay, Shift = start, TimeEnd = end,
        };
        VolunteerTaskWindow.SyncTypedTimes(t);
        db.VolunteerTasks.Add(t);
        db.SaveChanges();
        return t;
    }

    private static AvailabilityAutoAssignEngine Engine(CommunityHubDbContext db) => new(db, new FixedClock());

    // ── Scope ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_EMPTY_roster_falls_back_to_everyone_and_says_so()
    {
        // 🔴 The behaviour that keeps this shippable on the day it lands: nobody has curated the
        // roster yet, and a run that silently proposed nothing would read as a broken planner
        // rather than an empty list.
        using var db = Seed();
        AddTask(db, "Check-in desk", "07:00", "12:00", needed: 1);
        AddVolunteer(db, "a@x", "Full day");

        var r = await Engine(db).SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);

        Assert.True(r.ScopeFellBackToEveryone);
        Assert.Single(r.Proposals);
        Assert.Contains("worker roster is empty", r.Summarize());
    }

    [Fact]
    public async Task A_curated_roster_SCOPES_the_run_to_its_members()
    {
        // Operator 2026-09-22: *"we will only scope the first group."*
        using var db = Seed();
        AddTask(db, "Check-in desk", "07:00", "12:00", needed: 2);
        var worker = AddVolunteer(db, "worker@x", "Full day");
        var attendee = AddVolunteer(db, "attendee@x", "Full day");

        await new VolunteerPlanningRosterService(db, new FixedClock())
            .AddAsync(Organizer(), worker);

        var r = await Engine(db).SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);

        Assert.False(r.ScopeFellBackToEveryone);
        Assert.Equal(worker, Assert.Single(r.Proposals).ParticipantId);
        Assert.DoesNotContain(r.Proposals, p => p.ParticipantId == attendee);

        // ⚠️ Scoping REDUCES supply — stated, because the shortfall it creates is deliberate and
        // the remainder is a hand-placement list rather than a surprise.
        Assert.Equal(1, r.SlotsShort);
    }

    // ── Interval matching, against real stored rows ──────────────────────────────────────

    [Fact]
    public async Task A_MORNING_volunteer_is_not_proposed_for_an_AFTERNOON_task()
    {
        // 🔴 The concrete pre-§1297 defect, reproduced through the storage layer: Note was never
        // selected, so the slot was thrown away and this person scored as a valid candidate.
        using var db = Seed();
        AddTask(db, "Pack down", "13:00", "16:00", needed: 1);
        AddVolunteer(db, "morning@x", "Morning 6:40–12");

        var r = await Engine(db).SeedProposalsAsync(
            Organizer(), ParticipantRole.Volunteer, OrganizerId, PlannerScope.Everyone);

        Assert.Empty(r.Proposals);
        Assert.Equal(ShortfallReason.OutsideStatedHours, Assert.Single(r.Shortfalls).Reason);
    }

    [Fact]
    public async Task A_HAND_MADE_assignment_consumes_that_persons_timeslot()
    {
        // Leth's requirement, through the real tables: the manual row is an ordinary assignment,
        // and the interval model is what makes it block an overlapping proposal.
        using var db = Seed();
        var held = AddTask(db, "Badge desk", "09:00", "12:00", needed: 1);
        var clashing = AddTask(db, "Room host", "11:00", "13:00", needed: 1);
        var only = AddVolunteer(db, "only@x", "Full day");

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = held.Id, ParticipantId = only,
        });
        await db.SaveChangesAsync();

        var r = await Engine(db).SeedProposalsAsync(
            Organizer(), ParticipantRole.Volunteer, OrganizerId, PlannerScope.Everyone);

        Assert.Empty(r.Proposals);
        var s = Assert.Single(r.Shortfalls);
        Assert.Equal(clashing.Id, s.TaskId);
        Assert.Equal(ShortfallReason.EveryoneBusy, s.Reason);
    }

    [Fact]
    public async Task The_same_person_is_not_stacked_onto_simultaneous_tasks()
    {
        using var db = Seed();
        AddTask(db, "Desk A", "09:00", "12:00", needed: 1);
        AddTask(db, "Desk B", "09:00", "12:00", needed: 1);
        AddVolunteer(db, "one@x", "Full day");

        var r = await Engine(db).SeedProposalsAsync(
            Organizer(), ParticipantRole.Volunteer, OrganizerId, PlannerScope.Everyone);

        Assert.Single(r.Proposals);
        Assert.Equal(1, r.SlotsShort);
    }

    // ── The roster service itself ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_roster_page_shows_each_persons_stated_availability_beside_the_toggle()
    {
        // 🔑 The point of the page: adding somebody who answered "Morning only" to a full-day pool
        // is a contradiction, and this is where it should be visible.
        using var db = Seed();
        var pid = AddVolunteer(db, "morning@x", "Morning 6:40–12");
        var svc = new VolunteerPlanningRosterService(db, new FixedClock());

        var rows = await svc.LoadAsync(Organizer());

        var row = Assert.Single(rows);
        Assert.False(row.InRoster);
        var day = Assert.Single(row.Availability);
        Assert.Contains("Morning", day.Label);
        Assert.Contains("06:40–12:00", day.Label);
        Assert.True(day.Working);

        Assert.True(await svc.AddAsync(Organizer(), pid));
        Assert.False(await svc.AddAsync(Organizer(), pid));   // idempotent
        Assert.True((await svc.LoadAsync(Organizer())).Single().InRoster);

        Assert.True(await svc.RemoveAsync(Organizer(), pid));
        Assert.False(await svc.RemoveAsync(Organizer(), pid));
    }

    [Fact]
    public async Task Removing_somebody_from_scope_does_NOT_unassign_them()
    {
        // ⚠️ Removing a person from a list looks like it should drop their shifts. It must not:
        // un-assigning is a decision with a mail attached, and it belongs to the allocation queue.
        using var db = Seed();
        var task = AddTask(db, "Badge desk", "09:00", "12:00", needed: 1);
        var pid = AddVolunteer(db, "v@x", "Full day");
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = task.Id, ParticipantId = pid,
        });
        await db.SaveChangesAsync();

        var svc = new VolunteerPlanningRosterService(db, new FixedClock());
        await svc.AddAsync(Organizer(), pid);
        await svc.RemoveAsync(Organizer(), pid);

        Assert.Single(db.VolunteerTaskAssignments);
    }

    [Fact]
    public async Task Only_an_organizer_may_curate_the_roster()
    {
        using var db = Seed();
        var svc = new VolunteerPlanningRosterService(db, new FixedClock());
        var volunteerActor = new VolunteerStructureService.ActorContext(99, "v@x", ParticipantRole.Volunteer, EventId);

        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(() => svc.LoadAsync(volunteerActor));
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(() => svc.AddAsync(volunteerActor, 5));
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(() => svc.RemoveAsync(volunteerActor, 5));
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(() => svc.AddPreferredAsync(volunteerActor, 1, 5));
    }

    [Fact]
    public async Task Only_a_VOLUNTEER_can_be_put_on_the_worker_roster()
    {
        using var db = Seed();
        var organizer = new Participant
        {
            EventId = EventId, Email = "o@x", FullName = "O", Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(organizer);
        await db.SaveChangesAsync();

        var svc = new VolunteerPlanningRosterService(db, new FixedClock());
        await Assert.ThrowsAsync<VolunteerValidationException>(() => svc.AddAsync(Organizer(), organizer.Id));
    }

    [Fact]
    public async Task A_preferred_volunteer_is_picked_ahead_of_an_equally_available_one()
    {
        using var db = Seed();
        var category = new VolunteerCategory { EventId = EventId, Name = "Check-in" };
        db.VolunteerCategories.Add(category);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = EventId, CategoryId = category.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        var task = AddTask(db, "Badge desk", "09:00", "12:00", needed: 1);
        task.SubcategoryId = sub.Id;
        await db.SaveChangesAsync();

        var first = AddVolunteer(db, "aaa@x", "Full day");    // lower id — wins every other tie-break
        var wanted = AddVolunteer(db, "zzz@x", "Full day");

        var svc = new VolunteerPlanningRosterService(db, new FixedClock());
        await svc.AddPreferredAsync(Organizer(), category.Id, wanted);

        var r = await Engine(db).SeedProposalsAsync(
            Organizer(), ParticipantRole.Volunteer, OrganizerId, PlannerScope.Everyone);

        Assert.Equal(wanted, Assert.Single(r.Proposals).ParticipantId);
        Assert.NotEqual(first, r.Proposals[0].ParticipantId);
    }
}
