using CommunityHub.Core.Participants;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the PURE speaker-readiness calculator (REQUIREMENTS §134): it reduces
/// a speaker's resolved <see cref="ReadinessSignal"/> list to a score + a done/missing
/// split. No DB — fed synthetic signals so the full / partial / empty cases are exact.
/// FAKE data only.
/// </summary>
public sealed class SpeakerReadinessCalculatorTests
{
    private static ReadinessSignal Sig(string key, bool applicable, bool done, string? fix = "/fix") =>
        new(key, key.ToUpperInvariant(), applicable, done, fix);

    [Fact]
    public void Full_all_applicable_done_is_100_percent_and_ready()
    {
        var r = SpeakerReadinessCalculator.Compute(7, "Per Son", "per@x.test", new[]
        {
            Sig("a", applicable: true, done: true),
            Sig("b", applicable: true, done: true),
            Sig("c", applicable: true, done: true),
        });

        Assert.Equal(3, r.ApplicableCount);
        Assert.Equal(3, r.DoneCount);
        Assert.Equal(100, r.Percent);
        Assert.True(r.IsReady);
        Assert.Empty(r.MissingItems);
        Assert.Equal(3, r.DoneItems.Count);
        Assert.Equal("3 of 3 done", r.Summary);
    }

    [Fact]
    public void Partial_some_done_rounds_percent_and_lists_missing()
    {
        var r = SpeakerReadinessCalculator.Compute(7, "Per Son", "per@x.test", new[]
        {
            Sig("a", applicable: true, done: true),
            Sig("b", applicable: true, done: false, fix: "/fix-b"),
            Sig("c", applicable: true, done: false, fix: "/fix-c"),
        });

        Assert.Equal(3, r.ApplicableCount);
        Assert.Equal(1, r.DoneCount);
        Assert.Equal(33, r.Percent);          // round(100*1/3) = 33
        Assert.False(r.IsReady);
        Assert.Equal(new[] { "b", "c" }, r.MissingItems.Select(i => i.Key));
        Assert.Equal("/fix-b", r.MissingItems[0].FixLink);
        Assert.Equal("1 of 3 done", r.Summary);
    }

    [Fact]
    public void Empty_nothing_done_is_0_percent_and_not_ready()
    {
        var r = SpeakerReadinessCalculator.Compute(7, "Per Son", "per@x.test", new[]
        {
            Sig("a", applicable: true, done: false),
            Sig("b", applicable: true, done: false),
        });

        Assert.Equal(2, r.ApplicableCount);
        Assert.Equal(0, r.DoneCount);
        Assert.Equal(0, r.Percent);
        Assert.False(r.IsReady);
        Assert.Equal(2, r.MissingItems.Count);
        Assert.Empty(r.DoneItems);
    }

    [Fact]
    public void Non_applicable_signals_are_dropped_and_never_affect_the_score()
    {
        var r = SpeakerReadinessCalculator.Compute(7, "Per Son", "per@x.test", new[]
        {
            Sig("a", applicable: true, done: true),
            Sig("hotel", applicable: false, done: false), // not entitled — must not count
        });

        Assert.Equal(1, r.ApplicableCount);            // hotel dropped
        Assert.DoesNotContain(r.Items, i => i.Key == "hotel");
        Assert.Equal(100, r.Percent);                  // 1/1, not 1/2
        Assert.True(r.IsReady);
    }

    [Fact]
    public void No_applicable_items_is_0_percent_and_not_ready()
    {
        var r = SpeakerReadinessCalculator.Compute(7, "Per Son", "per@x.test", new[]
        {
            Sig("hotel", applicable: false, done: false),
        });

        Assert.Equal(0, r.ApplicableCount);
        Assert.Equal(0, r.Percent);
        Assert.False(r.IsReady);   // an empty item set is NOT "ready"
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Items_preserve_input_order()
    {
        var r = SpeakerReadinessCalculator.Compute(7, "Per Son", "per@x.test", new[]
        {
            Sig("first", applicable: true, done: false),
            Sig("second", applicable: true, done: true),
            Sig("third", applicable: true, done: false),
        });

        Assert.Equal(new[] { "first", "second", "third" }, r.Items.Select(i => i.Key));
    }
}
