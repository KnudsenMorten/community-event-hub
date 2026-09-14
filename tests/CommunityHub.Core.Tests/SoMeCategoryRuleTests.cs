using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1195 — ROUNDS, START DATES AND AN END DATE, PER CATEGORY.
///
/// <para>Operator 2026-09-12: <i>"basically we define the rules like start date, end date, cadence
/// inside the some settings and the some planner must recalculate if they are changed"</i> ·
/// <i>"where is the button to add a round if needed"</i> · <i>"when a some post has more rounds, it
/// should be added into the planner and planned"</i>.</para>
///
/// <para>🔴 <b>The first cut of this discarded the dates he had dictated.</b> Spreading <i>n</i>
/// rounds evenly between two ends is a tidy model, and it turned tracks at 28 Sep / 1 Dec / 15 Jan
/// into 28 Sep / ~14 Nov / ~30 Dec — the last inside the Christmas blackout, so it would move again.
/// He asked for the simpler model AND named specific rounds; a round he has dated has to keep its
/// day. <c>[[separate-proven-from-inferred]]</c></para>
/// </summary>
public sealed class SoMeCategoryRuleTests
{
    private static readonly DateTimeOffset EventStart = new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    private static SoMeCategoryRule Rule(int rounds, DateOnly? starts = null, DateOnly? ends = null) =>
        new()
        {
            Category = SoMeAnnouncementCategory.SpeakerTracks,
            Enabled = true,
            Rounds = rounds,
            StartsOn = starts,
            EndsOn = ends,
        };

