using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="OrganizerOverviewService"/> — the read-only
/// cross-role organizer overview (REQUIREMENTS §11). Uses the EF Core InMemory
/// provider so the real DbContext mapping + LINQ run (no SQL), seeded with one
/// rich edition (organizers, session + masterclass speakers, sponsors,
/// volunteers, attendees, tasks, a volunteer work tree, leads, help requests).
/// A second edition's rows are planted to prove every number is event-scoped.
/// </summary>
public sealed class OrganizerOverviewServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    // Fixed "today" so overdue maths is deterministic.
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 15);
    private static readonly DateOnly Past = new(2027, 1, 1);
    private static readonly DateOnly Future = new(2027, 2, 1);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"overview-{Guid.NewGuid():N}")
            .Options);

    private static OrganizerOverviewService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    /// <summary>Seed one fully-populated edition; returns participant ids by handle.</summary>
    private static async Task<Dictionary<string, int>> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "OV27", CommunityName = "Overview Test",
            DisplayName = "Overview Test 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        // A second edition so scoping can be asserted.
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTHER", CommunityName = "Other",
            DisplayName = "Other 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2),
            IsActive = false,
        });

        var ids = new Dictionary<string, int>();
        Participant P(string handle, ParticipantRole role, bool active = true, int ev = EventId)
        {
            var p = new Participant
            {
                EventId = ev, Email = $"{handle}@expertslive.dk",
                FullName = handle, Role = role, IsActive = active,
            };
            db.Participants.Add(p);
            return p;
        }

        var org = P("org", ParticipantRole.Organizer);
        var sp1 = P("sp1", ParticipantRole.Speaker);
        var sp2 = P("sp2", ParticipantRole.Speaker);
        var mc1 = P("mc1", ParticipantRole.Speaker);
        var vol1 = P("vol1", ParticipantRole.Volunteer);
        var vol2 = P("vol2", ParticipantRole.Volunteer);
        var volPending = P("volp", ParticipantRole.Volunteer, active: false); // pending application
        var spon = P("spon", ParticipantRole.Sponsor);
        // Other-edition participant — must never leak into EventId counts.
        P("ghost", ParticipantRole.Speaker, ev: OtherEventId);
        await db.SaveChangesAsync();

        foreach (var (h, p) in new[]
        {
            ("org", org), ("sp1", sp1), ("sp2", sp2), ("mc1", mc1),
            ("vol1", vol1), ("vol2", vol2), ("volp", volPending), ("spon", spon),
        })
        {
            ids[h] = p.Id;
        }

        // --- Tasks ----------------------------------------------------------
        void Task(int? assignee, string title, TaskState state, DateOnly? due,
                  string? sourceKey = null, int ev = EventId)
            => db.Tasks.Add(new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = assignee, Title = title,
                State = state, DueDate = due, SourceKey = sourceKey,
            });

        // Speaker milestone "Submit slides": sp1 done, sp2 overdue, mc1 open(future).
        Task(sp1.Id, "Submit slides", TaskState.Done, Past);
        Task(sp2.Id, "Submit slides", TaskState.Open, Past);     // overdue
        Task(mc1.Id, "Submit slides", TaskState.Open, Future);   // not overdue
        // Speaker milestone "Confirm bio": all done.
        Task(sp1.Id, "Confirm bio", TaskState.Done, Future);
        Task(sp2.Id, "Confirm bio", TaskState.Done, Future);
        // A volunteer's personal task (general category).
        Task(vol1.Id, "Bring lanyards", TaskState.Done, Future);
        // Sponsor tasks (sourceKey "sponsor:*"): 1 of 2 done.
        Task(spon.Id, "Upload logo", TaskState.Done, Future, "sponsor:42:logo");
        Task(null, "Booth layout", TaskState.Open, Past, "sponsor:42:booth"); // overdue + unassigned
        // Other-edition task — scope guard.
        Task(null, "Ghost task", TaskState.Open, Past, ev: OtherEventId);

        // --- Volunteer work tree --------------------------------------------
        var cat = new VolunteerCategory { Id = 10, EventId = EventId, Name = "Registration" };
        var cat2 = new VolunteerCategory { Id = 11, EventId = EventId, Name = "A/V" };
        db.VolunteerCategories.AddRange(cat, cat2);
        var subA = new VolunteerSubcategory { Id = 20, EventId = EventId, CategoryId = 10, Name = "Badge desk" };
        var subB = new VolunteerSubcategory { Id = 21, EventId = EventId, CategoryId = 11, Name = "Sound" };
        db.VolunteerSubcategories.AddRange(subA, subB);
        await db.SaveChangesAsync();

        var vtAssigned = new VolunteerTask { Id = 30, EventId = EventId, SubcategoryId = 20, Title = "Morning badge desk" };
        var vtOpen = new VolunteerTask { Id = 31, EventId = EventId, SubcategoryId = 20, Title = "Afternoon badge desk" };
        var vtAv = new VolunteerTask { Id = 32, EventId = EventId, SubcategoryId = 21, Title = "Mic runner" };       // open
        var vtCancelled = new VolunteerTask { Id = 33, EventId = EventId, SubcategoryId = 21, Title = "Old job", Status = VolunteerTaskStatus.Cancelled };
        // Other-edition volunteer task — scope guard.
        var vtGhost = new VolunteerTask { Id = 34, EventId = OtherEventId, SubcategoryId = 20, Title = "Ghost vol task" };
        db.VolunteerTasks.AddRange(vtAssigned, vtOpen, vtAv, vtCancelled, vtGhost);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = vtAssigned.Id, ParticipantId = vol1.Id,
        });

        // --- Help requests --------------------------------------------------
        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = EventId, TaskId = vtAssigned.Id, CategoryId = cat.Id,
            RequestedByParticipantId = vol1.Id, Message = "Need more lanyards",
            Status = VolunteerHelpStatus.Open,
        });
        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = EventId, TaskId = vtAssigned.Id, CategoryId = cat.Id,
            RequestedByParticipantId = vol1.Id, Message = "Resolved one",
            Status = VolunteerHelpStatus.Resolved,
        });
        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = OtherEventId, TaskId = vtGhost.Id, CategoryId = cat.Id,
            RequestedByParticipantId = vol1.Id, Message = "Ghost help",
            Status = VolunteerHelpStatus.Open,
        });

        // --- Sponsor leads --------------------------------------------------
        void Lead(SponsorLeadStatus status, int ev = EventId)
            => db.SponsorLeads.Add(new SponsorLead
            {
                EventId = ev, SponsorCompanyId = "42",
                ZohoRecordId = Guid.NewGuid().ToString("N"),
                FullName = "Lead Person", Email = "lead@expertslive.dk",
                Status = status, CapturedAt = Now, LastSyncedAt = Now,
            });
        Lead(SponsorLeadStatus.Open);
        Lead(SponsorLeadStatus.Open);
        Lead(SponsorLeadStatus.Processed);
        Lead(SponsorLeadStatus.Open, ev: OtherEventId); // scope guard

        // --- Attendees ------------------------------------------------------
        void Att(string email, bool checkedIn, int ev = EventId)
            => db.Attendees.Add(new Attendee
            {
                EventId = ev, Email = email, FirstName = "A", LastName = email,
                CheckedInAt = checkedIn ? Now : null,
            });
        Att("a1@expertslive.dk", checkedIn: true);
        Att("a2@expertslive.dk", checkedIn: true);
        Att("a3@expertslive.dk", checkedIn: false);
        Att("ghost@expertslive.dk", checkedIn: true, ev: OtherEventId); // scope guard

        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task Participation_counts_roles_and_active_state_scoped_to_event()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var o = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("Overview Test 2027", o.EventDisplayName);
        Assert.Equal(8, o.TotalPeople);           // ghost (other edition) excluded
        Assert.Equal(7, o.ActivePeople);          // volp inactive
        var vol = Assert.Single(o.RolesBreakdown, rc => rc.Role == nameof(ParticipantRole.Volunteer));
        Assert.Equal(3, vol.Total);
        Assert.Equal(2, vol.Active);
        Assert.Equal(1, vol.Inactive);
        // No leakage from the other edition's Speaker.
        var speakers = Assert.Single(o.RolesBreakdown, rc => rc.Role == nameof(ParticipantRole.Speaker));
        Assert.Equal(3, speakers.Total);   // sp1 + sp2 + mc1 (master-class folded into Speaker)
    }

    [Fact]
    public async Task Task_completion_overall_by_role_and_by_category()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var o = await NewSvc(db).BuildAsync(EventId);

        // 8 event tasks; 5 done (slides sp1, bio sp1, bio sp2, lanyards, sponsor logo).
        Assert.Equal(8, o.TaskOverall.Total);
        Assert.Equal(5, o.TaskOverall.Done);
        Assert.Equal(62, o.TaskOverall.Percent);  // round(5/8) = 62.5, banker's rounding -> 62

        // Unassigned bucket exists (the open booth-layout sponsor task).
        var unassigned = Assert.Single(o.TasksByRole, c => c.Label == "Unassigned");
        Assert.Equal(1, unassigned.Total);
        Assert.Equal(0, unassigned.Done);

        // Category split: General (5 tasks) + Sponsor (2 tasks).
        var general = Assert.Single(o.TasksByCategory, c => c.Label == "General");
        var sponsor = Assert.Single(o.TasksByCategory, c => c.Label == "Sponsor");
        Assert.Equal(6, general.Total);            // 6 non-sponsor tasks
        Assert.Equal(2, sponsor.Total);
        Assert.Equal(1, sponsor.Done);
    }

    [Fact]
    public async Task Speaker_milestones_group_by_title_with_done_and_overdue()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var o = await NewSvc(db).BuildAsync(EventId);

        var slides = Assert.Single(o.SpeakerMilestones, m => m.Milestone == "Submit slides");
        Assert.Equal(3, slides.Total);             // sp1 + sp2 + mc1
        Assert.Equal(1, slides.Done);              // only sp1
        Assert.Equal(1, slides.Overdue);           // sp2 past-due + open

        var bio = Assert.Single(o.SpeakerMilestones, m => m.Milestone == "Confirm bio");
        Assert.Equal(2, bio.Total);
        Assert.Equal(2, bio.Done);
        Assert.Equal(0, bio.Overdue);

        // Overdue milestones sort first.
        Assert.Equal("Submit slides", o.SpeakerMilestones.First().Milestone);
    }

    [Fact]
    public async Task Volunteer_coverage_counts_assigned_vs_open_excluding_cancelled()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var o = await NewSvc(db).BuildAsync(EventId);

        // 3 live volunteer tasks (cancelled + other-edition excluded).
        Assert.Equal(3, o.VolunteerTasksTotal);
        Assert.Equal(1, o.VolunteerTasksAssigned);
        Assert.Equal(2, o.VolunteerTasksOpen);

        var reg = Assert.Single(o.VolunteerCoverageByCategory, v => v.Category == "Registration");
        Assert.Equal(1, reg.Assigned);
        Assert.Equal(1, reg.Open);
        Assert.Equal(50, reg.Percent);

        var av = Assert.Single(o.VolunteerCoverageByCategory, v => v.Category == "A/V");
        Assert.Equal(0, av.Assigned);
        Assert.Equal(1, av.Open);                  // cancelled "Old job" not counted
    }

    [Fact]
    public async Task Sponsor_totals_and_attendee_checkin_scoped_to_event()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var o = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(2, o.SponsorTaskTotal);
        Assert.Equal(1, o.SponsorTaskDone);
        Assert.Equal(3, o.SponsorLeadTotal);       // other-edition lead excluded
        Assert.Equal(2, o.SponsorLeadOpen);

        Assert.Equal(3, o.AttendeeTotal);          // ghost attendee excluded
    }

    [Fact]
    public async Task Needs_attention_tiles_compute_overdue_unassigned_help_pending()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var o = await NewSvc(db).BuildAsync(EventId);

        // Overdue tasks: slides(sp2) + booth-layout = 2.
        Assert.Equal(2, o.OverdueTasks);
        // Unassigned volunteer tasks = open coverage = 2.
        Assert.Equal(2, o.UnassignedVolunteerTasks);
        // Open help requests: 1 (resolved + other-edition excluded).
        Assert.Equal(1, o.OpenHelpRequests);
        // Pending volunteer applications: volp.
        Assert.Equal(1, o.PendingVolunteerApplications);
    }

    [Fact]
    public async Task Empty_event_is_a_safe_all_zero_snapshot()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "EMPTY", CommunityName = "Empty",
            DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var o = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("Empty 2027", o.EventDisplayName);
        Assert.Equal(0, o.TotalPeople);
        Assert.Equal(0, o.TaskOverall.Total);
        Assert.Equal(0, o.TaskOverall.Percent);    // no divide-by-zero
        Assert.Empty(o.SpeakerMilestones);
        Assert.Empty(o.VolunteerCoverageByCategory);
        Assert.Equal(0, o.OverdueTasks);
        Assert.Equal(0, o.UnassignedVolunteerTasks);
        Assert.Equal(0, o.OpenHelpRequests);
        Assert.Equal(0, o.AttendeeTotal);
    }
}
