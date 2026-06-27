using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §150 ORGANIZER allocation queue: <see cref="OrganizerAllocationService"/> mirrors
/// <see cref="VolunteerAllocationService"/> but allocates ORGANIZERS, and the two queues
/// share the one <see cref="TaskAllocationDraft"/> table — kept apart purely by
/// <see cref="TaskAllocationDraft.TargetRole"/>. These assert:
///   * writes are organizer-only (a volunteer actor is denied);
///   * only Organizer-role TARGETS can be queued/committed;
///   * draft → commit creates real <see cref="VolunteerTaskAssignment"/> rows + clears
///     the consumed in-ring drafts + reports the affected people;
///   * the commit is RING-SCOPED via the distinct <c>organizer-allocation</c> feature
///     (out-of-ring targets stay queued — mirror of VolunteerAllocationRingScopeTests);
///   * TargetRole ISOLATION — an organizer draft never shows in the volunteer coverage
///     and a volunteer draft never shows in the organizer coverage (both directions),
///     plus a regression guard that the volunteer side counts only volunteer drafts.
/// </summary>
public sealed class OrganizerAllocationServiceTests
{
    private const int EventId = 21;
    private const int OrganizerId = 1;   // the actor doing the planning

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"org-alloc-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static VolunteerStructureService.ActorContext Volunteer() =>
        new(99, "vol-actor@example.test", ParticipantRole.Volunteer, EventId);

    private static int AddParticipant(CommunityHubDbContext db, string email, ParticipantRole role, Ring ring = Ring.Broad)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email,
            Role = role, IsActive = true, Ring = ring,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static int AddTask(CommunityHubDbContext db, string title, int needed)
    {
        var t = new VolunteerTask { EventId = EventId, Title = title, ResourcesNeeded = needed };
        db.VolunteerTasks.Add(t);
        db.SaveChanges();
        return t.Id;
    }

