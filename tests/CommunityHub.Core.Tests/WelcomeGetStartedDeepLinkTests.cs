using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §212: every role's WELCOME email primary CTA deep-links to THAT role's Get-Started
/// wizard, carried through the §169 personal magic-link so the recipient arrives
/// signed-in ON their Get-Started page. The template's <c>{{hubUrl}}</c> CTA is the
/// magic link (<c>{HubUrl}/go/{token}</c>); appending the role's get-started route
/// makes the rendered href <c>{HubUrl}/go/{token}/Forms/Wizard</c> (etc.), which the
/// <c>/go/{token}/{**target}</c> resolver lands signed-in on the deep-linked page.
///
/// <para>Asserts BOTH the shipped generic templates (publish-safe defaults) AND the
/// private per-edition copies under <c>config/email-templates/</c> — the ones actually
/// sent — point the same role-correct get-started route through the magic link, and that
/// the standing magic-link grant is still minted (auto-sign-in preserved). FAKE names only.</para>
/// </summary>
public sealed class WelcomeGetStartedDeepLinkTests
{
    private const string Origin = "https://hub.example";

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 6, 30, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>role → (welcome variant template key, that role's get-started route).</summary>
    public static TheoryData<ParticipantRole, string, string> RoleWelcomeRoutes()
    {
        var d = new TheoryData<ParticipantRole, string, string>();
        d.Add(ParticipantRole.Speaker, "welcome-speaker", "/Forms/Wizard");
        d.Add(ParticipantRole.Volunteer, "welcome-volunteer", "/Forms/Wizard");
        // §557 — Sponsor was the LONE outlier here: every other role already deep-linked to the
        // new Get Started, and this row is what kept the sponsor welcome pointing at the retired
        // /Sponsor/GetStarted (operator 2026-07-28, the fifth report in eight hours).
        d.Add(ParticipantRole.Sponsor, "welcome-sponsor", "/Forms/Wizard");
        d.Add(ParticipantRole.Media, "welcome-media", "/Forms/Wizard");
        d.Add(ParticipantRole.EventPartner, "welcome-eventpartner", "/Forms/Wizard");
        return d;
    }

    private static ServiceProvider BuildServices()
    {
        var dbName = $"welcome-gs-{Guid.NewGuid():N}";
        return new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-welcome-gs"))))
            .AddSingleton<TimeProvider>(new FixedClock())
            .AddScoped<IEmailMagicLinkService, EmailMagicLinkService>()
            .BuildServiceProvider();
    }

    /// <summary>Templates wired with the REAL §169 magic-link seam. <paramref name="usePrivate"/>
    /// renders the private per-edition config copies (config/email-templates) that actually ship;
    /// otherwise the generic shipped defaults are used (an absent private dir so they aren't shadowed).</summary>
    private static EmailTemplateProvider Templates(ServiceProvider sp, bool usePrivate) =>
        new(
            Options.Create(new EmailTemplateOptions
            {
                TemplateDirectory = RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = usePrivate
                    ? RepoPaths.PrivateEmailTemplates()
                    : Path.Combine(Path.GetTempPath(), "ceh-no-private-welcome-gs"),
                HubUrl = Origin,
            }),
            sp.GetRequiredService<IServiceScopeFactory>(),
            emailContext: null);

    private static async Task<int> SeedParticipantAsync(ServiceProvider sp, ParticipantRole role)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var ev = new Event { CommunityName = "Test Community", DisplayName = "Test Community 2027", Code = "TC27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "person@example.com", FullName = "Sample Person",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
            IsEventCoordinator = role == ParticipantRole.Sponsor,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>The <c>/go/{token}</c> token that follows <see cref="Origin"/> in a rendered CTA.</summary>
    private static string ExtractGoToken(string html)
    {
        var marker = $"{Origin}/go/";
        var i = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"expected a {marker} magic-link in the rendered body");
        var start = i + marker.Length;
        var end = start;
        while (end < html.Length && html[end] is not ('"' or '/' or '?' or '<')) end++;
        return html[start..end];
    }

    private static async Task<MagicLinkGrant?> GrantForAsync(ServiceProvider sp, int pid)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        return await db.MagicLinkGrants.SingleOrDefaultAsync(g => g.ParticipantId == pid);
    }

