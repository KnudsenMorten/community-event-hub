using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077.9 — the post-event thank-you: the pictures link and the LinkedIn tagging ask.
/// </summary>
/// <remarks>
/// <para>The operator's own wording (2026-08-11), and the last mail in the volume-package story.</para>
///
/// <para>🔴 <b>The refusals are the point.</b> It must not thank a company that DECLINED (they took
/// no benefits), and it must not go twice — a thank-you that arrives a second time stops being a
/// thank-you and becomes a mailing list.</para>
/// </remarks>
public sealed class VolumePackagePostEventMailTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-post-{Guid.NewGuid():N}").Options);

    private static string WriteConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceh-post-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "event.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string FullConfig = """
    {
      "code": "T27",
      "postEvent": {
        "picturesUrl": "https://pictures.test/2027",
        "linkedInFollowers": "approximately 30K",
        "linkedIn": [
          { "name": "First Person", "url": "https://linkedin.test/first" },
          { "name": "The company page", "url": "https://linkedin.test/company" }
        ]
      }
    }
    """;

    private static (VolumePackagePostEventMailService Svc, CapturingEmailSender Mail) New(
        CommunityHubDbContext db, string? configJson = null)
    {
        var mail = new CapturingEmailSender();
        var options = new EventConfigOptions
        {
            EventConfigPath = configJson is null ? string.Empty : WriteConfig(configJson),
        };
        return (new VolumePackagePostEventMailService(
            db, mail, new EmailContextAccessor(), new EventEditionConfigLoader(), options), mail);
    }

    private static async Task<VolumePackageCompany> SeedAsync(
        CommunityHubDbContext db, Action<VolumePackageCompany>? tweak = null)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "Experts Live Denmark",
            DisplayName = "ELDK27", IsActive = true,
        });
        var company = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
            ApproverEmail = "bea@globeteam.dk", ApproverName = "Bea Buyer",
            WizardCompletedAt = DateTimeOffset.UtcNow,
        };
        tweak?.Invoke(company);
        db.VolumePackageCompanies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    [Fact]
    public async Task It_thanks_them_and_carries_the_pictures_link_and_the_profiles()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail) = New(db, FullConfig);

        var result = await svc.SendAsync(company.Id);

        Assert.True(result.Sent);
        var sent = Assert.Single(mail.Messages);
        Assert.Equal("bea@globeteam.dk", sent.To);
        Assert.Contains("Thank you", sent.Subject);
        Assert.Contains("https://pictures.test/2027", sent.Html);
        Assert.Contains("approximately 30K", sent.Html);
        Assert.Contains("https://linkedin.test/first", sent.Html);
        Assert.Contains("The company page", sent.Html);
        Assert.Contains("next year", sent.Html);
    }

    /// <summary>
    /// 🔑 No configured gallery ⇒ the paragraph is LEFT OUT. A thank-you promising photographs at a
    /// link that 404s is worse than one that does not mention them.
    /// </summary>
    [Fact]
    public async Task With_no_configured_gallery_the_pictures_paragraph_is_left_out()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail) = New(db);   // no config file at all

        Assert.True((await svc.SendAsync(company.Id)).Sent);

        var html = Assert.Single(mail.Messages).Html;
        Assert.DoesNotContain("available here", html);
        Assert.DoesNotContain("tag us", html);
        // …but the thank-you itself still goes.
        Assert.Contains("Thank you again", html);
    }

    /// <summary>
    /// 🔴 A company that declined took no benefits. Thanking them "for qualifying for the volume
    /// package benefits, including the group photo" would thank them for what they turned down.
    /// </summary>
    [Fact]
    public async Task A_company_that_declined_is_never_thanked()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, c => c.DeclinedAt = DateTimeOffset.UtcNow);
        var (svc, mail) = New(db, FullConfig);

        var result = await svc.SendAsync(company.Id);

        Assert.False(result.Sent);
        Assert.Contains("declined", result.Problem);
        Assert.Empty(mail.Sent);
    }

    /// <summary>🔒 Once only — the stamp is what makes a thank-you a thank-you.</summary>
    [Fact]
    public async Task It_is_sent_once_and_then_refuses()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail) = New(db, FullConfig);

        Assert.True((await svc.SendAsync(company.Id)).Sent);
        var second = await svc.SendAsync(company.Id);

        Assert.False(second.Sent);
        Assert.Contains("Already sent", second.Problem);
        Assert.Single(mail.Messages);
        Assert.NotNull((await db.VolumePackageCompanies.SingleAsync()).PostEventMailSentAt);
    }

    [Fact]
    public async Task With_no_approver_there_is_nobody_to_thank()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, c => c.ApproverEmail = null);
        var (svc, mail) = New(db, FullConfig);

        Assert.False((await svc.SendAsync(company.Id)).Sent);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// ⚠️ A malformed config must not stop a thank-you an organizer has decided to send: the mail
    /// loses its optional paragraphs instead of throwing at the person who pressed the button.
    /// </summary>
    [Fact]
    public async Task A_broken_config_costs_the_paragraphs_not_the_mail()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail) = New(db, "{ this is not json");

        Assert.True((await svc.SendAsync(company.Id)).Sent);
        Assert.Contains("Thank you again", Assert.Single(mail.Messages).Html);
    }
}
