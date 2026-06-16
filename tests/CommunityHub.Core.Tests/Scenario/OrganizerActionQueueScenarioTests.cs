using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Reporting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an organizer drains the action queue (late-change items raised when
/// a participant edits hotel/dinner/travel after the cut-off) and watches the
/// dashboard counts react. The GUI counterpart (scenario-organizer.spec.ts)
/// signs in as the organizer and reads /Organizer/Dashboard.
///
/// IMPORTANT — dead-functionality finding (see REQUIREMENTS §11):
/// OrganizerActionItem rows are written by <see cref="OrganizerActionItemService"/>
/// and the entity carries a Resolve (ResolvedAt) field, but NOTHING in the web
/// app wires either side: no form handler calls UpsertOpenAsync, no page renders
/// or resolves the queue, and the dashboard does not surface it. So this test
/// exercises the EXISTING service + entity at the DB layer (open -> resolve) to
/// prove that half works, and asserts the dashboard counts that ARE live (overdue
/// tasks, attendee mismatches). The missing UI is tracked as a new ◻ requirement.
/// </summary>
public sealed class OrganizerActionQueueScenarioTests
{
    [Fact]
    public async Task Late_change_item_can_be_opened_then_resolved()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new OrganizerActionItemService(db, ScenarioFixture.Clock);

        // A speaker changes their hotel dates after the cut-off -> an action item.
        await svc.UpsertOpenAsync(
            seed.EventId, OrganizerActionItemService.TypeHotelChanged,
            seed.SpeakerOneId, "Hotel dates changed after cut-off");

        var open = await db.OrganizerActionItems
            .Where(a => a.EventId == seed.EventId && a.ResolvedAt == null)
            .ToListAsync();
        Assert.Single(open);

        // Re-edit refreshes the SAME row (idempotent on event/type/participant).
        await svc.UpsertOpenAsync(
            seed.EventId, OrganizerActionItemService.TypeHotelChanged,
            seed.SpeakerOneId, "Hotel dates changed AGAIN");
        Assert.Equal(1, await db.OrganizerActionItems.CountAsync(a => a.ResolvedAt == null));
        var refreshed = open[0];
        await db.Entry(refreshed).ReloadAsync();
        Assert.Equal("Hotel dates changed AGAIN", refreshed.Summary);
        Assert.NotNull(refreshed.UpdatedAt);

        // Organizer resolves it (the mutation a "Mark resolved" handler WOULD do).
        refreshed.ResolvedAt = ScenarioFixture.Clock.GetUtcNow();
        refreshed.ResolvedNotes = "Confirmed with hotel.";
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.OrganizerActionItems.CountAsync(a => a.ResolvedAt == null));
        Assert.Equal(1, await db.OrganizerActionItems.CountAsync(a => a.ResolvedAt != null));
    }

    [Fact]
    public async Task Dashboard_counts_reflect_live_db_state()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // Add an overdue speaker task (due before the seed clock's "today").
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.SpeakerOneId,
            Title = "Overdue thing",
            DueDate = new DateOnly(2026, 1, 1),    // well in the past
            State = TaskState.Open,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var reporting = new ReportingService(db, ScenarioFixture.Clock);
        var report = await reporting.BuildAsync(seed.EventId);

        // The dashboard's headline numbers come straight from this report.
        Assert.True(report.OverdueTasks >= 1, "overdue task should be counted");
        Assert.Equal(1, report.AttendeeMismatches); // one mismatch attendee seeded
        Assert.True(report.ActiveParticipants > 0);

        // Resolving the underlying state changes the count the dashboard shows.
        var overdue = await db.Tasks.SingleAsync(t => t.Title == "Overdue thing");
        overdue.State = TaskState.Done;
        await db.SaveChangesAsync();
        var after = await reporting.BuildAsync(seed.EventId);
        Assert.Equal(report.OverdueTasks - 1, after.OverdueTasks);
    }
}
