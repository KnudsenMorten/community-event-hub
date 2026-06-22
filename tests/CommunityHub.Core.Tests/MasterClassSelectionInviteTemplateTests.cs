using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FEATURE C5: <see cref="MasterClassEmailService.SendSelectionInviteAsync"/> renders
/// the <c>masterclass-selection-invite</c> TEMPLATE (private config copy wins, generic
/// shipped default is the fallback) instead of building HTML inline — while keeping the
/// 2-day-only gate, the <c>MasterClassInviteSentAt</c> tracking and idempotency.
/// </summary>
public sealed class MasterClassSelectionInviteTemplateTests
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
        // exercises the GENERIC default (the private ELDK copy is asserted elsewhere).
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ceh-no-private-mc"),
        }));
        var svc = new MasterClassSignupService(db);
        return new MasterClassEmailService(db, sender, new NoOpContext(), svc, templates);
    }

    private static async Task<(int ev, int att)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var a = new Attendee
        {
            EventId = e.Id, Email = "p@x.dk", FirstName = "Pat", LastName = "Lee",
            TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return (e.Id, a.Id);
    }

    [Fact]
    public async Task Selection_invite_renders_from_the_template()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, att) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = Build(db, sender);

        Assert.True(await svc.SendSelectionInviteAsync(att, "https://hub.test"));

        var m = Assert.Single(sender.Messages);
        // Subject + body come from the masterclass-selection-invite template.
        Assert.Contains("Choose your Master Class", m.Subject);
        Assert.Contains("C 2027", m.Subject);                          // {{eventDisplayName}} token
        Assert.Contains("Choose my Master Class", m.Html);             // the CTA from the template
        Assert.Contains("MyMasterClass?t=", m.Html);                   // {{selectionUrl}} secure link
        Assert.Contains("not confirmed", m.Html);

        // Tracking + 2-day gate still apply.
        Assert.NotNull(db.Attendees.Find(att)!.MasterClassInviteSentAt);
        Assert.False(await svc.SendSelectionInviteAsync(att, "https://hub.test")); // idempotent
    }
}
