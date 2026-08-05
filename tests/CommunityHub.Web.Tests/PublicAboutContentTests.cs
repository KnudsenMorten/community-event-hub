using CommunityHub.Content;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §327g — the public <c>/About</c> page renders the SAME markdown as the in-hub
/// introduction, minus everything fenced <c>internal-only</c>. The operator's instruction was
/// "remove the important security stuff and publish", so what matters is not that the page
/// looks right but that the withheld text is genuinely ABSENT from the output — stripped from
/// the source before rendering, not hidden with CSS where "view source" would reveal it.
/// </summary>
public sealed class PublicAboutContentTests
{
    private const string Start = ContentMarkdownRenderer.InternalOnlyStart;
    private const string End = ContentMarkdownRenderer.InternalOnlyEnd;

    [Fact]
    public void A_fenced_block_is_removed_entirely()
    {
        var md = $"public one\n{Start}\nSECRET DETAIL\n{End}\npublic two";

        var stripped = ContentMarkdownRenderer.StripInternalOnly(md);

        Assert.DoesNotContain("SECRET DETAIL", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain(Start, stripped, StringComparison.Ordinal);
        Assert.DoesNotContain(End, stripped, StringComparison.Ordinal);
        Assert.Contains("public one", stripped, StringComparison.Ordinal);
        Assert.Contains("public two", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_fenced_blocks_are_all_removed()
    {
        // The real page fences two separate places: the quick-links row and the whole
        // Security chapter. Missing the second one would publish it.
        var md = $"a\n{Start}\nHIDE1\n{End}\nb\n{Start}\nHIDE2\n{End}\nc";

        var stripped = ContentMarkdownRenderer.StripInternalOnly(md);

        Assert.DoesNotContain("HIDE1", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDE2", stripped, StringComparison.Ordinal);
        Assert.Contains("a", stripped, StringComparison.Ordinal);
        Assert.Contains("b", stripped, StringComparison.Ordinal);
        Assert.Contains("c", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unterminated_fence_fails_CLOSED_and_drops_the_rest()
    {
        // An author who forgets the closing marker must LOSE public content, never publish
        // the block they meant to withhold. Losing text is visible; leaking is not.
        var md = $"visible\n{Start}\nSECRET DETAIL\nmore secret";

        var stripped = ContentMarkdownRenderer.StripInternalOnly(md);

        Assert.DoesNotContain("SECRET DETAIL", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("more secret", stripped, StringComparison.Ordinal);
        Assert.Contains("visible", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_with_no_fences_is_returned_unchanged()
    {
        const string md = "# Title\n\nJust ordinary content.";
        Assert.Equal(md, ContentMarkdownRenderer.StripInternalOnly(md));
    }

    [Fact]
    public void The_real_intro_page_hides_its_security_chapter_from_the_public_view()
    {
        // Guards the actual shipped file: if someone unfences the Security chapter, or renames
        // the markers, this fails rather than silently publishing it.
        var path = FindContentFile("ceh-introduction.md");
        Assert.True(path is not null, "ceh-introduction.md was not found — has it moved?");

        var md = File.ReadAllText(path!);
        Assert.Contains(Start, md, StringComparison.Ordinal);   // still fenced at all

        var pub = ContentMarkdownRenderer.StripInternalOnly(md);

        // The chapter heading and a representative sentence from inside it must be gone.
        // §707.48 — every chapter shifted by one when "2. CEH by the numbers" was inserted, so
        // Security is now 6 and the survivors are 7 and 9. These assertions name the NUMBER on
        // purpose: they are what proves the internal-only markers still wrap the RIGHT chapter after
        // a renumber, which is precisely when a marker slips onto the wrong section and quietly
        // publishes it.
        Assert.DoesNotContain("## 6. Security", pub, StringComparison.Ordinal);
        Assert.DoesNotContain("Secrets and workload identity", pub, StringComparison.Ordinal);

        // …while the parts meant for the public survive.
        Assert.Contains("## 1. What is CEH?", pub, StringComparison.Ordinal);
        Assert.Contains("## 2. CEH by the numbers", pub, StringComparison.Ordinal);
        Assert.Contains("## 7. How it is built and released", pub, StringComparison.Ordinal);
        Assert.Contains("## 9. Who built it", pub, StringComparison.Ordinal);
    }

    /// <summary>Walk up from the test binaries to the repo's config/content folder.</summary>
    private static string? FindContentFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "content", "eldk27", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
