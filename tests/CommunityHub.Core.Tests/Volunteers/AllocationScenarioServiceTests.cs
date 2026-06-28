using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §129 generic stage → simulate → commit allocation scenarios (<see cref="AllocationScenarioService"/>):
///   * organizer-only writes (a volunteer actor is denied);
///   * build a scenario + stage moves (idempotent), drop-out auto-seeds an UNASSIGN per covered task;
///   * SIMULATE is read-only — coverage gaps, capacity breaches, double-booking conflicts, a
///     before/after delta — and writes NOTHING;
///   * COMMIT is atomic + audited (assignments created/removed, scenario stamped Committed),
///     refuses an EMPTY scenario, and gates an OVER-CAPACITY commit behind an acknowledgement;
///   * DISCARD marks the scenario discarded and never touches the live tables.
/// EF in-memory — synthetic ids + example.test addresses, no real data.
/// </summary>
public sealed class AllocationScenarioServiceTests
{
    private const int EventId = 31;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-28T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"alloc-scn-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static VolunteerStructureService.ActorContext Volunteer() =>
        new(99, "vol@example.test", ParticipantRole.Volunteer, EventId);

    private static AllocationScenarioService NewSvc(CommunityHubDbContext db) => new(db, new FixedClock());

    private static void SeedEvent(CommunityHubDbContext db)
    {
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        db.SaveChanges();
    }

    private static int AddPerson(CommunityHubDbContext db, string name, ParticipantRole role = ParticipantRole.Volunteer)
    {
        var p = new Participant { EventId = EventId, Email = $"{name}@example.test", FullName = name, Role = role, IsActive = true };
        db.Participants.Add(p); db.SaveChanges(); return p.Id;
    }

    private static int AddTask(CommunityHubDbContext db, string title, int needed, DateOnly? due = null)
    {
        var t = new VolunteerTask { EventId = EventId, Title = title, ResourcesNeeded = needed, DueDate = due };
        db.VolunteerTasks.Add(t); db.SaveChanges(); return t.Id;
    }

