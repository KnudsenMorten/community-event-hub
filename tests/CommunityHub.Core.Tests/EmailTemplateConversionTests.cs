using CommunityHub.Core.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Each previously-inline email now renders from its TEMPLATE (the generic shipped
/// publish-safe default here) instead of inline HTML — while every existing behaviour
/// (recipients, idempotency, ring/feature gate, .ics attachments, RingExempt) is
/// preserved. Mirrors <see cref="MasterClassConfirmedTemplateTests"/>: point the
/// provider at the shipped templates + an empty private dir so the GENERIC default is
/// exercised. The legacy inline-fallback (no provider) is still covered by the older
/// service tests.
/// </summary>
public sealed class EmailTemplateConversionTests
{
    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    /// <summary>A provider over the shipped generic templates + an empty private dir.</summary>
    private static EmailTemplateProvider Templates(string scope) =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ceh-no-private-{scope}"),
        }));

    private static async Task<(int ev, int mc, int att)> SeedMcAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int capacity = 5)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = capacity };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        var a = new Attendee { EventId = e.Id, Email = "p@x.dk", FirstName = "Pat", LastName = "Lee", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return (e.Id, s.Id, a.Id);
    }

    private static MasterClassEmailService McEmail(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender,
        MasterClassSignupService svc, string scope) =>
        new(db, sender, new NoOpContext(), svc, Templates(scope));

    // 1 -------------------------------------------------------------- waitlisted ----
    [Fact]
    public async Task Waitlisted_renders_from_template_with_terms()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedMcAsync(db);
        var mc2 = new Session { EventId = ev, Title = "MC2", Type = SessionType.MasterClass, MasterClassCapacity = 0 };
        db.Sessions.Add(mc2); await db.SaveChangesAsync();
        var sender = new CapturingEmailSender();
        var svc = new MasterClassSignupService(db);
        var email = McEmail(db, sender, svc, "mc-waitlisted");

        await svc.SignUpAsync(ev, att, mc);                              // confirmed in mc
        await svc.SignUpAsync(ev, att, mc2.Id, autoSwitchConsent: true); // waitlist mc2 (consented)
        var id = (await svc.SignupIdAsync(ev, att, mc2.Id))!.Value;

        await email.SendWaitlistedAsync(id, "https://hub.test");
        var m = Assert.Single(sender.Messages);
        Assert.Equal("p@x.dk", m.To);
        Assert.Contains("waitlist", m.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MC2", m.Subject);                          // {{masterClassTitle}}
        Assert.Contains("MyMasterClass?t=", m.Html);                // {{selfServiceUrl}}
        Assert.Contains("cancelled", m.Html, StringComparison.OrdinalIgnoreCase); // {{waitlistTerms}}
        Assert.Contains("The team", m.Html);                        // neutral sign-off
    }

    // 2 --------------------------------------------------------------- cancelled ----
    [Fact]
    public async Task Cancelled_renders_from_template()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedMcAsync(db);
        var sender = new CapturingEmailSender();
        var svc = new MasterClassSignupService(db);
        var email = McEmail(db, sender, svc, "mc-cancelled");

        await email.SendCancelledAsync(ev, "p@x.dk", "Pat", "Lee", "Deep Dive MC", "https://hub.test", att);
        var m = Assert.Single(sender.Messages);
        Assert.Equal("p@x.dk", m.To);
        Assert.Contains("Cancelled", m.Subject);
        Assert.Contains("Deep Dive MC", m.Subject);                 // {{masterClassTitle}}
        Assert.Contains("C 2027", m.Html);                          // {{eventDisplayName}}
        Assert.Contains("MyMasterClass?t=", m.Html);                // {{signupUrl}}
        // Operator 2026-06-24: dropped the "Questions? Email" + "The team" lines, and
        // the title already reads "… Master Class", so the body no longer says
        // "the Master Class" before it.
        Assert.DoesNotContain("The team", m.Html);
        Assert.DoesNotContain("the Master Class <strong>", m.Html);
    }

    // 3 ------------------------------------------------------------ reassignment ----
    [Fact]
    public async Task Reassignment_renders_from_template_with_held_class()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedMcAsync(db);
        var sender = new CapturingEmailSender();
        var svc = new MasterClassSignupService(db);
        var email = McEmail(db, sender, svc, "mc-reassign");

        Assert.True(await email.SendReassignmentValidationAsync(att, "Deep Dive MC", "https://hub.test"));
        var m = Assert.Single(sender.Messages);
        Assert.Contains("Validate your Master Class", m.Subject);
        Assert.Contains("C 2027", m.Subject);                       // {{eventDisplayName}}
        Assert.Contains("Deep Dive MC", m.Html);                    // {{heldMasterClass}} raw block
        Assert.Contains("MyMasterClass?t=", m.Html);                // {{selfServiceUrl}}
        Assert.Contains("The team", m.Html);
    }

    // 4a ------------------------------------------------------------------ offer ----
    [Fact]
    public async Task Offer_renders_from_offer_template()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, _) = await SeedMcAsync(db, capacity: 1);
        var a1 = await AttendeeAsync(db, ev, "a1@x.dk");
        var a2 = await AttendeeAsync(db, ev, "a2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, a1, mc);                          // confirmed
        await svc.SignUpAsync(ev, a2, mc, autoSwitchConsent: false);// waitlisted
        var promo = await svc.RemoveAsync(ev, a1, mc);              // a2 offered/promoted
        Assert.NotNull(promo!.PromotedSignupId);

        // Force an OFFERED state so the offer variant renders.
        var offered = db.MasterClassSignups.First(x => x.Id == promo.PromotedSignupId!.Value);
        offered.Status = MasterClassSignupStatus.Offered;
        offered.PromotionNotifiedAt = null;
        offered.OfferExpiresAt = new DateTimeOffset(2027, 2, 1, 12, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var email = new MasterClassPromotionEmailService(
            db, sender, new NoOpContext(), svc, Templates("mc-offer"));

        Assert.True(await email.SendPromotionAsync(offered.Id, "https://hub.test"));
        var m = Assert.Single(sender.Messages);
        Assert.Contains("held for you", m.Subject);
        Assert.Contains("Deep Dive MC", m.Subject);                 // {{masterClassTitle}}
        Assert.Contains("MyMasterClass?t=", m.Html);                // {{selfServiceUrl}}
        Assert.Contains("holding it for you by", m.Html);           // {{offerDeadline}}
        Assert.Contains("The team", m.Html);
    }

    // 4b -------------------------------------------------------------- promoted ----
    [Fact]
    public async Task Promoted_renders_from_promoted_template()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, _) = await SeedMcAsync(db, capacity: 1);
        var a1 = await AttendeeAsync(db, ev, "a1@x.dk");
        var a2 = await AttendeeAsync(db, ev, "a2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, a1, mc);                          // confirmed
        await svc.SignUpAsync(ev, a2, mc);                          // waitlisted
        var promo = await svc.RemoveAsync(ev, a1, mc);              // a2 promoted
        var signup = db.MasterClassSignups.First(x => x.Id == promo!.PromotedSignupId!.Value);
        // Ensure a CONFIRMED (not offered) seat so the promoted variant renders.
        signup.Status = MasterClassSignupStatus.Confirmed;
        signup.PromotionNotifiedAt = null;
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var email = new MasterClassPromotionEmailService(
            db, sender, new NoOpContext(), svc, Templates("mc-promoted"));

        Assert.True(await email.SendPromotionAsync(signup.Id, "https://hub.test"));
        var m = Assert.Single(sender.Messages);
        Assert.Equal("a2@x.dk", m.To);
        Assert.Contains("you're in", m.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Deep Dive MC", m.Subject);                 // {{masterClassTitle}}
        Assert.Contains("MyMasterClass?t=", m.Html);                // {{selfServiceUrl}}
        Assert.Contains("The team", m.Html);
    }

    // 5 ------------------------------------------------------------ month-reminder ----
    [Fact]
    public async Task MonthReminder_renders_from_template_and_keeps_ics()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedMcAsync(db);
        var sender = new CapturingEmailSender();
        var svc = new MasterClassSignupService(db);
        var email = McEmail(db, sender, svc, "mc-month");

        await svc.SignUpAsync(ev, att, mc);
        await svc.SetMonthReminderOptInAsync(ev, att, true);
        var id = (await svc.SignupIdAsync(ev, att, mc))!.Value;

        Assert.True(await email.SendMonthReminderAsync(id, "hub.test"));
        var m = Assert.Single(sender.IcsMessages);
        Assert.Contains("Coming up", m.Subject);
        Assert.Contains("Deep Dive MC", m.Subject);                 // {{masterClassTitle}}
        Assert.Contains("Deep Dive MC", m.Html);
        Assert.NotNull(sender.LastIcs);                             // .ics attachment preserved
        Assert.Contains("BEGIN:VCALENDAR", sender.LastIcs!);
        Assert.False(await email.SendMonthReminderAsync(id, "hub.test")); // idempotent
        Assert.Single(sender.Sent);
    }

    // 6 ------------------------------------------------------------------- PIN ----
    [Fact]
    public async Task PinSignin_renders_from_template_with_the_pin()
    {
        using var db = ScenarioFixture.NewDb();
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = e.Id, Email = "u@x.dk", FullName = "Sam Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p); await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new PinLoginService(
            db, new PinService(), sender, ScenarioFixture.Clock,
            new NoOpContext(), Templates("pin"));

        var result = await svc.RequestPinAsync(e.Id, "u@x.dk");
        Assert.True(result.Accepted);
        var m = Assert.Single(sender.Messages);
        Assert.Equal("u@x.dk", m.To);
        Assert.Contains("sign-in code", m.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C27 Event Hub", m.Subject);                // {{subjectPrefix}} (event code)
        Assert.Contains("Sam", m.Html);                             // {{firstName}}
        Assert.Contains("15 minutes", m.Html);                      // {{expiryMinutes}}
        // The PIN body token = the stored hash's plaintext; assert a 6-digit code shows.
        Assert.Matches(@"\d{6}", m.Html);
    }

    // 7 -------------------------------------------------------- calendar invite ----
    [Fact]
    public async Task CalendarInvite_renders_from_template_and_keeps_ics()
    {
        using var db = ScenarioFixture.NewDb();
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            CalendarSyncEnabled = true,
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = e.Id, Email = "v@x.dk", FullName = "Val Volunteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p); await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new CalendarInviteEmailService(
            db, sender, new NoOpContext(), ScenarioFixture.Clock, Templates("cal"));

        Assert.True(await svc.SendActivationInviteAsync(p.Id));
        var m = Assert.Single(sender.IcsMessages);
        Assert.Equal("v@x.dk", m.To);
        Assert.Contains("C 2027", m.Subject);                       // {{eventDisplayName}}
        Assert.Contains("Val", m.Html);                             // {{firstName}}
        Assert.Contains("The team", m.Html);
        Assert.NotNull(sender.LastIcs);                             // .ics attachment preserved
        Assert.Contains("BEGIN:VCALENDAR", sender.LastIcs!);
        Assert.False(await svc.SendActivationInviteAsync(p.Id));    // idempotent
    }

    // 8 ------------------------------------------------- session-evaluation results ----
    [Fact]
    public async Task SessionEval_renders_from_template_with_raw_results_html()
    {
        using var db = ScenarioFixture.NewDb();
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var spk = new Participant
        {
            EventId = e.Id, Email = "speaker@x.dk", FullName = "Sky Walker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(spk); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Cloud 101", Type = SessionType.TechnicalSession };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = spk.Id });
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new SessionEvaluationMailService(
            db, sender, ScenarioFixture.Clock, new NoOpContext(), Templates("eval"));

        var result = await svc.EmailResultsToSpeakersAsync(s.Id, "86% happy (42 votes)");
        Assert.True(result.Sent);
        var m = Assert.Single(sender.Messages);
        Assert.Equal("speaker@x.dk", m.To);
        Assert.Contains("Cloud 101", m.Subject);                    // {{sessionTitle}}
        Assert.Contains("Sky", m.Html);                             // {{firstName}}
        Assert.Contains("C 2027", m.Html);                          // {{eventDisplayName}}
        Assert.Contains("86% happy", m.Html);                       // {{resultsHtml}} raw token
    }

    private static async Task<int> AttendeeAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email)
    {
        var a = new Attendee { EventId = ev, Email = email, FirstName = "F", LastName = "L", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return a.Id;
    }
}
