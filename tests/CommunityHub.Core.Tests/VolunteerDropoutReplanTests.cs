using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Drop-out re-planning on the volunteer allocation queue: when a volunteer pulls
/// out, their real assignments are freed and the organizer's DRAFT queue is seeded
/// with backfill candidates for each now-short task (available volunteers preferred,
/// no duplicates), to be reviewed in the live simulation and committed. Nothing is
/// re-assigned until commit.
/// </summary>
public sealed class VolunteerDropoutReplanTests
{
    private const int EventId = 7;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-dropout-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static int AddVolunteer(CommunityHubDbContext db, string email)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email,
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    [Fact]
    public async Task Dropout_frees_slots_and_seeds_backfill_drafts_preferring_available()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        var task = new VolunteerTask { EventId = EventId, Title = "Registration", ResourcesNeeded = 2 };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        var dropped = AddVolunteer(db, "dropped@x");
        var kept = AddVolunteer(db, "kept@x");
        var avail = AddVolunteer(db, "available@x");
        var other = AddVolunteer(db, "other@x");

        // Two real assignments fill the task (dropped + kept).
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment { EventId = EventId, TaskId = task.Id, ParticipantId = dropped });
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment { EventId = EventId, TaskId = task.Id, ParticipantId = kept });
        // 'avail' submitted availability => should be preferred as backfill.
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = avail,
            Day = new DateOnly(2026, 6, 15), Level = VolunteerAvailabilityLevel.Full,
        });
        await db.SaveChangesAsync();

        var svc = new VolunteerAllocationService(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));
        var result = await svc.SeedDropoutBackfillAsync(Organizer(), dropped);

        // The leaver's slot is freed; one task affected; one backfill draft seeded.
        Assert.Equal(1, result.FreedAssignments);
        Assert.Equal(1, result.AffectedTasks);
        Assert.Equal(1, result.SeededBackfillDrafts);

        // Dropped volunteer no longer has a real assignment.
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == dropped));
        // Kept volunteer is untouched.
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == kept));

        // The seeded backfill draft is the AVAILABLE volunteer (preferred), owned by
        // the organizer, on the short task — and never the leaver or the on-task kept.
        var draft = await db.TaskAllocationDrafts.SingleAsync();
        Assert.Equal(task.Id, draft.TaskId);
        Assert.Equal(OrganizerId, draft.OwnerParticipantId);
        Assert.Equal(avail, draft.ParticipantId);
    }

    [Fact]
    public async Task Dropout_with_no_assignments_is_a_noop()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        await db.SaveChangesAsync();
        var nobody = AddVolunteer(db, "nobody@x");

        var svc = new VolunteerAllocationService(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));
        var result = await svc.SeedDropoutBackfillAsync(Organizer(), nobody);

        Assert.Equal(0, result.FreedAssignments);
        Assert.Equal(0, result.SeededBackfillDrafts);
        Assert.False(await db.TaskAllocationDrafts.AnyAsync());
    }

    [Fact]
    public async Task Dropout_replan_requires_organizer()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        await db.SaveChangesAsync();

        var svc = new VolunteerAllocationService(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));
        var volunteerActor = new VolunteerStructureService.ActorContext(99, "v@x", ParticipantRole.Volunteer, EventId);

        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.SeedDropoutBackfillAsync(volunteerActor, 5));
    }
}
