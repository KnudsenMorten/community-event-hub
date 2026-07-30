using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §252 gap audit F5 + F6 — ONE ring for the whole Master Class email funnel:
/// <list type="bullet">
/// <item>F5: every MC lifecycle send (confirmed, waitlisted, cancelled, promotion)
/// tags FeatureKey <c>welcome-email</c> — the same ring as the §241 selection invite
/// and the §243 ticket-cancellation — so raising ONE ring at go-live can never split
/// the funnel (invited but never confirmed, or confirmed-mail without invites). The
/// old <c>masterclass-invites</c> key is gone. These sends are NOT tagged
/// AttendeeWelcome (the §217 cap is only for the mass welcome).</item>
/// <item>F6: each send resolves the recipient's Participant id by email (Attendee
/// role preferred) and carries it in the EmailContext, so the §234 participant-keyed
/// ring gate can engage and a confirm/waitlist mail is never fail-closed dropped for
/// an address the sender alone cannot resolve.</item>
/// </list>
/// </summary>
public sealed class MasterClassRingUnificationTests
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

    private static async Task<(int ev, int mc, int att)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        var a = new Attendee { EventId = e.Id, Email = "p@x.dk", FirstName = "Pat", LastName = "Lee", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return (e.Id, s.Id, a.Id);
    }

    /// <summary>The provisioned login Participant the ring gate should key on.</summary>
    private static async Task<int> SeedParticipantAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email = "p@x.dk")
    {
        var p = new Participant
        {
            EventId = ev, Email = email, FullName = "Pat Lee",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static void AssertUnifiedContext(EmailContext? c, int ev, int? expectedPid)
    {
        Assert.NotNull(c);
        Assert.Equal("welcome-email", c!.FeatureKey);   // F5: ONE funnel ring
        Assert.Equal(expectedPid, c.ParticipantId);     // F6: gate keys on the PERSON
        Assert.False(c.AttendeeWelcome);                // §217 cap is only for the mass welcome
        Assert.Equal(ev, c.EventId);
    }

    [Fact]
    public async Task Confirmed_send_rides_welcome_email_with_the_resolved_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var pid = await SeedParticipantAsync(db, ev);
        var ctx = new RecordingContext();
        var svc = new MasterClassSignupService(db);
        var email = new MasterClassEmailService(db, new CapturingEmailSender(), ctx, svc);
        await svc.SignUpAsync(ev, att, mc);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;

        await email.SendConfirmedAsync(id, Origin);

        AssertUnifiedContext(ctx.LastSet, ev, pid);
    }

    [Fact]
    public async Task Waitlisted_send_rides_welcome_email_with_the_resolved_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var pid = await SeedParticipantAsync(db, ev);
        var ctx = new RecordingContext();
        var svc = new MasterClassSignupService(db);
        var email = new MasterClassEmailService(db, new CapturingEmailSender(), ctx, svc);
        await svc.SignUpAsync(ev, att, mc);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;

        await email.SendWaitlistedAsync(id, Origin);

        AssertUnifiedContext(ctx.LastSet, ev, pid);
    }

    [Fact]
    public async Task Cancelled_send_rides_welcome_email_with_the_resolved_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _, att) = await SeedAsync(db);
        var pid = await SeedParticipantAsync(db, ev);
        var ctx = new RecordingContext();
        var email = new MasterClassEmailService(
            db, new CapturingEmailSender(), ctx, new MasterClassSignupService(db));

        await email.SendCancelledAsync(ev, "p@x.dk", "Pat", "Lee", "Deep Dive MC", Origin, att);

        AssertUnifiedContext(ctx.LastSet, ev, pid);
    }

    [Fact]
    public async Task Promotion_send_rides_welcome_email_with_the_resolved_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var pid = await SeedParticipantAsync(db, ev);
        var ctx = new RecordingContext();
        var svc = new MasterClassSignupService(db);
        var promo = new MasterClassPromotionEmailService(db, new CapturingEmailSender(), ctx, svc);
        await svc.SignUpAsync(ev, att, mc);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;   // Confirmed, un-notified

        Assert.True(await promo.SendPromotionAsync(id, Origin));

        AssertUnifiedContext(ctx.LastSet, ev, pid);
    }

    [Fact]
    public async Task Unresolvable_recipient_still_sends_with_null_participant_fail_safe()
    {
        // No provisioned Participant for the address ⇒ pid resolves null (fail-safe,
        // never throws) — the send still goes out on the welcome-email ring and the
        // sender's own address→participant fallback applies.
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var ctx = new RecordingContext();
        var svc = new MasterClassSignupService(db);
        var email = new MasterClassEmailService(db, new CapturingEmailSender(), ctx, svc);
        await svc.SignUpAsync(ev, att, mc);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;

        await email.SendConfirmedAsync(id, Origin);

        AssertUnifiedContext(ctx.LastSet, ev, expectedPid: null);
    }
}
