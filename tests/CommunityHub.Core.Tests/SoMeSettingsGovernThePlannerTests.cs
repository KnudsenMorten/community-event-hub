using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1184 — EVERY ANNOUNCEMENT DATE ON THE SETTINGS PAGE ACTUALLY GOVERNS THE PLANNER.
///
/// <para>Operator 2026-09-12: <i>"make sure that values here wins, so we dont have static values in
/// the code"</i> · <i>"make sure that you include round 1 and 2 where needed in the some settings"</i>
/// · <i>"it is important that the some planner uses these dates/settings, check code"</i>.</para>
///
/// <para>🔴 <b>This is the §1178 lesson turned into a guard.</b> That morning cost a day because
/// <c>Session.IsTestData</c> had four READERS and no writer — a field that looked like a control and
/// was not. The announcement dates are the same shape of risk pointed the other way: a settings field
/// with a writer and no reader looks like a control and is not. These pin that each one reaches the
/// planner, so a future field cannot be added to the page and quietly do nothing.</para>
/// </summary>
public sealed class SoMeSettingsGovernThePlannerTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-govern-{Guid.NewGuid():N}").Options);

    private static SoMeScheduleService Svc(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        return new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock);
    }

    /// <summary>An edition with two real sponsors, both with artwork, and every date set.</summary>
    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", DisplayName = "Experts Live Denmark 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId,
            EventSystemUrl = "https://example.test",
            EventTags = "#ELDK27",
            OrganizerCredits = "Organizers",
            MaxPostsPerDay = 2,
            // Every announcement date, all different, so each assertion can only pass by reading
            // the field it names.
            SponsorAnnouncementFrom = new DateOnly(2026, 10, 15),
            SponsorRound2From = new DateOnly(2026, 11, 20),
            SponsorCategoryRound1From = new DateOnly(2026, 12, 15),
            SponsorCategoryRound2From = new DateOnly(2027, 1, 15),
        });

        foreach (var (id, tier) in new[] { ("100", SponsorPackage.Gold), ("200", SponsorPackage.Silver) })
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = EventId, SponsorCompanyId = id, CompanyName = $"Sponsor {id}",
                SponsorPackage = tier, Status = SponsorStatus.Active,
            });
            // §854 — a sponsor is only plannable once their graphic exists.
            db.GraphicAssets.Add(new GraphicAsset
            {
                EventId = EventId, Type = GraphicAssetType.Sponsor, SponsorCompanyId = id,
                FileName = $"sponsor-{id}.png", Status = GraphicAssetStatus.Generated,
                CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            });
            // A real contact, so TestDataScope does not read the company as test data.
            db.Participants.Add(new Participant
            {
                Id = int.Parse(id), EventId = EventId, Email = $"c{id}@example.test",
                FullName = $"Contact {id}", Role = ParticipantRole.Sponsor, SponsorCompanyId = id,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static DateTimeOffset At(int y, int m, int d) =>
        SoMeSchedulePlanner.ToUtc(new DateOnly(y, m, d), SoMeSchedulePlanner.PreferredTimes[0]);

    private static async Task<List<SoMePost>> PostsAsync(
        CommunityHubDbContext db, SoMeTemplateKind kind, int occurrence) =>
        await db.SoMePosts.AsNoTracking()
            .Where(p => p.TemplateKind == kind && p.Occurrence == occurrence && !p.IsDeleted)
            .ToListAsync();

    /// <summary>🔴 Type 4 round 1 obeys <c>SponsorAnnouncementFrom</c>.</summary>
    [Fact]
    public async Task Sponsor_round_1_obeys_its_setting()
    {
        using var db = await SeedAsync();
        await Svc(db).RunAsync(EventId);

        var posts = await PostsAsync(db, SoMeTemplateKind.Sponsor, 1);
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.True(p.ScheduledAtUtc >= At(2026, 10, 15),
            $"sponsor round 1 landed {p.ScheduledAtUtc:dd-MM-yyyy}, before its 15 Oct setting"));
    }

    /// <summary>
    /// 🔴 §1184's gap: Type 4 round 2 has its OWN date. Before this it shared round 1's floor, so the
    /// reminder could be placed the same week as the announcement.
    /// </summary>
    [Fact]
    public async Task Sponsor_round_2_obeys_its_own_setting()
    {
        using var db = await SeedAsync();
        await Svc(db).RunAsync(EventId);

        var posts = await PostsAsync(db, SoMeTemplateKind.Sponsor, 2);
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.True(p.ScheduledAtUtc >= At(2026, 11, 20),
            $"sponsor round 2 landed {p.ScheduledAtUtc:dd-MM-yyyy}, before its 20 Nov setting — "
            + "it is still sharing round 1's floor"));
    }

    /// <summary>
    /// 🔴 §1185 — THREE track rounds, each from its own date. Operator 2026-09-12: *"speaker tracks
    /// must have 3 rounds … first runs in sept as now. second runs in early dec and third runs from
    /// mid jan 27"*.
    /// </summary>
    /// <remarks>
    /// ⚠️ Asserts the ROUND COUNT too. The dates are only half of it: <c>Times()</c> reads a saved
    /// cadence row first, so a posting-frequency page saying 2 means round 3 is never planned and the
    /// date governs nothing — the §1178 defect (a setting with no effect) waiting to happen again.
    /// </remarks>
    [Fact]
    public async Task Speaker_tracks_run_three_rounds_each_from_its_own_date()
    {
        using var db = await SeedAsync();
        // A track with a real speaker, so it is announceable at all.
        db.Sessions.Add(new Session
        {
            Id = 900, EventId = EventId, Title = "Zero Trust in Practice", Track = "Security",
            CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.Participants.Add(new Participant
        {
            Id = 900, EventId = EventId, Email = "s900@example.test", FullName = "Speaker 900",
            Role = ParticipantRole.Speaker, IsActive = true,
        });
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = 900, ParticipantId = 900 });

        var s = await db.SoMeSettings.FirstAsync();
        s.SpeakerAnnouncementFrom = new DateOnly(2026, 9, 28);
        s.SpeakerTracksRound2From = new DateOnly(2026, 12, 3);
        s.SpeakerTracksRound3From = new DateOnly(2027, 1, 15);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var rounds = await db.SoMePosts.AsNoTracking()
            .Where(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks && !p.IsDeleted)
            .ToListAsync();

        // 🔑 Three rounds must actually EXIST, not merely be dated.
        Assert.Equal(3, rounds.Select(p => p.Occurrence).Distinct().Count());

        foreach (var (occurrence, from) in new[]
                 {
                     (1, At(2026, 9, 28)), (2, At(2026, 12, 3)), (3, At(2027, 1, 15)),
                 })
        {
            var posts = rounds.Where(p => p.Occurrence == occurrence).ToList();
            Assert.NotEmpty(posts);
            Assert.All(posts, p => Assert.True(p.ScheduledAtUtc >= from,
                $"track round {occurrence} landed {p.ScheduledAtUtc:dd-MM-yyyy}, before its setting"));
        }
    }

    /// <summary>
    /// 🔒 §908's guard, extended to three rounds: a LATER round's floor is never EARLIER than the one
    /// before it, so out-of-order dates cannot put a reminder back in September.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>What this does NOT claim.</b> The clamp makes the three FLOORS monotonic; it does not
    /// order the placements. When two rounds end up on the same floor — which only happens if the
    /// dates are entered out of order — the stable-hash search may place either first, and that is
    /// acceptable: the campaign is protected from a reminder months before its announcement, which
    /// was the actual risk. Asserting strict placement order here would be asserting something the
    /// planner has never promised. <c>[[separate-proven-from-inferred]]</c>
    /// </remarks>
    [Fact]
    public async Task A_later_round_never_lands_before_an_earlier_one()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(new Session
        {
            Id = 901, EventId = EventId, Title = "Late Track", Track = "Identity",
            CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.Participants.Add(new Participant
        {
            Id = 901, EventId = EventId, Email = "s901@example.test", FullName = "Speaker 901",
            Role = ParticipantRole.Speaker, IsActive = true,
        });
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = 901, ParticipantId = 901 });

        var s = await db.SoMeSettings.FirstAsync();
        // Deliberately out of order: rounds 2 and 3 dated BEFORE round 1.
        s.SpeakerAnnouncementFrom = new DateOnly(2026, 12, 1);
        s.SpeakerTracksRound2From = new DateOnly(2026, 10, 1);
        s.SpeakerTracksRound3From = new DateOnly(2026, 9, 20);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var posts = await db.SoMePosts.AsNoTracking()
            .Where(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks && !p.IsDeleted)
            .ToListAsync();

        Assert.NotEmpty(posts);

        // 🔑 Rounds 2 and 3 were dated BEFORE round 1 (October and September). The clamp must pull
        // them up to round 1's floor, so nothing lands in the months he mistyped.
        var roundOneFloor = At(2026, 12, 1);
        Assert.All(posts, p => Assert.True(p.ScheduledAtUtc >= roundOneFloor,
            $"track round {p.Occurrence} landed {p.ScheduledAtUtc:dd-MM-yyyy} — before round 1's "
            + "1 Dec floor, so an out-of-order date escaped the clamp"));
    }

    /// <summary>Type 3 round 1 obeys <c>SponsorCategoryRound1From</c>, not the sponsor floor.</summary>
    [Fact]
    public async Task Tier_round_1_obeys_its_own_setting_not_the_sponsor_floor()
    {
        using var db = await SeedAsync();
        await Svc(db).RunAsync(EventId);

        var posts = await PostsAsync(db, SoMeTemplateKind.SponsorCategory, 1);
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.True(p.ScheduledAtUtc >= At(2026, 12, 15),
            $"tier round 1 landed {p.ScheduledAtUtc:dd-MM-yyyy}, before its 15 Dec setting"));
    }

    /// <summary>Type 3 round 2 obeys <c>SponsorCategoryRound2From</c>.</summary>
    [Fact]
    public async Task Tier_round_2_obeys_its_own_setting()
    {
        using var db = await SeedAsync();
        await Svc(db).RunAsync(EventId);

        var posts = await PostsAsync(db, SoMeTemplateKind.SponsorCategory, 2);
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.True(p.ScheduledAtUtc >= At(2027, 1, 15),
            $"tier round 2 landed {p.ScheduledAtUtc:dd-MM-yyyy}, before its 15 Jan setting"));
    }

    /// <summary>
    /// 🔒 And none of them land on the holidays — 15 December is eight days before the break, so the
    /// tier round is the one most likely to spill into it.
    /// </summary>
    [Fact]
    public async Task No_planned_post_lands_on_the_holiday_blackout()
    {
        using var db = await SeedAsync();
        await Svc(db).RunAsync(EventId);

        foreach (var p in await db.SoMePosts.AsNoTracking().Where(x => !x.IsDeleted).ToListAsync())
        {
            var day = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).DateTime);
            Assert.False(SoMeBlackout.IsBlackedOut(day),
                $"post {p.Id} ({p.TemplateKind} #{p.Occurrence}) landed on {day:dd-MM-yyyy}");
        }
    }

    /// <summary>
    /// ⚠️ Every setting is OPTIONAL. With the dates cleared the planner still plans — a blank field
    /// is "no opinion", never "hold everything", and a page full of empty dates must not silence the
    /// campaign.
    /// </summary>
    [Fact]
    public async Task With_every_date_cleared_the_planner_still_plans()
    {
        using var db = await SeedAsync();
        var s = await db.SoMeSettings.FirstAsync();
        s.SponsorAnnouncementFrom = null;
        s.SponsorRound2From = null;
        s.SponsorCategoryRound1From = null;
        s.SponsorCategoryRound2From = null;
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        Assert.True(result.Created > 0, $"nothing was planned: {result.Message}");
    }
}
