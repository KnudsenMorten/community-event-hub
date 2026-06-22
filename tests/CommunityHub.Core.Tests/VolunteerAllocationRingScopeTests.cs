using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §23a category-3 QUEUE: the volunteer-allocation commit is RING-SCOPED.
/// When the feature's released ring is lowered (to test a change), a draft whose TARGET
/// volunteer is OUT of that ring is NOT committed and is LEFT in the queue
/// (committed-but-dormant) — so promoting the ring + re-committing picks it up. With the
/// shipped default (Broad), every target is in scope (no behaviour change — covered by
/// the buckets scenario test).
/// </summary>
public sealed class VolunteerAllocationRingScopeTests
{
    private const int EventId = 11;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-22T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-ringscope-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static int AddVolunteer(CommunityHubDbContext db, string email, Ring ring)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email,
            Role = ParticipantRole.Volunteer, IsActive = true, Ring = ring,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static VolunteerAllocationService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    [Fact]
    public async Task Commit_with_a_lowered_ring_commits_only_in_ring_and_keeps_out_of_ring_drafts()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        var task = new VolunteerTask { EventId = EventId, Title = "Registration", ResourcesNeeded = 5 };
        db.VolunteerTasks.Add(task);

        // The organizer has lowered volunteer-allocation to Ring1 to test a change.
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = VolunteerAllocationService.FeatureKey,
            Enabled = true,
            ReleasedToRingOverride = Ring.Ring1,
        });
        await db.SaveChangesAsync();

        var inRing = AddVolunteer(db, "in-ring@x", Ring.Ring1);   // in scope
        var outRing = AddVolunteer(db, "out-ring@x", Ring.Broad); // above the released ring

        db.TaskAllocationDrafts.Add(new TaskAllocationDraft { EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = task.Id, ParticipantId = inRing });
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft { EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = task.Id, ParticipantId = outRing });
        await db.SaveChangesAsync();

        var result = await NewSvc(db).CommitAsync(Organizer());

        // Only the in-ring volunteer is committed; the out-of-ring one is skipped.
        Assert.Equal(1, result.Committed);
        Assert.Equal(1, result.SkippedOutOfRing);
        Assert.Equal(0, result.SkippedDuplicate);

        // In-ring volunteer now has a REAL assignment; out-of-ring does not.
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == inRing));
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == outRing));

        // The out-of-ring draft REMAINS in the queue (dormant); the in-ring one is consumed.
        var remainingDrafts = await db.TaskAllocationDrafts.Select(d => d.ParticipantId).ToListAsync();
        Assert.Equal(new[] { outRing }, remainingDrafts);
    }

    [Fact]
    public async Task Commit_with_default_broad_ring_commits_everyone()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        var task = new VolunteerTask { EventId = EventId, Title = "Registration", ResourcesNeeded = 5 };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        // No FeatureSetting row => the catalog default (Broad/GA) applies. Both volunteers
        // (whatever their ring) are in scope — current behaviour, no regression.
        var a = AddVolunteer(db, "a@x", Ring.Ring1);
        var b = AddVolunteer(db, "b@x", Ring.Broad);
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft { EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = task.Id, ParticipantId = a });
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft { EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = task.Id, ParticipantId = b });
        await db.SaveChangesAsync();

        var result = await NewSvc(db).CommitAsync(Organizer());

        Assert.Equal(2, result.Committed);
        Assert.Equal(0, result.SkippedOutOfRing);
        Assert.False(await db.TaskAllocationDrafts.AnyAsync());   // queue fully consumed
    }
}
