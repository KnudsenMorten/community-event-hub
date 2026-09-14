using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1060(a) — a placeholder abstract is not a description.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"ai must not approve a speaker session if text is blank or something
/// tbd or we are working on it and will release session description soon, then it must hold
/// back"</i>.</para>
///
/// <para>🔒 <b>The asymmetry these tests exist to protect.</b> A false NEGATIVE publishes one weak
/// post — visible, fixable. A false POSITIVE withholds a REAL session from the campaign silently, and
/// nobody is told their talk was skipped. So the false-positive cases below are not padding: they are
/// the more important half, and the <c>Real_abstracts_are_never_called_placeholders</c> theory is the
/// one to run first if this rule is ever tuned.</para>
/// </remarks>
public sealed class SoMePlaceholderTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public void Blank_is_missing(string? text) =>
        Assert.True(SoMePlaceholderText.IsMissingOrPlaceholder(text));

    [Theory]
    // The bare markers.
    [InlineData("TBD")]
    [InlineData("tbd")]
    [InlineData("  TBD.  ")]          // decorated with punctuation + whitespace
    [InlineData("(tbd)")]
    [InlineData("TBA")]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("none")]
    [InlineData("todo")]
    [InlineData("xxx")]
    [InlineData("-")]
    [InlineData("...")]
    [InlineData("?")]
    // Whole-sentence placeholders.
    [InlineData("To be announced")]
    [InlineData("to be confirmed")]
    [InlineData("Coming soon")]
    [InlineData("Description coming soon")]
    [InlineData("stay tuned")]
    [InlineData("More to follow")]
    public void A_bare_placeholder_is_a_placeholder(string text) =>
        Assert.True(SoMePlaceholderText.IsMissingOrPlaceholder(text));

    /// <summary>🔑 His own words, verbatim — the case this rule was written for.</summary>
    [Theory]
    [InlineData("we are working on it and will release session description soon")]
    [InlineData("We're working on it and will release the session description soon.")]
    [InlineData("Session description will be added shortly.")]
    [InlineData("The abstract follows.")]
    [InlineData("Awaiting the description from the speaker")]
    [InlineData("Details to follow")]
    [InlineData("work in progress")]
    [InlineData("Still writing this one")]
    [InlineData("Full session details will be published soon — watch this space.")]
    public void A_sentence_promising_the_description_later_is_a_placeholder(string text) =>
        Assert.True(SoMePlaceholderText.IsMissingOrPlaceholder(text));

    /// <summary>
    /// 🔴 THE HALF THAT MATTERS MOST. Each of these is a REAL abstract that contains a word the
    /// rule keys on. Calling any of them a placeholder silently drops a genuine talk from the
    /// campaign — and nobody is told, which is the failure §1060(a) is least able to detect.
    /// </summary>
    [Theory]
    // "coming soon" inside real prose — the case that set the length bound.
    [InlineData("In this session we take a deep dive into what's coming soon in Azure: the new "
              + "networking stack, the identity changes landing this autumn, and how to prepare "
              + "your estate for both without a migration weekend. Demos throughout, and a "
              + "checklist you can take back to work on Monday.")]
    // "details" and "soon" both present, in a real description.
    [InlineData("We will cover the details of Conditional Access design, then look at what is "
              + "changing soon in token protection. Expect live demos, failure modes, and the "
              + "three misconfigurations we see most often in production tenants across Europe.")]
    // A short but genuine abstract with none of the markers.
    [InlineData("A practical hour on Kubernetes cost control.")]
    [InlineData("Live hacking of a Danish web shop, then fixing it.")]
    // "TBD" mentioned as content deep inside a long real abstract.
    [InlineData("Estimating cloud spend is hard when half the architecture is still marked TBD on "
              + "the whiteboard. This session shows how we forecast anyway: the three numbers that "
              + "actually move the bill, a spreadsheet you can copy, and what we got wrong on our "
              + "first two attempts at a 400-server migration.")]
    public void Real_abstracts_are_never_called_placeholders(string text) =>
        Assert.False(SoMePlaceholderText.IsMissingOrPlaceholder(text));

    /// <summary>
    /// ⚠️ "none of this is magic" starts with a whole-text placeholder word. Whole-text matching is
    /// what keeps it prose — a substring rule would have refused to announce this session.
    /// </summary>
    [Fact]
    public void A_placeholder_word_that_merely_STARTS_a_real_sentence_is_prose()
    {
        Assert.True(SoMePlaceholderText.IsMissingOrPlaceholder("none"));
        Assert.False(SoMePlaceholderText.IsMissingOrPlaceholder(
            "None of this is magic: we build the pipeline live, break it twice, and fix it."));
    }

    [Fact]
    public void IsRealDescription_is_the_exact_inverse()
    {
        Assert.False(SoMePlaceholderText.IsRealDescription("TBD"));
        Assert.True(SoMePlaceholderText.IsRealDescription("A practical hour on Kubernetes cost control."));
    }
}
