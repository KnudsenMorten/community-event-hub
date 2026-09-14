using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §914 — A CARD REMOVED WITHOUT A ROUTE LEFT BEHIND IS A PAGE NOBODY CAN OPEN.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"this overview is too confusing we need to merge this into
/// fewer"</i>. The SoMe hub had grown to THIRTEEN cards — every § that shipped something added one
/// and none ever removed one — so it showed the same posts three times and the same setup four
/// times. Merged to six.</para>
///
/// <para>⚠️ <b>The danger of merging is silent.</b> Removing a card does not break a build, a
/// breadcrumb or any existing test: the page simply becomes unreachable by clicking, and only shows
/// up when someone goes looking for it and cannot find it. That had ALREADY happened once — §889
/// built the posts list and he could not find it (<i>"i still dont see the list functionality"</i>)
/// because nothing linked to it.</para>
///
/// <para>🔒 So each absorbed page is asserted to be linked FROM the card that absorbed it. Adding a
/// card back is fine; quietly dropping the link is what fails here.</para>
/// </remarks>
public sealed class SoMeHubMergeReachabilityTests
{
    /// <summary>absorbed page → the hub card it was merged into.</summary>
    public static TheoryData<string, string> Merges => new()
    {
        // The same posts, three ways ⇒ one card.
        { "SoMeCalendar",    "SoMeQueue" },
        { "SoMePostEditor",  "SoMeQueue" },
        // What the automatic posts say, and how often ⇒ one card.
        { "SoMeTemplates",   "SoMeScheduler" },
        { "SoMeCadence",     "SoMeScheduler" },
        // The starting steps ⇒ folded into "Set up social media".
        { "SoMeSettings",    "SoMeSetup" },
        { "LinkedInConnect", "SoMeSetup" },
        { "AssetLocations",  "SoMeSetup" },
    };

    private static string OrganizerPagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", "Organizer");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate src/CommunityHub/Pages/Organizer.");
    }

    [Theory]
    [MemberData(nameof(Merges))]
    public void An_absorbed_page_is_still_linked_from_the_card_that_absorbed_it(
        string absorbedPage, string parentPage)
    {
        var parentFile = Path.Combine(OrganizerPagesDir(), parentPage + ".cshtml");
        Assert.True(File.Exists(parentFile), $"{parentPage}.cshtml not found");

        var markup = File.ReadAllText(parentFile);
        var linked = Regex.IsMatch(
            markup,
            $@"asp-page\s*=\s*""/Organizer/{Regex.Escape(absorbedPage)}""",
            RegexOptions.IgnoreCase);

        Assert.True(
            linked,
            $"'{absorbedPage}' lost its hub card in the §914 merge and '{parentPage}' does not link "
            + $"to it — it is now reachable only by typing the URL.");
    }

    /// <summary>
    /// The hub itself stays SHORT. Not a style rule: thirteen cards is how it became unreadable, and
    /// the accretion happened one well-meaning card at a time.
    /// <para>§1215 — raised 7 → 8 at the operator's explicit request, for Posting capacity. A raise
    /// needs his word, not a well-meaning card.</para>
    /// </summary>
    [Fact]
    public void The_SoMe_hub_stays_under_nine_cards()
    {
        var hub = File.ReadAllText(Path.Combine(OrganizerPagesDir(), "SoMe.cshtml"));
        var cards = Regex.Matches(hub, @"new\(""/Organizer/").Count;

        Assert.True(cards <= 8, $"the SoMe hub is back up to {cards} cards — merge before adding.");
    }
}
