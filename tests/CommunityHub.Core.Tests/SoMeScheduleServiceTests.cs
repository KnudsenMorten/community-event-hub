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
                EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor, SponsorCompanyId = $"co-{i}",
                StableKey = $"sponsor-co-{i}", FileName = $"sponsor-co-{i}.png",
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

    /// <summary>
    /// 🔴 §905 — A TEST FIXTURE IS NEVER ANNOUNCED. Operator 2026-08-06:
    /// <i>"all have the test flag but some service doesn't handle it"</i>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not hypothetical: two posts announcing "Test Exhibitor Session Preday" / "…MainDay" were
    /// found queued on PROD for January and February 2027. They were held, so only the approval gate
    /// stood between a fixture and the company page — and a gate a human has to remember is not a
    /// control.
    /// <para>🔒 The test session here is ACTIVE, because that is the state a real fixture is in. The
    /// two on PROD happened to be deactivated as well, which is what kept them out of the post TEXT
    /// while they were still rendered into the track GIF.</para>
    /// </remarks>
    [Fact]
    public async Task A_session_whose_whole_lineup_is_test_accounts_is_not_announced()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 1);

        var tester = new Participant
        {
            EventId = EventId, Email = "test-speaker@expertslive.dk",
            FullName = "Test-Speaker-Exhibitor", IsTestUser = true, IsActive = true,
        };
        db.Participants.Add(tester);
        var testSession = new Session
        {
            EventId = EventId, SessionizeId = "s-test", Title = "Test Exhibitor Session Preday",
            Track = "TestOnlyTrack", Type = SessionType.TechnicalSession,
        };
        db.Sessions.Add(testSession);
        await db.SaveChangesAsync();

        db.SessionSpeakers.Add(new SessionSpeaker
        {
            SessionId = testSession.Id, ParticipantId = tester.Id,
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var subjects = await db.SoMePosts.Select(p => p.SubjectKey).ToListAsync();

        // No session post for the fixture…
        Assert.DoesNotContain($"session:{testSession.Id}", subjects);
        // …and no TRACK post for a track that only exists because of it.
        Assert.DoesNotContain("track:TestOnlyTrack", subjects);
        // The real session is still announced — this must not have swept the campaign clean.
        Assert.Contains(subjects, s => s != null && s.StartsWith("session:", StringComparison.Ordinal));
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
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor, SponsorCompanyId = "co-NEW",
            StableKey = "sponsor-co-NEW", FileName = "sponsor-co-NEW.png",
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

    /// <summary>
    /// 🔴 §901 — THE PLANNER STORES THE TEMPLATE, NOT THE RENDERING.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This test used to assert the exact opposite</b> — that <c>AutoText</c> came back with
    /// "Company 1" in it and <c>DoesNotContain("{")</c>. It passed, and it was pinning the defect:
    /// a post frozen at plan time publishes August's data in January, and one whose speaker list was
    /// empty when it was planned can never repair itself. The values it asserted are still asserted,
    /// one test down — through the composer, which is where resolution belongs.
    /// </remarks>
    [Fact]
    public async Task The_body_is_stored_as_the_template_so_it_can_resolve_later()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);

        // The tokens are still tokens. §892 converted 82 bodies to exactly this shape for ELDK28
        // reuse; the planner minting rendered replacements is what was undoing that work.
        Assert.Contains("{SponsorName}", post.AutoText);
        Assert.Contains("{EventTags}", post.AutoText);

        // …and nothing has been resolved INTO the stored body.
        Assert.DoesNotContain("Company 1", post.AutoText);
        Assert.DoesNotContain("#ELDK27 #ExpertsLiveDK", post.AutoText);
    }

    /// <summary>§901 — the round trip: what is stored resolves to what used to be stored.</summary>
    /// <remarks>
    /// 🔑 The point of the change is that this resolution happens at PUBLISH, against today's data,
    /// rather than at plan time against the day the post was created.
    /// </remarks>
    [Fact]
    public async Task The_stored_template_resolves_to_the_real_values_at_compose_time()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        var composer = new SoMePostComposer(db, new SoMeVariableResolver(db));
        var body = composer.Resolve(post.EffectiveText, await composer.ValuesForAsync(post));

        Assert.Contains("Company 1", body);
        Assert.Contains("Gold", body);
        Assert.Contains("#ELDK27 #ExpertsLiveDK", body);
        Assert.Contains("ELDK27 Organizers:", body);

        // 🔒 No token survives composition — including {IntroText}, which has no generator wired in
        // this fixture. It is KNOWN (§901: read back from the post's own column), so it empties and
        // the renderer closes the gap rather than publishing a literal placeholder.
        Assert.DoesNotContain("{", body);
    }

    /// <summary>
    /// 🔴 §915 — THE PICTURE IS ATTACHED AT BIRTH, so he never picks it from a dropdown.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-06: <i>"you need to pre-stage the linking + picture, so i dont have to do
    /// that"</i>. The post always knew its SUBJECT (that is what fills the variables), but only
    /// Type 5 carried an ImageRef — so every track, session, tier and sponsor post arrived with an
    /// empty picture field.
    /// <para>🔑 The join is the part worth pinning: the graphic store keys a track by SLUG
    /// (<c>track:ai-for-makers-copilot-agents</c>) while the post keys it by DISPLAY NAME
    /// (<c>track:AI for Makers (Copilot &amp; Agents)</c>). Slugging is lossy and cannot be
    /// reversed, so the match is made by slugging the post's key.</para>
    /// </remarks>
    [Fact]
    public async Task A_planned_post_arrives_with_its_subjects_graphic_already_attached()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        // A track whose display name slugs lossily — the case a naive string match gets wrong.
        var session = new Session
        {
            EventId = EventId, SessionizeId = "s-ai", Title = "Build your own AI",
            Track = "AI for Makers (Copilot & Agents)", Type = SessionType.TechnicalSession,
        };
        db.Sessions.Add(session);
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.TrackBundle,
            StableKey = "track:ai-for-makers-copilot-agents",
            FileName = "track-ai-for-makers-copilot-agents.gif",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor, SponsorCompanyId = "co-1",
            StableKey = "sponsor:co-1", FileName = "sponsor-co-1.png",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var trackPost = await db.SoMePosts
            .FirstOrDefaultAsync(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks);
        Assert.NotNull(trackPost);
        Assert.Equal("track-ai-for-makers-copilot-agents.gif", trackPost!.ImageRef);

        var sponsorPost = await db.SoMePosts
            .FirstOrDefaultAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        Assert.NotNull(sponsorPost);
        Assert.Equal("sponsor-co-1.png", sponsorPost!.ImageRef);
    }

    /// <summary>🔒 §915 — a subject with no graphic yet is an ordinary state, not an error.</summary>
    [Fact]
    public async Task A_subject_with_no_graphic_yet_simply_has_no_picture()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 0, sessions: 1);

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts
            .FirstOrDefaultAsync(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks);
        Assert.NotNull(post);
        Assert.Null(post!.ImageRef);
    }

    /// <summary>Counts how many times the AI endpoint is actually asked for a teaser.</summary>
    private sealed class CountingIntroHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"A teaser.\"}}]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static SoMeScheduleService SvcWithIntro(
        CommunityHubDbContext db, CountingIntroHandler handler)
    {
        var clock = new FixedClock(Now);
        var intro = new SoMeIntroGenerator(
            new HttpClient(handler),
            new CommunityHub.Core.Assistant.OpenAiOptions
            {
                Enabled = true, Endpoint = "https://example.openai.azure.com",
                Deployment = "gpt", ApiKey = "k",
            });
        return new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock, null, intro);
    }

    /// <summary>
    /// 🔴 §911 — THE TEASER IS WRITTEN ONCE AND REUSED. This is what makes the feature affordable.
    /// </summary>
    /// <remarks>
    /// The planner discards and re-plans its proposals on EVERY tick (§848.2, every 5 minutes).
    /// Generating unconditionally meant ~78 AI calls per run forever — which is what killed the run
    /// on 2026-08-06 and emptied his queue (§906). The endpoint was never the problem; the COUNT was.
    /// <para>🔒 A teaser belongs to (subject, occurrence), not to a row id, so it survives the row
    /// being discarded and re-created.</para>
    /// </remarks>
    [Fact]
    public async Task The_teaser_is_generated_once_and_reused_on_later_runs()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 1);
        var handler = new CountingIntroHandler();

        await SvcWithIntro(db, handler).RunAsync(EventId);
        var afterFirst = handler.Calls;
        Assert.True(afterFirst > 0, "the first run should write some teasers");

        // Every post whose BODY uses a teaser now carries one.
        // ⚠️ §923 — not "every non-EventPost": the shipped Sponsor template has no {IntroText} at
        // all, so a sponsor post correctly has none and asking for one would be the waste §923
        // removed.
        Assert.All(
            await db.SoMePosts.Where(p => p.AutoText.Contains("{IntroText}")).ToListAsync(),
            p => Assert.False(string.IsNullOrWhiteSpace(p.IntroText)));

        // 🔑 Re-plan. The rows are discarded and re-created — and NOT ONE new call is made.
        await SvcWithIntro(db, handler).RunAsync(EventId);

        Assert.Equal(afterFirst, handler.Calls);
        // …and they kept their teasers across the discard-and-re-create, which is the point.
        Assert.All(
            await db.SoMePosts.Where(p => p.AutoText.Contains("{IntroText}")).ToListAsync(),
            p => Assert.False(string.IsNullOrWhiteSpace(p.IntroText)));
    }

    /// <summary>
    /// 🔴 §923 — NO TEASER IS WRITTEN FOR A BODY THAT DOES NOT USE ONE.
    /// </summary>
    /// <remarks>
    /// Measured on PROD minutes after the AI was switched on: Type 1 had 16 posts, <b>zero</b> of
    /// whose bodies reference the teaser — and 11 had already been generated. His track wordings
    /// simply do not use one.
    /// <para>⚠️ The cost is not only the wasted call: §911's budget is 12 per RUN, so a teaser
    /// written for a track post is a slot NOT spent on a session post that needs one.</para>
    /// </remarks>
    [Fact]
    public async Task No_teaser_is_generated_for_a_wording_that_does_not_reference_it()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        // A sponsor wording with no teaser token — like his real track wordings.
        db.SoMeBodySamples.Add(new SoMeBodySample
        {
            EventId = EventId, Kind = SoMeTemplateKind.Sponsor, SortOrder = 0,
            Body = "✨ {SponsorName} ✨ — {EventTags}",
        });
        await db.SaveChangesAsync();

        var handler = new CountingIntroHandler();
        await SvcWithIntro(db, handler).RunAsync(EventId);

        var sponsorPosts = await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.Sponsor).ToListAsync();
        Assert.NotEmpty(sponsorPosts);
        Assert.All(sponsorPosts, p => Assert.True(
            string.IsNullOrEmpty(p.IntroText),
            "a teaser was written for a body that never renders it"));

        // ⚠️ NOT `handler.Calls == 0`: seeding a sponsor also produces a TIER post, whose shipped
        // template DOES use {IntroText} and therefore legitimately generates one. The assertion is
        // about the posts whose bodies do not use it, which is the whole rule.
        Assert.All(
            await db.SoMePosts.Where(p => !p.AutoText.Contains("{IntroText}")).ToListAsync(),
            p => Assert.True(string.IsNullOrEmpty(p.IntroText)));
    }

    /// <summary>🔒 …but a body that DOES use it still gets one.</summary>
    [Fact]
    public async Task A_wording_that_references_the_teaser_still_gets_one()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        db.SoMeBodySamples.Add(new SoMeBodySample
        {
            EventId = EventId, Kind = SoMeTemplateKind.Sponsor, SortOrder = 0,
            Body = "✨ {SponsorName} ✨\n{IntroText}\n{EventTags}",
        });
        await db.SaveChangesAsync();

        var handler = new CountingIntroHandler();
        await SvcWithIntro(db, handler).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        Assert.False(string.IsNullOrWhiteSpace(post.IntroText));
        Assert.True(handler.Calls > 0);
    }

    /// <summary>
    /// 🔒 §911 — ONE RUN CANNOT SPEND MORE THAN ITS BUDGET, which is what stops the FIRST run
    /// (where every post is a miss) from being the one that dies.
    /// </summary>
    [Fact]
    public async Task A_single_run_never_exceeds_the_teaser_budget()
    {
        using var db = NewDb();
        // Comfortably more subjects than the 12-per-run ceiling.
        await SeedAsync(db, sponsors: 12, sessions: 12);
        var handler = new CountingIntroHandler();

        await SvcWithIntro(db, handler).RunAsync(EventId);

        Assert.True(handler.Calls <= 12, $"one run made {handler.Calls} AI calls — the budget is 12");

        // …and the backlog genuinely fills in on later ticks rather than stalling for ever.
        var afterFirst = await db.SoMePosts.CountAsync(p => p.IntroText != null);
        await SvcWithIntro(db, handler).RunAsync(EventId);
        Assert.True(
            await db.SoMePosts.CountAsync(p => p.IntroText != null) > afterFirst,
            "a second run should add more teasers, not stall");
    }

    /// <summary>
    /// 🔴 §909 — A FLAGGED SESSION IS NOT ANNOUNCED, even when REAL speakers are on it.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-06: <i>"remove test sessions"</i>, said while looking at "Test Master Class"
    /// and "Test Session" queued to publish.
    /// <para>🔑 §905's derived rule ("has speakers and every one is a test user") could not see
    /// them: each carries FOUR REAL speakers. A session is test because of what it IS — which is
    /// why the explicit flag had to exist, exactly as it did for sponsors.</para>
    /// </remarks>
    [Fact]
    public async Task A_session_flagged_as_test_is_not_announced_even_with_real_speakers()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 0, sessions: 1);

        var real = new Participant
        {
            EventId = EventId, Email = "real.speaker@example.test", FullName = "Real Speaker",
            IsTestUser = false, IsActive = true,
        };
        db.Participants.Add(real);
        var fixtureSession = new Session
        {
            EventId = EventId, SessionizeId = "s-fixture", Title = "Test Master Class",
            Track = "Security", Type = SessionType.MasterClass,
            IsTestData = true,
        };
        db.Sessions.Add(fixtureSession);
        await db.SaveChangesAsync();

        db.SessionSpeakers.Add(new SessionSpeaker
        {
            SessionId = fixtureSession.Id, ParticipantId = real.Id,
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var subjects = await db.SoMePosts.Select(p => p.SubjectKey).ToListAsync();

        Assert.DoesNotContain($"session:{fixtureSession.Id}", subjects);
        // The real session alongside it is still announced — the flag is a scalpel, not a sweep.
        Assert.Contains(subjects, s => s != null && s.StartsWith("session:", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🔴 §925 — A TRACK WAITS FOR ITS OWN LINE-UP TO SETTLE, not for a date.
    /// </summary>
    /// <remarks>
    /// §920 made a session's readiness data-driven and left the track floor as a hand-set date — the
    /// last thing standing in for a fact, with §851's flaw in miniature: it expires whether or not
    /// the sessions came.
    /// <para>🔑 A track still receiving sessions has not settled; one whose newest session is a week
    /// old has. Self-adjusting: if the CfS sync slips, the track's readiness slips with it.</para>
    /// </remarks>
    [Fact]
    public async Task A_track_still_receiving_sessions_waits_longer_than_one_that_has_settled()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 0, sessions: 0);

        // No hand-set floor — the data alone must decide.
        var settings = await db.SoMeSettings.FirstAsync();
        settings.SpeakerAnnouncementFrom = null;
        await db.SaveChangesAsync();

        // "Settled": its only session arrived well over a week ago.
        db.Sessions.Add(new Session
        {
            EventId = EventId, SessionizeId = "s-old", Title = "Old", Track = "Settled",
            Type = SessionType.TechnicalSession, CreatedAt = Now.AddDays(-30),
        });
        // "Arriving": a session landed today, so the line-up is still filling.
        db.Sessions.Add(new Session
        {
            EventId = EventId, SessionizeId = "s-new", Title = "New", Track = "Arriving",
            Type = SessionType.TechnicalSession, CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var settled = await db.SoMePosts
            .Where(p => p.SubjectKey == "track:Settled" && p.Occurrence == 1).FirstAsync();
        var arriving = await db.SoMePosts
            .Where(p => p.SubjectKey == "track:Arriving" && p.Occurrence == 1).FirstAsync();

        // The track still receiving sessions is announced LATER than the settled one.
        Assert.True(
            arriving.ScheduledAtUtc > settled.ScheduledAtUtc,
            $"a track still receiving sessions ({arriving.ScheduledAtUtc:u}) should wait longer "
            + $"than a settled one ({settled.ScheduledAtUtc:u})");

        // …and not before its own settle period has elapsed.
        Assert.True(
            arriving.ScheduledAtUtc >= Now.AddDays(7),
            $"the arriving track was announced {arriving.ScheduledAtUtc:u}, inside its settle period");
    }

    /// <summary>
    /// 🔴 §920 — A SESSION IS ANNOUNCEABLE BECAUSE IT EXISTS; A TRACK STILL WAITS.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"remove the 7th sept blocker. we will change the dependency as
    /// only active sessions in ceh can be planned … this principle change will also allow me to
    /// start schedule for example master class session which are approved"</i>, and
    /// <i>"active is also when they are synced from sessionize and exist in ceh"</i>.</para>
    ///
    /// <para>🔑 §851's hard 7 Sep floor was a DATE standing in for a FACT, and it was wrong in both
    /// directions: it blocked master classes that are already confirmed, and it would have lifted on
    /// 7 Sep whether or not the CfS had actually synced. A session only syncs once it is decided, so
    /// the row's arrival IS the decision.</para>
    ///
    /// <para>⚠️ A TRACK keeps the floor: it lists a whole track's speakers, so it is incomplete
    /// until that track's sessions have arrived. Once PUBLISHED it cannot pick anyone up — §901's
    /// late resolution does not save an already-sent post.</para>
    /// </remarks>
    [Fact]
    public async Task A_session_is_not_speaker_gated_but_a_track_still_is()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 0, sessions: 2);

        // A gate far in the future — under §851 it held BOTH types back.
        var settings = await db.SoMeSettings.FirstAsync();
        settings.SpeakerAnnouncementFrom = new DateOnly(2026, 12, 1);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var gate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        // 🔑 Sessions are free of it — they can be announced before the gate date.
        var sessions = await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.Session).ToListAsync();
        Assert.NotEmpty(sessions);
        Assert.Contains(sessions, p => p.ScheduledAtUtc < gate);

        // ⚠️ Tracks still respect it.
        var tracks = await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks).ToListAsync();
        Assert.NotEmpty(tracks);
        Assert.All(tracks, p => Assert.True(
            p.ScheduledAtUtc >= gate,
            $"a track post landed {p.ScheduledAtUtc:u}, before the track floor"));
    }

    /// <summary>
    /// §908/§1195 — TRACKS RUN IN ROUNDS: the first as soon as the speaker gate allows, the LAST one
    /// month out, and any in between spread across the gap.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"build post for each speakertracks so they run 2 times; now and
    /// jan 2027"</i>. The generic spread had been putting the eight tracks one per month from
    /// September to February — announced once, then silent for weeks.</para>
    ///
    /// <para>🔴 <b>§1195 — this said "TWO rounds" and asserted round 2 was the month-out one.</b>
    /// Operator 2026-09-12: <i>"speaker tracks must have 3 rounds"</i>, so the month-out round is now
    /// round 3 and round 2 is an intermediate. The RULE the test protects is unchanged and is what it
    /// now asserts: the first round waits for the gate, the LAST lands in the final month. Pinning it
    /// to "round 2" was pinning the cadence, not the rule — and the cadence is his to change.</para>
    ///
    /// <para>🔒 "Now" is "as soon as the gate allows" (his choice when asked): a track post lists
    /// {Speakers}, and posting before the CfS decision announces a line-up the post can never
    /// correct (§851).</para>
    /// </remarks>
    [Fact]
    public async Task Track_posts_run_from_the_gate_with_the_last_round_one_month_before_the_event()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 0, sessions: 2);

        var settings = await db.SoMeSettings.FirstAsync();
        settings.SpeakerAnnouncementFrom = new DateOnly(2026, 9, 7);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var trackPosts = await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks)
            .ToListAsync();
        Assert.NotEmpty(trackPosts);

        // Round 1: never before the gate, and promptly after it — not spread into the autumn.
        var round1 = trackPosts.Where(p => p.Occurrence == 1).ToList();
        Assert.NotEmpty(round1);
        Assert.All(round1, p => Assert.True(
            p.ScheduledAtUtc >= new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero),
            $"round 1 landed {p.ScheduledAtUtc:u}, before the speaker gate"));
        Assert.All(round1, p => Assert.True(
            p.ScheduledAtUtc < new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
            $"round 1 landed {p.ScheduledAtUtc:u} — that is the old spread, not a round"));

        // The LAST round: the month before the event (ELDK27 starts 9 Feb 2027 ⇒ from 9 Jan).
        // 🔑 Found from the data rather than hardcoded to "2", so the assertion survives him
        // changing the cadence again — which is exactly what broke it this time.
        var lastRound = trackPosts.Max(p => p.Occurrence!.Value);
        var final = trackPosts.Where(p => p.Occurrence == lastRound).ToList();
        Assert.NotEmpty(final);
        Assert.All(final, p => Assert.True(
            p.ScheduledAtUtc >= new DateTimeOffset(2027, 1, 9, 0, 0, 0, TimeSpan.Zero),
            $"the last round landed {p.ScheduledAtUtc:u}, before the one-month-out window"));
        Assert.All(final, p => Assert.True(
            p.ScheduledAtUtc < new DateTimeOffset(2027, 2, 9, 0, 0, 0, TimeSpan.Zero),
            $"the last round landed {p.ScheduledAtUtc:u}, after the event started"));

        // ⚠️ And every round in between sits between the two — an intermediate reminder that lands
        // before its own announcement, or after the final push, is the failure the clamp prevents.
        foreach (var round in trackPosts.Select(p => p.Occurrence!.Value).Distinct().Order())
        {
            var earliest = trackPosts.Where(p => p.Occurrence == round).Min(p => p.ScheduledAtUtc);
            if (round > 1)
            {
                var before = trackPosts.Where(p => p.Occurrence == round - 1).Min(p => p.ScheduledAtUtc);
                Assert.True(earliest >= before,
                    $"round {round} opened before round {round - 1}");
            }
        }
    }

    /// <summary>§908 — the post is worded from HIS catalog when the edition has one.</summary>
    [Fact]
    public async Task A_planned_post_draws_its_wording_from_the_sample_catalog()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);

        db.SoMeBodySamples.Add(new SoMeBodySample
        {
            EventId = EventId, Kind = SoMeTemplateKind.Sponsor, SortOrder = 0,
            Body = "HIS WORDING: {SponsorName} — {EventTags}",
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);

        Assert.StartsWith("HIS WORDING:", post.AutoText, StringComparison.Ordinal);
        // 🔒 §901 still holds — the sample is stored as a TEMPLATE, tokens intact.
        Assert.Contains("{SponsorName}", post.AutoText);
    }

    /// <summary>
    /// 🔴 §907 — A TYPE 5 POST IS SEEDED WITH HIS COPY, NOT WITH <c>{EventPostBody}</c>.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-06: <i>"i have NOT asked for a eventPostBody variable - it makes NO sense"</i>
    /// and <i>"i must be able to edit and see text + variables in the post editor"</i>.
    /// <para>§901 correctly stopped the planner storing a RENDERING — but for Type 5 the template is
    /// the single token <c>{EventPostBody}</c> plus the credit, so the stored post became three
    /// lines of tokens and the editor showed no words at all.</para>
    /// <para>🔒 ONE LEVEL ONLY: the deck's text lands in the post, and the tokens INSIDE it stay
    /// tokens — which is what keeps §901's guarantee and §892's ELDK28 reuse both intact.</para>
    /// </remarks>
    [Fact]
    public async Task An_event_post_is_seeded_with_the_deck_text_and_keeps_the_tokens_inside_it()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 0, sessions: 0);

        db.EventSoMePosts.Add(new EventSoMePost
        {
            EventId = EventId,
            Slug = "eldk27-announcement",
            Title = "Announcement",
            // His copy, tokenised by §904 — prose AND variables.
            Body = "Welcome back from summer.\n\nSee you at {EventVenueCityCountry}.\n\n{EventTags}",
            // The deck states the DATE; the scheduler owns the time of day (§834.4).
            Occurrences = new List<EventSoMePostOccurrence>
            {
                new()
                {
                    PostDate = new DateOnly(2026, 9, 1),
                    Sequence = 1,
                    GraphicFileName = "announcement.png",
                },
            },
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts
            .FirstOrDefaultAsync(p => p.TemplateKind == SoMeTemplateKind.EventPost);
        Assert.NotNull(post);

        // 🔑 His words are IN the post — the editor has something to show and edit.
        Assert.Contains("Welcome back from summer.", post!.AutoText);
        // …and the indirection is gone.
        Assert.DoesNotContain("{EventPostBody}", post.AutoText);
        // …while the variables inside his copy are still variables (§901 / §892 both hold).
        Assert.Contains("{EventVenueCityCountry}", post.AutoText);
        Assert.Contains("{EventTags}", post.AutoText);
    }

    /// <summary>
    /// §901 — <c>{IntroText}</c> is the ONE token that cannot resolve late, so it is stored on the
    /// post and read back. Without this the template's own opening line would publish verbatim.
    /// </summary>
    /// <remarks>
    /// 🔒 The null case is the one that matters: it is every AI failure path (§824.2D) and all of
    /// Type 5 (§834.5). It must render as an absent paragraph, never as the literal "{IntroText}".
    /// </remarks>
    [Theory]
    [InlineData("A great company.", "A great company.")]
    [InlineData(null, "")]
    public async Task The_stored_intro_resolves_and_a_missing_one_leaves_no_placeholder(
        string? intro, string expected)
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);
        var clock = new FixedClock(Now);
        var templates = new SoMeTemplateService(db, clock);
        await templates.SaveAsync(EventId, SoMeTemplateKind.Sponsor, "{IntroText}", "org@x.dk");

        await new SoMeScheduleService(db, templates, new SoMeVariableResolver(db), clock).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        post.IntroText = intro;
        await db.SaveChangesAsync();

        var composer = new SoMePostComposer(db, new SoMeVariableResolver(db));
        Assert.Equal(
            expected,
            composer.Resolve(post.EffectiveText, await composer.ValuesForAsync(post)));
    }

    [Fact]
    public async Task An_edited_template_is_what_gets_stored_and_composed()
    {
        using var db = NewDb();
        await SeedAsync(db, sponsors: 1, sessions: 0);
        var clock = new FixedClock(Now);
        var templates = new SoMeTemplateService(db, clock);
        await templates.SaveAsync(EventId, SoMeTemplateKind.Sponsor, "MY OWN: {SponsorName}", "org@x.dk");

        await new SoMeScheduleService(db, templates, new SoMeVariableResolver(db), clock).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.TemplateKind == SoMeTemplateKind.Sponsor);
        Assert.Equal("MY OWN: {SponsorName}", post.AutoText);

        var composer = new SoMePostComposer(db, new SoMeVariableResolver(db));
        Assert.Equal(
            "MY OWN: Company 1",
            composer.Resolve(post.EffectiveText, await composer.ValuesForAsync(post)));
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
                EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor, SponsorCompanyId = $"co-{i}",
                StableKey = $"sponsor-co-{i}", FileName = $"sponsor-co-{i}.png",
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
