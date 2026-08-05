using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Surveys;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.5 — a post-event survey closes ONE MONTH AFTER THE EVENT ENDS, and that date is derived.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Derived, never written down.</b> The work order names 10 March 2027 for ELDK27 and
/// then says to implement it as event-end + 1 month. A literal would be right exactly once: the next
/// edition would inherit the previous event's close date and its survey would open already closed,
/// with nothing on the page to explain why.</para>
///
/// <para>⚠️ <b>Every uncertainty leaves the survey as the organizer set it.</b> No close rule, no
/// active event, no definition — none of those may close a live survey, because a missing config
/// file silently shutting a form looks to a respondent like the event stopped caring.</para>
/// </remarks>
public sealed class SurveyAutoCloseTests
{
    private static readonly DateOnly EventEnd = new(2027, 2, 10);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Definitions(params SurveyDefinition[] defs) : ISurveyDefinitionSource
    {
        public SurveyDefinition? TryGet(string slug) =>
            defs.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"close-{Guid.NewGuid():N}").Options);

    private static async Task SeedAsync(CommunityHubDbContext db, bool activeEvent = true)
    {
        db.Events.Add(new Event
        {
            Id = 1, Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = EventEnd, IsActive = activeEvent,
        });
        await db.SaveChangesAsync();
    }

    private static SurveyDefinition Post(int? months) => new()
    {
        Slug = "post", Title = "T",
        ClosesMonthsAfterEventEnd = months,
        Questions = [new SurveyQuestion { Id = "q1", Kind = SurveyQuestionKind.FreeText, Prompt = "p" }],
    };

    private static SurveySummaryService Svc(
        CommunityHubDbContext db, DateTimeOffset now, ISurveyDefinitionSource? defs) =>
        new(db, new FixedClock(now), NullLogger<SurveySummaryService>.Instance, defs);

    private static DateTimeOffset Utc(int y, int m, int d) =>
        new(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>ELDK27's event ends 10 Feb 2027, so the survey is open through 10 March.</summary>
    [Fact]
    public async Task It_is_still_open_on_the_last_day_of_the_month_after_the_event()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // The work order's own example date. It must still ACCEPT answers that day, not turn people
        // away at midnight into it.
        var svc = Svc(db, Utc(2027, 3, 10), new Definitions(Post(1)));

        Assert.True(await svc.IsOpenAsync("post", default));
    }

    [Fact]
    public async Task It_is_closed_the_day_after()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db, Utc(2027, 3, 11), new Definitions(Post(1)));

        Assert.False(await svc.IsOpenAsync("post", default));
    }

    /// <summary>🔒 The derived date CLOSES; it can never re-open something an organizer closed.</summary>
    [Fact]
    public async Task An_organizer_can_still_close_it_EARLY_and_it_stays_closed()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db, Utc(2027, 2, 20), new Definitions(Post(1)));

        await svc.SetOpenAsync("post", false, "organizer@example.test", default);

        // Well before the derived date, but the manual flag wins on the closing side.
        Assert.False(await svc.IsOpenAsync("post", default));
    }

    [Fact]
    public async Task A_survey_with_no_close_rule_never_closes_on_its_own()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // The preliminary track-wizard survey has no rule and must be unaffected — years later.
        var svc = Svc(db, Utc(2030, 1, 1), new Definitions(Post(null)));

        Assert.True(await svc.IsOpenAsync("post", default));
    }

    /// <summary>
    /// ⚠️ Every uncertainty answers "still open". A missing definition source, a missing definition
    /// or no active event must never be the reason a live survey turns people away.
    /// </summary>
    [Theory]
    [InlineData(false, true)]    // no definition source at all (the pre-§6.5 construction)
    [InlineData(true, false)]    // definitions present, but no active event to derive a date from
    public async Task Uncertainty_never_closes_a_survey(bool withDefinitions, bool activeEvent)
    {
        using var db = NewDb();
        await SeedAsync(db, activeEvent);

        // Long past any plausible close date — so the ONLY thing keeping it open is the rule that
        // an unknown answers "open".
        var svc = Svc(db, Utc(2030, 1, 1), withDefinitions ? new Definitions(Post(1)) : null);

        Assert.True(await svc.IsOpenAsync("post", default));
    }

    /// <summary>A source that has no definition for this slug must not close it either.</summary>
    [Fact]
    public async Task An_unknown_slug_is_left_alone()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // The source works, it simply does not know "post" — e.g. the file failed to load (§773.1).
        var svc = Svc(db, Utc(2030, 1, 1), new Definitions());

        Assert.True(await svc.IsOpenAsync("post", default));
    }

    /// <summary>
    /// 🔒 The organizer LIST and the public page must give the same answer. Two answers to "is this
    /// open?" would show the organizer OPEN about a survey that is turning visitors away.
    /// </summary>
    [Fact]
    public async Task The_many_slug_lookup_agrees_with_the_single_one()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db, Utc(2027, 3, 11), new Definitions(Post(1)));

        var map = await svc.GetOpenStatesAsync(["post"], default);

        Assert.False(map["post"]);
        Assert.Equal(await svc.IsOpenAsync("post", default), map["post"]);
    }
}
