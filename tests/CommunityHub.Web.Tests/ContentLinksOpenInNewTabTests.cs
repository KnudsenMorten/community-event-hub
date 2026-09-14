using System.Text.RegularExpressions;
using CommunityHub.Content;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §429 — every embedded link on a CONTENT page opens in a new tab.
///
/// <para>Operator 2026-07-27, pointing at the <c>survey results</c> link inside the Session
/// Guidelines: <i>"this link (and any others embedded links) must always open a new tab, instead
/// of opening in the same tab - bad experience"</i> — followed by the fair question, <i>"why do
/// you not capture this across the tests or the audit"</i>.</para>
///
/// <para><b>The honest answer was: nothing tested it.</b> The renderer re-targeted only
/// <c>http(s)://</c> links, so the ROOT-RELATIVE ones sitting in the middle of the prose
/// (<c>/survey/…</c>, <c>/Info/…</c>) quietly navigated away from the page being read. No test
/// covered the rule, so the gap could only be found by a person losing their place. This file is
/// that missing coverage — it asserts the rule on the renderer AND sweeps every shipped content
/// file, so a newly-authored link is caught here rather than in production.</para>
/// </summary>
public sealed class ContentLinksOpenInNewTabTests
{
    [Fact]
    public void A_root_relative_link_opens_in_a_new_tab()
    {
        // THE reported link. It is internal, which is exactly why the old http-only rule missed it.
        var html = ContentMarkdownRenderer.OpenLinksInNewTab(
            "<p>Check the <a href=\"/survey/eldk27-topics/results\">survey results</a>.</p>");

        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void An_external_link_still_opens_in_a_new_tab()
    {
        // The pre-existing behaviour must survive the widening.
        var html = ContentMarkdownRenderer.OpenLinksInNewTab(
            "<a href=\"https://expertslive.dk\">Experts Live</a>");

        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void An_in_page_anchor_stays_in_the_same_tab()
    {
        // Jumping to a heading on the page you are already reading must NOT open a second copy
        // of that page — that would be the same annoyance this rule exists to remove.
        var html = ContentMarkdownRenderer.OpenLinksInNewTab(
            "<a href=\"#audience-level\">Audience level</a>");

        Assert.DoesNotContain("target=", html);
    }

    [Fact]
    public void A_link_that_already_declares_a_target_is_left_alone()
    {
        // An author who wrote target="_self" on purpose keeps it.
        const string src = "<a href=\"/Info/policies\" target=\"_self\">Policies</a>";
        Assert.Equal(src, ContentMarkdownRenderer.OpenLinksInNewTab(src));
    }

    [Fact]
    public void Every_link_in_every_shipped_content_page_would_open_in_a_new_tab()
    {
        // The sweep the operator asked for: not "does the helper work" but "is the rule actually
        // true of what we ship". Renders each content file the way the page does and checks the
        // resulting anchors.
        var renderer = new ContentMarkdownRenderer();
        var offences = new List<string>();
        var checkedLinks = 0;
        var downloadsSeen = 0;

        foreach (var file in ContentFiles())
        {
            var slug = Path.GetFileNameWithoutExtension(file);
            if (!renderer.TryRender(slug, out var html)) continue;

            foreach (Match a in Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var tag = a.Value;
                var href = Regex.Match(tag, "href=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (!href.Success || href.Groups[1].Value.StartsWith('#')) continue;

                // §661 — a FILE DOWNLOAD is EXEMPT, and must actively NOT be re-targeted. It never
                // navigates the page, so a new tab opens, finds an attachment it cannot render, and
                // closes itself: the flash the operator reported on the speaker-template button.
                if (ContentMarkdownRenderer.IsFileDownload(href.Groups[1].Value))
                {
                    downloadsSeen++;
                    if (tag.Contains("target=\"_blank\"", StringComparison.OrdinalIgnoreCase))
                    {
                        offences.Add($"{slug}.md: {href.Groups[1].Value} is a DOWNLOAD but opens a "
                                     + "new tab — the tab flashes open and shut (§661).");
                    }
                    continue;
                }

                checkedLinks++;
                if (!tag.Contains("target=", StringComparison.OrdinalIgnoreCase))
                {
                    offences.Add($"{slug}.md: {href.Groups[1].Value} would replace the page "
                                 + "being read instead of opening a new tab.");
                }
            }
        }

        Assert.True(checkedLinks > 0, "The sweep found no links at all — the content path is wrong, "
                                      + "which would make this test a permanent no-op.");
        // §661: the download exemption must be exercised by real shipped content, or a regression
        // that re-targets downloads would sail through this sweep unnoticed.
        Assert.True(downloadsSeen > 0, "No download link was found in the shipped content — the §661 "
                                       + "exemption is untested and this sweep would not catch it.");
        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
    }

    [Fact]
    public void A_download_link_stays_in_the_same_tab()
    {
        // THE reported link (operator 2026-07-29): "it is like it opens up a page and then it
        // closes again and start the file download".
        const string src = "<a class=\"task-link-btn\" href=\"/speaker-template/download\">Get it</a>";
        Assert.Equal(src, ContentMarkdownRenderer.OpenLinksInNewTab(src));
    }

    [Theory]
    // Route-shaped downloads.
    [InlineData("/speaker-template/download", true)]
    [InlineData("/logo-pack/download", true)]
    [InlineData("/session-slides/12/final/download", true)]
    [InlineData("/session-slides/batch-download", true)]
    [InlineData("/session-slides/batch-download?ids=1&ids=2", true)]   // query must not hide it
    // File-shaped downloads.
    [InlineData("/media/deck.pptx", true)]
    [InlineData("https://example.com/guide.PDF", true)]
    // Pages you READ — these must keep opening in a new tab.
    [InlineData("/survey/eldk27-topics/results", false)]
    [InlineData("/Info/policies", false)]
    [InlineData("https://expertslive.dk", false)]
    [InlineData("/downloads", false)]              // a PAGE listing downloads, not a download
    [InlineData("/download-centre", false)]        // ...nor is this one
    public void Download_detection_separates_files_from_pages(string href, bool isDownload)
    {
        Assert.Equal(isDownload, ContentMarkdownRenderer.IsFileDownload(href));
    }

    private static List<string> ContentFiles()
    {
        var dir = Path.Combine(FindRepoRoot(), "config", "content", "eldk27");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.md").ToList()
            : new List<string>();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
