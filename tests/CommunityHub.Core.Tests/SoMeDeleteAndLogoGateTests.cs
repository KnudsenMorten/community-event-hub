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
/// §853 (delete is durable) and §1370 (a sponsor with no logo is planned but held; supersedes §854).
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
    /// 🔴 §1370 (supersedes §854) — operator 2026-10-27: sponsors without a logo or text "should
    /// still be planned. once they live up they should be scheduled". Both rounds get a seat, held:
    /// not approved, and the approval gate refuses them until the graphic exists.
    /// </summary>
    [Fact]
    public async Task A_sponsor_without_a_graphic_is_planned_but_held()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        await svc.RunAsync(EventId);

        var waiting = await db.SoMePosts
            .Where(p => p.SubjectKey == "sponsor:co-nologo")
            .OrderBy(p => p.Occurrence)
            .ToListAsync();

        _out.WriteLine("co-nologo posts: " + string.Join(", ",
            waiting.Select(p => $"#{p.Occurrence} {p.ScheduledAtUtc:yyyy-MM-dd} {p.PlanState}")));

        Assert.Equal(new[] { 1, 2 }, waiting.Select(p => p.Occurrence ?? 0).ToArray());
        Assert.All(waiting, p =>
        {
            Assert.False(p.IsActive);
            Assert.Equal(SoMePostPlanState.Proposed, p.PlanState);
            Assert.Null(p.ImageRef);
        });

        var gate = new SoMeApprovalGate(db);
        Assert.NotNull(await gate.BlockedReasonAsync(waiting[0]));
        Assert.True(await db.SoMePosts.AnyAsync(p => p.SubjectKey == "sponsor:co-withlogo"));
    }

    /// <summary>
    /// 🔒 §854.1 — a held sponsor must not become INVISIBLE. §842.5 owes every sponsor two
    /// announcements, so the missing logo is named in the run message for him to chase.
    /// </summary>
    [Fact]
    public async Task A_sponsor_waiting_for_a_graphic_is_reported_by_name()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        var run = await svc.RunAsync(EventId);

        _out.WriteLine(run.Message);

        Assert.Contains("co-nologo", svc.SponsorsWaitingForGraphic);
        Assert.DoesNotContain("co-withlogo", svc.SponsorsWaitingForGraphic);
        Assert.Contains("planned but held", run.Message);
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

    /// <summary>
    /// 🔴 §1368 — a DELETED post is not "no room". PROD 2026-10-26: Surveil's first post was published
    /// and then deleted from the queue; every run reported "sponsor:36 #1 could not fit before the
    /// event", and the new conflict mail sent it to the organizers as a full calendar.
    /// </summary>
    [Fact]
    public async Task A_deleted_post_is_not_reported_as_no_room()
    {
        using var db = NewDb();
        await SeedAsync(db, withGraphic: "co-withlogo");

        var svc = Svc(db);
        await svc.RunAsync(EventId);

        var victim = await db.SoMePosts
            .Where(p => p.SubjectKey == "sponsor:co-withlogo")
            .OrderBy(p => p.Occurrence)
            .FirstAsync();
        victim.IsDeleted = true;
        victim.IsActive = false;
        await db.SaveChangesAsync();

        var run = await svc.RunAsync(EventId);

        Assert.DoesNotContain(run.NoRoom, x => x.StartsWith("sponsor:co-withlogo", StringComparison.Ordinal));
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
