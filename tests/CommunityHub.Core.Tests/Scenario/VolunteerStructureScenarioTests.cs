using CommunityHub.Core.Email;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Actor = CommunityHub.Core.Domain.VolunteerStructureService.ActorContext;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the volunteer work structure — a 3-level hierarchy
/// (Category → Subcategory → Task) where volunteers link to the lowest level,
/// each category is owned by an organizer LEAD plus a VOLUNTEER SUPERVISOR
/// appointed from the pool, and a volunteer on a task can ask their category's
/// supervisor for help.
///
/// These tests drive the real <see cref="VolunteerStructureService"/> (the same
/// server-side authority the pages call), so they prove the permission model and
/// rollups end-to-end against the EF model, not just the page glue.
/// </summary>
public sealed class VolunteerStructureScenarioTests
{
    private static VolunteerStructureService NewService(CommunityHub.Core.Data.CommunityHubDbContext db)
        => new(db, ScenarioFixture.Clock);

    private static Actor Organizer(ScenarioSeed.SeedResult s)
        => new(s.OrganizerId, ScenarioSeed.OrganizerEmail, ParticipantRole.Organizer, s.EventId);

    private static Actor Volunteer(ScenarioSeed.SeedResult s, int id, string email)
        => new(id, email, ParticipantRole.Volunteer, s.EventId);

