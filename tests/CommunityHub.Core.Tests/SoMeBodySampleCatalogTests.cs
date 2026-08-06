using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §908 — a mix of wordings that STAYS PUT.
/// </summary>
/// <remarks>
/// 🔴 The property under test is the one the word "randomize" hides. The planner re-plans its
/// un-accepted proposals every five minutes (§848.2), so a real <see cref="Random"/> would re-word
/// every unapproved post continuously — he would read a post, leave it, and find it saying something
/// else. Varied ACROSS posts, identical WITHIN a post, forever.
/// </remarks>
public sealed class SoMeBodySampleCatalogTests
{
    private static readonly string[] Pool =
        ["wording A", "wording B", "wording C", "wording D", "wording E"];

    [Fact]
    public void The_same_post_draws_the_same_wording_every_time()
    {
        var first = SoMeBodySampleCatalog.Pick(Pool, "track:Azure", 2);

        // A thousand re-plans later — this is the assertion that rules Random out.
        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal(first, SoMeBodySampleCatalog.Pick(Pool, "track:Azure", 2));
        }
    }

    [Fact]
    public void The_two_rounds_of_one_track_can_read_differently()
    {
        // Occurrence is part of the hash, so a track's second announcement is not a copy of its
        // first — which is the point of announcing it twice.
        var one = SoMeBodySampleCatalog.Pick(Pool, "track:Azure", 1);
        var two = SoMeBodySampleCatalog.Pick(Pool, "track:Azure", 2);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one, two);
    }

    /// <summary>Across a realistic campaign the pool is actually USED, not collapsed onto one entry.</summary>
    [Fact]
    public void A_campaign_spreads_across_the_catalog()
    {
        var tracks = new[]
        {
            "track:Azure", "track:Identity", "track:Intune", "track:Security",
            "track:Microsoft 365", "track:Data Compliance & Security",
            "track:AI for Makers (Copilot & Agents)", "track:AI for Engineers/Developers",
        };

        var drawn = tracks
            .SelectMany(t => new[] { 1, 2 }.Select(n => SoMeBodySampleCatalog.Pick(Pool, t, n)))
            .Distinct()
            .Count();

        // 16 posts over a pool of 5: a hash that clustered onto one or two wordings would defeat
        // the whole feature, so this asserts real variety rather than merely "not always equal".
        Assert.True(drawn >= 4, $"only {drawn} of {Pool.Length} wordings were used across 16 posts");
    }

    /// <summary>🔒 An empty pool means "he has written no samples" — fall back, never throw.</summary>
    [Fact]
    public void An_empty_catalog_yields_null_so_the_single_template_still_wins()
    {
        Assert.Null(SoMeBodySampleCatalog.Pick(Array.Empty<string>(), "track:Azure", 1));
    }

    [Fact]
    public void A_single_sample_is_always_that_sample()
    {
        Assert.Equal("only one", SoMeBodySampleCatalog.Pick(["only one"], "session:12", 1));
    }

    /// <summary>The index must stay inside the pool for any subject, including odd ones.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("track:Ærø & Ø")]
    [InlineData("session:2147483647")]
    public void Any_subject_lands_inside_the_catalog(string subject)
    {
        Assert.Contains(SoMeBodySampleCatalog.Pick(Pool, subject, 1), Pool);
    }
}