    private static void Assign(CommunityHubDbContext db, int taskId, int pid)
    {
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment { EventId = EventId, TaskId = taskId, ParticipantId = pid });
        db.SaveChanges();
    }

    // ------------------------------------------------------------ access ----

    [Fact]
    public async Task Writes_are_organizer_only()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.CreateAsync(Volunteer(), AllocationScenarioKind.VolunteerAllocation, "x"));
    }

    [Fact]
    public async Task Hotel_allocation_is_a_later_phase_and_is_refused()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        await Assert.ThrowsAsync<VolunteerValidationException>(
            () => svc.CreateAsync(Organizer(), AllocationScenarioKind.HotelAllocation, "rooms"));
    }

    // ------------------------------------------------------ stage moves ----

    [Fact]
    public async Task Staging_a_move_is_idempotent()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Reg desk", 2);
        var alex = AddPerson(db, "Alex");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Plan");

        await svc.AddTaskMoveAsync(Organizer(), sid, alex, task);
        await svc.AddTaskMoveAsync(Organizer(), sid, alex, task);   // same move again

        Assert.Equal(1, db.AllocationScenarioMoves.Count(m => m.ScenarioId == sid));
    }

    // --------------------------------------------------------- simulate ----

    [Fact]
    public async Task Simulate_is_read_only_and_reports_gap_then_filled()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Reg desk", 2);          // needs 2
        var alex = AddPerson(db, "Alex");
        var sam = AddPerson(db, "Sam");
        Assign(db, task, alex);                           // 1 live → short by 1
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Fill it");
        await svc.AddTaskMoveAsync(Organizer(), sid, sam, task);   // stage +1

        var sim = await svc.SimulateAsync(Organizer(), sid);

        var d = Assert.Single(sim.TaskDeltas);
        Assert.Equal(1, d.LiveAssigned);
        Assert.Equal(1, d.Added);
        Assert.Equal(2, d.Projected);     // now fully covered
        Assert.False(d.IsGap);
        Assert.False(d.IsBreach);
        Assert.False(sim.HasProblems);
        // READ-ONLY: no assignment was created by simulating.
        Assert.Equal(1, db.VolunteerTaskAssignments.Count());
    }

    [Fact]
    public async Task Simulate_flags_over_capacity_breach()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Tiny task", 1);          // needs 1
        var a = AddPerson(db, "A"); var b = AddPerson(db, "B");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Overfill");
        await svc.AddTaskMoveAsync(Organizer(), sid, a, task);
        await svc.AddTaskMoveAsync(Organizer(), sid, b, task);   // 2 vs needed 1

        var sim = await svc.SimulateAsync(Organizer(), sid);
        var d = Assert.Single(sim.Breaches);
        Assert.Equal(1, d.Overage);
        Assert.True(sim.HasProblems);
    }

    [Fact]
    public async Task Simulate_flags_double_booking_conflict_on_same_due_date()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var day = new DateOnly(2027, 2, 9);
        var t1 = AddTask(db, "Hall A", 1, day);
        var t2 = AddTask(db, "Hall B", 1, day);          // same day
        var alex = AddPerson(db, "Alex");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Clash");
        await svc.AddTaskMoveAsync(Organizer(), sid, alex, t1);
        await svc.AddTaskMoveAsync(Organizer(), sid, alex, t2);

        var sim = await svc.SimulateAsync(Organizer(), sid);
        var c = Assert.Single(sim.Conflicts);
        Assert.Equal(alex, c.ParticipantId);
        Assert.Contains("double-booking", c.Detail);
    }

    // ----------------------------------------------------------- commit ----

    [Fact]
    public async Task Commit_applies_moves_atomically_and_stamps_audit()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Reg desk", 2);
        var alex = AddPerson(db, "Alex"); var sam = AddPerson(db, "Sam");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Plan");
        await svc.AddTaskMoveAsync(Organizer(), sid, alex, task);
        await svc.AddTaskMoveAsync(Organizer(), sid, sam, task);

        var outcome = await svc.CommitAsync(Organizer(), sid);

        Assert.Equal(2, outcome.Assigned);
        Assert.Equal(2, db.VolunteerTaskAssignments.Count(a => a.TaskId == task));
        var scn = db.AllocationScenarios.Single(s => s.Id == sid);
        Assert.Equal(AllocationScenarioStatus.Committed, scn.Status);
        Assert.NotNull(scn.CommittedAt);
        Assert.Equal("org@example.test", scn.CommittedByEmail);
    }

    [Fact]
    public async Task Commit_refuses_an_empty_scenario()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Empty");
        await Assert.ThrowsAsync<VolunteerValidationException>(() => svc.CommitAsync(Organizer(), sid));
    }

    [Fact]
    public async Task Commit_gates_over_capacity_until_acknowledged()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Tiny", 1);
        var a = AddPerson(db, "A"); var b = AddPerson(db, "B");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Over");
        await svc.AddTaskMoveAsync(Organizer(), sid, a, task);
        await svc.AddTaskMoveAsync(Organizer(), sid, b, task);

        // First commit refuses (would over-fill).
        await Assert.ThrowsAsync<VolunteerValidationException>(() => svc.CommitAsync(Organizer(), sid));
        Assert.Empty(db.VolunteerTaskAssignments);

        // Acknowledged → commits.
        var outcome = await svc.CommitAsync(Organizer(), sid, acknowledgeOverCapacity: true);
        Assert.Equal(2, outcome.Assigned);
    }

    [Fact]
    public async Task Committed_scenario_cannot_be_edited()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Reg desk", 1);
        var alex = AddPerson(db, "Alex");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Plan");
        await svc.AddTaskMoveAsync(Organizer(), sid, alex, task);
        await svc.CommitAsync(Organizer(), sid);

        await Assert.ThrowsAsync<VolunteerValidationException>(
            () => svc.AddTaskMoveAsync(Organizer(), sid, alex, task));
    }

    // ---------------------------------------------------------- discard ----

    [Fact]
    public async Task Discard_marks_discarded_and_touches_no_live_rows()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var task = AddTask(db, "Reg desk", 1);
        var alex = AddPerson(db, "Alex");
        var sid = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Plan");
        await svc.AddTaskMoveAsync(Organizer(), sid, alex, task);

        await svc.DiscardAsync(Organizer(), sid);

        Assert.Equal(AllocationScenarioStatus.Discarded, db.AllocationScenarios.Single(s => s.Id == sid).Status);
        Assert.Empty(db.VolunteerTaskAssignments);
    }

    // ----------------------------------------------------- drop-out hook ----

    [Fact]
    public async Task DropOut_seeds_a_backfill_scenario_with_an_unassign_per_covered_task()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var t1 = AddTask(db, "Reg desk", 2);
        var t2 = AddTask(db, "Cloakroom", 1);
        var jordan = AddPerson(db, "Jordan");
        Assign(db, t1, jordan);
        Assign(db, t2, jordan);   // Jordan covers two tasks

        var sid = await svc.SeedDropOutBackfillAsync(Organizer(), jordan);

        Assert.NotNull(sid);
        var scn = await svc.GetWithMovesAsync(Organizer(), sid!.Value);
        Assert.Equal(AllocationScenarioKind.DropOutBackfill, scn!.Kind);
        Assert.Equal(jordan, scn.DroppedParticipantId);
        Assert.Equal(2, scn.Moves.Count);
        Assert.All(scn.Moves, m => Assert.Equal(AllocationMoveOp.Unassign, m.Op));
        // Read-only seeding: the live assignments are still there until commit.
        Assert.Equal(2, db.VolunteerTaskAssignments.Count(a => a.ParticipantId == jordan));
    }

    [Fact]
    public async Task DropOut_of_someone_covering_nothing_seeds_no_scenario()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var nobody = AddPerson(db, "Nobody");
        Assert.Null(await svc.SeedDropOutBackfillAsync(Organizer(), nobody));
    }

    [Fact]
    public async Task DropOut_backfill_commit_removes_the_dropped_persons_assignments()
    {
        using var db = NewDb(); SeedEvent(db);
        var svc = NewSvc(db);
        var t1 = AddTask(db, "Reg desk", 2);
        var jordan = AddPerson(db, "Jordan");
        var priya = AddPerson(db, "Priya");
        Assign(db, t1, jordan);
        var sid = (await svc.SeedDropOutBackfillAsync(Organizer(), jordan))!.Value;
        // Organizer stages Priya as the replacement.
        await svc.AddTaskMoveAsync(Organizer(), sid, priya, t1);

        var outcome = await svc.CommitAsync(Organizer(), sid);

        Assert.Equal(1, outcome.Assigned);     // Priya in
        Assert.Equal(1, outcome.Unassigned);   // Jordan out
        Assert.False(db.VolunteerTaskAssignments.Any(a => a.ParticipantId == jordan));
        Assert.True(db.VolunteerTaskAssignments.Any(a => a.ParticipantId == priya && a.TaskId == t1));
    }
}
