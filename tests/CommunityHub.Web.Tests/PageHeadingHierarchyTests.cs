using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Accessibility (WCAG 2.4.6 "Headings and Labels" + 1.3.1 "Info and
/// Relationships"): every authenticated content page must lead with a single
/// top-level &lt;h1&gt; rather than starting its heading outline at &lt;h2&gt;.
///
/// The a11y pass promoted each covered page's title heading from &lt;h2&gt; to
/// &lt;h1&gt; (section sub-headings stay &lt;h2&gt;). This static check over the Razor
/// sources (no app host — same approach as <see cref="BreadcrumbWiringTests"/>)
/// guards the property going forward: across the covered Pages tree, any page that
/// renders section headings (&lt;h2&gt;) must also render a page-title &lt;h1&gt;, so a
/// future page added with only an &lt;h2&gt; title is caught here.
/// </summary>
public sealed class PageHeadingHierarchyTests
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

    // The areas the a11y heading pass covered. Each is scanned recursively.
    private static readonly string[] CoveredAreas =
        { "Forms", "Organizer", "Sponsor", "Attendee", "Speaker", "Volunteer" };

    private static IEnumerable<string> CoveredPages()
    {
        var root = PagesDir();

        // Two single role-home / profile pages at the Pages root that were in scope.
        foreach (var f in new[] { "Index.cshtml", "Profile.cshtml" })
            yield return Path.Combine(root, f);

        foreach (var area in CoveredAreas)
        {
            var areaDir = Path.Combine(root, area);
            if (!Directory.Exists(areaDir)) continue;
            foreach (var f in Directory.EnumerateFiles(areaDir, "*.cshtml", SearchOption.AllDirectories))
            {
                // Skip Razor partials (e.g. _SpeakerTaskRow.cshtml) — they have no page title.
                if (Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
    }

    [Fact]
    public void Covered_pages_with_section_headings_also_have_a_top_level_h1()
    {
        var offenders = new List<string>();

        foreach (var path in CoveredPages())
        {
            var html = File.ReadAllText(path);
            var hasH2 = html.Contains("<h2", StringComparison.Ordinal);
            var hasH1 = html.Contains("<h1", StringComparison.Ordinal);

            // Redirect-only / heading-less pages (e.g. Attendee/MasterClassQa) render
            // no headings at all — nothing to assert. We only require an <h1> once a
            // page introduces an <h2>, which would otherwise start the outline at h2.
            if (hasH2 && !hasH1)
                offenders.Add(Path.GetFileName(Path.GetDirectoryName(path)!) + "/" + Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "These covered pages have <h2> section headings but no <h1> page title " +
            "(promote the title heading to <h1>):\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("Index.cshtml")]
    [InlineData("Profile.cshtml")]
    public void Root_role_home_and_profile_have_an_h1(string file)
    {
        var html = File.ReadAllText(Path.Combine(PagesDir(), file));
        Assert.Contains("<h1", html, StringComparison.Ordinal);
    }
}
