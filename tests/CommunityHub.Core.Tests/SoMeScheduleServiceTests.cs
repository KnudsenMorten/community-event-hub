using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.21 — turning CEH's data into a queue of HELD, scheduled posts.
/// </summary>
/// <remarks>
/// 🔒 The safety property, asserted in more than one way because everything else depends on it:
/// <b>every post this creates is INACTIVE.</b> The dispatcher publishes only an ACTIVE queued post,
/// so the campaign can be planned continuously while nothing reaches the company page until a human
/// turns a row on (§824.8 Q2).
/// </remarks>
public sealed class SoMeScheduleServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-sched-{Guid.NewGuid():N}").Options);

    private static SoMeScheduleService Svc(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        return new SoMeScheduleService(db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock);
    }

    private static async Task SeedAsync(
        CommunityHubDbContext db, int sponsors = 2, int sessions = 1, bool withSettings = true)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        if (withSettings)
        {
            db.SoMeSettings.Add(new SoMeSettings
            {
                EventId = EventId, Enabled = true,
                EventSystemUrl = "https://eldk27.expertslive.dk",
                EventTags = "#ELDK27 #ExpertsLiveDK",
                OrganizerCredits = "Organizer One | Organizer Two",
            });
        }
        for (var i = 1; i <= sponsors; i++)
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = EventId, SponsorCompanyId = $"co-{i}", CompanyName = $"Company {i}",
                SponsorPackage = SponsorPackage.Gold, SocialMediaIntro = "We do things.",
                WebsiteUrl = $"https://company{i}.com",
            });

            // 🔒 §854 — a sponsor is only announceable once their LOGO has produced a graphic
            // ("silver sponsor apento is shown in the list even though they have not uploaded logo
            // yet"). These fixtures are sponsors who HAVE delivered, so they get one — otherwise
            // every sponsor test would be exercising the not-plannable path by accident.
            db.GraphicAssets.Add(new GraphicAsset
            {
                EventId = EventId, Type = GraphicAssetType.Sponsor, SponsorCompanyId = $"co-{i}",
                StableKey = $"sponsor-co-{i}",
                CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }
        for (var i = 1; i <= sessions; i++)
        {
            db.Sessions.Add(new Session
            {
                EventId = EventId, SessionizeId = $"s{i}", Title = $"Session {i}",
                Track = "AI", Type = SessionType.TechnicalSession,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Every_post_it_creates_is_held_for_approval()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var result = await Svc(db).RunAsync(EventId);

        Assert.True(result.Created > 0);
        var posts = await db.SoMePosts.ToListAsync();
        // 🔒 The whole safety model, in one assertion.
        Assert.All(posts, p => Assert.False(p.IsActive));
        Assert.All(posts, p => Assert.Equal(SoMePostStatus.Queued, p.Status));
        Assert.Contains("HELD for approval", result.Message);
    }

    /// <summary>
    /// §848.2 — running twice must not GROW the queue. It may now RE-PLAN it.
    /// </summary>
    /// <remarks>
    /// 🔑 The rule this protects is unchanged — "a timer must never fill the queue with duplicates of
    /// one announcement". What changed is the mechanism: the planner used to skip everything it had
    /// already placed; since §848.2 it discards its own un-accepted PROPOSALS and plans them again,
    /// which is what lets the campaign improve as sponsors, sessions and graphics arrive over months
    /// (<i>"initialy the planner proposes a schedule, then i decide - and then things are locked
    /// down"</i>).
    ///
    /// ⚠️ So the assertion is on the resulting COUNT, not on "created 0". Discarding 9 and creating
    /// 9 is stable; ending with 18 would be the duplicate bug.
    /// </remarks>
    [Fact]
    public async Task Running_twice_does_not_grow_the_queue()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        await svc.RunAsync(EventId);
        var countAfterFirst = await db.SoMePosts.CountAsync();

        await svc.RunAsync(EventId);

        Assert.Equal(countAfterFirst, await db.SoMePosts.CountAsync());

        // And no subject is announced more often than its cadence allows.
        var duplicates = await db.SoMePosts
            .GroupBy(p => new { p.SubjectKey, p.Occurrence })
            .Where(g => g.Count() > 1)
            .CountAsync();

        Assert.Equal(0, duplicates);
    }

    [Fact]
    public async Task An_approval_or_an_edit_survives_the_next_run()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);
        await svc.RunAsync(EventId);

        // He approves one and rewrites another.
        var approved = await db.SoMePosts.OrderBy(p => p.Id).FirstAsync();
        approved.IsActive = true;
        var edited = await db.SoMePosts.OrderBy(p => p.Id).Skip(1).FirstAsync();
        edited.ManualTextOverride = "my own words";
        var movedTo = edited.ScheduledAtUtc.AddDays(3);
        edited.ScheduledAtUtc = movedTo;
        await db.SaveChangesAsync();

        await svc.RunAsync(EventId);

        // ⚠️ The scheduler ADDS; it does not curate. Anything else would undo his work nightly.
        var reloadedApproved = await db.SoMePosts.FindAsync(approved.Id);
        var reloadedEdited = await db.SoMePosts.FindAsync(edited.Id);
        Assert.True(reloadedApproved!.IsActive);
        Assert.Equal("my own words", reloadedEdited!.ManualTextOverride);
        Assert.Equal(movedTo, reloadedEdited.ScheduledAtUtc);
    }

    [Fact]
    public async Task A_new_sponsor_gets_posts_without_disturbing_the_existing_queue()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);
        await svc.RunAsync(EventId);
        var before = await db.SoMePosts
            .Select(p => new { p.Id, p.ScheduledAtUtc, p.SubjectKey }).ToListAsync();

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-NEW", CompanyName = "Newcomer",
            SponsorPackage = SponsorPackage.Gold,
        });
        // §854 — the newcomer has delivered their logo, so they are announceable.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Type = GraphicAssetType.Sponsor, SponsorCompanyId = "co-NEW",
            StableKey = "sponsor-co-NEW",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();

        await svc.RunAsync(EventId);

        // §824.1 — the newcomer is announced TWICE, whatever else the run re-planned.
        var newcomerPosts = await db.SoMePosts.CountAsync(p => p.SubjectKey == "sponsor:co-NEW");
        Assert.Equal(2, newcomerPosts);

        // §848.2 — the OTHER subjects are all still planned. ⚠️ Their exact slots may have MOVED:
        // the planner re-plans its own un-accepted proposals now, and adding a sponsor legitimately
        // changes the spread. What must not happen is a subject losing its posts, so the assertion
        // is on the subjects present, not on the ids and datetimes they had before.
        var subjectsBefore = before.Select(b => b.SubjectKey).Distinct().OrderBy(s => s).ToList();
        var subjectsAfter = await db.SoMePosts
            .Select(p => p.SubjectKey).Distinct().ToListAsync();

        foreach (var s in subjectsBefore)
        {
            Assert.Contains(s, subjectsAfter);
        }

        // …and no two posts share a slot.
        var slots = await db.SoMePosts.Select(p => p.ScheduledAtUtc).ToListAsync();
        Assert.Equal(slots.Count, slots.Distinct().Count());
    }

    [Fact]
    public async Task The_body_is_composed_from_the_edition_template_and_its_data()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        Assert.Contains("Company 1", post.AutoText);
        Assert.Contains("Gold", post.AutoText);
        Assert.Contains("#ELDK27 #ExpertsLiveDK", post.AutoText);
        Assert.Contains("ELDK27 Organizers:", post.AutoText);
        // ◻ {IntroText} has no generator yet — it is KNOWN, so it empties and the gap closes rather
        // than publishing a literal placeholder.
        Assert.DoesNotContain("{", post.AutoText);
    }

    [Fact]
    public async Task An_edited_template_is_what_gets_composed()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);
        var clock = new FixedClock(Now);
        var templates = new SoMeTemplateService(db, clock);
        await templates.SaveAsync(EventId, SoMeTemplateKind.Sponsor, "MY OWN: {SponsorName}", "org@x.dk");

        await new SoMeScheduleService(db, templates, new SoMeVariableResolver(db), clock).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        Assert.Equal("MY OWN: Company 1", post.AutoText);
    }

    [Fact]
    public async Task Each_post_records_what_it_is_about()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 1);

        await Svc(db).RunAsync(EventId);

        var posts = await db.SoMePosts.ToListAsync();
        Assert.All(posts, p => Assert.NotNull(p.TemplateKind));
        Assert.All(posts, p => Assert.False(string.IsNullOrWhiteSpace(p.SubjectKey)));
        Assert.All(posts, p => Assert.NotNull(p.Occurrence));
        // Without these three, "already planned" is unanswerable and the queue fills with duplicates.
        Assert.Contains(posts, p => p.SubjectKey == "sponsor:co-1" && p.Occurrence == 1);
        Assert.Contains(posts, p => p.SubjectKey == "sponsor:co-1" && p.Occurrence == 2);
        Assert.Contains(posts, p => p.SubjectKey!.StartsWith("track:"));
        Assert.Contains(posts, p => p.SubjectKey!.StartsWith("session:"));
        Assert.Contains(posts, p => p.SubjectKey!.StartsWith("tier:"));
    }

    [Fact]
    public async Task Nothing_is_composed_until_the_edition_has_its_post_footer()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 3, sessions: 2, withSettings: false);

        var result = await Svc(db).RunAsync(EventId);

        // 🔒 THE TRAP THIS CLOSES. The scheduler never re-composes — that is what protects his
        // approvals and edits — so whatever a post says when created, it says forever. Running
        // before the footer exists would bake forty footerless posts into the queue, and the gap
        // closes so cleanly that they would read as finished.
        Assert.Equal(0, result.Created);
        Assert.Empty(await db.SoMePosts.ToListAsync());

        // Silence would be the worst outcome: the message says what is missing and where to fix it.
        Assert.Contains("event link", result.Message);
        Assert.Contains("hashtags", result.Message);
        Assert.Contains("organizer credit", result.Message);
        Assert.Contains("SoMeSettings", result.Message);
    }

    [Fact]
    public async Task A_partly_filled_footer_still_blocks_and_names_only_what_is_missing()
    {
        using var db = NewDb();
        await SeedAsync(db, withSettings: false);
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.expertslive.dk",
            EventTags = "#ELDK27",
            // organizer credit still blank
        });
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        Assert.Equal(0, result.Created);
        Assert.Contains("organizer credit", result.Message);
        Assert.DoesNotContain("hashtags", result.Message);   // don't send him hunting for what is fine
    }

    [Fact]
    public async Task An_edition_that_has_already_started_is_not_announced()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "PAST", CommunityName = "ELDK", DisplayName = "Past",
            StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 7, 2), IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        Assert.Equal(0, result.Created);
        Assert.Empty(await db.SoMePosts.ToListAsync());
        Assert.Contains("already started", result.Message);
    }

    [Fact]
    public async Task Nothing_lands_after_the_event_and_the_shortfall_is_reported()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SOON", CommunityName = "ELDK", DisplayName = "Soon",
            // Two weekdays from "now" ⇒ at most 4 slots for far more wanted posts.
            StartDate = new DateOnly(2026, 8, 8), EndDate = new DateOnly(2026, 8, 9), IsActive = true,
        });
        // The footer must exist or §824.23 refuses to compose at all — which is a different test.
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.expertslive.dk",
            EventTags = "#ELDK27", OrganizerCredits = "Organizer One",
        });
        for (var i = 1; i <= 12; i++)
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = EventId, SponsorCompanyId = $"co-{i}", CompanyName = $"Company {i}",
                SponsorPackage = SponsorPackage.Gold,
            });
            // §854 — they must have delivered a logo to be planned at all; this test is about
            // running out of WEEKDAYS, not about sponsors who are not ready.
            db.GraphicAssets.Add(new GraphicAsset
            {
                EventId = EventId, Type = GraphicAssetType.Sponsor, SponsorCompanyId = $"co-{i}",
                StableKey = $"sponsor-co-{i}",
                CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            });
        }
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        var eventStart = SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 8), new TimeOnly(0, 0));
        Assert.All(await db.SoMePosts.ToListAsync(), p => Assert.True(p.ScheduledAtUtc < eventStart));
        // ⚠️ Named, not swallowed: running out of weekdays is a decision someone has to make.
        Assert.NotEmpty(result.NoRoom);
        Assert.Contains("could not fit", result.Message);
    }
}
