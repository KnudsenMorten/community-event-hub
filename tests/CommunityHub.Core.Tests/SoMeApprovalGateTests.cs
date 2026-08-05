using System;
using System.Threading.Tasks;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §850 — A SPONSOR WHO HAS NOT DELIVERED THEIR SOCIAL-MEDIA TEXT CANNOT BE APPROVED.
///
/// <para>Operator 2026-08-05: <i>"if the sponsor has not delivered the social media text (specific
/// field in ceh per sponsor), then it is not eligible yet, so it cannot be approved (blocker). it is
/// ok, that it is planned, but it can newer be approved"</i>.</para>
///
/// <para>🔑 The distinction under test is PLANNED vs APPROVED. The post is allowed to exist with a
/// date — the campaign still reads as complete and the slot is reserved — but the sponsor's missing
/// deliverable can never reach LinkedIn.</para>
/// </summary>
public sealed class SoMeApprovalGateTests
{
    private const int EventId = 1;
    private const string CompanyId = "co-1";

    private static CommunityHub.Core.Data.CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHub.Core.Data.CommunityHubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <param name="logoFileName">
    /// §867.1 — the SECOND deliverable. Operator 2026-08-05: <i>"there are dependencies so if they
    /// havent upload logo + delivered some text, they are not ready for some posting"</i>.
    /// 🔒 Defaults to a logo BEING present, so the text-focused tests above keep testing the text —
    /// a default of "no logo" would make every one of them pass for the wrong reason.
    /// </param>
    private static async Task SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, string? socialText,
        string? logoFileName = "robopack-logo.png")
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", DisplayName = "Experts Live Denmark 2027",
            CommunityName = "Experts Live Denmark",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = CompanyId, CompanyName = "Robopack",
            SponsorPackage = SponsorPackage.Gold, SocialMediaIntro = socialText,
            LogoRasterFileName = logoFileName,
        });

        await db.SaveChangesAsync();
    }

    private static SoMePost SponsorPost() => new()
    {
        Id = 10, EventId = EventId, Type = SoMePostType.Sponsor,
        TemplateKind = SoMeTemplateKind.Sponsor, SubjectKey = $"sponsor:{CompanyId}",
        Occurrence = 1, ScheduledAtUtc = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
        AutoText = "Welcome Robopack!",
        // ⚠️ SoMePost.IsActive defaults to TRUE on a bare `new`. The PLANNER always sets it false
        // (§824.21a — every planned post is held), so the fixture matches what the planner creates
        // rather than the CLR default, or these tests would start from an already-approved post.
        IsActive = false,
    };

    [Fact]
    public async Task A_sponsor_without_social_media_text_cannot_be_approved()
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(SponsorPost());

        Assert.NotNull(reason);
        // He is told WHAT to fix, not merely that it is refused.
        Assert.Contains("social-media text", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Robopack", reason);
    }

    [Fact]
    public async Task Whitespace_is_not_a_delivered_text()
    {
        // A field someone tabbed through is not a deliverable.
        using var db = NewDb();
        await SeedAsync(db, socialText: "   ");

        Assert.NotNull(await new SoMeApprovalGate(db).BlockedReasonAsync(SponsorPost()));
    }

    [Fact]
    public async Task Once_the_text_is_delivered_the_post_can_be_approved()
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: "Robopack empowers IT teams…");

        Assert.Null(await new SoMeApprovalGate(db).BlockedReasonAsync(SponsorPost()));
    }

    [Fact]
    public async Task A_sponsor_without_a_logo_cannot_be_approved_even_with_text()
    {
        // 🔴 §867.1 — the dependency that was MISSING from this gate. A sponsor could deliver
        // perfect copy and still have no logo, and the post read as ready — while §854 had already
        // established that no logo means no graphic, so the post could not have been made anyway.
        using var db = NewDb();
        await SeedAsync(db, socialText: "Robopack empowers IT teams…", logoFileName: null);

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(SponsorPost());

        Assert.NotNull(reason);
        Assert.Contains("logo", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Robopack", reason);
    }

    [Fact]
    public async Task Both_deliverables_missing_are_named_together()
    {
        // He has to chase BOTH; being told about one, fixing it, and then being refused again for
        // the other is the shape §854 calls out — report everything that is missing, at once.
        using var db = NewDb();
        await SeedAsync(db, socialText: null, logoFileName: null);

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(SponsorPost());

        Assert.NotNull(reason);
        Assert.Contains("social-media text", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logo", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔒 The gate is on APPROVAL only — planning a post is explicitly fine (<i>"it is ok, that it is
    /// planned"</i>), so nothing here may stop a post existing.
    /// </summary>
    [Fact]
    public async Task The_post_is_still_planned_while_it_is_blocked()
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);

        var post = SponsorPost();
        db.SoMePosts.Add(post);
        await db.SaveChangesAsync();

        Assert.NotNull(await new SoMeApprovalGate(db).BlockedReasonAsync(post));

        // It exists, it has its date, and it is simply not approved.
        var stored = await db.SoMePosts.SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), stored.ScheduledAtUtc);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task Non_sponsor_posts_are_never_blocked_by_this_gate()
    {
        // A track, session or event post has no sponsor deliverable to wait for; gating them would
        // stall the campaign for no reason.
        using var db = NewDb();
        await SeedAsync(db, socialText: null);

        var sessionPost = new SoMePost
        {
            Id = 11, EventId = EventId, Type = SoMePostType.Speaker,
            TemplateKind = SoMeTemplateKind.Session, SubjectKey = "session:5", Occurrence = 1,
        };

        Assert.Null(await new SoMeApprovalGate(db).BlockedReasonAsync(sessionPost));
    }

    [Fact]
    public async Task Turning_a_post_OFF_is_never_blocked()
    {
        // ⚠️ A blocker that also stopped him WITHDRAWING a post would be a trap, not a safeguard.
        using var db = NewDb();
        await SeedAsync(db, socialText: null);

        var post = SponsorPost();
        post.IsActive = true;
        db.SoMePosts.Add(post);
        await db.SaveChangesAsync();

        var queue = new SoMeQueueService(db, TimeProvider.System, null, new SoMeApprovalGate(db));

        var problem = await queue.TrySetActiveAsync(EventId, post.Id, isActive: false, "mok@");

        Assert.Null(problem);
        Assert.False((await db.SoMePosts.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Approving_through_the_queue_service_is_refused_with_the_reason()
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);
        db.SoMePosts.Add(SponsorPost());
        await db.SaveChangesAsync();

        var queue = new SoMeQueueService(db, TimeProvider.System, null, new SoMeApprovalGate(db));

        var problem = await queue.TrySetActiveAsync(EventId, 10, isActive: true, "mok@");

        Assert.NotNull(problem);
        // 🔒 And it really did not approve — the refusal is not merely a message.
        Assert.False((await db.SoMePosts.SingleAsync()).IsActive);
    }
}