    private static void SeedEvent(CommunityHubDbContext db) =>
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });

    private static OrganizerAllocationService NewOrgSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    private static VolunteerAllocationService NewVolSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    // =====================================================================
    //  Access control.
    // =====================================================================

    [Fact]
    public async Task Writes_are_organizer_only()
    {
        using var db = NewDb();
        SeedEvent(db);
        var taskId = AddTask(db, "Setup", 2);
        var orgTarget = AddParticipant(db, "target-org@x", ParticipantRole.Organizer);
        await db.SaveChangesAsync();
        var svc = NewOrgSvc(db);

        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.AddDraftAsync(Volunteer(), taskId, orgTarget));
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.CommitAsync(Volunteer()));
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.DiscardAsync(Volunteer()));
    }

    [Fact]
    public async Task Only_organizer_role_targets_can_be_queued()
    {
        using var db = NewDb();
        SeedEvent(db);
        var taskId = AddTask(db, "Setup", 2);
        var volunteerTarget = AddParticipant(db, "a-volunteer@x", ParticipantRole.Volunteer);
        await db.SaveChangesAsync();

        // Queueing a VOLUNTEER through the organizer queue is rejected.
        await Assert.ThrowsAsync<VolunteerValidationException>(
            () => NewOrgSvc(db).AddDraftAsync(Organizer(), taskId, volunteerTarget));
    }

    // =====================================================================
    //  Draft -> commit.
    // =====================================================================

    [Fact]
    public async Task Commit_creates_assignment_clears_in_ring_drafts_and_reports_affected()
    {
        using var db = NewDb();
        SeedEvent(db);
        var taskId = AddTask(db, "Stage build", 3);
        var orgA = AddParticipant(db, "org-a@x", ParticipantRole.Organizer);
        var orgB = AddParticipant(db, "org-b@x", ParticipantRole.Organizer);
        await db.SaveChangesAsync();

        var svc = NewOrgSvc(db);
        Assert.True(await svc.AddDraftAsync(Organizer(), taskId, orgA));
        Assert.True(await svc.AddDraftAsync(Organizer(), taskId, orgB));

        // No FeatureSetting row => catalog default (Broad/GA) => both in scope.
        var result = await svc.CommitAsync(Organizer());

        Assert.Equal(2, result.Committed);
        Assert.Equal(0, result.SkippedOutOfRing);
        Assert.Equal(0, result.SkippedDuplicate);
        Assert.Equal(new[] { orgA, orgB }.OrderBy(x => x),
                     result.AffectedParticipantIds.OrderBy(x => x));

        // Real assignments exist; the organizer draft queue is fully consumed.
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == orgA));
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == orgB));
        Assert.False(await db.TaskAllocationDrafts.AnyAsync(d => d.TargetRole == ParticipantRole.Organizer));
    }

    [Fact]
    public async Task Commit_with_a_lowered_ring_commits_only_in_ring_and_keeps_out_of_ring_drafts()
    {
        using var db = NewDb();
        SeedEvent(db);
        var taskId = AddTask(db, "Registration", 5);

        // The organizer has lowered organizer-allocation to Ring1 to test a change.
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = OrganizerAllocationService.FeatureKey,
            Enabled = true,
            ReleasedToRingOverride = Ring.Ring1,
        });
        var inRing = AddParticipant(db, "in-ring-org@x", ParticipantRole.Organizer, Ring.Ring1);
        var outRing = AddParticipant(db, "out-ring-org@x", ParticipantRole.Organizer, Ring.Broad);
        await db.SaveChangesAsync();

        var svc = NewOrgSvc(db);
        Assert.True(await svc.AddDraftAsync(Organizer(), taskId, inRing));
        Assert.True(await svc.AddDraftAsync(Organizer(), taskId, outRing));

        var result = await svc.CommitAsync(Organizer());

        Assert.Equal(1, result.Committed);
        Assert.Equal(1, result.SkippedOutOfRing);
        Assert.Equal(new[] { inRing }, result.AffectedParticipantIds);

        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == inRing));
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == outRing));

        // The out-of-ring draft REMAINS queued (dormant); the in-ring one is consumed.
        var remaining = await db.TaskAllocationDrafts.Select(d => d.ParticipantId).ToListAsync();
        Assert.Equal(new[] { outRing }, remaining);
    }

    // =====================================================================
    //  TargetRole isolation (both directions) + volunteer-side regression.
    // =====================================================================

    [Fact]
    public async Task Organizer_draft_is_invisible_to_volunteer_coverage_and_vice_versa()
    {
        using var db = NewDb();
        SeedEvent(db);
        var taskId = AddTask(db, "Shared task", 4);
        var orgTarget = AddParticipant(db, "iso-org@x", ParticipantRole.Organizer);
        var volTarget = AddParticipant(db, "iso-vol@x", ParticipantRole.Volunteer);
        await db.SaveChangesAsync();

        // Same owning organizer stages ONE organizer draft and ONE volunteer draft.
        Assert.True(await NewOrgSvc(db).AddDraftAsync(Organizer(), taskId, orgTarget));
        Assert.True(await NewVolSvc(db).AddDraftAsync(Organizer(), taskId, volTarget));

        // Organizer coverage sees ONLY the organizer draft.
        var orgCoverage = await NewOrgSvc(db).LoadCoverageAsync(Organizer());
        Assert.Equal(1, orgCoverage.Single(c => c.TaskId == taskId).DraftCount);

        // Volunteer coverage sees ONLY the volunteer draft (organizer draft is invisible).
        var volCoverage = await NewVolSvc(db).LoadCoverageAsync(Organizer());
        Assert.Equal(1, volCoverage.Single(c => c.TaskId == taskId).DraftCount);

        // Per-task coverage agrees in both directions.
        var orgTask = await NewOrgSvc(db).LoadTaskCoverageAsync(Organizer(), taskId);
        var volTask = await NewVolSvc(db).LoadTaskCoverageAsync(Organizer(), taskId);
        Assert.Equal(1, orgTask!.DraftCount);
        Assert.Equal(1, volTask!.DraftCount);
    }

    [Fact]
    public async Task Volunteer_coverage_counts_only_volunteer_target_drafts_regression()
    {
        using var db = NewDb();
        SeedEvent(db);
        var taskId = AddTask(db, "Coverage", 5);
        var volTarget = AddParticipant(db, "reg-vol@x", ParticipantRole.Volunteer);
        await db.SaveChangesAsync();

        // One legitimate volunteer draft (via the volunteer service)...
        Assert.True(await NewVolSvc(db).AddDraftAsync(Organizer(), taskId, volTarget));

        // ...plus two stray organizer-targeted draft rows for the SAME owner+task that
        // must NOT be counted by the volunteer coverage.
        var orgX = AddParticipant(db, "reg-org-x@x", ParticipantRole.Organizer);
        var orgY = AddParticipant(db, "reg-org-y@x", ParticipantRole.Organizer);
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = taskId,
            ParticipantId = orgX, TargetRole = ParticipantRole.Organizer,
        });
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = taskId,
            ParticipantId = orgY, TargetRole = ParticipantRole.Organizer,
        });
        await db.SaveChangesAsync();

        var coverage = await NewVolSvc(db).LoadCoverageAsync(Organizer());
        Assert.Equal(1, coverage.Single(c => c.TaskId == taskId).DraftCount);

        // And the volunteer commit only consumes the volunteer draft, leaving the two
        // organizer drafts untouched in the shared table.
        var result = await NewVolSvc(db).CommitAsync(Organizer());
        Assert.Equal(1, result.Committed);
        Assert.Equal(2, await db.TaskAllocationDrafts
            .CountAsync(d => d.TargetRole == ParticipantRole.Organizer));
    }
}
