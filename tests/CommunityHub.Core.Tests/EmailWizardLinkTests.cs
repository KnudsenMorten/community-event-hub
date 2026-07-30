using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §398 — no e-mail may send anyone to a RETIRED Get-Started surface.
///
/// <para>Operator 2026-07-26, and the frustration is earned: <i>"the button in the welcome mail to
/// speakers goes to … /Forms/SpeakerWizard - we dont use that anymoe … i am tired of these old
/// links that still exist."</i> It was the third such report in a day — the retired
/// <c>/Attendee</c> page produced two more.</para>
///
/// <para><b>Why these survive so well:</b> the old pages still EXIST and still render, so a stale
/// link is not a 404 and nothing fails. It looks completely fine until somebody follows it and ends
/// up on a surface that no longer reflects how the product works. Only an explicit assertion catches
/// that class of rot.</para>
///
/// <para>Covers BOTH template layers — the shipped defaults and the private overlay that beats
/// them — because a link fixed in one and not the other is exactly how this recurs.</para>
/// </summary>
public sealed class EmailWizardLinkTests
{
    /// <summary>
    /// Retired Get-Started entry points. <c>/Sponsor/GetStarted</c> is deliberately ABSENT: sponsors
    /// keep their own bespoke wizard (their steps embed whole sections), so it is a live surface.
    /// </summary>
    private static readonly string[] RetiredRoutes =
    {
        "/Forms/SpeakerWizard",
        "/Forms/GetStarted",
        "/Attendee/Index",
        "/MyMasterClass",
    };

    [Fact]
    public void No_email_template_links_to_a_retired_wizard_surface()
    {
        var repo = FindRepoRoot();
        var offences = new List<string>();

        foreach (var dir in new[]
                 {
                     Path.Combine(repo, "templates", "emails"),
                     Path.Combine(repo, "config", "email-templates"),
                 }.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.html"))
            {
                var text = File.ReadAllText(file);
                foreach (var route in RetiredRoutes)
                {
                    // Match the LINK form only, so prose mentioning a path in a comment is fine.
                    if (text.Contains("{{hubUrl}}" + route, StringComparison.Ordinal)
                        || text.Contains("\"" + route, StringComparison.Ordinal))
                    {
                        offences.Add(
                            $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)} → {route}");
                    }
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "These e-mails send people to a retired Get-Started surface. The page still renders, so "
            + "nothing errors — it just shows them a version of the product we no longer use:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void The_email_SENDERS_do_not_build_retired_wizard_urls_either()
    {
        // A link built in C# is just as live as one in a template, and harder to spot.
        var repo = FindRepoRoot();
        var offences = new List<string>();

        var senderDirs = new[]
        {
            Path.Combine(repo, "src", "CommunityHub.Core", "Email"),
            Path.Combine(repo, "src", "CommunityHub.Core", "Reminders"),
        };

        foreach (var dir in senderDirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (var route in new[] { "/Forms/SpeakerWizard", "/Forms/GetStarted" })
                {
                    // Only a STRING LITERAL counts; the same path in a doc comment is documentation.
                    if (text.Contains("\"" + route, StringComparison.Ordinal)
                        || text.Contains(route + "\"", StringComparison.Ordinal))
                    {
                        offences.Add($"{Path.GetFileName(file)} → {route}");
                    }
                }
            }
        }

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
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
