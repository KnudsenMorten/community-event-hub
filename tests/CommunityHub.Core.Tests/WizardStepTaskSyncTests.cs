using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §173e: the FOUR data-signal Get-Started step tasks (Calendar email, Speaker details,
/// Profile, Code of Conduct) that, before §173e, had no task at all are now kept in sync —
/// BOTH ways — by <see cref="FormTaskReconciler"/>: the step's data present ⇒ task Done; the
/// step un-answered ⇒ task reopens. These prove the reconciler flips each one off its own
/// signal and never touches a step whose task doesn't exist.
/// </summary>
public sealed class WizardStepTaskSyncTests
{
    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<int> SeedParticipantAsync(CommunityHubDbContext db, int ev, ParticipantRole role)
    {
        var p = new Participant
        {
            EventId = ev, FullName = "Person One", Email = "p@x.dk",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task SeedStepTaskAsync(CommunityHubDbContext db, int ev, int pid, string sourceKey)
    {
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = pid, Title = "Step", State = TaskState.Open,
            SourceKey = sourceKey, IsMandatory = false,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TaskState> StateAsync(CommunityHubDbContext db, string sourceKey) =>
        (await db.Tasks.FirstAsync(t => t.SourceKey == sourceKey)).State;

    [Fact]
    public async Task Profile_task_completes_on_phone_and_reopens_when_cleared()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Volunteer);
        var key = WizardStepTaskKeys.Profile(pid);
        await SeedStepTaskAsync(db, ev, pid, key);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        Assert.Equal(TaskState.Open, await StateAsync(db, key));

        // Add a phone ⇒ the profile step is done ⇒ task Done.
        var p = await db.Participants.FirstAsync(x => x.Id == pid);
        p.Phone = "+45 12 34 56 78";
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, await StateAsync(db, key));

        // Clear it ⇒ step un-answered ⇒ task reopens (CompletedAt cleared).
        p.Phone = "";
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        var reopened = await db.Tasks.FirstAsync(t => t.SourceKey == key);
        Assert.Equal(TaskState.Open, reopened.State);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task Details_task_completes_off_the_speaker_edit_marker()
    {
        // §313: the calendar: task is RETIRED (the optional Calendar-email step never has
        // a task, and the reconciler no longer tracks the key) — only details is synced.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Speaker);
        var det = WizardStepTaskKeys.SpeakerDetails(pid);
        await SeedStepTaskAsync(db, ev, pid, det);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = ev, ParticipantId = pid });
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, await StateAsync(db, det));   // marker not stamped yet

        var prof = await db.SpeakerProfiles.FirstAsync(x => x.ParticipantId == pid);
        prof.BioLastEditedBySpeakerAt = ScenarioFixture.Clock.GetUtcNow();
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, await StateAsync(db, det));
    }

    [Fact]
    public async Task Accept_task_completes_on_a_policy_acceptance_row()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Organizer);
        var key = WizardStepTaskKeys.Accept(pid);
        await SeedStepTaskAsync(db, ev, pid, key);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, await StateAsync(db, key));

        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = ev, ParticipantId = pid, AcceptedByEmail = "p@x.dk",
            AcceptedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, await StateAsync(db, key));
    }

    [Fact]
    public async Task Availability_task_completes_on_a_day_availability_row_and_reopens()
    {
        // §234 (7a): the availability: wizard-step task follows the §148 per-day
        // availability form's OWN data — both ways.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Volunteer);
        var key = WizardStepTaskKeys.Availability(pid);
        await SeedStepTaskAsync(db, ev, pid, key);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, await StateAsync(db, key));

        var day = new VolunteerDayAvailability
        { EventId = ev, ParticipantId = pid, Day = new DateOnly(2027, 2, 9) };
        db.VolunteerDayAvailabilities.Add(day);
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, await StateAsync(db, key));

        db.VolunteerDayAvailabilities.Remove(day);
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, await StateAsync(db, key));
    }

    [Fact]
    public async Task Volunteer_form_task_completes_on_selected_shifts_not_on_day_availability()
    {
        // §234 (7a): volunteer-form: is OWNED by the SHIFTS wizard — its completion
        // signal is a VolunteerAvailability row WITH shifts selected. A §148 per-day
        // availability row (the OTHER live form) must NOT close it any more.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Volunteer);
        var key = $"volunteer-form:{pid}";
        await SeedStepTaskAsync(db, ev, pid, key);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        // The cross-wire: day availability saved ⇒ the shifts task must stay OPEN.
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        { EventId = ev, ParticipantId = pid, Day = new DateOnly(2027, 2, 9) });
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, await StateAsync(db, key));

        // A shifts-wizard submission with no shifts picked is not a completion either.
        var avail = new VolunteerAvailability { EventId = ev, ParticipantId = pid, SelectedShifts = "" };
        db.VolunteerAvailabilities.Add(avail);
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, await StateAsync(db, key));

        // Shifts actually selected ⇒ Done.
        avail.SelectedShifts = "shift-1,shift-2";
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, await StateAsync(db, key));
    }

    [Fact]
    public async Task Reconciler_is_a_noop_when_the_role_has_no_step_task()
    {
        // A participant with NONE of the four step tasks: the reconcile must not throw and
        // must create nothing.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Attendee);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Empty(await db.Tasks.ToListAsync());
    }
}
