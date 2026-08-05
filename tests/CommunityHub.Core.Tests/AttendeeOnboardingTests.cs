using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §207/§208 — attendee onboarding: the 2-day Master Class selection task + its 2-week
/// reminder, the two-way MC reconciliation, the 1-day provisioning + welcome (with hub CTA),
/// all offline (EF in-memory + the real shipped templates + a fixed clock).
/// </summary>
public sealed class AttendeeOnboardingTests
{
    private static readonly DateTimeOffset Welcome = new(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-templates"),
            HubUrl = "https://hub.example.test",
        }));

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T 2027", Code = "T27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<int> SeedAttendeeParticipantAsync(
        CommunityHubDbContext db, int eventId, string email, TicketStatus ticket)
    {
        db.Attendees.Add(new Attendee
        {
            EventId = eventId, BackstageTicketId = "t-" + email, Email = email,
            FullName = "Avery Attendee", TicketStatus = ticket,
            // §358: these tests model an attendee who HAS been invited — the cadence is anchored on
            // the selection invite. The helper never set this, and the builder used to anchor on the
            // TASK date, so it did not matter. It does now: an attendee with no invite stamp is
            // deliberately never chased (you cannot remind someone about a mail they never got),
            // which is exactly the state right after an organizer §355 reset. Stamped in the past so
            // the cadence arithmetic these tests assert is unchanged.
            MasterClassInviteSentAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var p = new Participant
        {
            EventId = eventId, Email = email, FullName = "Avery Attendee",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    // ----- §207 Master Class selection task ------------------------------

    [Fact]
    public async Task Mc_task_seeded_for_two_day_attendee_only_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var twoDay = await SeedAttendeeParticipantAsync(db, ev, "two@x.dk", TicketStatus.TwoDay);
        await SeedAttendeeParticipantAsync(db, ev, "one@x.dk", TicketStatus.Other);

        var seeder = new AttendeeMasterClassTaskSeeder(db, ScenarioFixture.Clock);
        Assert.Equal(1, await seeder.SeedAsync(ev));     // only the 2-day holder
        Assert.Equal(0, await seeder.SeedAsync(ev));     // idempotent

        var key = AttendeeMasterClassTaskSeeder.SourceKeyFor(twoDay);
        Assert.True(await db.Tasks.AnyAsync(t => t.SourceKey == key && t.State == TaskState.Open));
        // The 1-day holder gets NO Master Class task.
        Assert.False(await db.Tasks.AnyAsync(t =>
            t.SourceKey == AttendeeMasterClassTaskSeeder.SourceKeyFor(twoDay + 1)));
    }

    [Fact]
    public async Task Mc_reconciler_marks_done_on_confirmed_signup_and_reopens()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedAttendeeParticipantAsync(db, ev, "two@x.dk", TicketStatus.TwoDay);
        await new AttendeeMasterClassTaskSeeder(db, ScenarioFixture.Clock).SeedAsync(ev);
        var key = AttendeeMasterClassTaskSeeder.SourceKeyFor(pid);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        // A confirmed Master Class signup for the attendee (matched by email) ⇒ Done.
        var attendee = await db.Attendees.FirstAsync(a => a.Email == "two@x.dk");
        var session = new Session { EventId = ev, Title = "MC", SessionizeId = "s1" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev, AttendeeId = attendee.Id, SessionId = session.Id,
            Status = MasterClassSignupStatus.Confirmed,
        });
        await db.SaveChangesAsync();

        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, (await db.Tasks.FirstAsync(t => t.SourceKey == key)).State);

        // Remove the confirmed seat ⇒ the task reopens.
        db.MasterClassSignups.RemoveRange(db.MasterClassSignups);
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, (await db.Tasks.FirstAsync(t => t.SourceKey == key)).State);
    }

    [Fact]
    public async Task Mc_selection_task_closes_for_a_cancelled_attendee_and_reopens_on_reactivation()
    {
        // §234 1 (FormTaskReconciler extension): a soft-cancelled attendee's open
        // Master-Class selection task is CLOSED (no more nagging) and reopens if the
        // ticket reactivates while they still have no confirmed seat.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedAttendeeParticipantAsync(db, ev, "two@x.dk", TicketStatus.TwoDay);
        await new AttendeeMasterClassTaskSeeder(db, ScenarioFixture.Clock).SeedAsync(ev);
        var key = AttendeeMasterClassTaskSeeder.SourceKeyFor(pid);
        var reconciler = new FormTaskReconciler(db, ScenarioFixture.Clock);

        var attendee = await db.Attendees.FirstAsync(a => a.Email == "two@x.dk");
        attendee.MirrorState = MirrorState.Cancelled;
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Done, (await db.Tasks.FirstAsync(t => t.SourceKey == key)).State);

        attendee.MirrorState = MirrorState.Active;
        await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(ev, pid, default);
        Assert.Equal(TaskState.Open, (await db.Tasks.FirstAsync(t => t.SourceKey == key)).State);
    }

    [Fact(Skip = "733.1 - the per-task party/master-class cadences are RETIRED (operator 2026-07-31: the Get Started wizard is chased once every 14 days by getstarted-digest). Kept, not deleted, so re-enabling restores this coverage.")]
    public async Task Mc_reminder_is_2_weeks_from_welcome_and_stops_on_confirmed()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedAttendeeParticipantAsync(db, ev, "two@x.dk", TicketStatus.TwoDay);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = pid, Title = "Select your Master Class",
            State = TaskState.Open, SourceKey = AttendeeMasterClassTaskSeeder.SourceKeyFor(pid),
            CreatedAt = Welcome,
        });
        await db.SaveChangesAsync();

        var clock = new FixedClock(Welcome);
        var builder = new AttendeeMasterClassReminderBuilder(db, Templates(), clock);

        // §232: quiet on the day of welcome (the welcome is the day-0 nudge); the FIRST
        // reminder fires 2 weeks later, then a new window every 14 days.
        Assert.Empty(await builder.BuildDueAsync(ev));
        clock.Set(Welcome.AddDays(14));
        var m = (await builder.BuildDueAsync(ev)).Single();
        // §707.11 — the occasion is the DAY now (was `:wk1`); "due" is lastSent + interval.
        Assert.EndsWith($":{Welcome.AddDays(14):yyyyMMdd}", m.OccasionKey);
        Assert.Equal("attendee-masterclass", m.ReminderType);
        // §707.11 — and it carries its own mail identity, split out of task-deadline-reminder.
        Assert.Equal("attendee-masterclass-reminder", m.MailKey);

        // A confirmed Master Class ⇒ stop.
        var attendee = await db.Attendees.FirstAsync(a => a.Email == "two@x.dk");
        var session = new Session { EventId = ev, Title = "MC", SessionizeId = "s1" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev, AttendeeId = attendee.Id, SessionId = session.Id,
            Status = MasterClassSignupStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        Assert.Empty(await builder.BuildDueAsync(ev));
    }

    [Fact(Skip = "733.1 - the per-task party/master-class cadences are RETIRED (operator 2026-07-31: the Get Started wizard is chased once every 14 days by getstarted-digest). Kept, not deleted, so re-enabling restores this coverage.")]
    public async Task Mc_reminder_stops_for_a_cancelled_attendee_and_resumes_on_reactivation()
    {
        // §234 1: a soft-cancelled attendee (no ACTIVE ticket on their email) must stop
        // receiving the biweekly Master-Class selection cadence; a reappearing ticket
        // (MirrorState flips back to Active) resumes it.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedAttendeeParticipantAsync(db, ev, "two@x.dk", TicketStatus.TwoDay);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = pid, Title = "Select your Master Class",
            State = TaskState.Open, SourceKey = AttendeeMasterClassTaskSeeder.SourceKeyFor(pid),
            CreatedAt = Welcome,
        });
        await db.SaveChangesAsync();

        var clock = new FixedClock(Welcome.AddDays(14));
        var builder = new AttendeeMasterClassReminderBuilder(db, Templates(), clock);
        Assert.Single(await builder.BuildDueAsync(ev));          // active ⇒ reminded

        var attendee = await db.Attendees.FirstAsync(a => a.Email == "two@x.dk");
        attendee.MirrorState = MirrorState.Cancelled;
        await db.SaveChangesAsync();
        Assert.Empty(await builder.BuildDueAsync(ev));           // cancelled ⇒ silence

        attendee.MirrorState = MirrorState.Active;
        await db.SaveChangesAsync();
        Assert.Single(await builder.BuildDueAsync(ev));          // reactivated ⇒ resumes
    }

    // ----- §208 1-day provisioning + welcome -----------------------------

    [Fact]
    public async Task One_day_holder_is_provisioned_to_a_login_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t1", Email = "one@x.dk",
            FullName = "One Day", TicketStatus = TicketStatus.Other,
        });
        await db.SaveChangesAsync();

        var svc = new AttendeeWelcomeProvisioningService(
            db, ScenarioFixture.Clock, NullLogger<AttendeeWelcomeProvisioningService>.Instance);
        var created = await svc.ProvisionOneDayAsync(ev);

        var pid = Assert.Single(created);
        var p = await db.Participants.SingleAsync(x => x.Id == pid);
        Assert.Equal(ParticipantRole.Attendee, p.Role);
        Assert.True(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);
        Assert.Null(p.WelcomeWithLoginSentAt);                 // caller welcomes
        Assert.Empty(await svc.ProvisionOneDayAsync(ev));      // idempotent
    }

    [Fact]
    public async Task One_day_welcome_is_retired_never_sends_and_never_stamps()
    {
        // §299 OPEN-26 (operator 2026-07-23): 1-day attendees get NO welcome mail. The §208
        // send path is inert — no mail, no WelcomeWithLoginSentAt stamp, ever.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedAttendeeParticipantAsync(db, ev, "one@x.dk", TicketStatus.Other);

        var sender = new CapturingEmailSender();
        var clock = new FixedClock(Welcome);
        var svc = new AttendeeOneDayWelcomeEmailService(db, Templates(), sender, clock);

        Assert.False(await svc.SendForProvisioningAsync(pid));
        Assert.Empty(sender.Messages);
        Assert.Null((await db.Participants.FirstAsync(p => p.Id == pid)).WelcomeWithLoginSentAt);
    }
}
