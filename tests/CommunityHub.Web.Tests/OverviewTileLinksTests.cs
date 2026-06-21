using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// REQUIREMENTS §21 Organizer "clickable stat tiles → pre-filtered grid": the
/// /Organizer/Overview "Needs attention" tiles must be ANCHOR links that deep-link
/// into the grid pre-filtered to exactly the rows each number counts (no more
/// dead-end stats). The link targets mirror the Command-center attention tiles
/// (the verified source of truth, CommandCenterService), so this guards against the
/// tiles silently reverting to plain &lt;div&gt;s or drifting off the real routes.
///
/// Cheap static check over the Razor source — no app host needed (same approach as
/// LayoutSectionsTests).
/// </summary>
public sealed class OverviewTileLinksTests
{
    private static string OverviewCshtml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "CommunityHub", "Pages", "Organizer", "Overview.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Overview.cshtml from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Attention_tiles_are_anchor_links_not_dead_divs()
    {
        var html = OverviewCshtml();
        // The four needs-attention tiles must each be a clickable anchor.
        Assert.Contains("a class=\"ov-tile", html);
        Assert.DoesNotContain("<div class=\"ov-tile", html);
    }

    [Theory]
    // Overdue tasks -> the open-tasks grid sorted by due date.
    [InlineData("/Organizer/TasksTable", "asp-route-StateFilter=\"Open\"")]
    // Unassigned volunteer tasks + open help requests -> the volunteer work tree.
    [InlineData("/Organizer/VolunteerStructure", null)]
    // Pending volunteer applications -> inactive volunteers on the participant grid.
    [InlineData("/Organizer/Participants", "asp-route-RoleFilter=\"Volunteer\"")]
    public void Each_tile_deep_links_to_the_pre_filtered_grid(string page, string? routeMarker)
    {
        var html = OverviewCshtml();
        Assert.Contains($"asp-page=\"{page}\"", html);
        if (routeMarker is not null)
            Assert.Contains(routeMarker, html);
    }

    [Fact]
    public void Pending_volunteers_tile_filters_to_inactive_volunteers()
    {
        var html = OverviewCshtml();
        Assert.Contains("asp-route-ActiveFilter=\"inactive\"", html);
    }
}
