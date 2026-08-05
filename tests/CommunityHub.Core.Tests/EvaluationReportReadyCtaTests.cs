using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §752.10 — the report-ready mail's button must deep-link to the speaker's RESULTS page.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this test exists.</b> The operator clicked "Read your evaluation" and landed on
/// the hub home page. App Insights showed his request as a bare <c>/go/{token}</c> with no
/// destination attached, while a probe fired seconds later against the same deployed build showed a
/// destination arriving and being honoured — so the redirect machinery was never at fault. The mail
/// simply did not carry the link it was supposed to.</para>
///
/// <para>🔒 <b>The real defect was the missing assertion.</b> The suite proved this mail rendered,
/// carried its PDF, and named the session — everything except the one thing the mail exists to do.
/// A CTA that points at the wrong place renders as a perfectly normal button, so it cannot be caught
/// by reading the mail either. That is precisely the §557 argument, which was written after the same
/// class of bug shipped five times in eight hours: <i>"an audit is a promise about the past"</i>.
/// The check has to be mechanical and it has to fail the build.</para>
///
/// <para>🔑 The link is asserted in the TEMPLATE, which is where it now lives. It used to be
/// assembled in C# as <c>?r=/Speaker/Results</c> — a second way of expressing a deep link that no
/// other CTA used. Nine others use <c>{{hubUrl}}/Path</c>, handled by <c>/go/{token}/{**target}</c>
/// since §169. Operator 2026-08-01: <i>"make it consistent with rest urls"</i>.</para>
/// </remarks>
public sealed class EvaluationReportReadyCtaTests
{
    private const string TemplateName = "session-evaluation-report-ready.html";
    private const string Destination = "{{hubUrl}}/Speaker/Results";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "templates")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string TemplateText() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "templates", "emails", TemplateName));

    /// <summary>
    /// The plain-HTML anchor AND the Outlook VML button. 🔒 Both, because desktop Outlook renders
    /// the VML and ignores the anchor entirely (the §-VML button standard) — fixing only the one you
    /// can see in a browser leaves the button broken for exactly the audience most likely to click
    /// it from a laptop.
    /// </summary>
    [Fact]
    public void Both_the_anchor_and_the_Outlook_VML_button_point_at_the_results_page()
    {
        var html = TemplateText();

        var hrefs = System.Text.RegularExpressions.Regex
            .Matches(html, "href=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(h => h.Contains("hubUrl", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, hrefs.Count);                       // the anchor and the VML roundrect
        Assert.All(hrefs, h => Assert.Equal(Destination, h));
    }

    /// <summary>
    /// 🔒 The bare hub is the FAILURE this test was written for: <c>{{hubUrl}}</c> alone still
    /// renders a working button, still signs the speaker in, and still looks correct in review —
    /// it just strands them on the home page with no route to the thing the mail promised.
    /// </summary>
    [Fact]
    public void The_button_never_points_at_the_bare_hub()
    {
        var html = TemplateText();

        Assert.DoesNotContain("href=\"{{hubUrl}}\"", html);
        // And not the retired query form either — one deep-link convention, not two (§752.10).
        Assert.DoesNotContain("?r=", html);
    }
}
