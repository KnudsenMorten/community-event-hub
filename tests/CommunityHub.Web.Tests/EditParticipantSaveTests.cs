using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The "Modify" half of the participant-CRUD fix (REQUIREMENTS §21): the grid's
/// per-row "Edit / Modify" opens the real participant editor
/// (<see cref="EditParticipantModel"/>), and saving it persists the core fields
/// an organizer manages — name, email, persona, and the active/cancellation
/// switch. This drives the actual page-model POST over a fake organizer session
/// and asserts the row is updated. FAKE names only.
/// </summary>
public sealed class EditParticipantSaveTests
{
    private const int EventId = 11;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"editpart-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    /// <summary>Never actually sends — the edit-save path does not send mail.</summary>
    private sealed class NoOpEmailSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static WelcomeEmailService NewWelcome(CommunityHubDbContext db, TimeProvider clock)
        => new(db,
               new EmailTemplateProvider(Options.Create(new EmailTemplateOptions())),
               new NoOpEmailSender(),
               clock);

    private static ClaimsPrincipal OrganizerSession(Participant org)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, org.Id.ToString()),
            new(ClaimTypes.Email, org.Email),
            new(ClaimTypes.Name, org.FullName),
            new(ClaimTypes.Role, org.Role.ToString()),
            new("EventId", org.EventId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static EditParticipantModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var clock = new FixedClock();
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new EditParticipantModel(db, accessor, NewWelcome(db, clock), clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<(Participant org, Participant target)> SeedAsync(CommunityHubDbContext db)
    {
        var org = new Participant
        {
            EventId = EventId, Email = "org@example.test", FullName = "Org Person",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        var target = new Participant
        {
            EventId = EventId, Email = "old@example.test", FullName = "Old Name",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.AddRange(org, target);
        await db.SaveChangesAsync();
        return (org, target);
    }

    [Fact]
    public async Task Organizer_can_modify_name_email_persona_and_active_state()
    {
        using var db = NewDb();
        var (org, target) = await SeedAsync(db);

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http);
        model.Id = target.Id;
        model.Email = "new@example.test";
        model.FullName = "New Name";
        model.Phone = "+45 12 34 56 78";
        model.Role = ParticipantRole.Speaker;
        model.IsActive = false;

        await model.OnPostAsync(default);

        var reloaded = await db.Participants.FindAsync(target.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("new@example.test", reloaded!.Email);
        Assert.Equal("New Name", reloaded.FullName);
        Assert.Equal("+45 12 34 56 78", reloaded.Phone);
        Assert.Equal(ParticipantRole.Speaker, reloaded.Role);
        Assert.False(reloaded.IsActive);
        Assert.Equal("Saved.", model.Message);
    }

    [Fact]
    public async Task Modify_rejects_email_that_collides_with_another_row()
    {
        using var db = NewDb();
        var (org, target) = await SeedAsync(db);
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "taken@example.test", FullName = "Someone Else",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http);
        model.Id = target.Id;
        model.Email = "taken@example.test";
        model.FullName = "Old Name";
        model.Role = ParticipantRole.Attendee;
        model.IsActive = true;

        await model.OnPostAsync(default);

        Assert.NotNull(model.Error);
        var reloaded = await db.Participants.FindAsync(target.Id);
        Assert.Equal("old@example.test", reloaded!.Email); // unchanged
    }
}
