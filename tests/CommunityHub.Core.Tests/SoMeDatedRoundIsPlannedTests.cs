using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1207 — "IF I DEFINE 3 ROUNDS, WE MUST PLAN 3 ROUNDS" (operator 2026-09-12, as a bug).
///
/// <para>🔴 <b>The failure, exactly.</b> §1195 seeds a new rule's <c>Rounds</c> from the OLD
/// posting-frequency table (<c>SoMeCadenceSettings.Occurrences</c>) so a configured edition keeps its
/// choice — while <c>RoundSeeds</c> seeds the round DATES from the settings columns, including
/// <c>SpeakerTracksRound3From</c>. On an edition whose frequency page still said <b>2</b>, that
/// produced <c>Rounds = 2</c> AND a dated round <b>3</b>. <c>Windows</c> iterated 1..Rounds, so round
/// 3 was dropped: a date entered, stored, displayed on the settings page — and never planned.</para>
///
/// <para>🔑 Two numbers for one idea, and the stale one won silently. The rule is now one line:
/// <b>a dated round is a round.</b></para>
/// </summary>
public sealed class SoMeDatedRoundIsPlannedTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset EventStart =
        new(2027, 2, 9, 8, 0, 0, TimeSpan.Zero);

    private static SoMeCategoryRule Rule(int rounds, bool enabled = true) => new()
    {
        EventId = EventId,
        Category = SoMeAnnouncementCategory.SpeakerTracks,
        Enabled = enabled,
        Rounds = rounds,
        StartsOn = new DateOnly(2026, 9, 28),
    };

    private static Dictionary<int, DateOnly> Dated(params (int Round, DateOnly Day)[] days) =>
        days.ToDictionary(d => d.Round, d => d.Day);

    /// <summary>🔴 The reported case: three dated rounds, a saved count of two.</summary>
    [Fact]
    public void A_dated_round_beyond_the_saved_count_is_still_a_round()
    {
        var starts = Dated(
            (1, new DateOnly(2026, 9, 28)),
            (2, new DateOnly(2026, 12, 1)),
            (3, new DateOnly(2027, 1, 15)));

        Assert.Equal(3, SoMeCategoryRules.EffectiveRounds(Rule(rounds: 2), starts));
    }

    /// <summary>🔑 And the WINDOWS agree — the count and the dates come from one function.</summary>
    [Fact]
    public void The_third_window_exists_and_keeps_his_date()
    {
        var starts = Dated(
            (1, new DateOnly(2026, 9, 28)),
            (2, new DateOnly(2026, 12, 1)),
            (3, new DateOnly(2027, 1, 15)));

        var windows = SoMeCategoryRules.Windows(Rule(rounds: 2), EventStart, starts);

        Assert.True(windows.ContainsKey(3), "round 3 was dated and must have a window");
        Assert.Equal(
            new DateOnly(2027, 1, 15),
            DateOnly.FromDateTime(windows[3].OpensUtc!.Value.UtcDateTime));
    }

    /// <summary>A saved count HIGHER than the dated rounds is untouched — undated rounds spread.</summary>
    [Fact]
    public void A_higher_saved_count_still_wins()
    {
        var starts = Dated((1, new DateOnly(2026, 9, 28)));

        Assert.Equal(4, SoMeCategoryRules.EffectiveRounds(Rule(rounds: 4), starts));
    }

    /// <summary>
    /// 🔒 A DISABLED category stays at zero. `Enabled` is how a category is switched off, and a
    /// leftover date must not resurrect one he has turned off.
    /// </summary>
    [Fact]
    public void A_disabled_category_is_still_zero_however_many_rounds_are_dated()
    {
        var starts = Dated((1, new DateOnly(2026, 9, 28)), (2, new DateOnly(2026, 12, 1)));

        Assert.Equal(0, SoMeCategoryRules.EffectiveRounds(Rule(rounds: 2, enabled: false), starts));
    }

    [Fact]
    public void With_no_dated_rounds_the_saved_count_is_the_answer()
    {
        Assert.Equal(2, SoMeCategoryRules.EffectiveRounds(Rule(rounds: 2), null));
        Assert.Equal(2, SoMeCategoryRules.EffectiveRounds(Rule(rounds: 2), new Dictionary<int, DateOnly>()));
    }

    /// <summary>
    /// ⚠️ It cannot get STUCK high: `SaveAsync` deletes round rows above the saved count, so lowering
    /// the number takes the extra dates with it. Proven through the real service, because that
    /// deletion is the only thing keeping this rule reversible.
    /// </summary>
    [Fact]
    public async Task Lowering_the_count_removes_the_extra_dates_so_it_drops_back()
    {
        using var db = new CommunityHubDbContext(
            new DbContextOptionsBuilder<CommunityHubDbContext>()
                .UseInMemoryDatabase($"some-rounds-{Guid.NewGuid():N}").Options);

        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        var svc = new SoMeCategoryRules(db);
        var cat = SoMeAnnouncementCategory.SpeakerTracks;

        await svc.SaveAsync(EventId, cat, enabled: true, rounds: 3, endsOn: null,
            roundStarts: new Dictionary<int, DateOnly?>
            {
                [1] = new DateOnly(2026, 9, 28),
                [2] = new DateOnly(2026, 12, 1),
                [3] = new DateOnly(2027, 1, 15),
            },
            byEmail: "organizer@example.test");

        var afterThree = await svc.GetAllAsync(EventId);
        var startsThree = await svc.RoundStartsAsync(EventId);
        Assert.Equal(3, SoMeCategoryRules.EffectiveRounds(afterThree[cat], startsThree[cat]));

        // Now he decides two is enough.
        await svc.SaveAsync(EventId, cat, enabled: true, rounds: 2, endsOn: null,
            roundStarts: new Dictionary<int, DateOnly?>
            {
                [1] = new DateOnly(2026, 9, 28),
                [2] = new DateOnly(2026, 12, 1),
            },
            byEmail: "organizer@example.test");

        var afterTwo = await svc.GetAllAsync(EventId);
        var startsTwo = await svc.RoundStartsAsync(EventId);
        var dated = startsTwo.TryGetValue(cat, out var d) ? d : null;

        Assert.Equal(2, SoMeCategoryRules.EffectiveRounds(afterTwo[cat], dated));
    }

    /// <summary>
    /// 🔴 END TO END, through the real planner: a track gets THREE posts when round 3 is dated, even
    /// though the seeded rule says two. This is the assertion the bug report is about.
    /// </summary>
    [Fact]
    public async Task The_planner_plans_the_third_round_the_settings_page_shows()
    {
        using var db = new CommunityHubDbContext(
            new DbContextOptionsBuilder<CommunityHubDbContext>()
                .UseInMemoryDatabase($"some-rounds-e2e-{Guid.NewGuid():N}").Options);

        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });

        // The three dates he dictated, in the columns §1195 seeds the rounds from.
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.example.test", EventTags = "#ELDK27",
            OrganizerCredits = "Organizer One",
            SpeakerAnnouncementFrom = new DateOnly(2026, 9, 28),
            SpeakerTracksRound2From = new DateOnly(2026, 12, 1),
            SpeakerTracksRound3From = new DateOnly(2027, 1, 15),
            CallForSpeakersClosesOn = new DateOnly(2026, 8, 31),
        });

        // 🔴 THE TRAP: the old posting-frequency page still says TWO.
        db.SoMeCadenceSettings.Add(new SoMeCadenceSetting
        {
            EventId = EventId, Kind = SoMeTemplateKind.SpeakerTracks, Enabled = true, Occurrences = 2,
        });

        db.Sessions.Add(new Session
        {
            Id = 10, EventId = EventId, Title = "A real talk", Track = "Security",
        });
        await db.SaveChangesAsync();

        var clock = new FixedNow(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));
        var svc = new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock);

        await svc.RunAsync(EventId);

        var rounds = await db.SoMePosts
            .Where(p => p.EventId == EventId && !p.IsDeleted
                        && p.TemplateKind == SoMeTemplateKind.SpeakerTracks
                        && p.SubjectKey == "track:Security")
            .Select(p => p.Occurrence)
            .ToListAsync();

        Assert.Equal(3, rounds.Count);
        Assert.Contains(3, rounds);
    }

    private sealed class FixedNow : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedNow(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
