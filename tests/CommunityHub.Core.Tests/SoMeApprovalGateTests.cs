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

        // 🔴 §1060(g) — a subject with no RELEASED graphic is now blocked outright ("hard block").
        // ⚠️ This is a fixture completion, not a weakening: the sponsor-deliverable tests still get
        // their own blocker, because the graphic is asked LAST precisely so a chaseable problem
        // (missing text/logo) is never hidden behind one the sponsor cannot act on.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor,
            SponsorCompanyId = CompanyId, StableKey = $"sponsor:{CompanyId}",
            FileName = "robopack-some.png",
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
        // A track, session or event post has no SPONSOR deliverable to wait for; gating them on one
        // would stall the campaign for no reason.
        using var db = NewDb();
        await SeedAsync(db, socialText: null);

        // 🔒 §926 — the session must carry an abstract, or the OTHER rule blocks it and this test
        // would pass for the wrong reason. Session 5 does not exist in the seed, so the sponsor
        // half is what is being measured either way; adding it makes that explicit.
        db.Sessions.Add(new Session
        {
            Id = 5, EventId = EventId, Title = "Deep Dive: Entra ID",
            Abstract = "What we cover, and who it is for.",
        });

        // 🔴 §1060(g) — AND NOW IT NEEDS A GRAPHIC TOO, for exactly the reason the comment above
        // gives about the abstract: without one, the graphic rule blocks the post and this test
        // would pass for the wrong reason (or fail for one). The test's claim is unchanged — no
        // SPONSOR deliverable gates a session post — but "never blocked by this gate" is no longer
        // true in general, and the fixture has to say which rule it is holding still.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Session,
            SessionId = 5, StableKey = "session:5", FileName = "session-5.png",
        });
        await db.SaveChangesAsync();

        var sessionPost = new SoMePost
        {
            Id = 11, EventId = EventId, Type = SoMePostType.Speaker,
            TemplateKind = SoMeTemplateKind.Session, SubjectKey = "session:5", Occurrence = 1,
        };

        Assert.Null(await new SoMeApprovalGate(db).BlockedReasonAsync(sessionPost));
    }

    /// <summary>
    /// 🔴 §926 — A SESSION WITH NO DESCRIPTION IS NOT READY. Operator 2026-08-06: <i>"master class
    /// announcement and sessions has dependency to description. if empty it is not ready"</i>.
    /// </summary>
    /// <remarks>
    /// ⚠️ §922's general empty-variable rule CANNOT catch this: his 38 session wordings never print
    /// {SessionAbstract}, they print the AI teaser — and the abstract is what that teaser is written
    /// FROM. Measured the same day: "ELDK27 Welcome" has no abstract and had already been given an
    /// invented teaser ("where the energy is high and the community comes alive"), which §911 would
    /// then have stored and reused for ever.
    /// </remarks>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("A real description of the talk.", false)]
    public async Task Session_post_waits_for_the_session_description(string? text, bool blocked)
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);

        db.Sessions.Add(new Session
        {
            Id = 7, EventId = EventId, Title = "Master Class: Identity", Abstract = text,
        });

        // §1060(g) — a released graphic, so the `blocked: false` case measures the DESCRIPTION rule
        // and not the graphic one. The blocked cases are unaffected either way: the description is
        // asked first, because it is the blocker a human can clear.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Session,
            SessionId = 7, StableKey = "session:7", FileName = "session-7.png",
        });
        await db.SaveChangesAsync();

        var post = new SoMePost
        {
            Id = 12, EventId = EventId, Type = SoMePostType.Speaker,
            TemplateKind = SoMeTemplateKind.Session, SubjectKey = "session:7", Occurrence = 1,
        };

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(post);

        if (blocked)
        {
            Assert.NotNull(reason);
            // §850 — a refusal carries its reason, in words he can act on.
            Assert.Contains("description", reason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(reason);
        }
    }

    // ---- §1060(m) — session types whose SHAPE decides eligibility -----------

    /// <summary>
    /// 🔴 ONE linked speaker on a co-presented format is the dangerous case — not zero.
    /// Zero fails loudly elsewhere; exactly one looks complete on every screen and would announce a
    /// co-taught session as a solo talk, publicly, naming the wrong people.
    /// </summary>
    [Theory]
    [InlineData(SessionType.MasterClass, 1, true)]
    [InlineData(SessionType.PanelDiscussion, 1, true)]
    [InlineData(SessionType.MasterClass, 2, false)]
    [InlineData(SessionType.PanelDiscussion, 3, false)]
    // ⚠️ The control cases. One speaker is entirely normal here, and a rule applied to every type
    // would block most of the campaign — so the gate has to be measured against a type it must
    // NOT touch, or "blocks a solo master class" and "blocks everything" look identical.
    [InlineData(SessionType.TechnicalSession, 1, false)]
    [InlineData(SessionType.Keynote, 1, false)]
    public async Task A_co_presented_format_needs_more_than_one_linked_speaker(
        SessionType type, int speakers, bool blocked)
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);
        await SeedSessionAsync(db, id: 21, type: type, speakerCount: speakers);

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(SessionPost(21));

        if (blocked)
        {
            Assert.NotNull(reason);
            Assert.Contains("speaker", reason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(reason);
        }
    }

    /// <summary>
    /// §1218 — the organizer's one-speaker override lifts the co-presented block, but only for
    /// exactly ONE linked speaker: zero still has nobody to announce, confirmed or not. And the
    /// override is offered (SingleSpeakerOverrideAsync) on exactly the sessions the rule applies to.
    /// </summary>
    [Theory]
    [InlineData(SessionType.MasterClass, 1, true, false, true)]
    [InlineData(SessionType.PanelDiscussion, 1, true, false, true)]
    [InlineData(SessionType.MasterClass, 1, false, true, true)]
    [InlineData(SessionType.MasterClass, 0, true, true, false)]
    [InlineData(SessionType.MasterClass, 2, false, false, false)]
    [InlineData(SessionType.TechnicalSession, 1, true, false, false)]
    public async Task The_one_speaker_override_lifts_the_co_presented_block_for_exactly_one_speaker(
        SessionType type, int speakers, bool confirmed, bool blocked, bool offered)
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);
        await SeedSessionAsync(db, id: 24, type: type, speakerCount: speakers);

        var session = await db.Sessions.FindAsync(24);
        session!.SoMeSingleSpeakerConfirmed = confirmed;
        await db.SaveChangesAsync();

        var gate = new SoMeApprovalGate(db);
        var reason = await gate.BlockedReasonAsync(SessionPost(24));
        var offer = await gate.SingleSpeakerOverrideAsync(SessionPost(24));

        if (blocked) Assert.NotNull(reason); else Assert.Null(reason);
        Assert.Equal(offered, offer is not null);
        if (offer is not null) Assert.Equal(confirmed, offer.Confirmed);
    }

    /// <summary>
    /// 🛑 Ask-the-Experts is never announced — and the SENTENCE matters as much as the refusal.
    /// Every other blocker here ends "…becomes approvable", because every other one is clearable.
    /// This one is permanent, so it must not read as a to-do that sends someone hunting for a fix.
    /// </summary>
    [Fact]
    public async Task Ask_the_experts_is_refused_permanently_and_does_not_read_as_a_to_do()
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);
        await SeedSessionAsync(db, id: 22, type: SessionType.AskTheExperts, speakerCount: 3);

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(SessionPost(22));

        Assert.NotNull(reason);
        Assert.Contains("never announced", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("becomes approvable", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- §1060(l) — the AI's STORED verdict ---------------------------------

    /// <summary>
    /// 🔴 The fail-direction, and the most important test in this file.
    /// <c>null</c> means NEVER JUDGED, and must not block: on a host with no model configured — DEV,
    /// or PROD before the first daily sweep — a blocking null would silently stop the whole campaign.
    /// A <c>false</c> blocks and quotes the model's reason; a <c>true</c> passes.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task The_stored_AI_verdict_blocks_only_on_an_explicit_no(bool? verdict, bool blocked)
    {
        using var db = NewDb();
        await SeedAsync(db, socialText: null);
        await SeedSessionAsync(db, id: 23, type: SessionType.TechnicalSession, speakerCount: 1);

        var session = await db.Sessions.FindAsync(23);
        session!.SoMeTextEligible = verdict;
        session.SoMeTextEligibleReason = "says the abstract will follow later";
        await db.SaveChangesAsync();

        var reason = await new SoMeApprovalGate(db).BlockedReasonAsync(SessionPost(23));

        if (blocked)
        {
            Assert.NotNull(reason);
            // The model's own words reach the person who has to act (§854).
            Assert.Contains("abstract will follow later", reason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(reason);
        }
    }

    private static SoMePost SessionPost(int sessionId) => new()
    {
        Id = 100 + sessionId, EventId = EventId, Type = SoMePostType.Speaker,
        TemplateKind = SoMeTemplateKind.Session, SubjectKey = $"session:{sessionId}", Occurrence = 1,
    };

    /// <summary>A session with a real abstract, a released graphic, and N linked speakers — so the
    /// ONLY thing the calling test varies is the rule it names.</summary>
    private static async Task SeedSessionAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int id, SessionType type, int speakerCount)
    {
        db.Sessions.Add(new Session
        {
            Id = id, EventId = EventId, Title = $"Session {id}", Type = type,
            Abstract = "A practical hour on Kubernetes cost control, with live demos.",
        });
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Session,
            SessionId = id, StableKey = $"session:{id}", FileName = $"session-{id}.png",
        });

        for (var i = 0; i < speakerCount; i++)
        {
            var pid = id * 100 + i;
            db.Participants.Add(new Participant
            {
                Id = pid, EventId = EventId, Email = $"s{pid}@test.dk", FullName = $"Speaker {pid}",
                Role = ParticipantRole.Speaker,
            });
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = id, ParticipantId = pid });
        }

        await db.SaveChangesAsync();
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
