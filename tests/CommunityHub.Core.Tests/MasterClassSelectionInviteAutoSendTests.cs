using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §241 — the Master-Class selection invite (the de-facto 2-day welcome, §215) is now
/// AUTO-SENT by the sync jobs (AttendeeBackstageSyncJob 10-min pull + ZohoWebhookDrainJob
/// webhook path) to every eligible-not-invited ACTIVE 2-day attendee, and:
/// <list type="bullet">
/// <item><see cref="Attendee.MasterClassInviteSentAt"/> stamps ONLY on an actually-DELIVERED
/// send (the <see cref="IEmailDeliveryOutcome"/> seam) — a ring-dropped invite stays in
/// <see cref="MasterClassSignupService.EligibleNotInvitedIdsAsync"/> so the next sweep
/// auto-retries it once rings widen;</item>
/// <item>the send is tagged FeatureKey <c>welcome-email</c> + AttendeeWelcome (the §217
/// <c>Email:AttendeeWelcomeMaxReleaseRing</c> cap) with the recipient's provisioned
/// Participant id, so the sender ring-gates on the PERSON;</item>
/// <item>the sweep the jobs run (EligibleNotInvitedIdsAsync → SendSelectionInviteAsync)
/// reaches ONLY eligible-not-invited ACTIVE 2-day attendees and is idempotent.</item>
/// </list>
/// Legacy wiring without the seam keeps the old always-stamp behaviour (pinned by the
/// existing MasterClassEmailServiceTests / AttendeeLifecycleEndToEndTests).
/// </summary>
public sealed class MasterClassSelectionInviteAutoSendTests
{
    private const string Origin = "https://hub.test";