    [Theory]
    [MemberData(nameof(RoleWelcomeRoutes))]
    public async Task Shipped_welcome_cta_deep_links_to_the_roles_get_started_via_the_magic_link(
        ParticipantRole role, string templateKey, string getStartedRoute)
    {
        await using var sp = BuildServices();
        var pid = await SeedParticipantAsync(sp, role);
        var templates = Templates(sp, usePrivate: false);

        var tokens = templates.NewTokenSet(pid);
        tokens["firstName"] = "Sample";
        tokens["eventDisplayName"] = "Test Community 2027";
        tokens["communityName"] = "Test Community";
        tokens["roleName"] = "participant";
        tokens["roleGuidance"] = "guidance";
        tokens["roleLine"] = "role line";
        tokens["sponsorRole"] = "sponsor contact";

        var body = templates.Render(templateKey, tokens).HtmlBody;

        // The hubUrl CTA is the recipient's /go magic-link AND it deep-links to their
        // get-started route → arrives signed-in on Get-Started (§212 via §169).
        var token = ExtractGoToken(body);
        Assert.Contains($"{Origin}/go/{token}{getStartedRoute}", body);

        // Auto-sign-in preserved: the standing magic-link grant was minted.
        var grant = await GrantForAsync(sp, pid);
        Assert.NotNull(grant);
        Assert.True(grant!.MultiUse);
    }

    [Theory]
    [MemberData(nameof(RoleWelcomeRoutes))]
    public async Task Private_edition_welcome_cta_deep_links_to_the_roles_get_started_via_the_magic_link(
        ParticipantRole role, string templateKey, string getStartedRoute)
    {
        // The private config/email-templates copies are the ones actually sent — they must
        // ALSO route through {{hubUrl}} (magic link) to the role's get-started page.
        await using var sp = BuildServices();
        var pid = await SeedParticipantAsync(sp, role);
        var templates = Templates(sp, usePrivate: true);

        var tokens = templates.NewTokenSet(pid);
        tokens["firstName"] = "Sample";
        tokens["eventDisplayName"] = "Test Community 2027";
        tokens["communityName"] = "Test Community";

        var body = templates.Render(templateKey, tokens).HtmlBody;

        var token = ExtractGoToken(body);
        Assert.Contains($"{Origin}/go/{token}{getStartedRoute}", body);
        Assert.NotNull(await GrantForAsync(sp, pid));
    }

    [Fact]
    public async Task Generic_welcome_cta_deep_links_to_the_generic_get_started_wizard()
    {
        // welcome.html is the fallback for roles with no variant (e.g. Organizer) — its CTA
        // deep-links to the generic /Forms/Wizard wizard through the magic link.
        await using var sp = BuildServices();
        var pid = await SeedParticipantAsync(sp, ParticipantRole.Organizer);
        var templates = Templates(sp, usePrivate: false);

        var tokens = templates.NewTokenSet(pid);
        tokens["firstName"] = "Sample";
        tokens["communityName"] = "Test Community";
        tokens["eventDisplayName"] = "Test Community 2027";
        tokens["roleName"] = "organizer";
        tokens["roleGuidance"] = "guidance";

        var body = templates.Render("welcome", tokens).HtmlBody;

        var token = ExtractGoToken(body);
        Assert.Contains($"{Origin}/go/{token}/Forms/Wizard", body);
    }

    [Fact]
    public async Task Attendee_one_day_welcome_cta_deep_links_to_get_started()
    {
        // §208: the 1-day attendee welcome's single task (Party signup) lives on the
        // attendee Get-Started stepper — the CTA deep-links there through the magic link.
        await using var sp = BuildServices();
        var pid = await SeedParticipantAsync(sp, ParticipantRole.Attendee);
        var templates = Templates(sp, usePrivate: false);

        var tokens = templates.NewTokenSet(pid);
        tokens["firstName"] = "Sample";
        tokens["communityName"] = "Test Community";
        tokens["eventDisplayName"] = "Test Community 2027";

        var body = templates.Render("welcome-attendee-1day", tokens).HtmlBody;

        var token = ExtractGoToken(body);
        Assert.Contains($"{Origin}/go/{token}/Forms/Wizard", body);
    }

    [Fact]
    public async Task Welcome_cta_still_targets_get_started_without_a_participant_fail_safe()
    {
        // Fail-safe: with no participant the magic link is not minted, but the CTA still
        // targets the role's get-started route (plain hub URL + route) — never breaks.
        await using var sp = BuildServices();
        await SeedParticipantAsync(sp, ParticipantRole.Speaker);
        var templates = Templates(sp, usePrivate: false);

        var tokens = templates.NewTokenSet();   // no participant
        tokens["firstName"] = "Sample";
        tokens["eventDisplayName"] = "Test Community 2027";
        tokens["communityName"] = "Test Community";

        var body = templates.Render("welcome-speaker", tokens).HtmlBody;

        Assert.Contains($"href=\"{Origin}/Forms/Wizard\"", body);
        Assert.DoesNotContain($"{Origin}/go/", body);
    }
}
