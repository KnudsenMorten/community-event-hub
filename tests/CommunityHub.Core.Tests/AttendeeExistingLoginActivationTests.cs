using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔒 §707.12 — BUYING A TICKET MUST ACTIVATE AN EXISTING LOGIN, OR ITS MAGIC LINK IS DEAD.
/// </summary>
/// <remarks>
/// <para>Reported live by the operator 2026-07-30: he bought a 2-day ticket for an address that was
/// ALREADY in the database, received the selection invite, pressed its magic-link button and landed
/// on the PIN sign-in page instead of the hub.</para>
///
/// <para>The grant was healthy. The ACCOUNT was not: provisioning skipped every address it already
/// knew, so the person stayed <c>IsActive = false</c> — and
/// <c>EmailMagicLinkService.ResolveAsync</c> refuses an inactive participant. The mail promised a
/// one-tap sign-in that could never work.</para>
///
/// <para>⚠️ This is the 2026-08-11 ticket-launch path: ANY buyer the hub already knows (a speaker, a
/// volunteer, a past import, a pre-selection queue row) would have hit it.</para>
/// </remarks>
public sealed class AttendeeExistingLoginActivationTests
{
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static AttendeeWelcomeProvisioningService Service(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now),
            NullLogger<AttendeeWelcomeProvisioningService>.Instance);

    [Fact]
    public async Task An_existing_inactive_login_is_activated_when_they_buy_a_two_day_ticket()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);

        // The person is ALREADY known — e.g. a Sessionize pre-selection row — and cannot sign in.
        db.Participants.Add(new Participant
        {
            EventId = ev, Email = "known@x.dk", FullName = "Known Person",
            Role = ParticipantRole.Speaker,
            IsActive = false,
            LifecycleState = ParticipantLifecycleState.Inactive,
        });
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-1", Email = "known@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        await Service(db).ProvisionAsync(ev);

        var p = await db.Participants.FirstAsync(x => x.Email == "known@x.dk");
        Assert.True(p.IsActive, "the magic link in their invite cannot resolve while this is false");
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);
        // 🔑 Their ROLE is untouched — a speaker who buys a ticket is still a speaker.
        Assert.Equal(ParticipantRole.Speaker, p.Role);
    }

    /// <summary>
    /// 🔒 §707.14 POLICY — A TICKET PURCHASE CLEARS AN ORGANIZER DEACTIVATION.
    /// </summary>
    /// <remarks>
    /// This INVERTS the original assertion. The first cut refused to override a human decision;
    /// the operator chose the opposite on 2026-07-30 (*"i agree it should flip it on"*), because
    /// the alternative is someone holding a PAID ticket who silently cannot sign in, with a dead
    /// magic link as the only symptom — which is how this whole thread began.
    ///
    /// <para>The tombstone is CLEARED, not bypassed, so the row stops claiming "an organizer turned
    /// this off" once a purchase has overruled it. And per §707.15 the welcome is re-armed, so they
    /// are onboarded again rather than returning live but silent.</para>
    /// </remarks>
    [Fact]
    public async Task A_ticket_purchase_clears_an_organizer_deactivation_and_re_arms_the_welcome()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);

        db.Participants.Add(new Participant
        {
            EventId = ev, Email = "banned@x.dk", FullName = "Previously Blocked",
            Role = ParticipantRole.Attendee,
            IsActive = false,
            LifecycleState = ParticipantLifecycleState.Inactive,
            DeactivatedByOrganizerAt = Now.AddDays(-3),
            WelcomeWithLoginSentAt = Now.AddDays(-20),
        });
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-2", Email = "banned@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
            MasterClassInviteSentAt = Now.AddDays(-19),
        });
        db.SentReminders.Add(new SentReminder
        {
            EventId = ev, RecipientEmail = "banned@x.dk", ReminderType = "welcome",
            OccasionKey = "welcome", SentAt = Now.AddDays(-20),
        });
        await db.SaveChangesAsync();

        await Service(db).ProvisionAsync(ev);

        var p = await db.Participants.FirstAsync(x => x.Email == "banned@x.dk");
        Assert.True(p.IsActive, "a paid ticket must not leave the buyer locked out");
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);
        Assert.Null(p.DeactivatedByOrganizerAt);

        // §707.15 — re-onboarded, not merely re-enabled.
        Assert.Null(p.WelcomeWithLoginSentAt);
        Assert.Empty(db.SentReminders.Where(s => s.ReminderType == "welcome"));
        Assert.Null((await db.Attendees.FirstAsync()).MasterClassInviteSentAt);
    }

    /// <summary>
    /// 🔒 §707.12 — BUY → CANCEL → BUY AGAIN. Operator 2026-07-30: *"this scenario would be very
    /// common"*.
    /// </summary>
    /// <remarks>
    /// The middle step is the dangerous one. <c>holders</c> is keyed on TicketStatus and does NOT
    /// filter MirrorState, so a CANCELLED ticket is still in that set — re-activating from it would
    /// undo the §216 cancelled-ticket lockout on the next 10-minute sync, letting someone back in
    /// minutes after their cancellation locked them out. Entitlement must be "≥1 ACTIVE ticket".
    /// </remarks>
    [Fact]
    public async Task Cancelled_stays_locked_out_but_a_re_purchase_restores_the_login()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);

        db.Participants.Add(new Participant
        {
            EventId = ev, Email = "repeat@x.dk", FullName = "Repeat Buyer",
            Role = ParticipantRole.Attendee,
            // §216 locked them out when they cancelled. Note: a ticket-sync lockout deliberately
            // does NOT set DeactivatedByOrganizerAt, so only the ticket state can speak for it.
            IsActive = false,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        var cancelled = new Attendee
        {
            EventId = ev, BackstageTicketId = "t-9", Email = "repeat@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled,
        };
        db.Attendees.Add(cancelled);
        await db.SaveChangesAsync();

        // STEP 1 — cancelled only: the sync must NOT hand the login back.
        await Service(db).ProvisionAsync(ev);
        var afterCancel = await db.Participants.FirstAsync(x => x.Email == "repeat@x.dk");
        Assert.False(afterCancel.IsActive, "a cancelled ticket must not resurrect the login");

        // STEP 2 — they buy again: a NEW active mirror row lands beside the cancelled one.
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-10", Email = "repeat@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        await Service(db).ProvisionAsync(ev);

        var afterRebuy = await db.Participants.FirstAsync(x => x.Email == "repeat@x.dk");
        Assert.True(afterRebuy.IsActive, "the re-purchase must bring the login (and its magic link) back");
        Assert.Equal(ParticipantLifecycleState.Active, afterRebuy.LifecycleState);
    }

    /// <summary>
    /// 🔒 §707.12 — RE-ASSIGNMENT: the ticket moves from one person to another. Operator
    /// 2026-07-30: *"3 becomes inactive and 4 becomes active"*.
    /// </summary>
    /// <remarks>
    /// Two halves, owned by two different services, and this asserts provisioning's half:
    /// the GAINER must end up fully login-capable (<c>IsActive</c> AND <c>LifecycleState</c>),
    /// while the LOSER must not be handed access just because their address still appears on the
    /// old cancelled/reassigned row. The lock-out half is <c>AttendeeTicketSyncService</c>'s §216
    /// reconcile, which flips <c>IsActive</c> for both the old and the new address.
    ///
    /// <para>🔑 Provisioning has to set <b>LifecycleState</b> too: §216 deliberately toggles only
    /// <c>IsActive</c>, and PIN sign-in requires BOTH (magic-link resolution checks only
    /// <c>IsActive</c>, so the two gates can otherwise disagree about the same person).</para>
    /// </remarks>
    [Fact]
    public async Task A_reassigned_ticket_activates_the_new_holder_and_not_the_old_one()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);

        // Both people already exist as logins; neither can sign in yet.
        foreach (var mail in new[] { "three@x.dk", "four@x.dk" })
        {
            db.Participants.Add(new Participant
            {
                EventId = ev, Email = mail, FullName = mail,
                Role = ParticipantRole.Attendee,
                IsActive = false,
                LifecycleState = ParticipantLifecycleState.Inactive,
            });
        }

        // The ticket USED to be attendee 3's (now cancelled on that address) and is now 4's.
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-old", Email = "three@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled,
        });
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-old", Email = "four@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        await Service(db).ProvisionAsync(ev);

        var three = await db.Participants.FirstAsync(x => x.Email == "three@x.dk");
        var four = await db.Participants.FirstAsync(x => x.Email == "four@x.dk");

        Assert.False(three.IsActive, "the previous holder no longer has an active ticket");
        Assert.True(four.IsActive, "the new holder must be able to use the magic link in their invite");
        Assert.Equal(ParticipantLifecycleState.Active, four.LifecycleState);
    }

    [Fact]
    public async Task An_already_active_login_is_left_alone()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);

        db.Participants.Add(new Participant
        {
            EventId = ev, Email = "fine@x.dk", FullName = "Fine Person",
            Role = ParticipantRole.Attendee,
            IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t-3", Email = "fine@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        // No new participant is created for an address that already exists.
        Assert.Empty(await Service(db).ProvisionAsync(ev));

        var p = await db.Participants.FirstAsync(x => x.Email == "fine@x.dk");
        Assert.True(p.IsActive);
    }
}