    /// <summary>Captures the ambient EmailContext the service sets around the send.</summary>
    private sealed class RecordingContext : IEmailContextAccessor
    {
        public EmailContext? Current { get; private set; }
        public EmailContext? LastSet { get; private set; }
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) { Current = c; LastSet = c; return new D(); }
    }

    /// <summary>A settable delivered-vs-dropped seam (what BrevoEmailSender would report).</summary>
    private sealed class MutableOutcome : IEmailDeliveryOutcome
    {
        public bool LastSendDelivered { get; set; }
        public string? LastDropReason => LastSendDelivered ? null : "ring-drop";
        public void Record(bool delivered, string? reason = null) { }
    }

    private static async Task<int> SeedEventAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static async Task<int> SeedAttendeeAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email,
        TicketStatus status = TicketStatus.TwoDay,
        MirrorState mirror = MirrorState.Active,
        DateTimeOffset? invitedAt = null)
    {
        var a = new Attendee
        {
            EventId = ev, Email = email, FirstName = "Pat", LastName = "Lee",
            TicketStatus = status, MirrorState = mirror, MasterClassInviteSentAt = invitedAt,
        };
        db.Attendees.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    // ------------------------------------------------------------------
    // Delivery-gated stamp: ring-dropped ⇒ no stamp ⇒ auto-retry eligible.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ring_dropped_invite_does_not_stamp_and_a_later_delivered_send_does()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var att = await SeedAttendeeAsync(db, ev, "p@x.dk");
        var sender = new CapturingEmailSender();
        var signups = new MasterClassSignupService(db);
        var outcome = new MutableOutcome { LastSendDelivered = false };
        var svc = new MasterClassEmailService(db, sender, new RecordingContext(), signups, outcome: outcome);

        // The transport was invoked but reported a DROP (ring/kill-switch) ⇒ NOT sent:
        // no stamp, and the attendee STAYS eligible for the next sync sweep.
        Assert.False(await svc.SendSelectionInviteAsync(att, Origin));
        Assert.Single(sender.Messages);                                   // dispatched, but dropped
        Assert.Null(db.Attendees.Find(att)!.MasterClassInviteSentAt);
        Assert.Equal(att, Assert.Single(await signups.EligibleNotInvitedIdsAsync(ev)));

        // Rings widened ⇒ the SAME (no force) send now delivers ⇒ stamped + no longer eligible.
        outcome.LastSendDelivered = true;
        Assert.True(await svc.SendSelectionInviteAsync(att, Origin));
        Assert.Equal(2, sender.Messages.Count);
        Assert.NotNull(db.Attendees.Find(att)!.MasterClassInviteSentAt);
        Assert.Empty(await signups.EligibleNotInvitedIdsAsync(ev));

        // Idempotent from here: a further sweep send is suppressed by the stamp.
        Assert.False(await svc.SendSelectionInviteAsync(att, Origin));
        Assert.Equal(2, sender.Messages.Count);
    }

    [Fact]
    public async Task Delivered_send_with_the_seam_stamps_normally()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var att = await SeedAttendeeAsync(db, ev, "p@x.dk");
        var sender = new CapturingEmailSender();
        var svc = new MasterClassEmailService(
            db, sender, new RecordingContext(), new MasterClassSignupService(db),
            outcome: new MutableOutcome { LastSendDelivered = true });

        Assert.True(await svc.SendSelectionInviteAsync(att, Origin));
        Assert.Single(sender.Messages);
        Assert.NotNull(db.Attendees.Find(att)!.MasterClassInviteSentAt);
    }

    // ------------------------------------------------------------------
    // §217/§241 tagging: attendee welcome + welcome-email + the person's id.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Selection_invite_is_tagged_attendee_welcome_keyed_on_the_recipients_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var att = await SeedAttendeeAsync(db, ev, "p@x.dk");
        // The provisioned login Participant (what AttendeeWelcomeProvisioningService mints).
        var p = new Participant
        {
            EventId = ev, Email = "p@x.dk", FullName = "Pat Lee",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var ctx = new RecordingContext();
        var svc = new MasterClassEmailService(
            db, new CapturingEmailSender(), ctx, new MasterClassSignupService(db));
        Assert.True(await svc.SendSelectionInviteAsync(att, Origin));

        var c = ctx.LastSet;
        Assert.NotNull(c);
        Assert.True(c!.AttendeeWelcome);                 // §217 cap applies
        Assert.Equal("welcome-email", c.FeatureKey);     // ring-gated as any welcome
        Assert.Equal(p.Id, c.ParticipantId);             // gate keys on the PERSON
        Assert.Equal(ev, c.EventId);
    }

    // ------------------------------------------------------------------
    // The sweep the sync jobs run: eligible-not-invited ACTIVE 2-day only.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sync_sweep_sends_only_to_eligible_not_invited_active_two_day_attendees()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var eligible = await SeedAttendeeAsync(db, ev, "new@x.dk");
        await SeedAttendeeAsync(db, ev, "done@x.dk", invitedAt: DateTimeOffset.UtcNow);   // already invited
        await SeedAttendeeAsync(db, ev, "gone@x.dk", mirror: MirrorState.Cancelled);      // soft-cancelled
        await SeedAttendeeAsync(db, ev, "oneday@x.dk", status: TicketStatus.Other);       // not 2-day

        var sender = new CapturingEmailSender();
        var signups = new MasterClassSignupService(db);
        var svc = new MasterClassEmailService(
            db, sender, new RecordingContext(), signups,
            outcome: new MutableOutcome { LastSendDelivered = true });

        // Exactly what AttendeeBackstageSyncJob / ZohoWebhookDrainJob run per sweep.
        var sent = 0;
        foreach (var id in await signups.EligibleNotInvitedIdsAsync(ev))
            if (await svc.SendSelectionInviteAsync(id, Origin)) sent++;

        Assert.Equal(1, sent);
        var m = Assert.Single(sender.Messages);
        Assert.Equal("new@x.dk", m.To);
        Assert.NotNull(db.Attendees.Find(eligible)!.MasterClassInviteSentAt);

        // A second sweep (the next 10-minute run) finds nobody and sends nothing.
        Assert.Empty(await signups.EligibleNotInvitedIdsAsync(ev));
        Assert.Single(sender.Messages);
    }

    [Fact]
    public async Task Ring_dropped_sweep_recipient_is_retried_by_the_next_sweep()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var att = await SeedAttendeeAsync(db, ev, "p@x.dk");
        var sender = new CapturingEmailSender();
        var signups = new MasterClassSignupService(db);
        var outcome = new MutableOutcome { LastSendDelivered = false };
        var svc = new MasterClassEmailService(db, sender, new RecordingContext(), signups, outcome: outcome);

        // Sweep 1: everything ring-drops ⇒ nothing counts as sent, everyone stays eligible.
        var sent = 0;
        foreach (var id in await signups.EligibleNotInvitedIdsAsync(ev))
            if (await svc.SendSelectionInviteAsync(id, Origin)) sent++;
        Assert.Equal(0, sent);
        Assert.Single(await signups.EligibleNotInvitedIdsAsync(ev));

        // Operator widens the ring ⇒ sweep 2 delivers + stamps without any manual resend.
        outcome.LastSendDelivered = true;
        foreach (var id in await signups.EligibleNotInvitedIdsAsync(ev))
            if (await svc.SendSelectionInviteAsync(id, Origin)) sent++;
        Assert.Equal(1, sent);
        Assert.NotNull(db.Attendees.Find(att)!.MasterClassInviteSentAt);
        Assert.Empty(await signups.EligibleNotInvitedIdsAsync(ev));
    }
}
