using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §349 (operator 2026-07-26: <i>"can you give me a complete overview of ALL emails going out to me
/// and show their complete text (subject + text)"</i>) — <c>docs/EMAIL-INVENTORY.md</c> answers
/// that, and its one open risk was written into the backlog as a promise: *"keep it current —
/// regenerate whenever a template changes, or it becomes another stale doc."*
///
/// <para>A promise is not a mechanism. These tests make the drift a RED TEST instead: the
/// inventory must list every shipped template, list nothing that no longer exists, and name the
/// right LAYER for each one. The layer matters more than it looks — <c>config/email-templates/</c>
/// (the private overlay) BEATS <c>templates/emails/</c>, so an inventory naming the wrong layer
/// shows the operator text that is not what his event actually sends.</para>
///
/// <para>What this deliberately does NOT check is the body PROSE — comparing rendered text would
/// mean re-implementing the renderer in a test. It catches the drift that actually happens: a
/// template added, removed, or newly overridden.</para>
/// </summary>
public sealed class EmailInventoryCurrencyTests
{
    /// <summary>The shared shell every body is wrapped in — not a template in its own right.</summary>
    private const string LayoutShell = "_layout";

    // | 10 | `masterclass-confirmed` | **private overlay** |
    private static readonly Regex Row =
        new(@"^\|\s*\d+\s*\|\s*`(?<key>[^`]+)`\s*\|\s*(?<layer>[^|]+?)\s*\|", RegexOptions.Multiline);

    [InternalDocsFact]
    public void The_inventory_lists_every_shipped_template_and_nothing_that_is_gone()
    {
        var repo = FindRepoRoot();
        var shipped = TemplateKeys(Path.Combine(repo, "templates", "emails"));
        var listed = ListedRows(repo).Keys.ToHashSet(StringComparer.Ordinal);

        var missing = shipped.Where(k => !listed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var stale = listed.Where(k => !shipped.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            "docs/EMAIL-INVENTORY.md is missing template(s) — regenerate it: " + string.Join(", ", missing));
        Assert.True(
            stale.Count == 0,
            "docs/EMAIL-INVENTORY.md lists template(s) that no longer exist — regenerate it: "
            + string.Join(", ", stale));
    }

    [InternalDocsFact]
    public void Each_row_names_the_layer_that_actually_wins()
    {
        // Precedence at send time is: DB override > config/email-templates > templates/emails.
        // A test cannot see a DB override (which is why the doc says so in prose), but it CAN
        // verify the file layers — and getting THOSE wrong is what would quietly show the operator
        // the shipped default when his event is really sending the private overlay.
        var repo = FindRepoRoot();
        var overlayDir = Path.Combine(repo, "config", "email-templates");
        var overlay = TemplateKeys(overlayDir);

        var wrong = new List<string>();
        foreach (var (key, layer) in ListedRows(repo))
        {
            var saysOverlay = layer.Contains("overlay", StringComparison.OrdinalIgnoreCase);
            var isOverlay = overlay.Contains(key);

            if (saysOverlay != isOverlay)
            {
                wrong.Add($"{key}: doc says \"{layer}\", files say "
                          + (isOverlay ? "private overlay" : "shipped"));
            }
        }

        Assert.True(
            wrong.Count == 0,
            "docs/EMAIL-INVENTORY.md names the wrong layer — regenerate it:\n  "
            + string.Join("\n  ", wrong));
    }

    [InternalDocsFact]
    public void The_inventory_still_warns_that_a_DB_override_beats_both_files()
    {
        // The single most important caveat in the document: an edition with a saved override on
        // /Organizer/EmailTemplates sends something this file cannot show. If that warning is ever
        // edited away, the doc starts reading as the whole truth when it is not.
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "EMAIL-INVENTORY.md"));

        Assert.Contains("DB override", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Organizer/EmailTemplates", text, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> ListedRows(string repo)
    {
        var text = File.ReadAllText(Path.Combine(repo, "docs", "EMAIL-INVENTORY.md"));
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Row.Matches(text))
        {
            rows[m.Groups["key"].Value] = m.Groups["layer"].Value;
        }
        Assert.True(rows.Count > 0, "No template rows parsed from docs/EMAIL-INVENTORY.md — has its table format changed?");
        return rows;
    }

    private static HashSet<string> TemplateKeys(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.html")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && n != LayoutShell)
                .ToHashSet(StringComparer.Ordinal)!
            : new HashSet<string>(StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        // Anchor on BOTH markers: templates/emails alone is not enough, because the templates are
        // copied into the test output, so bin/Debug/net10.0 matches it and the walk stops there.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "templates", "emails"))
                && File.Exists(Path.Combine(dir.FullName, "docs", "EMAIL-INVENTORY.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo root (templates/emails + docs/EMAIL-INVENTORY.md) from "
            + AppContext.BaseDirectory);
    }
}
