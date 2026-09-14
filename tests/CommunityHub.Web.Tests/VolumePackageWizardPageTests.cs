using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Pages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1077 stage 3 — the public wizard page: what the token opens, and what it refuses.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This is the hub's only anonymous page that WRITES.</b> The §1040 monitor page is
/// anonymous and read-only; this one saves what a company chooses. These tests hold the gate on
/// every entry point — <b>each handler resolves the token itself</b>, so a POST cannot be aimed at a
/// revoked link just because a GET happened to work earlier in the session.</para>
///
/// <para>⚠️ Unknown, revoked and expired all answer <b>404</b>, identically and on purpose:
/// distinguishing them would confirm to a stranger that a token is real.</para>
/// </remarks>
public sealed class VolumePackageWizardPageTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-wizard-page-{Guid.NewGuid():N}").Options);

    private static VolumePackageWizardService NewWizard(CommunityHubDbContext db) =>
        new(db, new NullSharePointFileStore(), TestDocLibrary.Resolver());

    /// <summary>§1077 stage 4 — the page also reads the group-photo slot (none in these fixtures).</summary>
    private static VolumePackageGroupPhotoService NewPhoto(CommunityHubDbContext db) =>
        new(db, new VolumePackageQualificationService(db));

    private static async Task<VolumePackageCompany> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        var c = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
            ApproverEmail = "bea@globeteam.dk",
        };
        db.VolumePackageCompanies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task A_live_link_opens_the_wizard_and_the_visit_is_recorded()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var wizard = NewWizard(db);
        var token = await wizard.IssueTokenAsync(company.Id);

        var page = new VolumePackageWizardModel(wizard, NewPhoto(db));
        var result = await page.OnGetAsync(token!, 1, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Globeteam", page.Company!.CustomName);
        // An organizer needs to see whether the link is being used before chasing or revoking it.
        Assert.Equal(1, (await db.VolumePackageCompanies.SingleAsync()).WizardOpenCount);
    }

    /// <summary>
    /// 🔒 Unknown, revoked and expired are all 404 — the same answer, so a stranger learns nothing
    /// about whether a token exists.
    /// </summary>
    [Fact]
    public async Task Unknown_and_revoked_links_are_both_a_plain_404()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var wizard = NewWizard(db);
        var token = await wizard.IssueTokenAsync(company.Id);
        await wizard.RevokeTokenAsync(company.Id);

        var page = new VolumePackageWizardModel(wizard, NewPhoto(db));

        Assert.IsType<NotFoundResult>(await page.OnGetAsync(token!, 1, CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await page.OnGetAsync("definitely-not-a-token", 1, CancellationToken.None));
    }

    /// <summary>
    /// 🔴 <b>Every POST re-checks the token.</b> Otherwise a revoked link stays usable for anyone
    /// who kept the page open — the revocation would be cosmetic, which is worse than none, because
    /// an organizer would believe the link was dead.
    /// </summary>
    [Fact]
    public async Task Every_write_handler_refuses_a_revoked_link()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var wizard = NewWizard(db);
        var token = await wizard.IssueTokenAsync(company.Id);
        await wizard.RevokeTokenAsync(company.Id);

        var page = new VolumePackageWizardModel(wizard, NewPhoto(db)) { Keynote = true };

        Assert.IsType<NotFoundResult>(await page.OnPostParticipationAsync(token!, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await page.OnPostCoordinatorAsync(token!, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await page.OnPostLogoAsync(token!, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await page.OnPostFinishAsync(token!, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await page.OnPostDeclineAsync(token!, CancellationToken.None));

        // Nothing was written by any of them.
        var saved = await db.VolumePackageCompanies.SingleAsync();
        Assert.False(saved.ApprovedKeynoteMention);
        Assert.Null(saved.WizardCompletedAt);
        Assert.Null(saved.DeclinedAt);
    }

    [Fact]
    public async Task Step_one_saves_the_choices_and_moves_on()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var wizard = NewWizard(db);
        var token = await wizard.IssueTokenAsync(company.Id);

        var page = new VolumePackageWizardModel(wizard, NewPhoto(db)) { Keynote = true, GroupPhoto = true };
        var result = await page.OnPostParticipationAsync(token!, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(2, redirect.RouteValues!["step"]);

        var saved = await db.VolumePackageCompanies.SingleAsync();
        Assert.True(saved.ApprovedKeynoteMention);
        Assert.False(saved.ApprovedSocialMediaAnnouncement);
        Assert.True(saved.ApprovedGroupPhoto);
    }

    /// <summary>
    /// The explicit opt-out ends the wizard — and stage 4's reminders read <c>DeclinedAt</c>, so
    /// stopping the asking is carried by the same field rather than a second flag to keep in step.
    /// </summary>
    [Fact]
    public async Task Declining_finishes_the_wizard_and_says_so()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var wizard = NewWizard(db);
        var token = await wizard.IssueTokenAsync(company.Id);

        var page = new VolumePackageWizardModel(wizard, NewPhoto(db));
        Assert.IsType<PageResult>(await page.OnPostDeclineAsync(token!, CancellationToken.None));

        var saved = await db.VolumePackageCompanies.SingleAsync();
        Assert.NotNull(saved.DeclinedAt);
        Assert.NotNull(saved.WizardCompletedAt);
    }

    /// <summary>Step 4 records LinkedIn and marks the wizard done.</summary>
    [Fact]
    public async Task Finishing_records_linkedin_and_completes()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var wizard = NewWizard(db);
        var token = await wizard.IssueTokenAsync(company.Id);

        var page = new VolumePackageWizardModel(wizard, NewPhoto(db))
        {
            LinkedInUrl = "https://www.linkedin.com/company/globeteam",
        };
        Assert.IsType<PageResult>(await page.OnPostFinishAsync(token!, CancellationToken.None));

        var saved = await db.VolumePackageCompanies.SingleAsync();
        Assert.Equal("https://www.linkedin.com/company/globeteam", saved.LinkedInUrl);
        Assert.NotNull(saved.WizardCompletedAt);
        Assert.True(page.Finished);
    }
}
