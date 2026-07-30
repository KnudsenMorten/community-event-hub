using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §355 — the organizer "reset attendee onboarding" action (operator 2026-07-26). Three
/// INDEPENDENT switches, because re-testing the party flow must not destroy a Master Class seat.
///
/// <para>These tests exist because the same reset was first performed by hand with SQL against
/// production, which showed the real risk: it is not one flag but rows across five tables, and
/// which of them exist differs per attendee.</para>
/// </summary>
public sealed class AttendeeOnboardingResetServiceTests
{
    private const string Email = "reset-me@example.test";

    private static async Task<(CommunityHub.Core.Data.CommunityHubDbContext Db, int EventId, int AttendeeId, int ParticipantId, int SessionId)>
        SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event
        {
            Code = "T27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var s = new Session
        {
            EventId = ev.Id, Title = "MC", Type = SessionType.MasterClass, MasterClassCapacity = 10,
        };
        db.Sessions.Add(s);

        var a = new Attendee
        {
            EventId = ev.Id, Email = Email, FirstName = "Re", LastName = "Set",
            TicketStatus = TicketStatus.TwoDay,
            MasterClassInviteSentAt = DateTimeOffset.UtcNow,
        };
        db.Attendees.Add(a);

        var p = new Participant
        {
            EventId = ev.Id, Email = Email, FullName = "Re Set", Role = ParticipantRole.Attendee,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
            WelcomeWithLoginSentAt = DateTimeOffset.UtcNow,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev.Id, AttendeeId = a.Id, SessionId = s.Id,
            Status = MasterClassSignupStatus.Confirmed,
        });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev.Id, ParticipantId = p.Id, Email = Email, Attending = true,
        });
        db.SentReminders.Add(new SentReminder
        {
            EventId = ev.Id, RecipientEmail = Email, ReminderType = "welcome", OccasionKey = $"welcome:{p.Id}",
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev.Id, AssignedParticipantId = p.Id, Title = "Party",
            SourceKey = $"party-form:{p.Id}", State = TaskState.Done,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev.Id, AssignedParticipantId = p.Id, Title = "MC",
            SourceKey = $"masterclass-form:{p.Id}", State = TaskState.Done,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return (db, ev.Id, a.Id, p.Id, s.Id);
    }

    /// <summary>
    /// THE POINT OF THE THREE SWITCHES: resetting the party must leave a confirmed Master Class
    /// seat completely alone. Getting this wrong would destroy real bookings during a test.
    /// </summary>
    [Fact]
    public async Task Party_only_reset_leaves_the_master_class_seat_and_the_welcome_untouched()
    {
        var (db, ev, aid, pid, _) = await SeedAsync();
        var svc = new AttendeeOnboardingResetService(db);

        var r = await svc.ResetAsync(ev, aid, resetWelcome: false, resetMasterClass: false, resetParty: true);

        Assert.True(r.Ok);
        Assert.Equal(1, r.RsvpsRemoved);
        Assert.Empty(db.PartyRsvps);
        // Untouched:
        Assert.Single(db.MasterClassSignups);
        Assert.NotNull(db.Attendees.Single(x => x.Id == aid).MasterClassInviteSentAt);
        Assert.NotNull(db.Participants.Single(x => x.Id == pid).WelcomeWithLoginSentAt);
        // The party task re-opened; the Master Class task did NOT.
        Assert.Equal(TaskState.Open, db.Tasks.Single(t => t.SourceKey == $"party-form:{pid}").State);
        Assert.Equal(TaskState.Done, db.Tasks.Single(t => t.SourceKey == $"masterclass-form:{pid}").State);
    }

    [Fact]
    public async Task MasterClass_only_reset_removes_the_seat_and_reopens_only_its_task()
    {
        var (db, ev, aid, pid, _) = await SeedAsync();
        var svc = new AttendeeOnboardingResetService(db);

        var r = await svc.ResetAsync(ev, aid, resetWelcome: false, resetMasterClass: true, resetParty: false);

        Assert.True(r.Ok);
        Assert.Equal(1, r.SignupsRemoved);
        Assert.Empty(db.MasterClassSignups);
        Assert.Single(db.PartyRsvps);                       // party untouched
        Assert.Equal(TaskState.Open, db.Tasks.Single(t => t.SourceKey == $"masterclass-form:{pid}").State);
        Assert.Equal(TaskState.Done, db.Tasks.Single(t => t.SourceKey == $"party-form:{pid}").State);
    }

    /// <summary>
    /// The welcome reset must clear BOTH markers — the per-participant stamp AND the
    /// address-keyed ledger row — or the mail stays suppressed by whichever one survived.
    /// </summary>
    [Fact]
    public async Task Welcome_reset_clears_both_the_stamp_and_the_ledger_row()
    {
        var (db, ev, aid, pid, _) = await SeedAsync();
        var svc = new AttendeeOnboardingResetService(db);

        var r = await svc.ResetAsync(ev, aid, resetWelcome: true, resetMasterClass: false, resetParty: false);

        Assert.True(r.Ok);
        Assert.Null(db.Attendees.Single(x => x.Id == aid).MasterClassInviteSentAt);
        Assert.Null(db.Participants.Single(x => x.Id == pid).WelcomeWithLoginSentAt);
        Assert.DoesNotContain(db.SentReminders, s => s.ReminderType == "welcome");
        Assert.True(r.LedgerRowsRemoved >= 1);
    }

    /// <summary>
    /// §340-G-1 — the reset must still find the ledger row when the attendee's ADDRESS has changed
    /// since the welcome went out.
    ///
    /// <para>§326bf moved the <c>welcome</c> send's idempotency to <c>welcome:{participantId}</c>
    /// because an address is not an identity: a typo correction or a re-import that changes the case
    /// leaves the stored row under the OLD address. An address-only clear misses it, deletes nothing
    /// and reports success — while the re-send stays suppressed by the row that survived. A reset
    /// that silently does half its job then reads as passing, which is the failure this pins
    /// shut.</para>
    /// </summary>
    [Fact]
    public async Task Welcome_reset_finds_the_ledger_row_even_when_the_ADDRESS_has_since_changed()
    {
        var (db, ev, aid, _, _) = await SeedAsync();

        // Re-stamp the existing welcome row as having gone to the person's FORMER address, keeping
        // its occasion key — exactly what a typo fix or a re-import leaves behind.
        var row = db.SentReminders.Single(s => s.ReminderType == "welcome");
        row.RecipientEmail = "old.address@example.test";
        await db.SaveChangesAsync();

        var svc = new AttendeeOnboardingResetService(db);
        var r = await svc.ResetAsync(ev, aid, resetWelcome: true, resetMasterClass: false, resetParty: false);

        Assert.True(r.Ok);
        Assert.DoesNotContain(db.SentReminders, s => s.ReminderType == "welcome");
        Assert.True(r.LedgerRowsRemoved >= 1);   // 0 before the fix — the silent no-op
    }

    [Fact]
    public async Task All_three_together_leaves_a_fresh_attendee()
    {
        var (db, ev, aid, pid, _) = await SeedAsync();
        var svc = new AttendeeOnboardingResetService(db);

        var r = await svc.ResetAsync(ev, aid, true, true, true);

        Assert.True(r.Ok);
        Assert.Empty(db.MasterClassSignups);
        Assert.Empty(db.PartyRsvps);
        Assert.Null(db.Attendees.Single(x => x.Id == aid).MasterClassInviteSentAt);
        Assert.All(db.Tasks.Where(t => t.AssignedParticipantId == pid),
            t => Assert.Equal(TaskState.Open, t.State));
        // The attendee row itself SURVIVES — this is a re-onboarding reset, not a deletion.
        Assert.NotNull(db.Attendees.SingleOrDefault(x => x.Id == aid));
        Assert.Equal(TicketStatus.TwoDay, db.Attendees.Single(x => x.Id == aid).TicketStatus);
    }

    /// <summary>Nothing ticked must be a no-op, not a silent full reset.</summary>
    [Fact]
    public async Task Nothing_selected_changes_nothing()
    {
        var (db, ev, aid, _, _) = await SeedAsync();
        var svc = new AttendeeOnboardingResetService(db);

        var r = await svc.ResetAsync(ev, aid, false, false, false);

        Assert.False(r.Ok);
        Assert.Single(db.MasterClassSignups);
        Assert.Single(db.PartyRsvps);
    }

    [Fact]
    public async Task Unknown_attendee_is_reported_not_thrown()
    {
        var (db, ev, _, _, _) = await SeedAsync();
        var svc = new AttendeeOnboardingResetService(db);

        var r = await svc.ResetAsync(ev, attendeeId: 987654, true, true, true);

        Assert.False(r.Ok);
        Assert.Contains("not found", r.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