    /// <summary>Add a second volunteer to the seed so we can test the pool /
    /// supervisor-vs-plain-volunteer distinction.</summary>
    private static async Task<int> AddVolunteerAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, ScenarioSeed.SeedResult s, string email)
    {
        var p = new Participant
        {
            EventId = s.EventId, Email = email, FullName = "Pool Volunteer",
            Role = ParticipantRole.Volunteer, IsActive = true, IsTestUser = true,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    // =====================================================================
    //  3-level CRUD + rollup
    // =====================================================================

    [Fact]
    public async Task Organizer_builds_three_level_tree_that_rolls_up()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Registration", "Front-of-house");
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Badge desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Staff badge desk", null,
            new DateOnly(2027, 2, 4), "Day 1, 08:00-10:00");

        // Each level carries the edition + parent, and rolls up Task -> Sub -> Cat.
        Assert.Equal(seed.EventId, cat.EventId);
        Assert.Equal(cat.Id, sub.CategoryId);
        Assert.Equal(sub.Id, task.SubcategoryId);

        var tree = await svc.LoadTreeAsync(seed.EventId);
        var loadedCat = Assert.Single(tree);
        var loadedSub = Assert.Single(loadedCat.Subcategories);
        var loadedTask = Assert.Single(loadedSub.Tasks);
        Assert.Equal("Registration", loadedCat.Name);
        Assert.Equal("Badge desk", loadedSub.Name);
        Assert.Equal("Staff badge desk", loadedTask.Title);
        Assert.Equal("Day 1, 08:00-10:00", loadedTask.Shift);
    }

    [Fact]
    public async Task Deleting_a_category_cascades_its_subcategories_and_tasks()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Teardown", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Pack-down", null);
        await svc.CreateTaskAsync(org, sub.Id, "Stack chairs", null, null, null);

        Assert.True(await svc.DeleteCategoryAsync(org, cat.Id));

        Assert.Equal(0, await db.VolunteerCategories.CountAsync());
        Assert.Equal(0, await db.VolunteerSubcategories.CountAsync());
        Assert.Equal(0, await db.VolunteerTasks.CountAsync());
    }

    // =====================================================================
    //  Lead = organizer, supervisor = volunteer
    // =====================================================================

    [Fact]
    public async Task Lead_must_be_an_organizer_and_supervisor_must_be_a_volunteer()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "A/V", null);

        // Lead = organizer: OK. Lead = a volunteer: rejected.
        Assert.True(await svc.SetLeadAsync(org, cat.Id, seed.OrganizerId));
        await Assert.ThrowsAsync<VolunteerValidationException>(
            () => svc.SetLeadAsync(org, cat.Id, seed.VolunteerId));

        // Supervisor = a volunteer from the pool: OK (this elevates them).
        Assert.True(await svc.AppointSupervisorAsync(org, cat.Id, seed.VolunteerId));
        // Supervisor = an organizer: rejected.
        await Assert.ThrowsAsync<VolunteerValidationException>(
            () => svc.AppointSupervisorAsync(org, cat.Id, seed.OrganizerId));

        var reloaded = await db.VolunteerCategories.SingleAsync(c => c.Id == cat.Id);
        Assert.Equal(seed.OrganizerId, reloaded.LeadParticipantId);
        Assert.Equal(seed.VolunteerId, reloaded.SupervisorParticipantId);

        // The appointed volunteer is STILL just a volunteer globally (no role flip).
        var sup = await db.Participants.SingleAsync(p => p.Id == seed.VolunteerId);
        Assert.Equal(ParticipantRole.Volunteer, sup.Role);
    }

    [Fact]
    public async Task Appointing_a_supervisor_is_organizer_only()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);
        var cat = await svc.CreateCategoryAsync(org, "Sponsor area", null);

        // A volunteer cannot appoint themselves (or anyone) as supervisor.
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.AppointSupervisorAsync(
                Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail),
                cat.Id, seed.VolunteerId));
    }

    // =====================================================================
    //  Supervisor scope enforcement
    // =====================================================================

    [Fact]
    public async Task Supervisor_manages_only_their_own_category()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var mine = await svc.CreateCategoryAsync(org, "My category", null);
        var other = await svc.CreateCategoryAsync(org, "Other category", null);
        await svc.AppointSupervisorAsync(org, mine.Id, seed.VolunteerId);

        var sup = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);

        // Can manage their own category.
        Assert.True(await svc.CanManageCategoryAsync(sup, mine.Id));
        var sub = await svc.CreateSubcategoryAsync(sup, mine.Id, "Sub under mine", null);
        Assert.NotNull(sub);

        // Cannot touch a category they do not supervise.
        Assert.False(await svc.CanManageCategoryAsync(sup, other.Id));
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.CreateSubcategoryAsync(sup, other.Id, "Sneaky sub", null));
    }

    [Fact]
    public async Task Plain_volunteer_cannot_edit_the_tree()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);
        var cat = await svc.CreateCategoryAsync(org, "Cat", null);

        // A volunteer who is NOT a supervisor of anything.
        var plain = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.CreateSubcategoryAsync(plain, cat.Id, "Nope", null));
        // Categories themselves are organizer-only.
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.CreateCategoryAsync(plain, "Self-made", null));
    }

    // =====================================================================
    //  Assignment scoping
    // =====================================================================

    [Fact]
    public async Task Only_volunteers_can_be_assigned_and_only_managers_assign()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Rooms", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Room A", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Host room A", null, null, null);

        // Assigning a non-volunteer (a speaker) is rejected.
        await Assert.ThrowsAsync<VolunteerValidationException>(
            () => svc.AssignVolunteerAsync(org, task.Id, seed.SpeakerOneId));

        // Organizer assigns a volunteer: OK + idempotent on repeat.
        Assert.True(await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId));
        Assert.True(await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId));
        Assert.Equal(1, await db.VolunteerTaskAssignments
            .CountAsync(a => a.TaskId == task.Id && a.ParticipantId == seed.VolunteerId));

        // A plain volunteer cannot assign anyone.
        var second = await AddVolunteerAsync(db, seed, "second.volunteer@example.test");
        var plain = Volunteer(seed, second, "second.volunteer@example.test");
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.AssignVolunteerAsync(plain, task.Id, second));
    }

    [Fact]
    public async Task My_tasks_are_grouped_and_scoped_to_the_assigned_volunteer()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Registration", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Badge desk", null);
        var mine = await svc.CreateTaskAsync(org, sub.Id, "My task", null, null, null);
        var others = await svc.CreateTaskAsync(org, sub.Id, "Someone else's task", null, null, null);

        var second = await AddVolunteerAsync(db, seed, "second.volunteer@example.test");
        await svc.AssignVolunteerAsync(org, mine.Id, seed.VolunteerId);
        await svc.AssignVolunteerAsync(org, others.Id, second);

        var myTasks = await svc.LoadMyTasksAsync(seed.EventId, seed.VolunteerId);
        var t = Assert.Single(myTasks);
        Assert.Equal("My task", t.Title);
        Assert.Equal("Registration", t.Subcategory.Category.Name); // rollup loaded
    }

    [Fact]
    public async Task Volunteer_updates_only_their_own_assigned_task_status()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Cat", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Sub", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Task", null, null, null);
        await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId);

        var assignedVol = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        Assert.True(await svc.SetTaskStatusAsync(assignedVol, task.Id, VolunteerTaskStatus.Done));
        Assert.Equal(VolunteerTaskStatus.Done,
            (await db.VolunteerTasks.SingleAsync(x => x.Id == task.Id)).Status);

        // A different, unassigned volunteer cannot change it.
        var other = await AddVolunteerAsync(db, seed, "unassigned@example.test");
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.SetTaskStatusAsync(
                Volunteer(seed, other, "unassigned@example.test"),
                task.Id, VolunteerTaskStatus.Open));
    }

    // =====================================================================
    //  Help channel raise / answer
    // =====================================================================

    [Fact]
    public async Task Assigned_volunteer_raises_help_supervisor_answers_and_resolves()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Registration", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Badge desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Host badge desk", null, null, null);

        // Appoint a supervisor (one volunteer) and assign a DIFFERENT volunteer.
        var supervisorId = await AddVolunteerAsync(db, seed, "supervisor.vol@example.test");
        await svc.AppointSupervisorAsync(org, cat.Id, supervisorId);
        await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId);

        // The assigned volunteer raises help; it inherits the task's category.
        var worker = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        var req = await svc.RaiseHelpAsync(worker, task.Id, "Printer is jammed");
        Assert.Equal(cat.Id, req.CategoryId);
        Assert.Equal(VolunteerHelpStatus.Open, req.Status);

        // The supervisor sees it in their category inbox and answers it.
        var supervisor = Volunteer(seed, supervisorId, "supervisor.vol@example.test");
        var inbox = await svc.LoadHelpForCategoryAsync(seed.EventId, cat.Id);
        Assert.Single(inbox);

        Assert.True(await svc.AnswerHelpAsync(
            supervisor, req.Id, "Use the spare printer at the back", VolunteerHelpStatus.Resolved));
        var answered = await db.VolunteerHelpRequests.SingleAsync(h => h.Id == req.Id);
        Assert.Equal(VolunteerHelpStatus.Resolved, answered.Status);
        Assert.Equal("supervisor.vol@example.test", answered.RespondedByEmail);
        Assert.NotNull(answered.RespondedAt);
        Assert.NotNull(answered.ResolvedAt);
    }

    [Fact]
    public async Task Help_can_only_be_raised_by_an_assigned_volunteer_and_answered_within_scope()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Cat", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Sub", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Task", null, null, null);

        // A volunteer NOT assigned to the task cannot raise help on it.
        var notAssigned = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.RaiseHelpAsync(notAssigned, task.Id, "help"));

        // Assign + raise, then a supervisor of a DIFFERENT category cannot answer.
        await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId);
        var req = await svc.RaiseHelpAsync(notAssigned, task.Id, "now I can ask");

        var otherCat = await svc.CreateCategoryAsync(org, "Other", null);
        var foreignSupId = await AddVolunteerAsync(db, seed, "foreign.sup@example.test");
        await svc.AppointSupervisorAsync(org, otherCat.Id, foreignSupId);
        var foreignSup = Volunteer(seed, foreignSupId, "foreign.sup@example.test");
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.AnswerHelpAsync(foreignSup, req.Id, "nope", VolunteerHelpStatus.Answered));
    }

    // =====================================================================
    //  Edition scoping
    // =====================================================================

    [Fact]
    public async Task Structure_is_scoped_to_the_edition()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        await svc.CreateCategoryAsync(org, "Edition-A category", null);

        // A second edition with its own organizer.
        var evt2 = new Event
        {
            Code = "ELDK28", CommunityName = "Demo", DisplayName = "Demo 2028",
            StartDate = new DateOnly(2028, 2, 3), EndDate = new DateOnly(2028, 2, 4),
            IsActive = false, CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Events.Add(evt2);
        await db.SaveChangesAsync();
        var org2 = new Participant
        {
            EventId = evt2.Id, Email = "organizer2@expertslive.dk", FullName = "Org Two",
            Role = ParticipantRole.Organizer, IsActive = true, IsTestUser = true,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Participants.Add(org2);
        await db.SaveChangesAsync();

        var actor2 = new Actor(org2.Id, org2.Email, ParticipantRole.Organizer, evt2.Id);
        await svc.CreateCategoryAsync(actor2, "Edition-B category", null);

        // Each edition's tree contains only its own category.
        var treeA = await svc.LoadTreeAsync(seed.EventId);
        var treeB = await svc.LoadTreeAsync(evt2.Id);
        Assert.Equal("Edition-A category", Assert.Single(treeA).Name);
        Assert.Equal("Edition-B category", Assert.Single(treeB).Name);
    }

    // =====================================================================
    //  Help-request notification (routes to the category's supervisor,
    //  CC the organizer lead for oversight). The DEV redirect is applied by
    //  the IEmailSender layer; here we use the capturing sender + the REAL
    //  shipped template to prove routing end-to-end.
    // =====================================================================

    private static EmailTemplateProvider RealTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            HubUrl = "https://hub.example.test",
        }));

    private static VolunteerHelpNotificationService NewNotifier(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender)
        => new(db, new ParticipantEmailService(
            db, RealTemplates(), sender, new EmailContextAccessor()));

    [Fact]
    public async Task Raising_help_notifies_the_categorys_supervisor_and_cc_lead()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        var cat = await svc.CreateCategoryAsync(org, "Registration", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Badge desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Host badge desk", null, null, null);

        // Lead = the seeded organizer; supervisor = a volunteer from the pool;
        // the worker is a DIFFERENT volunteer assigned to the task.
        var supervisorId = await AddVolunteerAsync(db, seed, "supervisor.vol@example.test");
        await svc.SetLeadAsync(org, cat.Id, seed.OrganizerId);
        await svc.AppointSupervisorAsync(org, cat.Id, supervisorId);
        await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId);

        var worker = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        var req = await svc.RaiseHelpAsync(worker, task.Id, "Printer is jammed");

        // The notifier resolves the recipients off the help request's category.
        var sender = new CapturingEmailSender();
        var notifier = NewNotifier(db, sender);
        var recipients = await notifier.ResolveRecipientsAsync(seed.EventId, req.Id);
        Assert.Equal(supervisorId, recipients.SupervisorParticipantId);
        Assert.Equal(seed.OrganizerId, recipients.LeadParticipantId);

        var sentCount = await notifier.NotifySupervisorAsync(seed.EventId, req.Id);

        // Supervisor (primary) + organizer lead (oversight) both notified.
        Assert.Equal(2, sentCount);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains(sender.Sent, m => m.To == "supervisor.vol@example.test");
        Assert.Contains(sender.Sent, m => m.To == ScenarioSeed.OrganizerEmail);
        // The shipped template fills the task title + category into the subject.
        Assert.All(sender.Sent, m =>
        {
            Assert.Contains("Host badge desk", m.Subject);
            Assert.Contains("Registration", m.Subject);
        });
        // The volunteer's message reaches the supervisor's email body.
        var supMsg = Assert.Single(sender.Messages, m => m.To == "supervisor.vol@example.test");
        Assert.Contains("Printer is jammed", supMsg.Html);
    }

    [Fact]
    public async Task Help_notification_without_a_supervisor_is_a_safe_no_op()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var org = Organizer(seed);

        // Category has a lead but NO supervisor appointed yet.
        var cat = await svc.CreateCategoryAsync(org, "Registration", null);
        var sub = await svc.CreateSubcategoryAsync(org, cat.Id, "Badge desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Host badge desk", null, null, null);
        await svc.SetLeadAsync(org, cat.Id, seed.OrganizerId);
        await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId);

        var worker = Volunteer(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        var req = await svc.RaiseHelpAsync(worker, task.Id, "Need a hand");

        var sender = new CapturingEmailSender();
        var notifier = NewNotifier(db, sender);
        var recipients = await notifier.ResolveRecipientsAsync(seed.EventId, req.Id);
        Assert.Null(recipients.SupervisorParticipantId);

        // No supervisor → only the organizer lead is mailed for oversight; the
        // request still sits in the in-hub inbox regardless.
        var sentCount = await notifier.NotifySupervisorAsync(seed.EventId, req.Id);
        Assert.Equal(1, sentCount);
        Assert.Equal(ScenarioSeed.OrganizerEmail, Assert.Single(sender.Sent).To);
    }
}
