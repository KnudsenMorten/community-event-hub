using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §164 party rework: the AUTHENTICATED RSVP (head count + participant id), the
/// 16:00–18:30 window, the per-staff-role party task seeding, and the two-way task
/// reconciliation (RSVP ⇒ Done, un-answer ⇒ reopen). EF in-memory + a fixed clock.
/// </summary>
public class PartyTaskFlowTests
{
    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "Test 2027", Code = "TC27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<int> SeedParticipantAsync(
        CommunityHubDbContext db, int eventId, ParticipantRole role, string email = "p@x.dk")
    {
        var p = new Participant
        {
            EventId = eventId, FullName = "Person One", Email = email,
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    // ----- the 16:00–18:30 window ----------------------------------------

    [Fact]
    public async Task GetActiveParty_window_ends_at_18_30()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var p = await new PartyRsvpService(db).GetActivePartyAsync();
        Assert.NotNull(p);
        Assert.Equal(16, p!.StartHour);
        Assert.Equal(0, p.StartMinute);
        Assert.Equal(18, p.EndHour);
        Assert.Equal(30, p.EndMinute);     // §164: 18:30, not 18:00
    }

    // ----- authenticated RSVP: head count + participant id ----------------

    [Fact]
    public async Task Submit_stamps_headcount_and_participant_id()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Sponsor);
        var svc = new PartyRsvpService(db);

        await svc.SubmitAsync("ACME", "p@x.dk", attending: true, ipHash: null, headCount: 4, participantId: pid);

        var row = await svc.GetForParticipantAsync(ev, pid);
        Assert.NotNull(row);
        Assert.Equal(4, row!.HeadCount);
        Assert.Equal(pid, row.ParticipantId);
        Assert.True(row.Attending);
    }

    [Fact]
    public async Task Submit_drops_headcount_when_not_attending()
    {
        // A "no" RSVP carries no head count even if one is posted (no one is coming).
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Sponsor);
        var svc = new PartyRsvpService(db);

        await svc.SubmitAsync("ACME", "p@x.dk", attending: false, ipHash: null, headCount: 4, participantId: pid);

        var row = await svc.GetForParticipantAsync(ev, pid);
        Assert.NotNull(row);
        Assert.Null(row!.HeadCount);
        Assert.False(row.Attending);
    }

    [Fact]
    public async Task Anonymous_submit_has_no_headcount_or_participant_id()
    {
        // The existing anonymous path (4-arg) keeps working — null head count + null pid.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var svc = new PartyRsvpService(db);

        Assert.True((await svc.SubmitAsync("Jane", "jane@x.dk", true, null)).Ok);
        var row = (await svc.GetAllAsync(ev)).Single();
        Assert.Null(row.HeadCount);
        Assert.Null(row.ParticipantId);
    }

    // ----- party task seeding (staff roles only) --------------------------

    [Fact]
    public async Task Seeder_creates_one_party_task_per_staff_role_but_not_attendees()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var sponsor = await SeedParticipantAsync(db, ev, ParticipantRole.Sponsor, "s@x.dk");
        var speaker = await SeedParticipantAsync(db, ev, ParticipantRole.Speaker, "spk@x.dk");
        var media = await SeedParticipantAsync(db, ev, ParticipantRole.Media, "m@x.dk");
        var attendee = await SeedParticipantAsync(db, ev, ParticipantRole.Attendee, "a@x.dk");

        var seeder = new PartyTaskSeeder(db, ScenarioFixture.Clock);
        var created = await seeder.SeedAsync(ev);

        Assert.Equal(2, created);   // sponsor + speaker; NOT media, NOT attendee
        Assert.True(await HasPartyTask(db, ev, sponsor));
        Assert.True(await HasPartyTask(db, ev, speaker));
        Assert.False(await HasPartyTask(db, ev, media));
        Assert.False(await HasPartyTask(db, ev, attendee));

        // Idempotent — a second run creates nothing.
        Assert.Equal(0, await seeder.SeedAsync(ev));
    }

    [Fact]
    public async Task Seeder_task_for_sponsor_mentions_the_head_count()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var sponsor = await SeedParticipantAsync(db, ev, ParticipantRole.Sponsor);
        await new PartyTaskSeeder(db, ScenarioFixture.Clock).SeedAsync(ev);

        var task = await db.Tasks.FirstAsync(t => t.SourceKey == PartyTaskSeeder.SourceKeyFor(sponsor));
        Assert.Contains("how many", task.Description!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new DateOnly(2027, 1, 19), task.DueDate);   // 9 Feb − 21 days
    }

    // ----- two-way reconciliation -----------------------------------------

    [Fact]
    public async Task Reconciler_marks_party_task_done_on_rsvp_and_reopens_on_unanswer()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Volunteer);
        await new PartyTaskSeeder(db, ScenarioFixture.Clock).SeedAsync(ev);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        // Open to start with.
        Assert.Equal(TaskState.Open, await PartyTaskState(db, ev, pid));

        // RSVP (yes OR no — here a decline) ⇒ task Done.
        await new PartyRsvpService(db).SubmitAsync(
            "Vol", "p@x.dk", attending: false, ipHash: null, participantId: pid);
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, await PartyTaskState(db, ev, pid));

        // Un-answer (remove the RSVP) ⇒ task reopens so the reminder nags again.
        db.PartyRsvps.RemoveRange(db.PartyRsvps.Where(r => r.ParticipantId == pid));
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        var reopened = await db.Tasks.FirstAsync(t => t.SourceKey == PartyTaskSeeder.SourceKeyFor(pid));
        Assert.Equal(TaskState.Open, reopened.State);
        Assert.Null(reopened.CompletedAt);
    }

    private static Task<bool> HasPartyTask(CommunityHubDbContext db, int ev, int pid) =>
        db.Tasks.AnyAsync(t => t.EventId == ev && t.SourceKey == PartyTaskSeeder.SourceKeyFor(pid));

    private static async Task<TaskState> PartyTaskState(CommunityHubDbContext db, int ev, int pid) =>
        (await db.Tasks.FirstAsync(t => t.EventId == ev && t.SourceKey == PartyTaskSeeder.SourceKeyFor(pid))).State;
}
