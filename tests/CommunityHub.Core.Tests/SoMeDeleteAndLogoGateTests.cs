using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §853 (delete is durable) and §854 (a sponsor with no logo is not planned at all).
/// </summary>
public sealed class SoMeDeleteAndLogoGateTests
{
    private readonly ITestOutputHelper _out;
    public SoMeDeleteAndLogoGateTests(ITestOutputHelper o) => _out = o;

    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <param name="withGraphic">Companies that have uploaded a logo and had a graphic built.</param>
    private static async Task SeedAsync(CommunityHubDbContext db, params string[] withGraphic)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", DisplayName = "Experts Live Denmark 2027",
            CommunityName = "Experts Live Denmark",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });

        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.expertslive.dk",
            EventTags = "#ELDK27", OrganizerCredits = "Morten",
        });

        foreach (var id in new[] { "co-withlogo", "co-nologo" })
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = EventId, SponsorCompanyId = id, CompanyName = id,
                SponsorPackage = SponsorPackage.Silver,
                SocialMediaIntro = "text delivered",
                CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }

        foreach (var id in withGraphic)
        {
            db.GraphicAssets.Add(new GraphicAsset
            {
                EventId = EventId, Type = GraphicAssetType.Sponsor, SponsorCompanyId = id,
                StableKey = $"sponsor-{id}", FileName = $"sponsor-{id}.png",
                CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }

        await db.SaveChangesAsync();
    }

    private static SoMeScheduleService Svc(CommunityHubDbContext db) =>
        new(db,
            new SoMeTemplateService(db, TimeProvider.System),
            new SoMeVariableResolver(db),
            TimeProvider.System,
            NullLogger<SoMeScheduleService>.Instance,
            intro: null);

    /// <summary>
    /// 🔴 §854 — his bug: "silver sponsor apento is shown in the list even though they have not
    /// uploaded logo yet". No logo ⇒ no graphic ⇒ nothing to announce.
    /// </summary>
    [Fact]
    public async Task A_sponsor_without_a_graphic_is_not_planned()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        await svc.RunAsync(EventId);

        var planned = await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.Sponsor)
            .Select(p => p.SubjectKey).Distinct().ToListAsync();

        _out.WriteLine("planned sponsors: " + string.Join(", ", planned));

        Assert.Contains("sponsor:co-withlogo", planned);
        Assert.DoesNotContain("sponsor:co-nologo", planned);
    }

    /// <summary>
    /// 🔒 §854.1 — …but they must not become INVISIBLE. §842.5 owes every sponsor two announcements,
    /// so one silently absent for want of a logo is a breach waiting to happen.
    /// </summary>
    [Fact]
    public async Task A_sponsor_that_cannot_be_planned_is_reported_by_name()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        var run = await svc.RunAsync(EventId);

        _out.WriteLine(run.Message);

        Assert.Contains("co-nologo", svc.NotPlannableSponsors);
        Assert.DoesNotContain("co-withlogo", svc.NotPlannableSponsors);
        Assert.Contains("could NOT be planned", run.Message);
    }

    /// <summary>
    /// 🔴 §853.1 — a plain delete would LIE: since §848.2 the planner re-plans its own proposals, so
    /// a deleted proposal would reappear on the next run. Delete has to be durable.
    /// </summary>
    [Fact]
    public async Task A_deleted_post_is_not_proposed_again()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        await svc.RunAsync(EventId);

        var victim = await db.SoMePosts
            .Where(p => p.SubjectKey == "sponsor:co-withlogo")
            .OrderBy(p => p.Occurrence)
            .FirstAsync();

        var deletedOccurrence = victim.Occurrence;
        victim.IsDeleted = true;
        victim.IsActive = false;
        await db.SaveChangesAsync();

        // The planner runs again — as it does hourly.
        await svc.RunAsync(EventId);

        var live = await db.SoMePosts
            .Where(p => p.SubjectKey == "sponsor:co-withlogo" && !p.IsDeleted)
            .Select(p => p.Occurrence)
            .ToListAsync();

        _out.WriteLine("surviving occurrences: " + string.Join(", ", live));

        // 🔒 The deleted occurrence stays gone…
        Assert.DoesNotContain(deletedOccurrence, live);
        // …and the sponsor's OTHER post is untouched: deleting one must not cancel the rest.
        Assert.NotEmpty(live);
    }

    [Fact]
    public async Task Restoring_brings_the_post_back_into_the_campaign()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        await svc.RunAsync(EventId);

        var victim = await db.SoMePosts.FirstAsync(p => p.SubjectKey == "sponsor:co-withlogo");
        victim.IsDeleted = true;
        await db.SaveChangesAsync();
        await svc.RunAsync(EventId);

        // He changes his mind.
        victim.IsDeleted = false;
        await db.SaveChangesAsync();
        await svc.RunAsync(EventId);

        var live = await db.SoMePosts
            .CountAsync(p => p.SubjectKey == "sponsor:co-withlogo" && !p.IsDeleted);

        // Both contractual posts are back (§842.5).
        Assert.Equal(2, live);
    }
}
