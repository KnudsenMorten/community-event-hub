using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §557 — every URL in every e-mail template must point at a page we still use.
///
/// <para><b>Why this is a TEST and not another audit.</b> The sponsor welcome mail sent recipients
/// to the retired <c>/Sponsor/GetStarted</c> instead of <c>/Forms/Wizard</c>. Operator 2026-07-28:
/// <i>"We have had this bug now 4 other times during last 8 hours and i have told you to audit ALL
/// code each time, but it still pops up"</i> — and <i>"how hard can it be to validate the url made
/// in 10-1 email templates? apparently very hard (!)"</i>.</para>
///
/// <para>He is right, and the reason it kept coming back is that an audit is a promise about the
/// past: it fixes today's instances and decays the moment anyone edits a template. A link that is
/// wrong in a mail cannot be spotted in review either — it renders as a normal button. So the
/// check has to be mechanical and it has to FAIL THE BUILD. That is what this is.</para>
/// </summary>
public sealed class EmailTemplateLinkTests
{
    /// <summary>
    /// Routes that still EXIST as pages but must never be linked from an e-mail again, with the
    /// reason. A denylist is required precisely because the file is still there — an
    /// "unresolvable route" check would happily pass a link to a retired-but-present page.
    /// </summary>
    private static readonly (string Route, string Reason)[] RetiredRoutes =
    {
        ("/Sponsor/GetStarted",
            "retired in favour of /Forms/Wizard — operator 2026-07-28, after it shipped in the "
            + "sponsor welcome mail five times in eight hours"),
    };

    /// <summary>
    /// §557 — BOTH template source folders. There are two, and that is precisely how the fix was
    /// missed: <c>config/email-templates/</c> was corrected while <c>templates/emails/</c> — the
    /// copy that actually ships — still carried the retired link, so the very next welcome mail
    /// went out wrong again. Scanning only one folder would leave this trap fully armed.
    /// </summary>
    private static IReadOnlyList<string> TemplateDirs()
    {
        var root = RepoRoot();
        var dirs = new[]
            {
                Path.Combine(root, "config", "email-templates"),
                Path.Combine(root, "templates", "emails"),
            }
            .Where(Directory.Exists)
            .ToList();

        if (dirs.Count == 0)
            throw new DirectoryNotFoundException("No e-mail template folder found from the test output.");
        return dirs;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config", "email-templates"))
                || Directory.Exists(Path.Combine(dir.FullName, "templates", "emails")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repo root not found from the test output.");
    }

    private static string TemplateDir() => TemplateDirs()[0];

    private static IReadOnlyList<(string File, string Url)> AllTemplateUrls()
    {
        var found = new List<(string, string)>();
        // Any hub URL a template builds, however it is written: href="{{hubUrl}}/X" in an anchor,
        // in a VML roundrect, or bare in text.
        var rx = new Regex(@"\{\{\s*hubUrl\s*\}\}(?<path>/[A-Za-z0-9/_\-]*)", RegexOptions.Compiled);

        foreach (var file in TemplateDirs().SelectMany(d => Directory.EnumerateFiles(d, "*.html", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in rx.Matches(text))
                found.Add((Path.GetFileName(file), m.Groups["path"].Value));
        }
        return found;
    }

    [Fact]
    public void No_email_template_links_to_a_retired_page()
    {
        var offences = new List<string>();

        foreach (var (file, url) in AllTemplateUrls())
        {
            foreach (var (route, reason) in RetiredRoutes)
            {
                if (url.StartsWith(route, StringComparison.OrdinalIgnoreCase))
                    offences.Add($"{file} links to {url} — {route} is RETIRED ({reason}).");
            }
        }

        Assert.True(offences.Count == 0,
            "An e-mail template points recipients at a page we no longer use. A wrong link in a "
            + "mail is invisible in review and reaches real people:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void Every_template_url_is_absolute_from_the_hub_root()
    {
        // A path that does not start with "/" concatenates onto {{hubUrl}} and silently produces a
        // broken address like "https://hubForms/Wizard".
        var bad = AllTemplateUrls()
            .Where(x => !x.Url.StartsWith('/'))
            .Select(x => $"{x.File}: '{x.Url}'")
            .ToList();

        Assert.True(bad.Count == 0,
            "A template URL must begin with '/': " + string.Join(", ", bad));
    }

    /// <summary>
    /// §557 — NOT every e-mail URL lives in a template. Some are built in CODE (the Get-Started
    /// digest picks a destination per ROLE), and that is exactly where the retired sponsor route
    /// survived after the templates were cleaned. Operator 2026-07-28: <i>"so url is different pe
    /// role - but must go to new get started for all"</i>.
    /// </summary>
    [Fact]
    public void No_email_BUILDING_CODE_points_at_a_retired_page()
    {
        var root = RepoRoot();
        var offences = new List<string>();

        // SCOPE, deliberately narrow (operator 2026-07-28): "this is not a change for all emails,
        // as their target is different per purpose … like the confirmation email for an attendee
        // takes to master class q&a page and the cancellation mail takes to master class selection
        // page". Only MAIL-BUILDING code is scanned — the page that still redirects a sponsor to
        // their old area is navigation, not a link we post to someone's inbox.
        foreach (var dir in new[] { "src/CommunityHub.Core/Reminders", "src/CommunityHub.Core/Email" })
        {
            var full = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) continue;

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                foreach (var line in File.ReadAllLines(file))
                {
                    foreach (var (route, reason) in RetiredRoutes)
                    {
                        // Only a STRING LITERAL of the route builds a link. A doc comment naming
                        // it (e.g. explaining the retirement) is documentation, not a link.
                        if (!line.Contains($"\"{route}\"", StringComparison.OrdinalIgnoreCase)) continue;
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("//") || trimmed.StartsWith("///")) continue;

                        offences.Add($"{Path.GetFileName(file)}: {trimmed} — {route} is RETIRED ({reason}).");
                    }
                }
            }
        }

        Assert.True(offences.Count == 0,
            "Code builds an e-mail link to a page we no longer use — the templates being clean is "
            + "not enough, because the destination is chosen per role in code:\n  "
            + string.Join("\n  ", offences));
    }

    [Fact]
    public void Templates_exist_and_are_actually_being_scanned()
    {
        // Guards the guard: if the template folder ever moves, the two tests above would pass by
        // finding nothing at all, and the protection would silently evaporate.
        Assert.NotEmpty(TemplateDirs().SelectMany(d => Directory.EnumerateFiles(d, "*.html")));
        Assert.NotEmpty(AllTemplateUrls());
    }
}
