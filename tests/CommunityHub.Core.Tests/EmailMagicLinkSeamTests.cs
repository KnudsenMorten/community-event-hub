using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §169 CENTRAL seam: <see cref="EmailTemplateProvider.NewTokenSet"/> rewrites
/// the shared <c>hubUrl</c> CTA token to the addressed participant's personal
/// auto-login magic-link, so EVERY templated email that renders <c>{{hubUrl}}</c>
/// carries the recipient's sign-in link — without each sender doing it. Also pins
/// the fail-safe: with no participant (mass mail) the plain hub URL is kept.
/// </summary>
public class EmailMagicLinkSeamTests
{
    private const string Origin = "https://hub.example";

    private static ServiceProvider BuildProvider(string dbName) =>
        new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-emailmagic-seam"))))
            .AddSingleton(TimeProvider.System)
            .AddScoped<IEmailMagicLinkService, EmailMagicLinkService>()
            .BuildServiceProvider();

    private static EmailTemplateProvider NewProvider(ServiceProvider sp)
    {
        var opts = Options.Create(new EmailTemplateOptions { HubUrl = Origin });
        return new EmailTemplateProvider(opts, sp.GetRequiredService<IServiceScopeFactory>(), emailContext: null);
    }

    private static async Task<int> SeedAsync(ServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "person@example.com", FullName = "Test Person",
            Role = ParticipantRole.Speaker, IsActive = true, IsTestUser = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task NewTokenSet_for_a_participant_rewrites_hubUrl_to_their_magic_link()
    {
        await using var sp = BuildProvider($"seam-{Guid.NewGuid():N}");
        var pid = await SeedAsync(sp);
        var templates = NewProvider(sp);

        var tokens = templates.NewTokenSet(pid);

        Assert.StartsWith($"{Origin}/go/", tokens["hubUrl"]);
        Assert.Equal(tokens["hubUrl"], tokens["magicHubUrl"]);

        // The hubUrl carries the participant's standing token (a grant was minted).
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var grant = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == pid);
        Assert.Equal(EmailMagicLinkService.PurposeName, grant.Purpose);
        Assert.True(grant.MultiUse);
    }

    [Fact]
    public async Task NewTokenSet_without_a_participant_keeps_the_plain_hub_url()
    {
        await using var sp = BuildProvider($"seam-{Guid.NewGuid():N}");
        await SeedAsync(sp);
        var templates = NewProvider(sp);

        var tokens = templates.NewTokenSet();   // mass-mail / no participant

        Assert.Equal(Origin, tokens["hubUrl"]); // plain origin, no /go token
        Assert.False(tokens.ContainsKey("magicHubUrl"));
    }
}
