using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §885 — the prestaged call-to-action phrases behind <c>{Action_catalog_random}</c>.
/// <para>🔒 The rule these protect is not "is it random" but "is it STABLE once drawn". A phrase
/// that changed on every render would make the preview disagree with the published post — the exact
/// defect §863.4 was built to stop, and one that has already cost a real post.</para>
/// </summary>
public sealed class SoMeActionCatalogTests
{
    private const string Catalog = """
        See you there! 🎉

        Grab your ticket now 🎟️
        Don't miss it ⚡
        """;

    [Fact]
    public void Blank_lines_are_ignored_so_the_box_can_be_grouped_readably()
    {
        var phrases = SoMeActionCatalog.Parse(Catalog);

        Assert.Equal(3, phrases.Count);
        Assert.Equal("See you there! 🎉", phrases[0]);
        Assert.DoesNotContain(phrases, p => p.Length == 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void An_empty_catalog_yields_nothing_rather_than_a_placeholder(string? catalog)
    {
        Assert.Empty(SoMeActionCatalog.Parse(catalog));
        // 🔒 Null ⇒ §824.15's renderer DROPS the line, so a post never publishes a dangling label
        // or a literal "{Action_catalog_random}".
        Assert.Null(SoMeActionCatalog.Pick(catalog));
    }

    [Fact]
    public void A_picked_phrase_always_comes_from_the_catalog()
    {
        var phrases = SoMeActionCatalog.Parse(Catalog);

        for (var seed = 0; seed < 25; seed++)
        {
            Assert.Contains(SoMeActionCatalog.Pick(Catalog, new Random(seed)), phrases);
        }
    }

    /// <summary>
    /// True randomness repeats more than it feels like it should, and two identical posts in a row
    /// is exactly what he is trying to avoid by having a catalog at all.
    /// </summary>
    [Fact]
    public void The_next_phrase_differs_from_the_previous_one()
    {
        for (var seed = 0; seed < 25; seed++)
        {
            var next = SoMeActionCatalog.PickDifferentFrom(Catalog, "Don't miss it ⚡", new Random(seed));
            Assert.NotEqual("Don't miss it ⚡", next);
        }
    }

    /// <summary>A one-line catalog is a fixed closing line, not a broken configuration.</summary>
    [Fact]
    public void A_single_phrase_catalog_returns_that_phrase_even_if_it_repeats()
        => Assert.Equal("Only one 🎯",
            SoMeActionCatalog.PickDifferentFrom("Only one 🎯", "Only one 🎯"));

    /// <summary>
    /// §326k escapes these as little-text-format control characters, so a phrase containing one
    /// publishes with visible backslashes. Caught on SAVE, named, rather than found in a live post.
    /// </summary>
    [Theory]
    [InlineData("Meet us (at the booth)", '(')]
    [InlineData("Say @hello", '@')]
    [InlineData("Read [the guide]", '[')]
    [InlineData("100% sure_thing", '_')]
    public void A_phrase_that_would_publish_as_markup_is_reported_with_the_offending_character(
        string phrase, char expected)
    {
        var invalid = SoMeActionCatalog.Invalid(phrase);

        var bad = Assert.Single(invalid);
        Assert.Equal(phrase, bad.Phrase);
        Assert.Equal(expected, bad.Character);
    }

    /// <summary>Emoji are the whole point of the catalog and must never be treated as invalid.</summary>
    [Fact]
    public void Emoji_and_ordinary_punctuation_are_allowed()
        => Assert.Empty(SoMeActionCatalog.Invalid(Catalog));
}
