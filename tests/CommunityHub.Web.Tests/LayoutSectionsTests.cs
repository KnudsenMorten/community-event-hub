using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

// Regression guard for the class of bug that 500'd /Organizer/Hotels (2026-06-17):
// a page defined `@section Scripts { ... }` but _Layout.cshtml never rendered it, so the
// page threw "section 'Scripts' has not been rendered" AT RENDER TIME (an OnGet try/catch
// cannot catch a layout-render error). Any @section a page defines MUST be rendered by the
// layout. This is a cheap static check over the Razor sources — no app host needed.
public sealed class LayoutSectionsTests
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
        throw new DirectoryNotFoundException("Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_section_a_page_defines_is_rendered_by_the_layout()
    {
        var pages = PagesDir();
        var layout = File.ReadAllText(Path.Combine(pages, "Shared", "_Layout.cshtml"));

        // section names the layout renders (RenderSection / RenderSectionAsync / IgnoreSection)
        var rendered = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(layout, @"(?:RenderSectionAsync|RenderSection|IgnoreSection)\(\s*""(?<n>[^""]+)"""))
            rendered.Add(m.Groups["n"].Value);

        var defineRe = new Regex(@"@section\s+(?<n>[A-Za-z0-9_]+)");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').EndsWith("Shared/_Layout.cshtml", StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in defineRe.Matches(text))
            {
                var name = m.Groups["n"].Value;
                if (!rendered.Contains(name))
                    offenders.Add($"{Path.GetFileName(file)} defines @section {name} but _Layout never renders it");
            }
        }

        Assert.True(offenders.Count == 0,
            "Unrendered Razor sections (will 500 at render):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Layout_renders_the_Scripts_section()
    {
        var layout = File.ReadAllText(Path.Combine(PagesDir(), "Shared", "_Layout.cshtml"));
        Assert.Matches(@"RenderSectionAsync\(\s*""Scripts""", layout);
    }
}
