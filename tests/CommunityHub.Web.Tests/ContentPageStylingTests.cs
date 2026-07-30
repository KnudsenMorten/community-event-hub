using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §348 (operator 2026-07-26: <i>"it looks awful with the layout and it is very hard to read. if
/// you want to use tables, then should the lines in the table"</i>) — the content-hub markdown is
/// rendered by TWO surfaces, the in-hub <c>/Info/{slug}</c> page and the anonymous public
/// <c>/About</c>, and each used to carry its own copy of the styling.
///
/// <para>The copies drifted, exactly as duplicated CSS does: the §348 fixes (ruled cells, header
/// background, zebra striping, heading spacing) were applied to the in-hub page only, so the
/// PUBLIC page — the one shared with people outside the event — kept the old thin-underline
/// tables while its own comment claimed the two were identical. These tests pin the single
/// source and, more importantly, pin that neither page re-declares the rules locally: a local
/// override is how the drift comes back.</para>
/// </summary>
public sealed class ContentPageStylingTests
{
    private const string Partial = "_ContentPageStyles";

    [Fact]
    public void The_shared_partial_carries_the_348_table_rules()
    {
        var css = ReadPage("Shared", $"{Partial}.cshtml");

        // Ruled cells + a distinguishable header + zebra striping: the three things the operator's
        // screenshot was missing (rows ran together, columns drifted apart).
        Assert.Contains("border: 1px solid #d7dee8;", css, StringComparison.Ordinal);
        Assert.Contains(".content-page th", css, StringComparison.Ordinal);
        Assert.Contains("tbody tr:nth-child(even)", css, StringComparison.Ordinal);
        // …and the wide table must scroll INSIDE the card on a phone, never widen the page.
        Assert.Contains("overflow-x: auto", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Info", "Page.cshtml")]
    [InlineData(null, "About.cshtml")]
    public void Both_markdown_surfaces_render_the_shared_partial(string? folder, string file)
    {
        var page = folder is null ? ReadPage(file) : ReadPage(folder, file);

        Assert.Contains($"<partial name=\"{Partial}\" />", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Info", "Page.cshtml")]
    [InlineData(null, "About.cshtml")]
    public void Neither_surface_re_declares_the_markdown_styling_locally(string? folder, string file)
    {
        var page = folder is null ? ReadPage(file) : ReadPage(folder, file);

        // A page-local `.content-page table` / `th` / `code` rule is precisely how the public copy
        // fell behind. Page-SPECIFIC classes (the photo/venue galleries, .ab-scroll) are fine and
        // deliberately not matched here.
        Assert.DoesNotContain(".content-page table", page, StringComparison.Ordinal);
        Assert.DoesNotContain(".content-page th", page, StringComparison.Ordinal);
        Assert.DoesNotContain(".content-page code", page, StringComparison.Ordinal);
        Assert.DoesNotContain(".content-page h2", page, StringComparison.Ordinal);
    }

    private static string ReadPage(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pages = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(pages)) return File.ReadAllText(Path.Combine(pages, Path.Combine(parts)));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }
}
