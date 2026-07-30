using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §177 + §206 (operator 2026-06-30): the CREW + ATTENDEE party-RSVP reminder cadence —
/// every 2 weeks FROM WELCOME (the party task's CreatedAt, immediately, NO date gate), a
/// distinct OccasionKey per 14-day window so the engine sends one per window, stopping the
/// moment the person RSVPs (the party task flips to Done / a PartyRsvp appears).
/// </summary>
public sealed class AttendeePartyReminderTests
{
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>The welcome/anchor date the party task is created on.</summary>
    private static readonly DateTimeOffset Anchor =
        new(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);

    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));

    private static async Task<(int ev, int pid)> SeedAttendeePartyTaskAsync(
        CommunityHubDbContext db, TaskState state = TaskState.Open,
        ParticipantRole role = ParticipantRole.Attendee)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = "Attendee One", Email = "a@x.dk",
            Role = role, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev.Id, AssignedParticipantId = p.Id, Title = "Sign up for the Party",
            State = state, DueDate = null, IsMandatory = false,
            SourceKey = PartyTaskSeeder.SourceKeyFor(p.Id),
            CreatedAt = Anchor,   // §206: the cadence anchors on welcome (task creation).
        });
        await db.SaveChangesAsync();
        return (ev.Id, p.Id);
    }

    [Fact]
    public async Task First_reminder_waits_2_weeks_after_welcome()
    {
        // §232 (operator 2026-07-07): the welcome IS the day-0 nudge — no reminder fires
        // before 2 weeks have passed since the welcome (the task's creation).
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db);
        var clock = new FixedClock(Anchor);
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        // Day 0 (the welcome) and day 13: quiet.
        Assert.Empty(await builder.BuildDueAsync(ev));
        clock.Set(Anchor.AddDays(13));
        Assert.Empty(await builder.BuildDueAsync(ev));

        // Day 14 — the FIRST reminder.
        clock.Set(Anchor.AddDays(14));
        var m = Assert.Single(await builder.BuildDueAsync(ev));
        // §707.11 — the occasion is the DAY now (was `:wk1`); "due" is decided by lastSent+interval.
        Assert.EndsWith(":20260714", m.OccasionKey);
        Assert.Equal("attendee-party", m.ReminderType);
        Assert.Equal("a@x.dk", m.RecipientEmail);
        // §707.11 — and it carries its OWN mail identity, split out of task-deadline-reminder.
        Assert.Equal("attendee-party-reminder", m.MailKey);
    }

    /// <summary>Record a send in the ledger, exactly as <c>ReminderEngine</c> does on delivery.</summary>
    private static async Task LedgerAsync(
        CommunityHubDbContext db, int ev, Reminders.ReminderMessage m, DateTimeOffset when)
    {
        db.SentReminders.Add(new SentReminder
        {
            EventId = ev,
            RecipientEmail = m.RecipientEmail,
            ReminderType = m.ReminderType,
            OccasionKey = m.OccasionKey,
            SentAt = when,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 🔒 §707.11/§707.10 — the gap is measured from the LAST SEND, not from a calendar window.
    /// </summary>
    /// <remarks>
    /// Replaces <c>One_reminder_per_14_day_window</c>, which asserted the window model
    /// (<c>:wk1</c>, <c>:wk2</c>, …). Operator 2026-07-30: *"last known sendt + x date - once it hits
    /// the race time, when an email goes out"*. The distinction is not academic: under the old rule a
    /// send delayed by a ring drop landed late in its window while the NEXT window opened on
    /// schedule, so one person could get two mails a day apart. Coverage is rewritten, not deleted.
    /// </remarks>
    [Fact]
    public async Task Repeats_are_measured_from_the_last_send_not_a_calendar_window()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db);
        // §232: quiet until day 14 — the welcome is the day-0 nudge.
        var clock = new FixedClock(Anchor.AddDays(14));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        var first = Assert.Single(await builder.BuildDueAsync(ev));
        await LedgerAsync(db, ev, first, Anchor.AddDays(14));

        // Day 21 — only 7 days since that send ⇒ NOT due.
        clock.Set(Anchor.AddDays(21));
        Assert.Empty(await builder.BuildDueAsync(ev));

        // Day 28 — a full 14 days since the last send ⇒ due again. But simulate the §234 case: the
        // send is RING-DROPPED, so the engine does NOT ledger it.
        clock.Set(Anchor.AddDays(28));
        Assert.Single(await builder.BuildDueAsync(ev));

        // It therefore stays due and retries daily while the ring is closed.
        clock.Set(Anchor.AddDays(33));
        Assert.Single(await builder.BuildDueAsync(ev));

        // Day 34 — the ring widens and it finally DELIVERS, so now it is ledgered.
        clock.Set(Anchor.AddDays(34));
        var second = Assert.Single(await builder.BuildDueAsync(ev));
        await LedgerAsync(db, ev, second, Anchor.AddDays(34));

        // 🔑 THE DIFFERENCE FROM THE OLD MODEL. Day 42 is 8 days after the mail actually went out,
        // so it is NOT due. Under the window rule day 42 opened window 3 regardless of when the
        // previous one really left — which is how one person could get two mails a day apart.
        clock.Set(Anchor.AddDays(42));
        Assert.Empty(await builder.BuildDueAsync(ev));

        // A full interval after the REAL send ⇒ due.
        clock.Set(Anchor.AddDays(48));
        Assert.Single(await builder.BuildDueAsync(ev));
    }

    [Fact]
    public async Task Cancelled_attendee_gets_no_party_reminder()
    {
        // §234 1: a soft-cancelled ATTENDEE (their email holds NO MirrorState.Active
        // ticket) must stop receiving the biweekly party cadence — cancellation resets
        // RSVP/seat/login but previously left the reminder task nagging forever.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db);
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-1", Email = "a@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled,
        });
        await db.SaveChangesAsync();
        var clock = new FixedClock(Anchor.AddDays(14));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        Assert.Empty(await builder.BuildDueAsync(ev));
    }

    [Fact]
    public async Task Attendee_with_a_cancelled_AND_an_active_ticket_still_gets_the_reminder()
    {
        // §234 (operator): "you can easily have same mail address twice — fx one cancelled
        // ticket and one active" — entitlement = ≥1 ACTIVE ticket per email, so the
        // cancelled row must NOT silence the reminder.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db);
        db.Attendees.AddRange(
            new Attendee
            {
                EventId = ev, BackstageTicketId = "t-1", Email = "a@x.dk",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled,
            },
            new Attendee
            {
                EventId = ev, BackstageTicketId = "t-2", Email = "A@x.dk ",   // case/space-insensitive match
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
            });
        await db.SaveChangesAsync();
        var clock = new FixedClock(Anchor.AddDays(14));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        var m = Assert.Single(await builder.BuildDueAsync(ev));
        Assert.Equal("a@x.dk", m.RecipientEmail);
    }

    [Fact]
    public async Task Crew_member_with_no_attendee_row_keeps_the_reminder()
    {
        // §234 1: the cancelled-attendee gate applies to Role == Attendee ONLY — crew
        // roles have no Attendee mirror row at all and must keep their cadence.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db, role: ParticipantRole.Volunteer);
        var clock = new FixedClock(Anchor.AddDays(14));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        Assert.Single(await builder.BuildDueAsync(ev));
    }

    [Fact]
    public async Task Crew_roles_also_get_the_2_week_party_cadence()
    {
        // §206: the same cadence applies to crew (here: a Media crew member), not only attendees.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db, role: ParticipantRole.Media);
        var clock = new FixedClock(Anchor.AddDays(14));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        Assert.EndsWith(":20260714", (await builder.BuildDueAsync(ev)).Single().OccasionKey);
    }

    [Fact]
    public async Task Stops_once_the_attendee_has_rsvpd()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, pid) = await SeedAttendeePartyTaskAsync(db);
        var clock = new FixedClock(Anchor.AddDays(14));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        Assert.Single(await builder.BuildDueAsync(ev));   // still open ⇒ nags

        // RSVP recorded ⇒ stop (belt: a PartyRsvp row alone is enough).
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev, ParticipantId = pid, Name = "Attendee One", Email = "a@x.dk", Attending = true,
        });
        await db.SaveChangesAsync();
        Assert.Empty(await builder.BuildDueAsync(ev));
    }

    [Fact]
    public async Task Stops_once_the_party_task_is_done()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAttendeePartyTaskAsync(db, state: TaskState.Done);
        var clock = new FixedClock(Anchor.AddDays(28));
        var builder = new AttendeePartyReminderBuilder(db, Templates(), clock);

        Assert.Empty(await builder.BuildDueAsync(ev));
    }
}
