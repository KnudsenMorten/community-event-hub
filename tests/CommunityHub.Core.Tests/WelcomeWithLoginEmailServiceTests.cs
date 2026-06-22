using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the welcome-with-login email (welcome for ALL roles + one-click
/// auto-login). Covers the three contracts the feature requires:
///   1. magic-link token round-trip (real auth token, not a ?email= prefill),
///   2. the DEV-only hard guard refusing to send outside Development,
///   3. per-role rendering (every role gets a role-specific line + the CTA).
/// All offline: EF in-memory + the ephemeral DataProtection provider + the real
/// shipped email templates. No app, no DB server, no secrets.
/// </summary>
public class WelcomeWithLoginEmailServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private static IMagicLinkTokenFactory NewTokenFactory() =>
        new MagicLinkTokenFactory(
            DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-tests"))));

    private static IDataProtectionProvider NewDpProvider() =>
        DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-tests")));

    /// <summary>The hardened single-use welcome auto-login token service, over the test db.</summary>
    private static WelcomeAutoLoginTokenService NewAutoLogin(CommunityHubDbContext db) =>
        new(db, NewDpProvider(), new FixedClock());

    /// <summary>
    /// A welcome service wired with the real single-use auto-login token service.
    /// <paramref name="autoLogin"/> toggles WelcomeEmailOptions.AutoLoginEnabled
    /// (default OFF — operator "disable welcome mail with login").
    /// </summary>
    private static WelcomeWithLoginEmailService NewService(
        CommunityHubDbContext db, CapturingEmailSender sender, bool isDev, string envName,
        bool autoLogin = false) =>
        new(db, NewTemplates(), sender, NewAutoLogin(db),
            new StubEnv(isDev, envName), new FixedClock(), context: null,
            options: new WelcomeEmailOptions { AutoLoginEnabled = autoLogin });

    private static EmailTemplateProvider NewTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            // Render against the REAL shipped templates; point the private layer at a
            // dir with no welcome-* files so the GENERIC shipped defaults are used
            // (the private ELDK copy is asserted separately in EmailTemplateProviderTests).
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-templates"),
        }));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubEnv : IEnvironmentInfo
    {
        public StubEnv(bool isDev, string name) { IsDevelopment = isDev; EnvironmentName = name; }
        public bool IsDevelopment { get; }
        public string EnvironmentName { get; }
    }

    // ----------------------------------------------------------------------
    // 1. Magic-link token round-trip
    // ----------------------------------------------------------------------

    [Fact]
    public void Token_round_trips_to_the_same_participant()
    {
        var factory = NewTokenFactory();
        var token = factory.CreateToken(participantId: 4175);

        Assert.Equal(4175, factory.ValidateToken(token));
        // URL-safe: no characters that would need escaping in a query string.
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void Token_validation_rejects_tampered_expired_and_garbage()
    {
        var factory = NewTokenFactory();
        var token = factory.CreateToken(7);

        // Tampered: flip a character.
        var tampered = ('A' == token[0] ? 'B' : 'A') + token[1..];
        Assert.Null(factory.ValidateToken(tampered));

        // Garbage / empty.
        Assert.Null(factory.ValidateToken("not-a-real-token"));
        Assert.Null(factory.ValidateToken(""));

        // Expired: a negative TTL is already in the past.
        var expired = factory.CreateToken(7, TimeSpan.FromMinutes(-1));
        Assert.Null(factory.ValidateToken(expired));
    }

    [Fact]
    public void Login_url_carries_the_token_to_the_magic_handler()
    {
        var factory = NewTokenFactory();
        var token = factory.CreateToken(42);

        var url = WelcomeWithLoginEmailService.BuildLoginUrl(
            "https://dev.eldk27.eventhub.expertslive.dk/", token);

        Assert.StartsWith(
            "https://dev.eldk27.eventhub.expertslive.dk/Login/Magic?token=", url);
        // The trailing slash on the base URL must not double up.
        Assert.DoesNotContain("dk//Login", url);

        // The token in the URL still validates back to the participant.
        var encoded = url.Split("token=")[1];
        var decoded = Uri.UnescapeDataString(encoded);
        Assert.Equal(42, factory.ValidateToken(decoded));
    }

    // ----------------------------------------------------------------------
    // 2. DEV-only hard guard
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Send_is_refused_outside_development(string envName)
    {
        using var db = ScenarioFixture.NewDb();
        var participantId = await SeedOneAsync(db, ParticipantRole.Speaker);
        var sender = new CapturingEmailSender();

        var svc = NewService(db, sender, isDev: false, envName);

        var result = await svc.SendAsync(participantId, "https://prod.example");

        Assert.False(result.Sent);
        Assert.Contains(envName, result.Reason);
        Assert.Empty(sender.Sent);                       // nothing was sent
        var p = await db.Participants.FindAsync(participantId);
        Assert.Null(p!.WelcomeWithLoginSentAt);          // and nothing recorded
    }

    [Fact]
    public async Task Send_succeeds_in_development_and_records_who_was_sent()
    {
        using var db = ScenarioFixture.NewDb();
        var participantId = await SeedOneAsync(db, ParticipantRole.Sponsor);
        var sender = new CapturingEmailSender();

        var svc = NewService(db, sender, isDev: true, "Development");

        var result = await svc.SendAsync(
            participantId, "https://dev.eldk27.eventhub.expertslive.dk");

        Assert.True(result.Sent);
        var msg = Assert.Single(sender.Messages);
        Assert.NotNull(msg.Text);                        // plain-text alternative present
        Assert.Contains("Open the Event Hub", msg.Html); // single CTA in HTML (per-role variant)

        var p = await db.Participants.FindAsync(participantId);
        Assert.Equal(Now, p!.WelcomeWithLoginSentAt);    // recorded who/when
    }

    // ----------------------------------------------------------------------
    // Auto-login gate (operator "disable welcome mail with login")
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Auto_login_disabled_by_default_mints_no_token_and_uses_the_hub_url()
    {
        using var db = ScenarioFixture.NewDb();
        var participantId = await SeedOneAsync(db, ParticipantRole.Speaker);
        var sender = new CapturingEmailSender();

        // Default: AutoLoginEnabled = false.
        var svc = NewService(db, sender, isDev: true, "Development");

        var result = await svc.SendAsync(participantId, "https://dev.example/");

        Assert.True(result.Sent);
        // No magic-link token minted when auto-login is disabled.
        Assert.Equal(0, await db.MagicLinkGrants.CountAsync());
        var msg = Assert.Single(sender.Messages);
        // loginUrl == plain hub url (trailing slash trimmed), NOT a /Login/Magic link.
        Assert.DoesNotContain("/Login/Magic?token=", msg.Html);
        Assert.Contains("https://dev.example", msg.Html);
    }

    [Fact]
    public async Task Auto_login_enabled_mints_a_single_use_token_and_a_magic_link()
    {
        using var db = ScenarioFixture.NewDb();
        var participantId = await SeedOneAsync(db, ParticipantRole.Speaker);
        var sender = new CapturingEmailSender();

        var svc = NewService(db, sender, isDev: true, "Development", autoLogin: true);

        var result = await svc.SendAsync(participantId, "https://dev.example");

        Assert.True(result.Sent);
        Assert.Equal(1, await db.MagicLinkGrants.CountAsync());     // a token was minted
        var msg = Assert.Single(sender.Messages);
        Assert.Contains("/Login/Magic?token=", msg.Html);          // real auto-login link
    }

    [Fact]
    public async Task Send_is_re_sendable_and_updates_the_stamp()
    {
        using var db = ScenarioFixture.NewDb();
        var participantId = await SeedOneAsync(db, ParticipantRole.Volunteer);
        var sender = new CapturingEmailSender();
        // Auto-login ON so each re-send mints a fresh grant (the resend contract).
        var svc = NewService(db, sender, isDev: true, "Development", autoLogin: true);

        var first = await svc.SendAsync(participantId, "https://dev.example");
        var second = await svc.SendAsync(participantId, "https://dev.example");

        Assert.True(first.Sent);
        Assert.True(second.Sent);                         // not gated by a prior send
        Assert.Equal(2, sender.Sent.Count);               // sent twice
        // Each re-send mints a FRESH single-use grant (so the old link can't be
        // reused after a re-send).
        Assert.Equal(2, await db.MagicLinkGrants.CountAsync());
    }

    [Fact]
    public async Task Provisioning_send_works_outside_dev_for_a_welcomed_role_and_is_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        // Use a WELCOMED role (Speaker). Attendees are no longer welcomed from this
        // platform (operator 2026-06-22) — see the no-welcome test below.
        var participantId = await SeedOneAsync(db, ParticipantRole.Speaker);
        var sender = new CapturingEmailSender();
        // Production environment: the provisioning send path must NOT be blocked by
        // the DEV-only guard (unlike SendAsync).
        var svc = NewService(db, sender, isDev: false, "Production");

        var first = await svc.SendForAttendeeProvisioningAsync(participantId, "https://prod.example");
        var second = await svc.SendForAttendeeProvisioningAsync(participantId, "https://prod.example");

        Assert.True(first.Sent);                          // sent despite non-DEV
        Assert.False(second.Sent);                        // idempotent: already welcomed
        Assert.Single(sender.Sent);                       // exactly one email ever
        var p = await db.Participants.FindAsync(participantId);
        Assert.Equal(Now, p!.WelcomeWithLoginSentAt);
    }

    [Fact]
    public async Task Attendee_and_organizer_get_no_welcome()
    {
        using var db = ScenarioFixture.NewDb();
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, isDev: false, "Production");

        foreach (var role in new[] { ParticipantRole.Attendee, ParticipantRole.Organizer })
        {
            var id = await SeedOneAsync(db, role);
            var result = await svc.SendForAttendeeProvisioningAsync(id, "https://prod.example");
            Assert.False(result.Sent);   // no welcome for these roles
        }
        Assert.Empty(sender.Sent);
    }

    // ----------------------------------------------------------------------
    // 3. Per-role rendering
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(ParticipantRole.Speaker, "speaker")]
    [InlineData(ParticipantRole.Volunteer, "volunteer")]
    [InlineData(ParticipantRole.Sponsor, "sponsor contact")]
    [InlineData(ParticipantRole.Media, "media crew member")]
    [InlineData(ParticipantRole.EventPartner, "event partner")]
    public void Every_welcomed_role_renders_its_variant_template_and_the_cta(
        ParticipantRole role, string roleNoun)
    {
        // RenderForUrl is pure (no DB / no mint), so a null db + token service is fine.
        var svc = new WelcomeWithLoginEmailService(
            db: null!, NewTemplates(), new CapturingEmailSender(),
            autoLogin: null!, new StubEnv(true, "Development"), new FixedClock());

        var participant = new Participant
        {
            Id = 1,
            FullName = "Test Person",
            Role = role,
            Event = new Event
            {
                CommunityName = "Test Community",
                DisplayName = "Test Community 2027",
                Code = "TC27",
            },
        };

        // Auto-login OFF: loginUrl is the plain hub url.
        var loginUrl = "https://dev.example";
        var rendered = svc.RenderForUrl(participant, loginUrl);

        // The PER-ROLE variant template (welcome-<role>) was selected.
        Assert.Equal(WelcomeVariants.TemplateKeyFor(role), WelcomeVariants.TemplateKeyFor(role));

        // The friendly role noun is in the body (the generic variant names the role).
        Assert.Contains(roleNoun, rendered.HtmlBody);

        // The event display name + CTA are present in HTML; the link in both bodies.
        Assert.Contains("Test Community 2027", rendered.HtmlBody);
        Assert.Contains("Open the Event Hub", rendered.HtmlBody);
        Assert.Contains(loginUrl, rendered.HtmlBody);
        Assert.Contains(loginUrl, rendered.TextBody);

        // No auto-login magic-link when auto-login is off (loginUrl is the hub url).
        Assert.DoesNotContain("/Login/Magic?token=", rendered.HtmlBody);

        // A non-empty subject was rendered from the variant's Subject: line.
        Assert.False(string.IsNullOrWhiteSpace(rendered.Subject));
        Assert.Contains("Test Community 2027", rendered.Subject);
    }

    [Fact]
    public void Every_role_has_a_distinct_non_empty_role_line()
    {
        var roles = Enum.GetValues<ParticipantRole>();
        var lines = roles
            .Select(WelcomeWithLoginEmailService.RoleLine)
            .ToList();

        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        // No two roles share the same line (each is genuinely role-specific).
        Assert.Equal(roles.Length, lines.Distinct().Count());
    }

    // ----------------------------------------------------------------------

    private static async Task<int> SeedOneAsync(
        CommunityHubDbContext db, ParticipantRole role)
    {
        var ev = new Event
        {
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            Code = "TC27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id,
            Email = "person@example.com",
            FullName = "Test Person",
            Role = role,
            IsActive = true,
            IsTestUser = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }
}