    private static DateOnly Day(DateTimeOffset? utc) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(utc!.Value, SoMeSchedulePlanner.DanishTime).DateTime);

    /// <summary>🔴 THE CASE THAT FAILED FIRST: a dated round keeps its exact day.</summary>
    [Fact]
    public void Every_round_he_has_dated_keeps_that_exact_day()
    {
        var named = new Dictionary<int, DateOnly>
        {
            [1] = new(2026, 9, 28), [2] = new(2026, 12, 1), [3] = new(2027, 1, 15),
        };

        var windows = SoMeCategoryRules.Windows(Rule(3, new DateOnly(2026, 9, 28)), EventStart, named);

        Assert.Equal(new DateOnly(2026, 9, 28), Day(windows[1].OpensUtc));
        Assert.Equal(new DateOnly(2026, 12, 1), Day(windows[2].OpensUtc));
        Assert.Equal(new DateOnly(2027, 1, 15), Day(windows[3].OpensUtc));
    }

    /// <summary>
    /// An UNDATED round still spreads — so "rounds plus two ends" remains the model for anyone who
    /// does not want to pick every date.
    /// </summary>
    [Fact]
    public void An_undated_round_spreads_between_the_one_before_it_and_the_end()
    {
        var windows = SoMeCategoryRules.Windows(Rule(3, new DateOnly(2026, 9, 28)), EventStart);

        Assert.Equal(new DateOnly(2026, 9, 28), Day(windows[1].OpensUtc));
        Assert.True(windows[2].OpensUtc > windows[1].OpensUtc);
        Assert.True(windows[3].OpensUtc > windows[2].OpensUtc);
    }

    /// <summary>
    /// 🔒 §908's fallback survives: with nothing named, the LAST round lands one month out. For a
    /// two-round category this is exactly what it has always done.
    /// </summary>
    [Fact]
    public void The_last_undated_round_still_falls_back_to_one_month_before_the_event()
    {
        var windows = SoMeCategoryRules.Windows(Rule(2, new DateOnly(2026, 9, 7)), EventStart);

        Assert.Equal(new DateOnly(2027, 1, 9), Day(windows[2].OpensUtc));
    }

    /// <summary>
    /// 🔒 Monotonic whatever is typed — a reminder dated before its own announcement is worse than
    /// one with no date at all.
    /// </summary>
    [Fact]
    public void A_round_dated_before_the_one_before_it_is_pulled_forward()
    {
        var named = new Dictionary<int, DateOnly>
        {
            [1] = new(2026, 12, 1), [2] = new(2026, 10, 1),
        };

        var windows = SoMeCategoryRules.Windows(Rule(2, new DateOnly(2026, 12, 1)), EventStart, named);

        Assert.True(windows[2].OpensUtc >= windows[1].OpensUtc,
            "round 2 opened before round 1");
    }

    /// <summary>
    /// 🔴 §1194 — THE END DATE IS REAL NOW. Every round closes at it, which is what makes "no later
    /// than" enforceable rather than a comment.
    /// </summary>
    [Fact]
    public void Every_round_closes_at_the_end_date()
    {
        var windows = SoMeCategoryRules.Windows(
            Rule(2, new DateOnly(2026, 9, 28), new DateOnly(2027, 1, 26)), EventStart);

        Assert.All(windows.Values, w => Assert.Equal(new DateOnly(2027, 1, 26), Day(w.ClosesUtc)));
    }

    /// <summary>⚠️ An end date after the event is clamped — nothing publishes once it has started.</summary>
    [Fact]
    public void An_end_date_after_the_event_is_clamped_to_it()
    {
        var windows = SoMeCategoryRules.Windows(
            Rule(1, ends: new DateOnly(2027, 6, 1)), EventStart);

        Assert.Equal(EventStart, windows[1].ClosesUtc);
    }

    /// <summary>
    /// 🔑 "Add a round" is just a bigger count — the extra round appears, which is what makes the
    /// planner plan it.
    /// </summary>
    [Fact]
    public void Raising_the_round_count_produces_another_round()
    {
        var two = SoMeCategoryRules.Windows(Rule(2, new DateOnly(2026, 9, 28)), EventStart);
        var four = SoMeCategoryRules.Windows(Rule(4, new DateOnly(2026, 9, 28)), EventStart);

        Assert.Equal(2, two.Count);
        Assert.Equal(4, four.Count);
        Assert.Equal(new DateOnly(2026, 9, 28), Day(four[1].OpensUtc));
    }

    /// <summary>
    /// ⚠️ No start at all means no floor — each subject goes when IT is ready (§920), which is not
    /// the same as "now" and must not become it.
    /// </summary>
    [Fact]
    public void No_start_date_means_no_floor_at_all()
    {
        var windows = SoMeCategoryRules.Windows(Rule(2), EventStart);

        Assert.Null(windows[1].OpensUtc);
    }

    /// <summary>The shipped round counts, so a change to one is a decision rather than a slip.</summary>
    [Theory]
    [InlineData(SoMeAnnouncementCategory.SpeakerTracks, 3)]
    [InlineData(SoMeAnnouncementCategory.MasterClasses, 1)]
    [InlineData(SoMeAnnouncementCategory.TechnicalSessions, 1)]
    [InlineData(SoMeAnnouncementCategory.SponsorSpeakerSessions, 2)]
    [InlineData(SoMeAnnouncementCategory.SponsorTiers, 2)]
    [InlineData(SoMeAnnouncementCategory.Sponsors, 2)]
    public void The_shipped_round_counts(SoMeAnnouncementCategory category, int expected) =>
        Assert.Equal(expected, SoMeCategoryRules.DefaultRounds(category));

    /// <summary>
    /// 🔑 His numbering: the three session categories are LETTERED, so the page cannot show three
    /// rows all reading "Type 2".
    /// </summary>
    [Fact]
    public void The_session_categories_are_lettered()
    {
        Assert.StartsWith("Type 2a", SoMeCategoryRules.Label(SoMeAnnouncementCategory.MasterClasses), StringComparison.Ordinal);
        Assert.StartsWith("Type 2b", SoMeCategoryRules.Label(SoMeAnnouncementCategory.TechnicalSessions), StringComparison.Ordinal);
        Assert.StartsWith("Type 2c", SoMeCategoryRules.Label(SoMeAnnouncementCategory.SponsorSpeakerSessions), StringComparison.Ordinal);
        Assert.StartsWith("Type 1", SoMeCategoryRules.Label(SoMeAnnouncementCategory.SpeakerTracks), StringComparison.Ordinal);
    }

    /// <summary>⚠️ And they are listed in reading order, which is what he reported as wrong.</summary>
    [Fact]
    public void The_categories_are_listed_in_order()
    {
        Assert.Equal(
            [
                SoMeAnnouncementCategory.SpeakerTracks,
                SoMeAnnouncementCategory.MasterClasses,
                SoMeAnnouncementCategory.TechnicalSessions,
                SoMeAnnouncementCategory.SponsorSpeakerSessions,
                SoMeAnnouncementCategory.SponsorTiers,
                SoMeAnnouncementCategory.Sponsors,
                SoMeAnnouncementCategory.EventPosts,
            ],
            SoMeCategoryRules.All);
    }
}
