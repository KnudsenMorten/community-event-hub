using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Master Class lifecycle emails (<see cref="MasterClassEmailService"/>): confirmed,
/// waitlist-with-terms, cancellation, and the opt-in ~1-month-before calendar
/// reminder (.ics, idempotent). Capturing sender; EF in-memory.
/// </summary>
public class MasterClassEmailServiceTests
{
    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private static async Task<(int ev, int mc, int att)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, bool consent = false)
    {
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10) };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        var a = new Attendee { EventId = e.Id, Email = "p@x.dk", FirstName = "Pat", LastName = "Lee", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return (e.Id, s.Id, a.Id);
    }

    private static (MasterClassEmailService email, CapturingEmailSender sender, MasterClassSignupService svc) Build(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var svc = new MasterClassSignupService(db);
        var sender = new CapturingEmailSender();
        return (new MasterClassEmailService(db, sender, new NoOpContext(), svc), sender, svc);
    }

    [Fact]
    public async Task Confirmed_email_has_title_and_ics_link()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var (email, sender, svc) = Build(db);
        var r = await svc.SignUpAsync(ev, att, mc);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;

        await email.SendConfirmedAsync(id, "https://hub.test");
        var m = Assert.Single(sender.Messages);
        Assert.Equal("p@x.dk", m.To);
        Assert.Contains("Deep Dive MC", m.Subject);
        Assert.Contains("MyMasterClass.ics", m.Html);   // .ics download link
    }

    [Fact]
    public async Task Waitlist_email_includes_terms_when_consented()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        // Make it full so the next signer waitlists; have 'att' hold a seat then waitlist a 2nd.
        var mc2 = new Session { EventId = ev, Title = "MC2", Type = SessionType.MasterClass, MasterClassCapacity = 0 };
        db.Sessions.Add(mc2); await db.SaveChangesAsync();
        var (email, sender, svc) = Build(db);
        await svc.SignUpAsync(ev, att, mc);                              // confirmed in mc
        await svc.SignUpAsync(ev, att, mc2.Id, autoSwitchConsent: true); // waitlist mc2 (consented)
        var id = (await svc.SignupIdAsync(ev, att, mc2.Id))!.Value;

        await email.SendWaitlistedAsync(id, "https://hub.test");
        var m = Assert.Single(sender.Messages);
        Assert.Contains("waitlist", m.Subject, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancelled", m.Html, System.StringComparison.OrdinalIgnoreCase);  // the auto-switch terms
    }

    [Fact]
    public async Task Waitlist_email_states_queue_position()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var mc2 = new Session { EventId = ev, Title = "MC2", Type = SessionType.MasterClass, MasterClassCapacity = 0 };
        db.Sessions.Add(mc2); await db.SaveChangesAsync();
        var (email, sender, svc) = Build(db);
        var r = await svc.SignUpAsync(ev, att, mc2.Id);   // cap 0 → waitlisted at position #1
        var id = (await svc.SignupIdAsync(ev, att, mc2.Id))!.Value;

        // The page passes the queue position (from the SignUp result) into the email.
        await email.SendWaitlistedAsync(id, "https://hub.test", r.Signup!.WaitlistPosition);
        var m = Assert.Single(sender.Messages);
        Assert.Contains("#1", m.Html);                                              // their place in the queue
        Assert.Contains("on the waitlist", m.Html, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_email_sends()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var (email, sender, _) = Build(db);
        await email.SendCancelledAsync(ev, "p@x.dk", "Pat", "Lee", "Deep Dive MC", "https://hub.test", att);
        var m = Assert.Single(sender.Sent);
        Assert.Contains("Master Class cancelled", m.Subject);
    }

    [Fact]
    public async Task Selection_invite_goes_to_two_day_only_is_tracked_and_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);           // att is a 2-day attendee
        var oneDay = new Attendee { EventId = ev, Email = "one@x.dk", FirstName = "One", LastName = "Day", TicketStatus = TicketStatus.Other };
        db.Attendees.Add(oneDay); await db.SaveChangesAsync();
        var (email, sender, svc) = Build(db);

        Assert.False(await email.SendSelectionInviteAsync(oneDay.Id, "https://hub.test"));  // not 2-day -> skip
        Assert.True(await email.SendSelectionInviteAsync(att, "https://hub.test"));
        var m = Assert.Single(sender.Messages);
        Assert.Contains("Choose your Master Class", m.Subject);
        Assert.Contains("not confirmed until you complete the selection", m.Html);
        Assert.Contains("MyMasterClass?t=", m.Html);       // secure self-service link
        Assert.NotNull(db.Attendees.Find(att)!.MasterClassInviteSentAt);

        Assert.False(await email.SendSelectionInviteAsync(att, "https://hub.test"));        // already sent
        Assert.True(await email.SendSelectionInviteAsync(att, "https://hub.test", force: true)); // resend
        var (eligible, invited, notInvited) = await svc.InviteStatsAsync(ev);
        Assert.Equal(1, eligible); Assert.Equal(1, invited); Assert.Equal(0, notInvited);
    }

    [Fact]
    public void BuildIcs_falls_back_to_all_day_on_the_pre_day()
    {
        var ics = MasterClassEmailService.BuildIcs("hub.test", 5, "MC", null, null, new DateOnly(2027, 2, 9));
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20270209", ics);
        Assert.Contains("SUMMARY:Master Class — MC", ics);
    }

    [Fact]
    public async Task Month_reminder_sends_ics_once_for_opted_in_confirmed()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var (email, sender, svc) = Build(db);
        await svc.SignUpAsync(ev, att, mc);
        await svc.SetMonthReminderOptInAsync(ev, att, true);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;

        Assert.True(await email.SendMonthReminderAsync(id, "hub.test"));
        Assert.Equal("master-class.ics".Length > 0, sender.LastIcs is not null);  // .ics attached
        Assert.False(await email.SendMonthReminderAsync(id, "hub.test"));         // idempotent
        Assert.Single(sender.Sent);
    }
}
