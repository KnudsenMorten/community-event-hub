using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// Web tests for the §150 task-allocation REVIEW UI (unit C):
/// <list type="bullet">
/// <item>the NEW <see cref="OrganizerAllocationModel"/> (organizer queue) — organizer-only,
/// run-auto-assign seeds engine PROPOSALS, Add/Remove/Commit/Discard behave like the
/// volunteer page, and Commit notifies exactly once;</item>
/// <item>the EDITED <see cref="BucketAllocationModel"/> (volunteer queue) — the new
/// run-engine action + the per-row <c>Source</c> (Proposed vs Queued) surfaced for the
/// badge + the post-commit notify invocation.</item>
/// </list>
/// Drives the REAL page models + services over an in-memory DB with a FAKE current-participant
/// session and a FAKE <see cref="ICommitNotificationService"/> to count notify calls. The
/// queue stays SILENT (proposals + edits send nothing); only Commit notifies. FAKE names only.
/// </summary>
public sealed class OrganizerAllocationPageTests
{
    private const int EventId = 71;
    private static readonly DateOnly Day1 = new(2027, 9, 17);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"orgalloc-{Guid.NewGuid():N}").Options);

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    /// <summary>Records every commit-notify call so a test can assert "exactly once".</summary>
    private sealed class CountingNotifier : ICommitNotificationService
    {
        public int Calls { get; private set; }
        public ParticipantRole? LastRole { get; private set; }
        public IReadOnlyList<int>? LastIds { get; private set; }

        public Task NotifyCommitAsync(
            VolunteerStructureService.ActorContext actor,
            IReadOnlyList<int> affectedParticipantIds,
            ParticipantRole targetRole,
            CancellationToken ct = default)
        {
            Calls++;
            LastRole = targetRole;
            LastIds = affectedParticipantIds;
            return Task.CompletedTask;
        }
    }

    private static CurrentParticipant? Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return CurrentParticipant.FromPrincipal(principal);
    }

    private static async Task<CommunityHubDbContext> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "OA27", CommunityName = "C", DisplayName = "OA 2027",
            StartDate = Day1, EndDate = Day1, IsActive = true,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static Participant AddPerson(CommunityHubDbContext db, string email, ParticipantRole role)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email,
            Role = role, IsActive = true, Ring = Ring.Broad,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p;
    }

    private static VolunteerTask AddTask(CommunityHubDbContext db, string team, int needed)
    {
        var cat = new VolunteerCategory { EventId = EventId, Name = "Bucket " + team };
        db.VolunteerCategories.Add(cat);
        db.SaveChanges();
        var sub = new VolunteerSubcategory { EventId = EventId, CategoryId = cat.Id, Name = "Sub " + team };
        db.VolunteerSubcategories.Add(sub);
        db.SaveChanges();
        var task = new VolunteerTask
        {
            EventId = EventId, SubcategoryId = sub.Id, Title = "Task " + team,
            ResponsibleTeam = team, ResourcesNeeded = needed, DueDate = Day1,
            Status = VolunteerTaskStatus.Open,
        };
        db.VolunteerTasks.Add(task);
        db.SaveChanges();
        return task;
    }

    private static void AddAvailability(CommunityHubDbContext db, int participantId)
    {
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = participantId, Day = Day1,
            Level = VolunteerAvailabilityLevel.Full,
        });
        db.SaveChanges();
    }

    private static OrganizerAllocationService OrgSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    private static AvailabilityAutoAssignEngine Engine(CommunityHubDbContext db) =>
        new(db, new FixedClock());

    private static OrganizerAllocationModel NewOrgModel(
        CommunityHubDbContext db, Participant me, ICommitNotificationService notify)
        => new(db, new FakeAccessor(Session(me)), OrgSvc(db), Engine(db), notify,
               new HeuristicTaskGuidanceGenerator(), NullLogger<OrganizerAllocationModel>.Instance)
        {
            PageContext = new PageContext(),
        };

    // ===================================================================
    //  ORGANIZER queue (new page)
    // ===================================================================

    [Fact]
    public async Task OnGet_for_a_non_organizer_sets_AccessDenied()
    {
        using var db = await SeedAsync(NewDb());
        var vol = AddPerson(db, "vol@x.test", ParticipantRole.Volunteer);

        var model = NewOrgModel(db, vol, new CountingNotifier());
        await model.OnGetAsync(default);

        Assert.True(model.AccessDenied);
    }

    [Fact]
    public async Task RunAutoAssign_for_a_non_organizer_is_forbidden()
    {
        using var db = await SeedAsync(NewDb());
        var vol = AddPerson(db, "vol@x.test", ParticipantRole.Volunteer);

        var model = NewOrgModel(db, vol, new CountingNotifier());
        var result = await model.OnPostRunAutoAssignAsync(default);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(db.TaskAllocationDrafts);
    }

    [Fact]
    public async Task RunAutoAssign_seeds_engine_proposals_for_organizers()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var helper = AddPerson(db, "helper-org@x.test", ParticipantRole.Organizer);
        AddAvailability(db, helper.Id);
        AddTask(db, "ELDK", needed: 1);   // organizer-routed team

        var model = NewOrgModel(db, org, new CountingNotifier());
        var result = await model.OnPostRunAutoAssignAsync(default);

        Assert.IsType<RedirectToPageResult>(result);
        // The engine PROPOSED into THIS organizer's queue (stage 1) — marked EngineProposed,
        // targeting the Organizer role. The queue stayed SILENT (no real assignment yet).
        var proposals = await db.TaskAllocationDrafts
            .Where(d => d.OwnerParticipantId == org.Id)
            .ToListAsync();
        Assert.NotEmpty(proposals);
        Assert.All(proposals, d => Assert.Equal(DraftSource.EngineProposed, d.Source));
        Assert.All(proposals, d => Assert.Equal(ParticipantRole.Organizer, d.TargetRole));
        Assert.Empty(db.VolunteerTaskAssignments);
    }

    [Fact]
    public async Task AddDraft_then_RemoveDraft_round_trips_the_organizer_queue()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var helper = AddPerson(db, "helper-org@x.test", ParticipantRole.Organizer);
        var task = AddTask(db, "ELDK-MOK", needed: 2);

        var model = NewOrgModel(db, org, new CountingNotifier());
        var add = await model.OnPostAddDraftAsync(task.Id, helper.Id, default);
        Assert.IsType<RedirectToPageResult>(add);
        var draft = Assert.Single(db.TaskAllocationDrafts);
        Assert.Equal(ParticipantRole.Organizer, draft.TargetRole);
        Assert.Equal(helper.Id, draft.ParticipantId);

        var remove = await model.OnPostRemoveDraftAsync(task.Id, helper.Id, default);
        Assert.IsType<RedirectToPageResult>(remove);
        Assert.Empty(db.TaskAllocationDrafts);
    }

    [Fact]
    public async Task Commit_creates_assignments_and_notifies_exactly_once()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var helper = AddPerson(db, "helper-org@x.test", ParticipantRole.Organizer);
        var task = AddTask(db, "ELDK", needed: 1);
        var notifier = new CountingNotifier();

        var model = NewOrgModel(db, org, notifier);
        await model.OnPostAddDraftAsync(task.Id, helper.Id, default);

        var commit = await model.OnPostCommitAsync(default);

        Assert.IsType<RedirectToPageResult>(commit);
        // The draft became a REAL assignment, and the queue cleared.
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == helper.Id));
        Assert.Empty(db.TaskAllocationDrafts);
        // Commit is the ONLY email path — notified exactly once, for the Organizer queue.
        Assert.Equal(1, notifier.Calls);
        Assert.Equal(ParticipantRole.Organizer, notifier.LastRole);
    }

    [Fact]
    public async Task Discard_clears_the_organizer_queue_without_assigning()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var helper = AddPerson(db, "helper-org@x.test", ParticipantRole.Organizer);
        var task = AddTask(db, "ELDK", needed: 1);

        var model = NewOrgModel(db, org, new CountingNotifier());
        await model.OnPostAddDraftAsync(task.Id, helper.Id, default);

        var discard = await model.OnPostDiscardAsync(default);

        Assert.IsType<RedirectToPageResult>(discard);
        Assert.Empty(db.TaskAllocationDrafts);
        Assert.Empty(db.VolunteerTaskAssignments);
    }

    [Fact]
    public async Task Discard_for_a_non_organizer_is_forbidden()
    {
        using var db = await SeedAsync(NewDb());
        var vol = AddPerson(db, "vol@x.test", ParticipantRole.Volunteer);

        var model = NewOrgModel(db, vol, new CountingNotifier());
        // The service enforces organizer-only; the page surfaces that as Forbid.
        var result = await model.OnPostDiscardAsync(default);

        Assert.IsType<ForbidResult>(result);
    }

    // ===================================================================
    //  VOLUNTEER queue (BucketAllocation page) — edits added by unit C
    // ===================================================================

    private static VolunteerAllocationService VolSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    private static BucketAllocationModel NewBucketModel(
        CommunityHubDbContext db, Participant me, ICommitNotificationService notify)
        => new(db, new FakeAccessor(Session(me)), VolSvc(db),
               new VolunteerPlanImportService(db, new HeuristicTaskGuidanceGenerator(), new FixedClock()),
               new VolunteerPlanParser(),
               new HeuristicTaskGuidanceGenerator(),
               Engine(db), notify, NullLogger<BucketAllocationModel>.Instance)
        {
            PageContext = new PageContext(),
        };

    [Fact]
    public async Task Bucket_RunAutoAssign_seeds_volunteer_proposals()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var vol = AddPerson(db, "vol@x.test", ParticipantRole.Volunteer);
        AddAvailability(db, vol.Id);
        AddTask(db, "ELDK-Volunteers", needed: 1);   // volunteer-routed team

        var model = NewBucketModel(db, org, new CountingNotifier());
        var result = await model.OnPostRunAutoAssignAsync(default);

        Assert.IsType<RedirectToPageResult>(result);
        var proposals = await db.TaskAllocationDrafts
            .Where(d => d.OwnerParticipantId == org.Id)
            .ToListAsync();
        Assert.NotEmpty(proposals);
        Assert.All(proposals, d => Assert.Equal(DraftSource.EngineProposed, d.Source));
        Assert.All(proposals, d => Assert.Equal(ParticipantRole.Volunteer, d.TargetRole));
        Assert.Empty(db.VolunteerTaskAssignments);
    }

    [Fact]
    public async Task Bucket_Commit_invokes_the_notifier_exactly_once_for_volunteers()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var vol = AddPerson(db, "vol@x.test", ParticipantRole.Volunteer);
        var task = AddTask(db, "ELDK-Volunteers", needed: 1);
        var notifier = new CountingNotifier();

        var model = NewBucketModel(db, org, notifier);
        await model.OnPostAddDraftAsync(task.Id, vol.Id, default);

        var commit = await model.OnPostCommitAsync(default);

        Assert.IsType<RedirectToPageResult>(commit);
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.ParticipantId == vol.Id));
        Assert.Equal(1, notifier.Calls);
        Assert.Equal(ParticipantRole.Volunteer, notifier.LastRole);
    }

    [Fact]
    public async Task Bucket_OnGet_surfaces_each_drafts_Source_for_the_badge()
    {
        using var db = await SeedAsync(NewDb());
        var org = AddPerson(db, "org@x.test", ParticipantRole.Organizer);
        var v1 = AddPerson(db, "v1@x.test", ParticipantRole.Volunteer);
        var v2 = AddPerson(db, "v2@x.test", ParticipantRole.Volunteer);
        var task = AddTask(db, "ELDK-Volunteers", needed: 2);

        // One engine PROPOSED row + one hand-QUEUED row in the same organizer's queue.
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = EventId, OwnerParticipantId = org.Id, TaskId = task.Id,
            ParticipantId = v1.Id, TargetRole = ParticipantRole.Volunteer, Source = DraftSource.EngineProposed,
        });
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = EventId, OwnerParticipantId = org.Id, TaskId = task.Id,
            ParticipantId = v2.Id, TargetRole = ParticipantRole.Volunteer, Source = DraftSource.Manual,
        });
        await db.SaveChangesAsync();

        var model = NewBucketModel(db, org, new CountingNotifier());
        await model.OnGetAsync(default);

        Assert.Equal(2, model.Draft.Count);
        Assert.Contains(model.Draft, d => d.Source == DraftSource.EngineProposed);
        Assert.Contains(model.Draft, d => d.Source == DraftSource.Manual);
    }
}
