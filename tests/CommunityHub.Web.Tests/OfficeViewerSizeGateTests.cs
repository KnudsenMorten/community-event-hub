using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Pages.Sessions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §467 — the embedded Office viewer refuses a deck over its own size ceiling with
/// "File too large — the file specified is larger than what the Office Viewers are configured to
/// support". That failure is upstream and unfixable by us; what IS ours is not offering a "View"
/// button that is guaranteed to fail. These pin the decision rule, including the two directions
/// that matter most: an UNKNOWN size must never hide a viewable deck, and the ceiling must come
/// from CONFIG (Microsoft has moved it before).
/// </summary>
public sealed class OfficeViewerSizeGateTests
{
    private static SlidesModel Sut(long maxBytes) =>
        new(presentations: null!,
            spOptions: Options.Create(new GraphicsSharePointOptions { OfficeViewerMaxBytes = maxBytes }));

    [Fact]
    public void A_deck_over_the_ceiling_is_flagged_too_large()
    {
        Assert.True(Sut(10 * 1024 * 1024).TooLargeToView("deck.pptx", 12 * 1024 * 1024));
    }

    [Fact]
    public void A_deck_at_or_under_the_ceiling_stays_viewable()
    {
        var sut = Sut(10 * 1024 * 1024);
        Assert.False(sut.TooLargeToView("deck.pptx", 10 * 1024 * 1024));   // exactly at = fine
        Assert.False(sut.TooLargeToView("deck.pdf", 1024));
    }

    [Fact]
    public void An_UNKNOWN_size_is_never_treated_as_too_large()
    {
        // Only disable when we positively KNOW the file is over the ceiling. Guessing here would
        // hide a perfectly viewable deck behind a "too large" message that is not true.
        Assert.False(Sut(10 * 1024 * 1024).TooLargeToView("deck.pptx", null));
    }

    [Fact]
    public void A_non_viewable_TYPE_is_not_reported_as_too_large()
    {
        // A .zip has no View button at all; calling it "too large" would misdescribe why.
        Assert.False(Sut(1024).TooLargeToView("bundle.zip", 999_999));
    }

    [Fact]
    public void Zero_disables_the_check_entirely()
    {
        // The escape hatch: if Microsoft raises the limit, the operator sets 0 rather than waiting
        // for a code change.
        Assert.False(Sut(0).TooLargeToView("deck.pptx", long.MaxValue));
    }

    [Fact]
    public void The_ceiling_is_rendered_for_humans()
    {
        Assert.Equal("10 MB", Sut(10 * 1024 * 1024).OfficeViewerMaxDisplay);
        Assert.Equal("", Sut(0).OfficeViewerMaxDisplay);   // nothing to state when disabled
    }
}
