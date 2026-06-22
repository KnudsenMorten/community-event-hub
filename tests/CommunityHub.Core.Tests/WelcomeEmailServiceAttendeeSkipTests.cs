using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FEATURE C6: the LEGACY once-ever <see cref="WelcomeEmailService"/> must SKIP
/// attendees — they get the per-role variant welcome (welcome-attendee) via the
/// provisioning path instead, so there is never a double welcome. Every other role
/// still gets the legacy welcome.
/// </summary>
public sealed class WelcomeEmailServiceAttendeeSkipTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-22T09:00:00Z");
    }

    private static WelcomeEmailService NewService(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender)
    {
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ceh-no-private-welcome"),
        }));
        return new WelcomeEmailService(db, templates, sender, new FixedClock());
    }

    private static async Task<int> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, ParticipantRole role)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "p@x.dk", FullName = "Pat Lee",
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Attendee_is_skipped_by_the_legacy_welcome()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db, ParticipantRole.Attendee);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var sent = await svc.SendWelcomeAsync(id);

        Assert.False(sent);                  // attendee: no legacy welcome
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Non_attendee_still_gets_the_legacy_welcome()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db, ParticipantRole.Speaker);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var sent = await svc.SendWelcomeAsync(id);

        Assert.True(sent);
        Assert.Single(sender.Sent);
    }
}
