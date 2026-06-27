using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §150 step-2 availability auto-assign engine: proposes up to ResourcesNeeded
/// honouring stored VolunteerDayAvailability (Unavailable excluded, Full before
/// Half), skips Tracked-only tasks, never proposes someone already really-assigned
/// or already-drafted, generalizes over Volunteer/Organizer target roles, seeds
/// EngineProposed drafts idempotently, and NEVER sends email (the silent queue).
/// </summary>
public sealed class AvailabilityAutoAssignEngineTests
{
    private const int EventId = 7;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    /// <summary>An IEmailSender that records every send so a test can assert zero.</summary>
    private sealed class CountingEmailSender : IEmailSender
    {
        public int Sends { get; private set; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }

        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }

        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default)
        { Sends++; return Task.CompletedTask; }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"auto-assign-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static void SeedEvent(CommunityHubDbContext db) =>
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });

    private static int AddPerson(CommunityHubDbContext db, string email, ParticipantRole role)
    {
        var p = new Participant { EventId = EventId, Email = email, FullName = email, Role = role, IsActive = true };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static void AddDay(CommunityHubDbContext db, int pid, VolunteerAvailabilityLevel level) =>
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = pid, Day = new DateOnly(2026, 6, 15), Level = level,
        });

    private static AvailabilityAutoAssignEngine NewEngine(CommunityHubDbContext db) =>
        new(db, new FixedClock());

    // -------------------------------------------------------------------------
    //  PURE ComputeProposals
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputeProposals_excludes_unavailable_and_prefers_full_over_half()
    {
        var task = new VolunteerTask { Id = 10, EventId = EventId, Title = "Reg", ResourcesNeeded = 2, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };

        var candidates = new[]
        {
            new AvailabilityCandidate(ParticipantId: 5, Capacity: 0),    // Unavailable/Blocked -> excluded
            new AvailabilityCandidate(ParticipantId: 6, Capacity: 50),   // Half
            new AvailabilityCandidate(ParticipantId: 7, Capacity: 100),  // Full -> preferred first
        };

        var proposals = AvailabilityAutoAssignEngine.ComputeProposals(
            new[] { task }, candidates,
            existingCountByTask: new Dictionary<int, int>(),
            occupied: new HashSet<(int, int)>(),
            targetRole: ParticipantRole.Volunteer);

        // Two needed: Full (7) first, then Half (6); the zero-capacity person (5) is excluded.
        Assert.Equal(new[] { 7, 6 }, proposals.Select(p => p.ParticipantId).ToArray());
        Assert.All(proposals, p => Assert.Equal(ParticipantRole.Volunteer, p.TargetRole));
    }

    [Fact]
    public void ComputeProposals_skips_tracked_only_tasks()
    {
        var tracked = new VolunteerTask { Id = 1, EventId = EventId, Title = "Photo", ResourcesNeeded = 5, ResponsibleTeam = "Photo" };
        var routed = new VolunteerTask { Id = 2, EventId = EventId, Title = "Reg", ResourcesNeeded = 1, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };

        var proposals = AvailabilityAutoAssignEngine.ComputeProposals(
            new[] { tracked, routed },
            new[] { new AvailabilityCandidate(9, 100) },
            new Dictionary<int, int>(), new HashSet<(int, int)>(),
            ParticipantRole.Volunteer);

        var p = Assert.Single(proposals);
        Assert.Equal(2, p.TaskId); // only the routed task got a proposal
    }

    [Fact]
    public void ComputeProposals_honours_existing_assigned_and_drafted_budget()
    {
        var task = new VolunteerTask { Id = 3, EventId = EventId, Title = "Reg", ResourcesNeeded = 3, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };

        // 1 already assigned + 1 already drafted => only 1 of the 3 slots remains.
        var proposals = AvailabilityAutoAssignEngine.ComputeProposals(
            new[] { task },
            new[] { new AvailabilityCandidate(11, 100), new AvailabilityCandidate(12, 100) },
            existingCountByTask: new Dictionary<int, int> { [3] = 2 },
            occupied: new HashSet<(int, int)>(),
            targetRole: ParticipantRole.Volunteer);

        Assert.Single(proposals);
    }

    // -------------------------------------------------------------------------
    //  SeedProposalsAsync (persistence + idempotency + silent queue)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Seeds_engine_proposed_drafts_for_volunteer_queue_honouring_availability()
    {
        using var db = NewDb();
        SeedEvent(db);
        var task = new VolunteerTask { EventId = EventId, Title = "Registration", ResourcesNeeded = 2, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        var full = AddPerson(db, "full@x", ParticipantRole.Volunteer);
        var half = AddPerson(db, "half@x", ParticipantRole.Volunteer);
        var unavail = AddPerson(db, "unavail@x", ParticipantRole.Volunteer);
        AddDay(db, full, VolunteerAvailabilityLevel.Full);
        AddDay(db, half, VolunteerAvailabilityLevel.Half);
        AddDay(db, unavail, VolunteerAvailabilityLevel.Unavailable);
        await db.SaveChangesAsync();

        var sender = new CountingEmailSender();
        var seeded = await NewEngine(db).SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);

        Assert.Equal(2, seeded);
        var drafts = await db.TaskAllocationDrafts.OrderBy(d => d.ParticipantId).ToListAsync();
        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, d => Assert.Equal(DraftSource.EngineProposed, d.Source));
        Assert.All(drafts, d => Assert.Equal(ParticipantRole.Volunteer, d.TargetRole));
        Assert.All(drafts, d => Assert.Equal(OrganizerId, d.OwnerParticipantId));
        // Full + Half proposed; the Unavailable volunteer is never proposed.
        Assert.Contains(drafts, d => d.ParticipantId == full);
        Assert.Contains(drafts, d => d.ParticipantId == half);
        Assert.DoesNotContain(drafts, d => d.ParticipantId == unavail);

        // SILENT queue: nothing sent during seeding.
        Assert.Equal(0, sender.Sends);
    }

    [Fact]
    public async Task Seed_is_idempotent_on_rerun()
    {
        using var db = NewDb();
        SeedEvent(db);
        var task = new VolunteerTask { EventId = EventId, Title = "Reg", ResourcesNeeded = 2, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();
        AddDay(db, AddPerson(db, "a@x", ParticipantRole.Volunteer), VolunteerAvailabilityLevel.Full);
        AddDay(db, AddPerson(db, "b@x", ParticipantRole.Volunteer), VolunteerAvailabilityLevel.Full);
        await db.SaveChangesAsync();

        var engine = NewEngine(db);
        var first = await engine.SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);
        var second = await engine.SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);

        Assert.Equal(2, first);
        Assert.Equal(0, second); // re-run proposes nothing already drafted
        Assert.Equal(2, await db.TaskAllocationDrafts.CountAsync());
    }

    [Fact]
    public async Task Never_proposes_already_assigned_or_already_drafted_people()
    {
        using var db = NewDb();
        SeedEvent(db);
        var task = new VolunteerTask { EventId = EventId, Title = "Reg", ResourcesNeeded = 3, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        var assigned = AddPerson(db, "assigned@x", ParticipantRole.Volunteer);
        var drafted = AddPerson(db, "drafted@x", ParticipantRole.Volunteer);
        var fresh = AddPerson(db, "fresh@x", ParticipantRole.Volunteer);
        foreach (var id in new[] { assigned, drafted, fresh })
            AddDay(db, id, VolunteerAvailabilityLevel.Full);

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment { EventId = EventId, TaskId = task.Id, ParticipantId = assigned });
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft { EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = task.Id, ParticipantId = drafted });
        await db.SaveChangesAsync();

        var seeded = await NewEngine(db).SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);

        // 3 needed - 1 assigned - 1 drafted = 1 remaining slot => only 'fresh' proposed.
        Assert.Equal(1, seeded);
        var newDraft = await db.TaskAllocationDrafts.SingleAsync(d => d.Source == DraftSource.EngineProposed);
        Assert.Equal(fresh, newDraft.ParticipantId);
    }

    [Fact]
    public async Task Generalizes_for_organizer_target_role_and_skips_volunteer_tasks()
    {
        using var db = NewDb();
        SeedEvent(db);
        var orgTask = new VolunteerTask { EventId = EventId, Title = "Org work", ResourcesNeeded = 1, ResponsibleTeam = ResponsibleTeamRouter.OrganizerTeam };
        var volTask = new VolunteerTask { EventId = EventId, Title = "Vol work", ResourcesNeeded = 1, ResponsibleTeam = ResponsibleTeamRouter.VolunteerTeam };
        db.VolunteerTasks.AddRange(orgTask, volTask);
        await db.SaveChangesAsync();

        var organizerCandidate = AddPerson(db, "org-cand@x", ParticipantRole.Organizer);
        var volunteerCandidate = AddPerson(db, "vol-cand@x", ParticipantRole.Volunteer);
        AddDay(db, organizerCandidate, VolunteerAvailabilityLevel.Full);
        AddDay(db, volunteerCandidate, VolunteerAvailabilityLevel.Full);
        await db.SaveChangesAsync();

        var seeded = await NewEngine(db).SeedProposalsAsync(Organizer(), ParticipantRole.Organizer, OrganizerId);

        // Only the ELDK (organizer-routed) task is filled, only by an organizer.
        Assert.Equal(1, seeded);
        var draft = await db.TaskAllocationDrafts.SingleAsync();
        Assert.Equal(orgTask.Id, draft.TaskId);
        Assert.Equal(organizerCandidate, draft.ParticipantId);
        Assert.Equal(ParticipantRole.Organizer, draft.TargetRole);
    }

    [Fact]
    public async Task Tracked_only_tasks_are_never_seeded()
    {
        using var db = NewDb();
        SeedEvent(db);
        db.VolunteerTasks.Add(new VolunteerTask { EventId = EventId, Title = "Photo crew", ResourcesNeeded = 5, ResponsibleTeam = "Photo" });
        await db.SaveChangesAsync();
        AddDay(db, AddPerson(db, "v@x", ParticipantRole.Volunteer), VolunteerAvailabilityLevel.Full);
        await db.SaveChangesAsync();

        var seeded = await NewEngine(db).SeedProposalsAsync(Organizer(), ParticipantRole.Volunteer, OrganizerId);

        Assert.Equal(0, seeded);
        Assert.False(await db.TaskAllocationDrafts.AnyAsync());
    }

    [Fact]
    public async Task Seeding_requires_organizer()
    {
        using var db = NewDb();
        SeedEvent(db);
        await db.SaveChangesAsync();

        var volunteerActor = new VolunteerStructureService.ActorContext(99, "v@x", ParticipantRole.Volunteer, EventId);
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => NewEngine(db).SeedProposalsAsync(volunteerActor, ParticipantRole.Volunteer, OrganizerId));
    }
}
