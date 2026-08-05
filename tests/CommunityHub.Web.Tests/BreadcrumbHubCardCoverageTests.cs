using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityHub.Core.Navigation;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §831 — EVERY HUB CARD MUST PRODUCE A THREE-LEVEL BREADCRUMB.
///
/// <para>The operator reported the back link as wrong "on every page": walking
/// <b>Organizer Area → Marketing / SoMe → Content Studio</b> left the child page showing
/// <c>Organizer Area / Content Studio</c>, with the hub he had just clicked through missing.</para>
///
/// <para><b>The builder was never broken.</b> <c>ContentStudio</c> was absent from
/// <c>BreadcrumbBuilder.FeatureToHub</c>, so it fell to the closing root-only fallback — which
/// renders a perfectly plausible trail. That is why 54 of the 95 organizer pages could sit wrong
/// without anyone noticing: <b>the failure mode is silent</b>.</para>
///
/// <para>This test removes the silence. It parses the hub landing pages for their cards — the
/// authoritative "what is a child of this hub", because it is the path the operator actually walks —
/// and asserts each target resolves to <c>root ▸ that hub</c>. A page added to a hub without a map
/// entry now fails here instead of shipping.</para>
/// </summary>
public sealed class BreadcrumbHubCardCoverageTests
{
    /// <summary>The seven section hubs, by their Razor page name under Pages/Organizer.</summary>
    private static readonly string[] HubPages =
        ["People", "Content", "Comms", "SoMe", "Volunteers", "Logistics", "Setup"];

    /// <summary>
    /// Pages carded on TWO hubs. A trail has one parent, so the primary is asserted explicitly here
    /// rather than accepting whichever hub the test happened to scan first.
    /// </summary>
    private static readonly Dictionary<string, string> PrimaryHubOverrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Carded on Content + Setup — it configures the Sessionize import.
            ["/Organizer/SessionizeEndpointSettings"] = "/Organizer/Content",
            // Carded on SoMe + Comms — it is the social queue.
            ["/Organizer/SoMeQueue"] = "/Organizer/SoMe",
            // Carded on SoMe + Setup — these are the SoMe channel settings.
            ["/Organizer/SoMeSettings"] = "/Organizer/SoMe",
            // §833 — carded on SoMe + Comms. Primary = SoMe: "where is the post editor?" is a SoMe
            // question, and being filed only under Comms is exactly why he could not find it.
            ["/Organizer/SoMeTemplates"] = "/Organizer/SoMe",
        };

    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The card targets declared on a hub page. Both tile spellings are matched — the hubs use
    /// <c>new HubTile("/Organizer/X", …)</c> and the target-typed <c>new("/Organizer/X", …)</c>.
    /// </summary>
    private static IReadOnlyList<string> CardTargets(string hubPage)
    {
        var razor = File.ReadAllText(Path.Combine(PagesDir(), "Organizer", hubPage + ".cshtml"));
        return Regex.Matches(razor, "new(?:\\s+HubTile)?\\(\"(/Organizer/[A-Za-z0-9_]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [Fact]
    public void Every_hub_card_target_resolves_to_root_then_its_own_hub()
    {
        var failures = new List<string>();

        foreach (var hubPage in HubPages)
        {
            var hubHref = "/Organizer/" + hubPage;

            foreach (var target in CardTargets(hubPage))
            {
                // A hub may card another hub (or itself); those are not feature pages.
                if (HubPages.Any(h => string.Equals("/Organizer/" + h, target, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var expectedHub = PrimaryHubOverrides.TryGetValue(target, out var primary) ? primary : hubHref;

                // Only the hub that OWNS the page is asserted; the secondary card is a shortcut.
                if (!string.Equals(expectedHub, hubHref, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var trail = BreadcrumbBuilder.Build(target);

                if (trail.Count != 2)
                {
                    failures.Add(
                        $"{target} (carded on {hubPage}) produced a {trail.Count}-crumb trail, expected 2 "
                        + "(root ▸ hub). It is almost certainly missing from BreadcrumbBuilder.FeatureToHub, "
                        + "which silently degrades to root-only — the §831 defect.");
                    continue;
                }

                if (!string.Equals(trail[1].Href, expectedHub, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{target} points at {trail[1].Href}, expected {expectedHub}.");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Hub cards whose breadcrumb does not name their parent hub:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void The_page_he_reported_names_the_marketing_some_hub()
    {
        // The literal §831 reproduction: Organizer Area → Marketing / SoMe → Content Studio.
        var trail = BreadcrumbBuilder.Build("/Organizer/ContentStudio");

        Assert.Equal(2, trail.Count);
        Assert.Equal("/Organizer", trail[0].Href);
        Assert.Equal("/Organizer/SoMe", trail[1].Href);
    }
}
