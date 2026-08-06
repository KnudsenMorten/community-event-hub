using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §927 — the session-title exclusion filter. Operator 2026-08-06: <i>"exclude option with title
/// filters must be build like ask the experts*"</i>.
/// </summary>
public class SoMeTitleExclusionTests
{
    // 🔒 THE SAFETY DIRECTION. A blank setting must exclude NOTHING — an empty-matches-all bug here
    // would silently delete the entire session campaign, and it would look like the planner stopped.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void Empty_patterns_exclude_nothing(string? patterns)
    {
        Assert.False(SoMeTitleExclusions.IsExcluded("Ask the Experts — Identity", patterns));
        Assert.False(SoMeTitleExclusions.IsExcluded("Anything at all", patterns));
    }

    // His actual pattern, against his actual titles.
    [Theory]
    [InlineData("Ask the Experts — Identity", true)]
    [InlineData("ASK THE EXPERTS: Azure", true)]
    [InlineData("Ask the Experts", true)]                       // trailing * matches nothing too
    [InlineData("Ask the Expert", false)]                       // singular — a different session
    [InlineData("How to ask the experts anything", false)]      // anchored: not a "contains" match
    public void Star_matches_a_prefix(string title, bool excluded) =>
        Assert.Equal(excluded, SoMeTitleExclusions.IsExcluded(title, "ask the experts*"));

    // 🔑 A pattern with no star is EXACT, so it cannot quietly take more than it reads as.
    [Fact]
    public void Pattern_without_star_is_exact()
    {
        Assert.True(SoMeTitleExclusions.IsExcluded("ELDK27 Welcome", "ELDK27 Welcome"));
        Assert.False(SoMeTitleExclusions.IsExcluded("ELDK27 Welcome Drinks", "ELDK27 Welcome"));
    }

    // ⚠️ A real title carries regex metacharacters. Unescaped, "(Copilot & Agents)" would be read as
    // a capture group and the pattern would match the wrong things — or throw.
    [Fact]
    public void Punctuation_is_literal_not_regex()
    {
        Assert.True(SoMeTitleExclusions.IsExcluded(
            "AI for Makers (Copilot & Agents)", "AI for Makers (Copilot & Agents)"));
        Assert.False(SoMeTitleExclusions.IsExcluded("AI for Makers Copilot", "AI for Makers (Copilot)"));
        Assert.False(SoMeTitleExclusions.IsExcluded("Anything", "."));
    }

    [Fact]
    public void Any_pattern_in_the_list_excludes()
    {
        const string patterns = "ask the experts*\n\nExpo Floor*\n  Closing Note  ";

        Assert.True(SoMeTitleExclusions.IsExcluded("Expo Floor Walkthrough", patterns));
        Assert.True(SoMeTitleExclusions.IsExcluded("Closing Note", patterns));
        Assert.False(SoMeTitleExclusions.IsExcluded("Deep Dive: Entra ID", patterns));
        Assert.Equal(3, SoMeTitleExclusions.Parse(patterns).Count);
    }

    // A session with no title is not "matched by everything" — it is simply not excluded.
    [Fact]
    public void Missing_title_is_not_excluded() =>
        Assert.False(SoMeTitleExclusions.IsExcluded(null, "*"));

    // A bare "*" is the one pattern that DOES mean everything, and it is his to write.
    [Fact]
    public void Bare_star_matches_every_title() =>
        Assert.True(SoMeTitleExclusions.IsExcluded("Deep Dive: Entra ID", "*"));
}
