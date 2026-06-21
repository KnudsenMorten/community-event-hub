using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The waitlist→seat promotion email (<see cref="MasterClassPromotionEmailService"/>):
/// sends through the standard <see cref="IEmailSender"/> (so it's ring-gated +
/// EmailLog-recorded in production), addresses the promoted attendee, and is
/// idempotent via <c>PromotionNotifiedAt</c>. EF in-memory + a capturing sender.
/// </summary>
public class MasterClassPromotionEmailTests
{
    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext context) => new D();
    }

    private static async Task<(int ev, int mc)> SeedAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Deep Dive MC", Type = SessionType.CommunityMasterClass, MasterClassCapacity = 1 };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        return (e.Id, s.Id);
    }

    private static async Task<int> AttendeeAsync(CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email)
    {
        var a = new Attendee { EventId = ev, Email = email, FirstName = "F", LastName = "L", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return a.Id;
    }

    [Fact]
    public async Task Promotion_emails_the_promoted_attendee_once()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db);
        var a1 = await AttendeeAsync(db, ev, "a1@x.dk");
        var a2 = await AttendeeAsync(db, ev, "a2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, a1, mc);   // confirmed
        await svc.SignUpAsync(ev, a2, mc);   // waitlisted

        var promo = await svc.RemoveAsync(ev, a1, mc);   // a2 promoted
        Assert.NotNull(promo!.PromotedSignupId);

        var sender = new CapturingEmailSender();
        var email = new MasterClassPromotionEmailService(db, sender, new NoOpContext(), svc);

        var sent = await email.SendPromotionAsync(promo.PromotedSignupId!.Value, "https://hub.test");
        Assert.True(sent);
        var msg = Assert.Single(sender.Sent);
        Assert.Equal("a2@x.dk", msg.To);
        Assert.Contains("Deep Dive MC", msg.Subject);

        // Idempotent: a second send is a no-op (PromotionNotifiedAt stamped).
        var again = await email.SendPromotionAsync(promo.PromotedSignupId!.Value, "https://hub.test");
        Assert.False(again);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task No_send_for_a_waitlisted_or_unknown_signup()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db);
        var a1 = await AttendeeAsync(db, ev, "a1@x.dk");
        var a2 = await AttendeeAsync(db, ev, "a2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, a1, mc);   // confirmed
        var wl = await svc.SignUpAsync(ev, a2, mc);   // waitlisted

        var sender = new CapturingEmailSender();
        var email = new MasterClassPromotionEmailService(db, sender, new NoOpContext(), svc);

        // The waitlisted signup is not a confirmed seat -> no promotion email.
        var wlSignupId = db.MasterClassSignups.First(x => x.AttendeeId == a2).Id;
        Assert.False(await email.SendPromotionAsync(wlSignupId, "https://hub.test"));
        Assert.False(await email.SendPromotionAsync(999999, "https://hub.test"));
        Assert.Empty(sender.Sent);
    }
}
