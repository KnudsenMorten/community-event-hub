using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §375 — every e-mail CTA must use the **VML bulletproof** pattern, verified by the operator in
/// desktop Outlook 2026-07-26 (<i>"look ow the button now looks inside outlook in desktop -
/// perfect, make a note of this so we keep/remember this"</i>).
///
/// <para><b>Why this needs a test and not just a convention.</b> Outlook on Windows renders with the
/// WORD engine: it ignores <c>border-radius</c> outright and paints the Office theme's hyperlink
/// colour over yours. Phones and webmail use WebKit/Blink and honour both — so a broken button looks
/// PERFECT on the phone you happen to check, and three separate fixes (<c>border-radius</c> on the
/// <c>&lt;td&gt;</c>, <c>color:#ffffff !important</c>, a nested <c>&lt;font color&gt;</c>) were each
/// believed to have worked before the operator opened one on a desktop. A styling rule that only
/// fails on one client, silently, is exactly the thing to pin in CI.</para>
///
/// <para>The rule: a template that renders a filled pill anchor must ALSO carry a
/// <c>v:roundrect</c> for Outlook — the two halves of the pattern always ship together.</para>
/// </summary>
public sealed class EmailButtonBulletproofTests
{
    /// <summary>The non-Outlook half: an anchor styled as a filled pill.</summary>
    private static readonly Regex PillAnchor =
        new(@"<a[^>]*style=""[^""]*border-radius:999px[^""]*""", RegexOptions.IgnoreCase);

    private static readonly Regex RoundRect = new(@"<v:roundrect", RegexOptions.IgnoreCase);

    [Fact]
    public void Every_template_with_a_pill_button_also_ships_the_OUTLOOK_half()
    {
        var offences = new List<string>();

        foreach (var (label, file, text) in EachTemplate())
        {
            var pills = PillAnchor.Matches(text).Count;
            if (pills == 0) continue;

            var vml = RoundRect.Matches(text).Count;
            if (vml < pills)
            {
                offences.Add($"{label}: {pills} pill button(s) but only {vml} <v:roundrect> — "
                             + "desktop Outlook will render square corners and theme-coloured text");
            }
        }

        Assert.True(
            offences.Count == 0,
            "Outlook on Windows uses the Word engine and obeys ONLY the VML half of the pattern:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void The_outlook_half_keeps_the_pieces_that_actually_make_it_work()
    {
        // Each of these is load-bearing, and each is easy to "tidy away":
        //   arcsize   -> the pill shape itself (drop it and you get a rectangle)
        //   color     -> Word repaints link text unless the <center> sets it explicitly
        //   width     -> VML cannot auto-size; without it the label clips
        var offences = new List<string>();

        foreach (var (label, _, text) in EachTemplate())
        {
            foreach (Match m in Regex.Matches(text, @"<v:roundrect.*?</v:roundrect>", RegexOptions.Singleline))
            {
                var block = m.Value;
                if (!block.Contains("arcsize=", StringComparison.OrdinalIgnoreCase))
                    offences.Add($"{label}: <v:roundrect> without arcsize (no rounded corners)");
                if (!Regex.IsMatch(block, @"color:\s*#ffffff", RegexOptions.IgnoreCase))
                    offences.Add($"{label}: <v:roundrect> without an explicit white text colour");
                if (!Regex.IsMatch(block, @"width:\s*\d+px", RegexOptions.IgnoreCase))
                    offences.Add($"{label}: <v:roundrect> without a fixed width (VML cannot auto-size)");
            }
        }

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
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
                && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repo root from " + AppContext.BaseDirectory);
    }
}
