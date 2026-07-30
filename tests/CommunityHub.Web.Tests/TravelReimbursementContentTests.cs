using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §205: the dense travel-reimbursement intro paragraph in the Travel form fields
/// partial must be split into headed BULLET sections that read on mobile, WITHOUT
/// changing the exact figures. A static check over the Razor source (no app host),
/// in the same spirit as <see cref="MicroPolishWiringTests"/>.
/// </summary>
public sealed class TravelReimbursementContentTests
{
    private static string TravelFields()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "CommunityHub", "Pages", "Forms", "_TravelFields.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate _TravelFields.cshtml");
    }

    [Fact]
    public void Intro_is_rendered_as_bulleted_sections_not_one_dense_paragraph()
    {
        var html = TravelFields();
        // At least two <ul> bullet lists (the covered + the "Not covered" sections).
        var firstUl = html.IndexOf("<ul", StringComparison.OrdinalIgnoreCase);
        Assert.True(firstUl >= 0, "expected a bullet list in the travel intro");
        var secondUl = html.IndexOf("<ul", firstUl + 1, StringComparison.OrdinalIgnoreCase);
        Assert.True(secondUl > firstUl, "expected a second bullet list (Not covered section)");
    }

    [Fact]
    public void Sections_and_their_exact_figures_are_present()
    {
        var html = TravelFields();

        Assert.Contains("We cover economy flights only", html);
        Assert.Contains("European air travel", html);
        Assert.Contains("Intercontinental air travel", html);
        Assert.Contains("Not covered", html);

        // Exact reimbursement figures must be unchanged.
        Assert.Contains("EUR 400", html);
        Assert.Contains("EUR 300", html);
        Assert.Contains("EUR 800", html);
        Assert.Contains("EUR 600", html);

        // The 6-week booking rule + "after the event" reimbursement note survive.
        Assert.Contains("6 weeks", html);
        Assert.Contains("after the event", html);
    }

    [Fact]
    public void Not_covered_items_are_listed()
    {
        var html = TravelFields();
        Assert.Contains("Taxi", html);
        Assert.Contains("business class", html);
        Assert.Contains("parking", html);
        Assert.Contains("incidentals", html);
    }
}
