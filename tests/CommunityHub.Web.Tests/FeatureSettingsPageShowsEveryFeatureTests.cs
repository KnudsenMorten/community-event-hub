using System;
using System.IO;
using System.Linq;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §438 (operator 2026-07-27: <i>"what else is turned off?"</i>) — EVERY catalog feature must be
/// reachable on <c>/Organizer/Settings</c>.
///
/// <para>The page grouped features by "what a switch governs" and dropped the whole Email chapter
/// with <c>.Where(g =&gt; g.Key != FeatureGroup.Email)</c>. The intent was narrow — the
/// <c>outbound-email</c> master switch has its own card above, and rendering it twice would be
/// confusing. The effect was not narrow: <b>four</b> features filed under that chapter
/// (<c>welcome-email</c>, <c>magic-link</c>, <c>speaker-graphics-promote</c>,
/// <c>masterclass-notifications</c>) disappeared from the only page that can toggle them — and all
/// four are <c>DefaultEnabled: false</c>, so they were off AND invisible, with nothing on the page
/// hinting they existed. That is how §436's Help Promote mail could be "off" with no way to
/// switch it on.</para>
///
/// <para>A rendering bug like this is silent by nature — the page looks complete, because a
/// missing row leaves no gap. So the guard is a COUNT-based one over the real catalog: it fails
/// if the razor filters out any feature, whatever the mechanism.</para>
/// </summary>
public sealed class FeatureSettingsPageShowsEveryFeatureTests
{
    private static string SettingsRazor()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "CommunityHub", "Pages", "Organizer", "Settings.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Organizer/Settings.cshtml");
    }

    /// <summary>
    /// The exact regression: no whole-CHAPTER exclusion may sit in the grid query. Skipping the
    /// master switch BY KEY is fine (it is rendered in its own card); skipping a FeatureGroup is
    /// not, because every feature in that group vanishes with it.
    /// </summary>
    [Fact]
    public void The_grid_never_drops_a_whole_chapter()
    {
        var razor = SettingsRazor();

        Assert.DoesNotContain("g.Key != FeatureGroup.Email", razor);
        // Any other shape of the same mistake.
        Assert.DoesNotContain("Key != FeatureGroup.", razor);

        // The legitimate narrow skip — by KEY, not by group — must be what is there instead.
        Assert.Contains("s.Key != FeatureCatalog.OutboundEmailKey", razor);
    }

    /// <summary>
    /// Every catalog feature carries a <see cref="FeatureDescriptor.Governs"/> value that the page
    /// loops over. If a feature ever fell outside those three classes it would silently never
    /// render, which is the same failure by a different route.
    /// </summary>
    [Fact]
    public void Every_catalog_feature_falls_into_one_of_the_three_rendered_classes()
    {
        var rendered = new[] { FeatureGoverns.Feature, FeatureGoverns.Email, FeatureGoverns.Backend };

        var orphans = FeatureCatalog.All
            .Where(d => !rendered.Contains(d.Governs))
            .Select(d => d.Key)
            .ToList();

        Assert.True(orphans.Count == 0,
            "These features have no rendered class on /Organizer/Settings and would be invisible: "
            + string.Join(", ", orphans));
    }

    /// <summary>
    /// The four that were hidden are ordinary catalog members — this pins that they are still in the
    /// catalog and therefore reachable by the fix above.
    /// </summary>
    /// <remarks>
    /// §695 — the GROUP assertion was dropped deliberately. It required these four to live under
    /// `Email`, which is grouping by DELIVERY MECHANISM; the operator asked for grouping by ROLE
    /// ("this one should be under speaker role"), and `speaker-graphics-promote` moved to
    /// `SpeakersSessions` accordingly. What this test exists to guarantee is that a feature is
    /// PRESENT and therefore RENDERABLE — pinning which chapter it appears in turned a UI
    /// reorganisation into a failing test about visibility.
    /// </remarks>
    [Fact]
    public void The_four_previously_hidden_email_features_are_real_catalog_entries()
    {
        foreach (var key in new[]
                 {
                     "welcome-email", "magic-link",
                     "speaker-graphics-promote", "masterclass-notifications",
                 })
        {
            var d = FeatureCatalog.Find(key);
            Assert.True(d is not null, $"{key} is missing from the catalog");
            // A real, rendered group — never the unset default — so the row cannot be orphaned.
            Assert.True(
                Enum.IsDefined(typeof(FeatureGroup), d!.Group),
                $"{key} has an unrecognised group and would not render.");
        }
    }
}
