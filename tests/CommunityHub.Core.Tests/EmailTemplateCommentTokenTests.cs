using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// CRITICAL, found live 2026-07-26 (operator screenshot of the 2-day attendee invite):
/// <i>"attendee 2-day ticket holder mail is all wrong … there are 2 content, rendering problem and
/// the entire beginning is gone but shown in the 2nd part of the email"</i>.
///
/// <para><b>Cause.</b> <c>_layout.html</c> documented its own tokens INSIDE an HTML comment —
/// including the body token. The renderer substitutes tokens across the WHOLE file, comments
/// included, so the entire rendered e-mail was injected inside that comment. The result in a real
/// inbox: the body rendered once unstyled, the comment's documentation prose leaked into the
/// message as visible text ending in <c>--&gt;</c>, and the real body then rendered again below —
/// one e-mail, printed twice, with developer prose in the middle.</para>
///
/// <para>A second instance was found by the same sweep in <c>masterclass-cancelled.html</c>, whose
/// comment carried the hub-URL token — which renders as a live <b>magic-link</b>, i.e. an auto-login
/// URL written into the message source.</para>
///
/// <para>This test makes the whole class impossible to ship: <b>no <c>{{token}}</c> may appear
/// inside an HTML comment in any e-mail template, in either layer.</b> It is a one-line rule with a
/// one-line fix (name the token in prose, without braces) and it catches the defect at build time
/// rather than in someone's inbox.</para>
/// </summary>
public sealed class EmailTemplateCommentTokenTests
{
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex Token = new(@"\{\{\s*[A-Za-z0-9_]+\s*\}\}");

    /// <summary>
    /// An **MSO conditional** (<c>&lt;!--[if mso]&gt;</c> … <c>&lt;![endif]--&gt;</c>, and the
    /// inverted <c>[if !mso]</c> form) is a comment to every parser EXCEPT Outlook's, which reads it
    /// as markup. A token there is therefore INTENTIONAL and correct — it is how a bulletproof VML
    /// button carries its href, which is the only way to get rounded corners and white link text in
    /// Outlook's Word engine. The rule this file enforces is about DOCUMENTATION comments, so these
    /// are excluded by design rather than by oversight.
    /// </summary>
    private static bool IsMsoConditional(string comment) =>
        comment.StartsWith("<!--[if ", StringComparison.OrdinalIgnoreCase)
        || comment.StartsWith("<!--<![endif]", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void No_email_template_puts_a_TOKEN_inside_an_HTML_COMMENT()
    {
        var repo = FindRepoRoot();
        var dirs = new[]
        {
            Path.Combine(repo, "templates", "emails"),          // shipped defaults
            Path.Combine(repo, "config", "email-templates"),    // private overlay (wins at send time)
        };

        var offences = new List<string>();

        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.html"))
            {
                var text = File.ReadAllText(file);
                foreach (Match comment in HtmlComment.Matches(text))
                {
                    if (IsMsoConditional(comment.Value)) continue;

                    var tokens = Token.Matches(comment.Value)
                        .Select(m => m.Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (tokens.Count > 0)
                    {
                        offences.Add(
                            $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}"
                            + $" → {string.Join(", ", tokens)}");
                    }
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "A token inside an HTML comment is substituted like any other, so the value is injected "
            + "INTO the comment — which broke a live attendee e-mail (body rendered twice, developer "
            + "prose visible in the message). Name the token in prose WITHOUT double braces:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void The_layout_still_carries_the_warning_that_explains_the_rule()
    {
        // The rule is only obeyed if the next person editing the layout knows WHY. If this warning
        // is deleted, the comment becomes an inviting place to document tokens again.
        var layout = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "templates", "emails", "_layout.html"));

        Assert.Contains("DO NOT WRITE A TOKEN IN DOUBLE BRACES", layout, StringComparison.Ordinal);
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
        throw new DirectoryNotFoundException(
            "Could not locate the repo root from " + AppContext.BaseDirectory);
    }
}
