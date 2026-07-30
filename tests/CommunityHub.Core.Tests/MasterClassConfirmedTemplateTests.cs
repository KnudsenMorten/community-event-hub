using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FEATURE 3: <see cref="MasterClassEmailService.SendConfirmedAsync"/> renders the
/// <c>masterclass-confirmed</c> TEMPLATE (generic shipped default here) instead of
/// inline HTML, carrying the attendee LANDING PAGE url token + keeping the .ics +
/// self-service links.
/// </summary>
public sealed class MasterClassConfirmedTemplateTests
{
    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private static MasterClassEmailService Build(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender)
    {
        // Point at the shipped generic template + an empty private dir, so the test
        // exercises the GENERIC publish-safe default.
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ceh-no-private-mc-confirmed"),
        }));
        var svc = new MasterClassSignupService(db);
        return new MasterClassEmailService(db, sender, new NoOpContext(), svc, templates);
    }

    private static async Task<(int ev, int mc, int att)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            // §257: verify the ATTACHED-invite path (gated behind the auto-invite switch).
            AutoCalendarInvitesEnabled = true,
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        var a = new Attendee { EventId = e.Id, Email = "p@x.dk", FirstName = "Pat", LastName = "Lee", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return (e.Id, s.Id, a.Id);
    }

    [Fact]
    public async Task Confirmed_renders_from_template_with_landing_page_url()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = Build(db, sender);

        var signups = new MasterClassSignupService(db);
        var r = await signups.SignUpAsync(ev, att, mc);
        Assert.True(r.Ok);
        var id = (await signups.SignupIdAsync(ev, att, mc))!.Value;

        await svc.SendConfirmedAsync(id, "https://hub.test");

        // §193: the confirmation ATTACHES the calendar invite, so it sends via SendWithIcsAsync.
        var m = Assert.Single(sender.IcsMessages);
        Assert.Equal("p@x.dk", m.To);
        // Subject + body come from the masterclass-confirmed template tokens.
        Assert.Contains("Deep Dive MC", m.Subject);                 // {{masterClassTitle}}
        Assert.Contains("Deep Dive MC", m.Html);
        // §418 (operator 2026-07-27: "the button should redirect to the q&a page (which is the
        // topic) … it goes wrongly to master class selection"). REVERSED from §341-6/§351-7, and
        // that reversal is the point: the button has always read "Open my Master Class page", but
        // §365's sweep of every /Attendee link carried it to the SELECTION step along with the
        // genuinely-retired ones. Its destination was never the selection screen.
        //
        // It must be the per-class page AND a magic link, so the shape is the magic-link origin
        // plus the deep path: /go/{token}/MasterClassPage/{id}. Pinned as BOTH halves — the id is
        // what makes it *their* class, and losing the magic-link origin would silently drop people
        // on a login screen.
        Assert.Contains($"/MasterClassPage/{mc}", m.Html);
        Assert.DoesNotContain("/Forms/Wizard?step=masterclass", m.Html);
        Assert.DoesNotContain("MyMasterClass.ics", m.Html);         // §193: no .ics download link
        Assert.NotNull(sender.LastIcs);                             // calendar invite attached
        // §89: the "See you there, / The team" sign-off has been removed.
        Assert.DoesNotContain("The team", m.Html);

        // §210b: the body still carries the prominent registration/breakfast-from-07:00
        // come-early block. §341-1 retired the §210 "see the calendar invite" subject
        // pointer — the mail no longer mentions a calendar invite at all.
        Assert.DoesNotContain("see the calendar invite", m.Subject);
        // 369: the registration/breakfast call-out was REMOVED at the operator request.
        Assert.DoesNotContain("Registration &amp; breakfast", m.Html);
        Assert.DoesNotContain("Doors open at 08:00", m.Html);
        // §210b: the attached invite is the full Master Class day 08:00–16:00 (local).
        Assert.Contains("20270209T080000", sender.LastIcs!);
        Assert.Contains("20270209T160000", sender.LastIcs!);
        Assert.DoesNotContain("20270209T090000", sender.LastIcs!);
    }
}
