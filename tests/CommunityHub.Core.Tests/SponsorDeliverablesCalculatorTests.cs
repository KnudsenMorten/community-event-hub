using CommunityHub.Core.Sponsors;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the PURE sponsor-deliverables calculator (REQUIREMENTS §135): it reduces a
/// company's resolved <see cref="SponsorDeliverableSignal"/> list to a completion score + a
/// done/overdue/at-risk split. No DB — fed synthetic signals so the full / partial / overdue /
/// empty cases are exact. FAKE data only.
/// </summary>
public sealed class SponsorDeliverablesCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 6, 27);

    private static SponsorDeliverableSignal Sig(
        string key, bool applicable, bool done, DateOnly? deadline = null, string? fix = "/fix") =>
        new(key, key.ToUpperInvariant(), applicable, done, deadline, fix);

    private static SponsorDeliverables Compute(params SponsorDeliverableSignal[] signals) =>
        SponsorDeliverablesCalculator.Compute("9001", "ACME", isExhibitor: true, Today, signals);

    [Fact]
    public void Full_all_applicable_done_is_100_percent_complete_not_at_risk()
    {
        var r = Compute(
            Sig("a", applicable: true, done: true),
            Sig("b", applicable: true, done: true, deadline: Today.AddDays(-5)),  // past but done -> not overdue
            Sig("c", applicable: true, done: true));

        Assert.Equal(3, r.ApplicableCount);
        Assert.Equal(3, r.DoneCount);
        Assert.Equal(100, r.Percent);
        Assert.True(r.IsComplete);
        Assert.False(r.AtRisk);
        Assert.Equal(0, r.OverdueCount);
        Assert.Empty(r.MissingStages);
        Assert.Equal("3 of 3 done", r.Summary);
    }

    [Fact]
    public void Partial_some_done_rounds_percent_and_lists_missing()
    {
        var r = Compute(
            Sig("a", applicable: true, done: true),
            Sig("b", applicable: true, done: false, fix: "/fix-b"),
            Sig("c", applicable: true, done: false, fix: "/fix-c"));

        Assert.Equal(3, r.ApplicableCount);
        Assert.Equal(1, r.DoneCount);
        Assert.Equal(33, r.Percent);          // round(100*1/3) = 33
        Assert.False(r.IsComplete);
        Assert.Equal(new[] { "b", "c" }, r.MissingStages.Select(s => s.Key));
        Assert.Equal("/fix-b", r.MissingStages[0].FixLink);
    }

    [Fact]
    public void Overdue_when_not_done_and_deadline_is_in_the_past()
    {
        var r = Compute(
            Sig("late", applicable: true, done: false, deadline: Today.AddDays(-1)),
            Sig("soon", applicable: true, done: false, deadline: Today.AddDays(3)),
            Sig("undated", applicable: true, done: false, deadline: null));

        Assert.True(r.AtRisk);
        Assert.Equal(1, r.OverdueCount);
        Assert.Equal(new[] { "late" }, r.OverdueStages.Select(s => s.Key));
        Assert.True(r.Stages.Single(s => s.Key == "late").Overdue);
        Assert.False(r.Stages.Single(s => s.Key == "soon").Overdue);    // future deadline
        Assert.False(r.Stages.Single(s => s.Key == "undated").Overdue); // no deadline -> never overdue
    }

    [Fact]
    public void Deadline_equal_to_today_is_not_yet_overdue()
    {
        var r = Compute(Sig("due-today", applicable: true, done: false, deadline: Today));
        Assert.False(r.AtRisk);
        Assert.False(r.Stages.Single().Overdue);
    }

    [Fact]
    public void Non_applicable_signals_are_dropped_and_never_affect_the_score()
    {
        var r = Compute(
            Sig("onboarding", applicable: true, done: true),
            Sig("booth-members", applicable: false, done: false, deadline: Today.AddDays(-10))); // not exhibitor

        Assert.Equal(1, r.ApplicableCount);            // booth-members dropped
        Assert.DoesNotContain(r.Stages, s => s.Key == "booth-members");
        Assert.Equal(100, r.Percent);                  // 1/1, not 1/2
        Assert.True(r.IsComplete);
        Assert.False(r.AtRisk);                         // the overdue-but-dropped stage must not count
    }

    [Fact]
    public void No_applicable_stages_is_0_percent_and_not_complete()
    {
        var r = Compute(Sig("booth-members", applicable: false, done: false));

        Assert.Equal(0, r.ApplicableCount);
        Assert.Equal(0, r.Percent);
        Assert.False(r.IsComplete);   // an empty stage set is NOT "complete"
        Assert.False(r.AtRisk);
        Assert.Empty(r.Stages);
    }

    [Fact]
    public void Stages_preserve_input_lifecycle_order()
    {
        var r = Compute(
            Sig("onboarding", applicable: true, done: false),
            Sig("logo", applicable: true, done: true),
            Sig("tasks", applicable: true, done: false));

        Assert.Equal(new[] { "onboarding", "logo", "tasks" }, r.Stages.Select(s => s.Key));
    }

    [Fact]
    public void Company_identity_is_passed_through()
    {
        var r = SponsorDeliverablesCalculator.Compute(
            "9001", "ACME Corp", isExhibitor: false, Today,
            new[] { Sig("onboarding", applicable: true, done: true) });

        Assert.Equal("9001", r.CompanyId);
        Assert.Equal("ACME Corp", r.CompanyName);
        Assert.False(r.IsExhibitor);
    }
}
