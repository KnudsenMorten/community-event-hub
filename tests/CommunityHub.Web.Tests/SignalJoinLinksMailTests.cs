using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §779 — "email me the Signal join links".
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03: <i>"add button and instruction so they can send both links to their
/// mail from their mobile so they can join there. this is relevant when they signup from desktop
/// with no signal app installed"</i>.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class SignalJoinLinksMailTests
{
    private const int EventId = 1;

    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Exception? Throws { get; set; }

        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
        {
            if (Throws is not null) throw Throws;
            Sent.Add((to, subject, html));
            return Task.CompletedTask;
        }

        public Task SendAsync(string to, string subject, string html,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default) =>
            SendAsync(to, subject, html, ct);
        public Task SendAsync(string to, string subject, string html, string text,
            CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendAsync(string to, string subject, string html, string text,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default) =>
            SendAsync(to, subject, html, ct);
        public Task SendWithIcsAsync(string to, string subject, string html, string ics,
            string icsFileName, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendWithAttachmentsAsync(string to, string subject, string html,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) =>
            SendAsync(to, subject, html, ct);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"signal-{Guid.NewGuid():N}").Options);

    private static async Task<int> SeedAsync(CommunityHubDbContext db, ParticipantRole role, string? email = "ada@example.test")
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var p = new Participant
        {
            EventId = EventId, FullName = "Ada Lovelace", Email = email ?? string.Empty,
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>The real templates from disk — the same ones production renders.</summary>
    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = Path.Combine(RepoRoot(), "templates", "emails"),
            HubUrl = "https://hub.test",
            SupportEmail = "info@example.test",
            EventDisplayName = "ELDK 2027",
            BrandColor = "#1565c0",
            LogoUrl = "https://hub.test/logo.png",
        }));

    /// <summary>
    /// The REAL <c>signal-groups.eldk27.json</c> — the same file production resolves links from, so
    /// a config change that dropped a role's chat group would fail here rather than in his inbox.
    /// </summary>
    private static SignalGroupsProvider Groups() =>
        new(new SignalGroupsOptions
        {
            ConfigPath = Path.Combine(RepoRoot(), "config", "signal-groups.eldk27.json"),
        });

    private static (SignalFormService Svc, CapturingSender Mail) NewService(CommunityHubDbContext db)
    {
        var mail = new CapturingSender();
        return (new SignalFormService(db, Groups(), TimeProvider.System, Templates(), mail), mail);
    }

    /// <summary>A speaker gets BOTH of their links — his "send both links".</summary>
    [Fact]
    public async Task A_speaker_is_mailed_the_chat_AND_the_broadcast_link()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Speaker);
        var (svc, mail) = NewService(db);

        var (ok, message) = await svc.SendLinksEmailAsync(EventId, pid, ParticipantRole.Speaker);

        Assert.True(ok);
        var sent = Assert.Single(mail.Sent);
        Assert.Equal("ada@example.test", sent.To);

        var links = Groups().GetForRole(ParticipantRole.Speaker)!;
        Assert.Contains(links.ChatUrl!, sent.Html);
        Assert.Contains(links.BroadcastUrl!, sent.Html);

        // 🔑 The address is echoed back — they are about to look at a DIFFERENT device's inbox.
        Assert.Contains("ada@example.test", message);
    }

    /// <summary>
    /// 🔒 Media is broadcast-ONLY by config. Mailing it a chat link would put somebody in a group
    /// the configuration deliberately keeps them out of — "both links" means both of the links
    /// THIS role gets.
    /// </summary>
    [Fact]
    public async Task A_broadcast_only_role_is_never_mailed_a_chat_link()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Media);
        var (svc, mail) = NewService(db);

        var (ok, _) = await svc.SendLinksEmailAsync(EventId, pid, ParticipantRole.Media);

        Assert.True(ok);
        var sent = Assert.Single(mail.Sent);

        var speakerChat = Groups().GetForRole(ParticipantRole.Speaker)!.ChatUrl!;
        var volunteerChat = Groups().GetForRole(ParticipantRole.Volunteer)!.ChatUrl!;
        Assert.DoesNotContain(speakerChat, sent.Html);
        Assert.DoesNotContain(volunteerChat, sent.Html);
        Assert.Contains(Groups().GetForRole(ParticipantRole.Media)!.BroadcastUrl!, sent.Html);
    }

    /// <summary>
    /// A role with no Signal groups sends nothing. The step is not offered to them at all, but the
    /// handler re-derives the role server-side so a crafted POST cannot mine the links either.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Attendee)]
    public async Task An_out_of_scope_role_is_mailed_NOTHING(ParticipantRole role)
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, role);
        var (svc, mail) = NewService(db);

        var (ok, _) = await svc.SendLinksEmailAsync(EventId, pid, role);

        Assert.False(ok);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// ⚠️ The buttons must survive as BUTTONS. The two *Block tokens are raw HTML by the renderer's
    /// naming convention; if that ever changed they would arrive as visible escaped markup.
    /// </summary>
    [Fact]
    public async Task The_join_buttons_render_as_HTML_not_as_escaped_text()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Speaker);
        var (svc, mail) = NewService(db);

        await svc.SendLinksEmailAsync(EventId, pid, ParticipantRole.Speaker);
        var html = mail.Sent.Single().Html;

        Assert.Contains("v:roundrect", html);          // the Outlook/WORD-engine button
        Assert.DoesNotContain("&lt;v:roundrect", html); // ...not escaped into visible text
    }

    /// <summary>A participant with no usable address is told so, rather than silently "sent".</summary>
    [Fact]
    public async Task A_participant_without_an_email_address_is_told_so()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Speaker, email: "");
        var (svc, mail) = NewService(db);

        var (ok, message) = await svc.SendLinksEmailAsync(EventId, pid, ParticipantRole.Speaker);

        Assert.False(ok);
        Assert.Empty(mail.Sent);
        Assert.Contains("e-mail", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A send failure is reported honestly — never a cheerful "sent" for a mail that wasn't.</summary>
    [Fact]
    public async Task A_failed_send_is_reported_as_a_failure()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Speaker);
        var (svc, mail) = NewService(db);
        mail.Throws = new InvalidOperationException("Brevo said no");

        var (ok, message) = await svc.SendLinksEmailAsync(EventId, pid, ParticipantRole.Speaker);

        Assert.False(ok);
        Assert.DoesNotContain("Sent to", message);
    }

    /// <summary>
    /// 🔒 The mail is RING-EXEMPT and the catalog must say so, or the Settings page offers a ring
    /// control that governs nothing — the §326bx defect that cost him confidence in that page.
    /// </summary>
    [Fact]
    public void The_mail_is_registered_and_declared_ring_exempt()
    {
        Assert.True(EmailTemplateCatalog.Map.ContainsKey("signal-join-links"));
        Assert.True(EmailTemplateCatalog.IsRingExempt("signal-join-links"));
        Assert.False(string.IsNullOrWhiteSpace(EmailTemplateCatalog.RecipientHint("signal-join-links")));
    }
}
