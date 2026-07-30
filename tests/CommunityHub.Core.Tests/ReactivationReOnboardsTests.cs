using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔒 §707.15 — INACTIVE → ACTIVE MUST RE-SEND THE WELCOME.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-30: *"so both an organizer change and order, must send the welcome
/// again"* and *"inactive to active must send email again"* — a returning person is treated as a
/// new user who must be onboarded again.</para>
///
/// <para>The ORDER-driven half already worked (§253 G16 re-engage on the ticket sync). The
/// ORGANIZER half did not: a manual re-activation is not a cancellation, so that re-engage never
/// fired and the "already sent" stamps survived — the person came back with no welcome and no
/// magic link. That asymmetry is what he hit live, where participant 82 had to be re-armed by
/// hand.</para>
///
/// <para>These assert the STAMPS, not a send: clearing them is the whole mechanism, and the
/// reconcile jobs then send through the normal ring-gated path rather than a hand-rolled send.</para>
/// </remarks>
public sealed class ReactivationReOnboardsTests
{
    private const int EventId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"reonboard-{Guid.NewGuid():N}")
            .Options);

    private static ParticipantDeactivationService Sut(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new AuditTrailService(db, new FixedClock()));

    private static async Task<Participant> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role, string email, bool active)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        });
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = "Returning Person", Role = role,
            IsActive = active,
            LifecycleState = active ? ParticipantLifecycleState.Active : ParticipantLifecycleState.Inactive,
            DeactivatedByOrganizerAt = active ? null : FixedClock.Now.AddDays(-2),
            // They were welcomed before they were switched off.
            WelcomeWithLoginSentAt = FixedClock.Now.AddDays(-10),
        };
        db.Participants.Add(p);
        db.SentReminders.Add(new SentReminder
        {
            EventId = EventId, RecipientEmail = email, ReminderType = "welcome",
            OccasionKey = "welcome", SentAt = FixedClock.Now.AddDays(-10),
        });
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>
    /// 🔒 EVERY role, not just attendees. Operator 2026-07-30: *"for any roles, not only attendees.
    /// if a volunteer drops out and rejoins (active) or sponsor event coordinator"*.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    [InlineData(ParticipantRole.Attendee)]
    public async Task Reactivating_any_role_re_arms_the_welcome(ParticipantRole role)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, role, "back@x.dk", active: false);

        Assert.True((await Sut(db).ReactivateAsync(EventId, p.Id, "org@test")).Found);

        var after = await db.Participants.FirstAsync(x => x.Id == p.Id);
        Assert.True(after.IsActive);
        Assert.Null(after.WelcomeWithLoginSentAt);
        Assert.Empty(db.SentReminders.Where(s => s.ReminderType == "welcome"));
    }

    /// <summary>
    /// A sponsor EVENT COORDINATOR is the case he named explicitly — the flag must survive the
    /// round trip, because the §7c audience rule mails coordinators and nobody else.
    /// </summary>
    [Fact]
    public async Task A_sponsor_event_coordinator_returns_re_armed_and_still_a_coordinator()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Sponsor, "coord@x.dk", active: false);
        p.IsEventCoordinator = true;
        p.SponsorCompanyId = "acme";
        await db.SaveChangesAsync();

        await Sut(db).ReactivateAsync(EventId, p.Id, "org@test");

        var after = await db.Participants.FirstAsync(x => x.Id == p.Id);
        Assert.True(after.IsActive);
        Assert.True(after.IsEventCoordinator);
        Assert.Null(after.WelcomeWithLoginSentAt);
    }

    [Fact]
    public async Task Reactivating_a_two_day_attendee_also_re_arms_the_selection_invite()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Attendee, "back2day@x.dk", active: false);
        db.Attendees.Add(new Attendee
        {
            EventId = EventId, BackstageTicketId = "t-1", Email = "back2day@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
            MasterClassInviteSentAt = FixedClock.Now.AddDays(-9),
        });
        db.SentReminders.Add(new SentReminder
        {
            EventId = EventId, RecipientEmail = "back2day@x.dk",
            ReminderType = "pending-master-class-selection",
            OccasionKey = "pendingmc:back2day@x.dk", SentAt = FixedClock.Now.AddDays(-3),
        });
        await db.SaveChangesAsync();

        await Sut(db).ReactivateAsync(EventId, p.Id, "org@test");

        var a = await db.Attendees.FirstAsync();
        Assert.Null(a.MasterClassInviteSentAt);   // the sync re-sends the 2-day welcome
        Assert.Empty(db.SentReminders.Where(s => s.ReminderType == "pending-master-class-selection"));
    }

    /// <summary>
    /// 🔒 A HELD SEAT means the reassignment-VALIDATION mail applies, not a fresh "pick your Master
    /// Class" invite — the same rule §253 G16 uses on the order-driven path. Re-arming here would
    /// tell someone to choose a class they already hold.
    /// </summary>
    [Fact]
    public async Task A_confirmed_seat_suppresses_re_arming_the_selection_invite()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Attendee, "seat@x.dk", active: false);
        var att = new Attendee
        {
            EventId = EventId, BackstageTicketId = "t-2", Email = "seat@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
            MasterClassInviteSentAt = FixedClock.Now.AddDays(-9),
        };
        db.Attendees.Add(att);
        await db.SaveChangesAsync();

        var session = new Session { EventId = EventId, Title = "MC", SessionizeId = "s1" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = EventId, AttendeeId = att.Id, SessionId = session.Id,
            Status = MasterClassSignupStatus.Confirmed,
        });
        await db.SaveChangesAsync();

        await Sut(db).ReactivateAsync(EventId, p.Id, "org@test");

        var a = await db.Attendees.FirstAsync();
        Assert.NotNull(a.MasterClassInviteSentAt);
    }

    /// <summary>Idempotent: re-activating someone already active must not re-welcome them.</summary>
    [Fact]
    public async Task Reactivating_an_already_active_person_does_not_re_welcome()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Speaker, "fine@x.dk", active: true);

        await Sut(db).ReactivateAsync(EventId, p.Id, "org@test");

        var after = await db.Participants.FirstAsync(x => x.Id == p.Id);
        Assert.NotNull(after.WelcomeWithLoginSentAt);
        Assert.Single(db.SentReminders.Where(s => s.ReminderType == "welcome"));
    }
}
