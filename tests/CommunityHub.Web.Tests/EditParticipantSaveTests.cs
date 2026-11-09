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
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default)
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

    private static AttendeeOneDayWelcomeEmailService NewAttendeeWelcome(CommunityHubDbContext db, TimeProvider clock)
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
        return new EditParticipantModel(db, accessor, NewWelcome(db, clock), NewAttendeeWelcome(db, clock), clock)
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

    [Fact]
    public async Task Saving_a_speaker_creates_their_speaker_profile_with_the_name_once()
    {
        // 🔴 §1377 (PROD 2026-10-29): a speaker added by hand had no speaker profile until they filled in a form of
        // their own, so no category could be set and the Zoho push never saw them.
        using var db = NewDb();
        var (org, target) = await SeedAsync(db);

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http);
        model.Id = target.Id;
        model.Email = "old@example.test";
        model.FullName = "Cecilie Waagner Falkenstrom";
        model.Role = ParticipantRole.Speaker;
        model.IsActive = true;

        await model.OnPostAsync(default);
        await model.OnPostAsync(default);   // a second save must not add a second profile

        var profile = Assert.Single(db.SpeakerProfiles.Where(p => p.ParticipantId == target.Id));
        Assert.Equal(EventId, profile.EventId);
        Assert.Equal("Cecilie", profile.FirstName);
        Assert.Equal("Waagner Falkenstrom", profile.LastName);
        Assert.Null(profile.Category);   // the organizer still picks the category on Speaker Review
    }

    [Fact]
    public async Task Saving_a_non_speaker_creates_no_speaker_profile()
    {
        using var db = NewDb();
        var (org, target) = await SeedAsync(db);

        var http = new DefaultHttpContext { User = OrganizerSession(org) };
        var model = NewModel(db, http);
        model.Id = target.Id;
        model.Email = "old@example.test";
        model.FullName = "Old Name";
        model.Role = ParticipantRole.Attendee;
        model.IsActive = true;

        await model.OnPostAsync(default);

        Assert.Empty(db.SpeakerProfiles);
    }

    // ---------- §1379b — the test-user flag is editable by hand ----------

    private static async Task<(EditParticipantModel model, Participant target)> TestUserCaseAsync(
        CommunityHubDbContext db, bool flagBefore, CommunityHub.Core.Settings.Ring ring, bool requested)
    {
        var (org, target) = await SeedAsync(db);
        target.IsTestUser = flagBefore; target.Ring = ring;
        await db.SaveChangesAsync();
        var model = NewModel(db, new DefaultHttpContext { User = OrganizerSession(org) });
        model.Id = target.Id; model.Email = target.Email; model.FullName = target.FullName;
        model.Role = target.Role; model.IsActive = true; model.IsTestUser = requested;
        await model.OnPostAsync(default);
        return (model, (await db.Participants.FindAsync(target.Id))!);
    }

    [Fact]
    public async Task Clearing_the_test_flag_on_a_real_ring_person_saves()
    {
        using var db = NewDb();
        var (model, p) = await TestUserCaseAsync(db, flagBefore: true, CommunityHub.Core.Settings.Ring.Broad, requested: false);
        Assert.False(p.IsTestUser);
        Assert.Equal("Saved.", model.Message);
    }

    [Fact]
    public async Task Setting_the_test_flag_saves()
    {
        using var db = NewDb();
        var (_, p) = await TestUserCaseAsync(db, flagBefore: false, CommunityHub.Core.Settings.Ring.Broad, requested: true);
        Assert.True(p.IsTestUser);
    }

    [Fact]
    public async Task Ring1_person_stays_a_test_user_and_the_page_says_why()
    {
        using var db = NewDb();
        var (model, p) = await TestUserCaseAsync(db, flagBefore: true, CommunityHub.Core.Settings.Ring.Ring1, requested: false);
        Assert.True(p.IsTestUser);                 // §940 wins
        Assert.True(model.IsTestUser);             // the box re-renders ticked, so a 2nd save cannot clear it
        Assert.Contains("Ring 1", model.Message);
    }

    [Fact]
    public async Task Get_loads_the_flag_so_an_untouched_save_keeps_it()
    {
        using var db = NewDb();
        var (org, target) = await SeedAsync(db);
        target.IsTestUser = true; await db.SaveChangesAsync();
        var model = NewModel(db, new DefaultHttpContext { User = OrganizerSession(org) });
        model.Id = target.Id;
        await model.OnGetAsync(default);
        Assert.True(model.IsTestUser);
    }

    [Fact]
    public void Contact_inheriting_a_ring1_company_cannot_be_cleared()
    {
        var p = new Participant { Ring = CommunityHub.Core.Settings.Rings.Default, IsTestUser = true };
        var kept = EditParticipantModel.ApplyTestUserFlag(p, requested: false, companyRing: CommunityHub.Core.Settings.Ring.Ring1);
        Assert.True(kept);
        Assert.True(p.IsTestUser);

        var real = new Participant { Ring = CommunityHub.Core.Settings.Rings.Default, IsTestUser = true };
        Assert.False(EditParticipantModel.ApplyTestUserFlag(real, requested: false, companyRing: CommunityHub.Core.Settings.Ring.Broad));
        Assert.False(real.IsTestUser);
    }
}
