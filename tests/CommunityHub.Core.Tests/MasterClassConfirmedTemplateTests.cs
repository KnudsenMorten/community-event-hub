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

        var m = Assert.Single(sender.Messages);
        Assert.Equal("p@x.dk", m.To);
        // Subject + body come from the masterclass-confirmed template tokens.
        Assert.Contains("Deep Dive MC", m.Subject);                 // {{masterClassTitle}}
        Assert.Contains("Deep Dive MC", m.Html);
        Assert.Contains($"/MasterClassPage/{mc}", m.Html);          // {{landingPageUrl}}
        Assert.Contains("MyMasterClass.ics", m.Html);               // {{icsUrl}}
        Assert.Contains("MyMasterClass?t=", m.Html);                // {{selfServiceUrl}}
        // §89: the "See you there, / The team" sign-off has been removed.
        Assert.DoesNotContain("The team", m.Html);
    }
}
