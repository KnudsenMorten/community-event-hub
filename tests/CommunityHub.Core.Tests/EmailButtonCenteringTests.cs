using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §404 — every e-mail CTA and its caption must be CENTRED (operator 2026-07-26: <i>"bug: verify
/// that all buttons + text below is centered in all emails"</i>).
///
/// <para><b>What was actually wrong, because it is not the obvious thing.</b> Every button already
/// sat in a <c>&lt;td align="center"&gt;</c> — so by inspection it looked right, and that is why it
/// survived. But the wrapping <c>&lt;table&gt;</c> carried no width, and an HTML table shrinks to
/// its contents: the cell was faithfully centring the button inside a box exactly as wide as the
/// button, and the whole box sat on the LEFT. Thirty-three templates, all of them.</para>
///
/// <para>Two halves, both pinned here, because fixing either alone leaves a visibly wrong e-mail:
/// the table must be <c>width="100%"</c> for <c>align="center"</c> to mean anything, and the caption
/// under the button is a normal block that needs its own <c>text-align:center</c> or it sits left
/// under a centred button — which looks MORE broken than both being left.</para>
///
/// <para>Pinned rather than left to review: this is invisible in every preview that renders a
/// narrow body, and it reached production across the entire template set once already.</para>
/// </summary>
public sealed class EmailButtonCenteringTests
{
    /// <summary>A button table: a presentation table whose first cell centres its content.</summary>
    private static readonly Regex CentredButtonTable = new(
        @"<table role=""presentation""(?<attrs>[^>]*)>\s*<tr>\s*<td align=""center""",
        RegexOptions.IgnoreCase);

    /// <summary>The caption under a button — the "signs you in automatically" line.</summary>
    private static readonly Regex ButtonCaption = new(
        @"<p(?<attrs>[^>]*)>\s*\(?(?:the button signs you in|opens event hub|opens your (?:hub|get started))",
        RegexOptions.IgnoreCase);

    [Fact]
    public void A_button_table_is_full_width_so_align_center_actually_centres_it()
    {
        var offences = new List<string>();

        foreach (var (label, _, text) in EachTemplate())
        {
            foreach (Match m in CentredButtonTable.Matches(text))
            {
                var attrs = m.Groups["attrs"].Value;
                if (!attrs.Contains("width=\"100%\"", StringComparison.OrdinalIgnoreCase)
                    && !attrs.Contains("width:100%", StringComparison.OrdinalIgnoreCase))
                {
                    offences.Add($"{label}: <td align=\"center\"> inside a table with no width — "
                                 + "the table shrinks to the button and the whole thing sits LEFT");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "align=\"center\" centres content WITHIN the cell; without a full-width table there is "
            + "nothing to centre within:\n  " + string.Join("\n  ", offences));
    }

    [Fact]
    public void The_caption_under_a_button_is_centred_too()
    {
        var offences = new List<string>();

        foreach (var (label, _, text) in EachTemplate())
        {
            foreach (Match m in ButtonCaption.Matches(text))
            {
                if (!m.Groups["attrs"].Value.Contains("text-align:center", StringComparison.OrdinalIgnoreCase))
                {
                    offences.Add($"{label}: the button caption has no text-align:center — it will "
                                 + "sit left under a centred button");
                }
            }
        }

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
    }

    [Fact]
    public void The_sweep_actually_found_the_buttons_it_claims_to_be_guarding()
    {
        // Without this, a regex that silently stops matching turns both tests above into
        // always-green no-ops — the failure mode that lets a guard rot without anyone noticing.
        var tables = EachTemplate().Sum(t => CentredButtonTable.Matches(t.Text).Count);
        var captions = EachTemplate().Sum(t => ButtonCaption.Matches(t.Text).Count);

        Assert.True(tables >= 30, $"Expected the template set to contain the button tables; found {tables}.");
        Assert.True(captions >= 20, $"Expected the template set to contain button captions; found {captions}.");
    }

    private static IEnumerable<(string Label, string File, string Text)> EachTemplate()
    {
        var repo = FindRepoRoot();
        foreach (var dir in new[]
                 {
                     Path.Combine(repo, "templates", "emails"),
                     Path.Combine(repo, "config", "email-templates"),
                 }.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.html"))
            {
                yield return (
                    $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}",
                    file,
                    File.ReadAllText(file));
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "templates", "emails"))
                && File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
