using CommunityHub.Content;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §351-5 (operator 2026-07-26: <i>"dont include features which are turned off"</i>) — the
/// <c>[feature:key]</c> line directive that keeps the features-per-role tables in step with the
/// per-edition switches instead of being a static list that goes stale the next time a flag moves.
///
/// <para>Two things are worth pinning and both are here: the RESOLUTION (on ⇒ prefix stripped,
/// off ⇒ line gone, and the directive never reaching Markdig in either case), and the KEYS —
/// every key used in a shipped content file must be a real <see cref="FeatureCatalog"/> key. A
/// typo'd key silently resolves to "shown" at runtime, which is the failure mode a reader can
/// never detect, so it is made a red test instead.</para>
/// </summary>
public sealed class ContentFeatureDirectiveTests
{
    private static readonly Func<string, bool> AllOn = _ => true;
    private static readonly Func<string, bool> AllOff = _ => false;

    [Fact]
    public void An_enabled_feature_keeps_the_line_and_strips_the_directive()
    {
        var md = ContentMarkdownRenderer.ApplyFeatureDirectives(
            "| **Leads** | lead lists |", AllOn);

        Assert.Equal("| **Leads** | lead lists |", md);
    }

    [Fact]
    public void A_disabled_feature_removes_the_whole_line()
    {
        const string src = "| **Kept** | a |\n[feature:sponsor-leads]| **Leads** | b |\n| **Also kept** | c |";

        var md = ContentMarkdownRenderer.ApplyFeatureDirectives(src, AllOff);

        Assert.DoesNotContain("Leads", md, StringComparison.Ordinal);
        Assert.Contains("**Kept**", md, StringComparison.Ordinal);
        Assert.Contains("**Also kept**", md, StringComparison.Ordinal);
    }

    [Fact]
    public void The_directive_never_survives_into_the_markdown_either_way()
    {
        const string src = "[feature:surveys]| **Surveys** | x |";

        Assert.DoesNotContain(
            ContentMarkdownRenderer.FeaturePrefix,
            ContentMarkdownRenderer.ApplyFeatureDirectives(src, AllOn),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ContentMarkdownRenderer.FeaturePrefix,
            ContentMarkdownRenderer.ApplyFeatureDirectives(src, AllOff),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_named_key_decides_the_line()
    {
        const string src =
            "[feature:on-key]| kept |\n[feature:off-key]| dropped |";

        var md = ContentMarkdownRenderer.ApplyFeatureDirectives(
            src, key => key == "on-key");

        Assert.Contains("kept", md, StringComparison.Ordinal);
        Assert.DoesNotContain("dropped", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_directive_is_KEPT_not_swallowed()
    {
        // Visible-wrong beats silently-deleted: an author who mistypes the bracket sees the
        // problem on the page instead of losing a row without a trace.
        const string src = "[feature:no-closing-bracket | row |";

        Assert.Equal(src, ContentMarkdownRenderer.ApplyFeatureDirectives(src, AllOff));
    }

    [Fact]
    public void Content_with_no_directives_is_returned_unchanged()
    {
        const string md = "# Title\n\n| a | b |\n| --- | --- |\n| c | d |";
        Assert.Equal(md, ContentMarkdownRenderer.ApplyFeatureDirectives(md, AllOff));
    }

    [Fact]
    public void Dropping_a_row_leaves_the_TABLE_intact()
    {
        // The §348 scar: an HTML-comment fence inside a table TERMINATES it, so every later row
        // renders as literal "| cell | cell |". A prefix must not do that — the header, the
        // separator and the surviving rows have to stay contiguous with no blank line punched
        // into the body.
        const string src =
            "| Group | What |\n| --- | --- |\n| **A** | a |\n[feature:off]| **B** | b |\n| **C** | c |";

        var md = ContentMarkdownRenderer.ApplyFeatureDirectives(src, AllOff);

        Assert.Equal(
            "| Group | What |\n| --- | --- |\n| **A** | a |\n| **C** | c |",
            md);
        Assert.DoesNotContain("\n\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureKeysIn_reports_each_key_once()
    {
        const string src =
            "[feature:sponsor-leads]| a |\n[feature:surveys]| b |\n[feature:sponsor-leads]| c |";

        Assert.Equal(
            new[] { "sponsor-leads", "surveys" },
            ContentMarkdownRenderer.FeatureKeysIn(src));
    }

    [Fact]
    public void Every_key_used_by_a_shipped_content_file_is_a_REAL_catalog_key()
    {
        // The guard that makes the whole mechanism safe to use. An unknown key falls open to
        // "shown", so a typo would look exactly like a working directive — until someone turned
        // the real feature off and the row stubbornly stayed. Fail here instead.
        var dir = FindContentDir();
        Assert.True(dir is not null, "config/content/eldk27 was not found — has it moved?");

        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir!, "*.md"))
        {
            foreach (var key in ContentMarkdownRenderer.FeatureKeysIn(File.ReadAllText(file)))
            {
                used.Add(key);
            }
        }

        var unknown = used.Where(k => FeatureCatalog.Find(k) is null).ToList();
        Assert.True(
            unknown.Count == 0,
            $"Content files reference feature keys that are not in FeatureCatalog: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void The_intro_page_gates_its_sponsor_leads_rows()
    {
        // Pins the actual shipped file: sponsor-leads is OFF for this edition (§326bq — "sponsor
        // leads is not active"), and its rows are exactly what the operator saw the page
        // advertising. If someone un-tags them, this fails.
        var dir = FindContentDir();
        Assert.True(dir is not null);

        var md = File.ReadAllText(Path.Combine(dir!, "ceh-introduction.md"));

        Assert.Contains("sponsor-leads", ContentMarkdownRenderer.FeatureKeysIn(md));

        var off = ContentMarkdownRenderer.ApplyFeatureDirectives(md, AllOff);
        Assert.DoesNotContain("Lead and inquiry lists", off, StringComparison.Ordinal);
        Assert.DoesNotContain("**Sponsor leads**", off, StringComparison.Ordinal);

        var on = ContentMarkdownRenderer.ApplyFeatureDirectives(md, AllOn);
        Assert.Contains("Lead and inquiry lists", on, StringComparison.Ordinal);
        Assert.Contains("**Sponsor leads**", on, StringComparison.Ordinal);

        // …and the untagged prose is untouched in both directions.
        Assert.Contains("## 7. Features per role", off, StringComparison.Ordinal);
        Assert.Contains("## 7. Features per role", on, StringComparison.Ordinal);
    }

    /// <summary>Walk up from the test binaries to the repo's config/content folder.</summary>
    private static string? FindContentDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "content", "eldk27");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
