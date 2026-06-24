using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// REQUIREMENTS §21 cross-cutting "async loading/skeleton states for the
/// data-heavy grids". The skeleton is a progressive-enhancement behaviour wired
/// once in _Layout.cshtml (CSS + a shared script that watches any
/// <c>data-ceh-grid</c> container) and opted into per grid page via the
/// <c>data-ceh-grid</c> attribute — the same spirit as
/// <see cref="MicroPolishWiringTests"/> and <see cref="BreadcrumbWiringTests"/>,
/// so the cheapest faithful guard is a static check over the Razor sources (no
/// app host needed). It proves:
///   1. the layout carries the skeleton CSS + the shared script behaviour, and
///   2. every data-heavy organizer grid opts in (so a future edit that drops the
///      attribute is caught), and
///   3. the script behaves accessibly (aria-busy + an aria-live announcement)
///      and respects a reduced-motion preference, and
///   4. the Attendees CSV download link opts OUT (a download is not a page
///      replace, so it must not trip the skeleton).
/// </summary>
public sealed class GridSkeletonWiringTests
{
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

    private static string Page(params string[] parts) =>
        File.ReadAllText(Path.Combine(PagesDir(), Path.Combine(parts)));

    // §27: layout CSS + script now live in shared partials _Layout includes.
    private static string LayoutAll() =>
        Page("Shared", "_Layout.cshtml")
        + "\n" + Page("Shared", "_LayoutClientScripts.cshtml")
        + "\n" + Page("Shared", "_LayoutStyles.cshtml");

    [Fact]
    public void Layout_implements_the_grid_skeleton_behaviour()
    {
        var layout = LayoutAll();
        // The shared script watches the opt-in attribute and flips a busy state.
        Assert.Contains("data-ceh-grid", layout);
        Assert.Contains("data-ceh-grid-busy", layout);
        // The skeleton + shimmer CSS exists.
        Assert.Contains(".ceh-grid__skeleton", layout);
        Assert.Contains("ceh-shimmer", layout);
    }

    [Fact]
    public void Skeleton_is_accessible_busy_and_announced()
    {
        var layout = LayoutAll();
        // The busy grid is marked aria-busy and an aria-live region announces it.
        Assert.Contains("aria-busy", layout);
        Assert.Contains("aria-live", layout);
        Assert.Contains("data-ceh-grid-loading", layout); // localized text source
    }

    [Fact]
    public void Skeleton_respects_reduced_motion()
    {
        var layout = LayoutAll();
        Assert.Contains("prefers-reduced-motion", layout);
    }

    [Fact]
    public void Skeleton_is_one_shot_and_clears_on_bfcache_restore()
    {
        var layout = LayoutAll();
        // A single navigation arms it once; a back/forward bfcache restore clears it.
        Assert.Contains("if (armed) return", layout);
        Assert.Contains("pageshow", layout);
    }

    [Theory]
    [InlineData("Participants.cshtml")]
    [InlineData("Speakers.cshtml")]
    [InlineData("Attendees.cshtml")]
    [InlineData("Sessions.cshtml")]
    [InlineData("Sponsors.cshtml")]
    public void Each_data_heavy_grid_opts_into_the_skeleton(string file)
    {
        var page = Page("Organizer", file);
        Assert.Contains("data-ceh-grid", page);
        // The grid declares its localized "Loading…" text via the shared key.
        Assert.Contains("OrgGrid.Loading", page);
    }

    [Fact]
    public void Attendees_csv_export_link_opts_out_of_the_skeleton()
    {
        var page = Page("Organizer", "Attendees.cshtml");
        // The Export CSV link is a download, not a page replace.
        Assert.Contains("data-ceh-grid-skip", page);
    }
}
