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

    private static EmailTemplateProvider NewTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            // Render against the REAL shipped templates.
            TemplateDirectory = RepoPaths.EmailTemplates(),
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

        var svc = new WelcomeWithLoginEmailService(
            db, NewTemplates(), sender, NewTokenFactory(),
            new StubEnv(isDev: false, name: envName), new FixedClock());

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

        var svc = new WelcomeWithLoginEmailService(
            db, NewTemplates(), sender, NewTokenFactory(),
            new StubEnv(isDev: true, name: "Development"), new FixedClock());

        var result = await svc.SendAsync(
            participantId, "https://dev.eldk27.eventhub.expertslive.dk");

        Assert.True(result.Sent);
        var msg = Assert.Single(sender.Messages);
        Assert.NotNull(msg.Text);                        // plain-text alternative present
        Assert.Contains("Open my Event Hub", msg.Html);  // single CTA in HTML
        Assert.Contains("/Login/Magic?token=", msg.Html);// real auto-login link

        var p = await db.Participants.FindAsync(participantId);
        Assert.Equal(Now, p!.WelcomeWithLoginSentAt);    // recorded who/when
    }

    [Fact]
    public async Task Send_is_re_sendable_and_updates_the_stamp()
    {
        using var db = ScenarioFixture.NewDb();
        var participantId = await SeedOneAsync(db, ParticipantRole.Volunteer);
        var sender = new CapturingEmailSender();
        var svc = new WelcomeWithLoginEmailService(
            db, NewTemplates(), sender, NewTokenFactory(),
            new StubEnv(isDev: true, name: "Development"), new FixedClock());

        var first = await svc.SendAsync(participantId, "https://dev.example");
        var second = await svc.SendAsync(participantId, "https://dev.example");

        Assert.True(first.Sent);
        Assert.True(second.Sent);                         // not gated by a prior send
        Assert.Equal(2, sender.Sent.Count);               // sent twice
    }

    // ----------------------------------------------------------------------
    // 3. Per-role rendering
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(ParticipantRole.Organizer, "organizer")]
    [InlineData(ParticipantRole.Speaker, "speaker")]
    [InlineData(ParticipantRole.MasterclassSpeaker, "Master Class speaker")]
    [InlineData(ParticipantRole.Volunteer, "volunteer")]
    [InlineData(ParticipantRole.Sponsor, "sponsor contact")]
    [InlineData(ParticipantRole.Attendee, "attendee")]
    [InlineData(ParticipantRole.Video, "video crew member")]
    [InlineData(ParticipantRole.Camera, "photography crew member")]
    public void Every_role_renders_a_role_specific_line_and_the_cta(
        ParticipantRole role, string roleNoun)
    {
        var svc = new WelcomeWithLoginEmailService(
            db: null!, NewTemplates(), new CapturingEmailSender(),
            NewTokenFactory(), new StubEnv(true, "Development"), new FixedClock());

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

        var rendered = svc.Render(participant, "https://dev.example");

        // The role-specific line appears verbatim in both HTML and plain text.
        var roleLine = WelcomeWithLoginEmailService.RoleLine(role);
        Assert.Contains(roleLine, rendered.HtmlBody);
        Assert.Contains(roleLine, rendered.TextBody);

        // The friendly role noun is in the body.
        Assert.Contains(roleNoun, rendered.HtmlBody);

        // Single auto-login CTA in HTML; the link in both bodies.
        Assert.Contains("Open my Event Hub", rendered.HtmlBody);
        Assert.Contains("/Login/Magic?token=", rendered.HtmlBody);
        Assert.Contains("/Login/Magic?token=", rendered.TextBody);

        // Backstage explanation + brand-new framing present.
        Assert.Contains("Backstage", rendered.HtmlBody);
        Assert.Contains("Backstage", rendered.TextBody);
        Assert.Contains("brand-new", rendered.HtmlBody);
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
