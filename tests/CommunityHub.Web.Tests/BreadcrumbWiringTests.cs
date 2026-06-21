using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// REQUIREMENTS §21 cross-cutting "breadcrumbs" + "unsaved-changes guard".
///
/// Breadcrumbs are rendered by the shared _Breadcrumb partial (driven by the pure
/// BreadcrumbBuilder, unit-tested in Core) and included once in _Layout, so they
/// appear on every organizer back-office page with no per-page wiring. The
/// cheapest faithful guard — matching <see cref="MicroPolishWiringTests"/> — is a
/// static check over the Razor sources (no app host). It proves:
///   1. the layout includes the breadcrumb partial inside the content area;
///   2. the partial is driven by BreadcrumbBuilder + the page title leaf with the
///      right a11y semantics (nav landmark, ordered list, aria-current);
///   3. the highest-data-loss organizer editor (EditParticipant) opts into the
///      unsaved-changes + submit-loading guards, so a future edit that drops the
///      attribute is caught.
/// </summary>
public sealed class BreadcrumbWiringTests
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

    [Fact]
    public void Layout_includes_the_breadcrumb_partial_inside_the_content_area()
    {
        var layout = Page("Shared", "_Layout.cshtml");
        Assert.Contains("<partial name=\"_Breadcrumb\"", layout);
        // It sits inside <main> so it renders above the page body.
        var mainIdx = layout.IndexOf("<main", StringComparison.Ordinal);
        var partialIdx = layout.IndexOf("_Breadcrumb", StringComparison.Ordinal);
        var bodyIdx = layout.IndexOf("RenderBody()", StringComparison.Ordinal);
        Assert.True(mainIdx >= 0 && partialIdx > mainIdx && partialIdx < bodyIdx,
            "The breadcrumb partial must render inside <main> and before the page body.");
    }

    [Fact]
    public void Breadcrumb_partial_is_driven_by_the_builder_and_the_page_title_leaf()
    {
        var partial = Page("Shared", "_Breadcrumb.cshtml");
        Assert.Contains("BreadcrumbBuilder.Build", partial);
        // The leaf reuses the page title already set in ViewData.
        Assert.Contains("ViewData[\"Title\"]", partial);
    }

    [Fact]
    public void Breadcrumb_partial_has_accessible_trail_semantics()
    {
        var partial = Page("Shared", "_Breadcrumb.cshtml");
        Assert.Contains("<nav", partial);
        Assert.Contains("aria-label", partial);
        Assert.Contains("<ol>", partial);              // ordered = trail order
        Assert.Contains("aria-current=\"page\"", partial); // current page, not a link
        Assert.Contains("aria-hidden=\"true\"", partial);  // decorative separators
    }

    [Fact]
    public void Edit_participant_editor_opts_into_the_unsaved_changes_and_loading_guards()
    {
        var page = Page("Organizer", "EditParticipant.cshtml");
        Assert.Contains("data-ceh-dirty", page);
        Assert.Contains("data-ceh-loading", page);
        // The Cancel link opts OUT so leaving via Cancel never trips the prompt.
        Assert.Contains("data-ceh-allow-leave", page);
    }
}
