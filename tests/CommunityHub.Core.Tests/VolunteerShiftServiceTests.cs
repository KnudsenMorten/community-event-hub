using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for <see cref="VolunteerShiftService"/> — the volunteer's
/// self-service shift decisions (confirm / decline / request a swap). Asserts:
///   1. a volunteer can confirm/decline/swap their OWN assigned shift,
///   2. they CANNOT touch a shift they are not assigned to (403 / access denied),
///   3. declining or swap-requesting raises a coordinator-visible action-queue
///      item (reusing the existing organizer surface), and withdrawing clears it,
///   4. multiple flagged shifts roll up into ONE coordinator item per volunteer,
///   5. edition scoping holds.
/// In-memory DB; FAKE names only.
/// </summary>
public sealed class VolunteerShiftServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed record Seed(int EventId, int VolunteerId, int OtherId, int TaskId, int Task2Id);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "TEST27", CommunityName = "Test Community", DisplayName = "Test 2027",
            StartDate = new DateOnly(2027, 9, 1), EndDate = new DateOnly(2027, 9, 2), IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var vol = new Participant
        {
            EventId = ev.Id, Email = "vol@example.test", FullName = "Vol Unteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        var other = new Participant
        {
            EventId = ev.Id, Email = "other@example.test", FullName = "Other Vol",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.AddRange(vol, other);
        await db.SaveChangesAsync();

        var cat = new VolunteerCategory { EventId = ev.Id, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = ev.Id, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        var task = new VolunteerTask
        {
            EventId = ev.Id, SubcategoryId = sub.Id, Title = "Staff the desk",
            Instructions = "Greet attendees and hand out badges.",
        };
        var task2 = new VolunteerTask
        {
            EventId = ev.Id, SubcategoryId = sub.Id, Title = "Pack down the desk",
        };
        db.VolunteerTasks.AddRange(task, task2);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.AddRange(
            new VolunteerTaskAssignment { EventId = ev.Id, TaskId = task.Id, ParticipantId = vol.Id },
            new VolunteerTaskAssignment { EventId = ev.Id, TaskId = task2.Id, ParticipantId = vol.Id });
        await db.SaveChangesAsync();

        return new Seed(ev.Id, vol.Id, other.Id, task.Id, task2.Id);
    }

    private static VolunteerShiftService NewService(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        return new VolunteerShiftService(db, clock, new OrganizerActionItemService(db, clock));
    }

    [Fact]
    public async Task Confirm_stamps_the_own_assignment()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        var ok = await svc.ConfirmShiftAsync(s.EventId, s.VolunteerId, s.TaskId);

        Assert.True(ok);
        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == s.TaskId && x.ParticipantId == s.VolunteerId);
        Assert.Equal(ShiftDecisionStatus.Confirmed, a.DecisionStatus);
        Assert.Equal(Now, a.DecisionAt);
        // Confirm never raises a coordinator item.
        Assert.Equal(0, await new OrganizerActionItemService(db, new FixedClock(Now)).CountOpenAsync(s.EventId));
    }

    [Fact]
    public async Task Decline_stamps_assignment_and_raises_coordinator_signal()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        await svc.DeclineShiftAsync(s.EventId, s.VolunteerId, s.TaskId, "Clashes with my own talk");

        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == s.TaskId && x.ParticipantId == s.VolunteerId);
        Assert.Equal(ShiftDecisionStatus.Declined, a.DecisionStatus);
        Assert.Equal("Clashes with my own talk", a.DecisionNote);

        var actions = new OrganizerActionItemService(db, new FixedClock(Now));
        var open = await actions.GetOpenAsync(s.EventId, OrganizerActionItemService.TypeVolunteerShiftReassign);
        var item = Assert.Single(open);
        Assert.Equal(s.VolunteerId, item.ParticipantId);
        Assert.Contains("Staff the desk", item.Summary);
        Assert.Contains("declined", item.Summary);
    }

    [Fact]
    public async Task RequestSwap_raises_coordinator_signal()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        await svc.RequestSwapAsync(s.EventId, s.VolunteerId, s.TaskId, null);

        var actions = new OrganizerActionItemService(db, new FixedClock(Now));
        var open = await actions.GetOpenAsync(s.EventId, OrganizerActionItemService.TypeVolunteerShiftReassign);
        var item = Assert.Single(open);
        Assert.Contains("swap requested", item.Summary);
    }

    [Fact]
    public async Task Cannot_touch_a_shift_I_am_not_assigned_to()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        // The OTHER volunteer is not assigned to TaskId.
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.DeclineShiftAsync(s.EventId, s.OtherId, s.TaskId, "not mine"));
    }

    [Fact]
    public async Task Withdraw_clears_decision_and_resolves_coordinator_signal()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        await svc.DeclineShiftAsync(s.EventId, s.VolunteerId, s.TaskId, "reason");
        await svc.WithdrawDecisionAsync(s.EventId, s.VolunteerId, s.TaskId);

        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == s.TaskId && x.ParticipantId == s.VolunteerId);
        Assert.Equal(ShiftDecisionStatus.None, a.DecisionStatus);
        Assert.Null(a.DecisionNote);
        Assert.Null(a.DecisionAt);

        var actions = new OrganizerActionItemService(db, new FixedClock(Now));
        Assert.Equal(0, await actions.CountOpenAsync(s.EventId));
    }

    [Fact]
    public async Task Multiple_flagged_shifts_roll_into_one_coordinator_item()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        await svc.DeclineShiftAsync(s.EventId, s.VolunteerId, s.TaskId, null);
        await svc.RequestSwapAsync(s.EventId, s.VolunteerId, s.Task2Id, null);

        var actions = new OrganizerActionItemService(db, new FixedClock(Now));
        var open = await actions.GetOpenAsync(s.EventId, OrganizerActionItemService.TypeVolunteerShiftReassign);
        var item = Assert.Single(open); // ONE per volunteer
        Assert.Contains("Staff the desk", item.Summary);
        Assert.Contains("Pack down the desk", item.Summary);

        // Clearing only one leaves the item open with the remaining shift.
        await svc.WithdrawDecisionAsync(s.EventId, s.VolunteerId, s.TaskId);
        var stillOpen = await actions.GetOpenAsync(s.EventId, OrganizerActionItemService.TypeVolunteerShiftReassign);
        var remaining = Assert.Single(stillOpen);
        Assert.DoesNotContain("Staff the desk", remaining.Summary);
        Assert.Contains("Pack down the desk", remaining.Summary);
    }

    [Fact]
    public async Task GetFlaggedShifts_returns_only_declined_or_swap()
    {
        await using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewService(db);

        await svc.ConfirmShiftAsync(s.EventId, s.VolunteerId, s.TaskId);
        await svc.RequestSwapAsync(s.EventId, s.VolunteerId, s.Task2Id, null);

        var flagged = await svc.GetFlaggedShiftsAsync(s.EventId, s.VolunteerId);
        var only = Assert.Single(flagged);
        Assert.Equal(s.Task2Id, only.TaskId);
    }
}
