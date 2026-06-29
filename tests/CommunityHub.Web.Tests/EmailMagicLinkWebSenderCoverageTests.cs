using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Branding;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Pages;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §169 "for ALL emails" coverage at the WEB-PAGE senders (mirrors the Core
/// <c>EmailMagicLinkSenderCoverageTests</c>). Each organizer page that emails a
/// participant now passes the recipient's <see cref="Participant"/> id to
/// <see cref="EmailTemplateProvider.NewTokenSet(int?)"/>, so the §169 seam mints that
/// person's standing <c>/go</c> auto-login grant for the hub CTA — per-recipient on a
/// fan-out, and resolved by Email+EventId where the recipient is addressed by email.
/// Fail-safe: a non-participant recipient mints NOTHING and never throws.
///
/// <para>EF in-memory + an ephemeral DataProtection provider + the REAL shipped
/// templates, with the real <see cref="IEmailMagicLinkService"/> wired into the template
/// provider's scope so the grant is actually minted. FAKE names only.</para>
/// </summary>
public sealed class EmailMagicLinkWebSenderCoverageTests
{
    private const string Origin = "https://hub.example";
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A capturing IEmailSender — records every send (incl. the .ics path).</summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Messages { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        { Messages.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default)
        { Messages.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        { Messages.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }

        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody,
            string icsContent, string icsFileName, CancellationToken ct = default)
        { Messages.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }

        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
        { Messages.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    /// <summary>
    /// A DI container with the DbContext + the REAL §169 magic-link service over ONE
    /// shared in-memory store (the template provider opens its own scope to mint the
    /// link, so the name must be fixed or the magic service would see an empty db).
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var dbName = $"webmagic-{Guid.NewGuid():N}";
        return new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-webmagic"))))
            .AddSingleton<TimeProvider>(new FixedClock())
            .AddScoped<IEmailMagicLinkService, EmailMagicLinkService>()
            .BuildServiceProvider();
    }

    private static EmailTemplateProvider Templates(ServiceProvider sp) =>
        new(
            Options.Create(new EmailTemplateOptions
            {
                // The shipped templates are copied next to the test assembly
                // (templates/emails); the private layer points at an absent dir so an
                // edition's private copy never shadows the generic {{hubUrl}} CTA.
                TemplateDirectory = "templates/emails",
                PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-webmagic"),
                HubUrl = Origin,
            }),
            sp.GetRequiredService<IServiceScopeFactory>(),
            emailContext: null);

    private static ICurrentParticipantAccessor Accessor(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var http = new DefaultHttpContext { User = principal };
        return new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
    }

    private static PageContext PageCtx() => new() { HttpContext = new DefaultHttpContext() };

    private static Event NewEvent() => new()
    {
        Code = "TC27", CommunityName = "Test Community", DisplayName = "Test Community 2027",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
    };

    private static Participant NewOrganizer(int eventId) => new()
    {
        EventId = eventId, Email = "olivia@example.com", FullName = "Olivia Organizer",
        Role = ParticipantRole.Organizer, IsActive = true,
    };

    // ----------------------------------------------------------------------
    // AppGame gift reminder — sponsor-contact FAN-OUT is personalized per recipient.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task App_game_reminder_mints_each_sponsor_contacts_own_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var organizer = NewOrganizer(ev.Id);
        db.Participants.Add(organizer);

        Participant Contact(string email) => new()
        {
            EventId = ev.Id, Email = email, FullName = "Sample Person",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "ACME", IsActive = true,
        };
        var c1 = Contact("c1@example.com");
        var c2 = Contact("c2@example.com");
        db.Participants.AddRange(c1, c2);
        var part = new AppGameParticipation
        {
            EventId = ev.Id, SponsorCompanyId = "ACME", CompanyName = "Acme",
            GiftDescription = "A drone",
        };
        db.AppGameParticipations.Add(part);
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var page = new AppGameModel(db, Accessor(organizer), Templates(sp), sender, new FixedClock())
        { PageContext = PageCtx() };

        await page.OnPostSendReminderAsync(part.Id, default);

        Assert.Equal(2, sender.Messages.Count);
        // Each sponsor contact got their OWN standing grant (and the organizer got none).
        Assert.True(await db.MagicLinkGrants.AnyAsync(g => g.ParticipantId == c1.Id));
        Assert.True(await db.MagicLinkGrants.AnyAsync(g => g.ParticipantId == c2.Id));
        Assert.False(await db.MagicLinkGrants.AnyAsync(g => g.ParticipantId == organizer.Id));
    }

    // ----------------------------------------------------------------------
    // Group-photo invite — resolved to the company lead's Participant when known.
    // ----------------------------------------------------------------------

    private static GroupPhotoRegistration QualifyingRegistration(int eventId, string leadEmail) => new()
    {
        EventId = eventId, CompanyName = "Acme", ContactName = "Sample Lead",
        ContactEmail = leadEmail, TicketCount = GroupPhotoRegistration.QualifyingTicketThreshold + 1,
        ScheduledAtUtc = Now.AddDays(20),
    };

    [Fact]
    public async Task Group_photo_invite_to_a_participant_lead_mints_their_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var organizer = NewOrganizer(ev.Id);
        var lead = new Participant
        {
            EventId = ev.Id, Email = "lead@example.com", FullName = "Sample Lead",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "ACME", IsActive = true,
        };
        db.Participants.AddRange(organizer, lead);
        db.GroupPhotoRegistrations.Add(QualifyingRegistration(ev.Id, "lead@example.com"));
        await db.SaveChangesAsync();
        var reg = await db.GroupPhotoRegistrations.SingleAsync();

        var sender = new CapturingEmailSender();
        var page = new GroupPhotosModel(db, Accessor(organizer), Templates(sp), sender, new FixedClock())
        { PageContext = PageCtx() };

        await page.OnPostSendInviteAsync(reg.Id, default);

        var m = Assert.Single(sender.Messages);
        Assert.Equal("lead@example.com", m.To);
        var grant = await db.MagicLinkGrants.SingleAsync();
        Assert.Equal(lead.Id, grant.ParticipantId);          // the lead's personal grant
    }

    [Fact]
    public async Task Group_photo_invite_to_an_external_lead_mints_nothing_and_does_not_throw()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var organizer = NewOrganizer(ev.Id);
        db.Participants.Add(organizer);
        // Lead address is NOT a Participant in this edition.
        db.GroupPhotoRegistrations.Add(QualifyingRegistration(ev.Id, "external@example.com"));
        await db.SaveChangesAsync();
        var reg = await db.GroupPhotoRegistrations.SingleAsync();

        var sender = new CapturingEmailSender();
        var page = new GroupPhotosModel(db, Accessor(organizer), Templates(sp), sender, new FixedClock())
        { PageContext = PageCtx() };

        await page.OnPostSendInviteAsync(reg.Id, default);   // fail-safe: never throws

        var m = Assert.Single(sender.Messages);
        Assert.Equal("external@example.com", m.To);
        Assert.Empty(db.MagicLinkGrants);                    // no grant minted (plain hub URL)
    }

    // ----------------------------------------------------------------------
    // Travel-reimbursement-paid — the claimant is a Participant (pass row.ParticipantId).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Travel_reimbursement_paid_mints_the_claimants_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var organizer = NewOrganizer(ev.Id);
        var speaker = new Participant
        {
            EventId = ev.Id, Email = "speaker@example.com", FullName = "Sample Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.AddRange(organizer, speaker);
        await db.SaveChangesAsync();
        db.TravelReimbursements.Add(new TravelReimbursement
        {
            EventId = ev.Id, ParticipantId = speaker.Id, RequestReimbursement = true,
            ClaimAmountEur = 120m,
        });
        await db.SaveChangesAsync();
        var claim = await db.TravelReimbursements.SingleAsync();

        var sender = new CapturingEmailSender();
        var page = new TravelReimbursementsModel(
            db, Accessor(organizer), new FixedClock(), sender, Templates(sp),
            new ActiveEventNameProvider(sp.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<TravelReimbursementsModel>.Instance)
        { PageContext = PageCtx() };

        await page.OnPostMarkPaidAsync(claim.Id, notes: null, default);

        var m = Assert.Single(sender.Messages);
        Assert.Equal("speaker@example.com", m.To);
        var grant = await db.MagicLinkGrants.SingleAsync();
        Assert.Equal(speaker.Id, grant.ParticipantId);
    }

    // ----------------------------------------------------------------------
    // First-login portal welcome — rendered for the signed-in participant (their id),
    // so the welcome variant's {{hubUrl}} CTA is their personal /go magic-link.
    // ----------------------------------------------------------------------

    [Fact]
    public void Welcome_fragment_carries_the_signed_in_participants_magic_link()
    {
        using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        db.SaveChanges();
        var speaker = new Participant
        {
            EventId = ev.Id, Email = "speaker@example.com", FullName = "Sample Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        db.SaveChanges();

        var page = new WelcomeModel(db, Accessor(speaker), new FixedClock(), Templates(sp))
        { PageContext = PageCtx() };

        page.OnGet();

        // The welcome-speaker variant's hub CTA is the participant's /go magic-link…
        Assert.Contains($"{Origin}/go/", page.WelcomeBodyHtml);
        // …and the standing grant was minted for them.
        Assert.True(db.MagicLinkGrants.Any(g => g.ParticipantId == speaker.Id));
    }
}
