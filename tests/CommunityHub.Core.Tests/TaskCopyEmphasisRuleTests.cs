using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §669 — THE EMPHASIS RULE, enforced instead of remembered.
///
/// <para>
/// §654 applied emphasis to the tasks that were being looked at, was marked delivered, and the
/// operator found the next task unchanged: <i>"i see that you have NOT gone through all tasks for
/// all roes… important things like price and steps are not in bold"</i>. A sweep that is only ever
/// a sweep decays the moment the next task is authored, so the rule lives here as a test.
/// </para>
///
/// <para><b>THE RULE.</b> In a task body:</para>
/// <list type="bullet">
///   <item><b>Bold</b> = money, quantities, deadlines/dates and times, step/section headings, and
///         the exact product name to search for in the webshop.</item>
///   <item>Plain = everything else.</item>
///   <item><b>Underline is not emphasis.</b> It marks structure, so a heading is
///         <c>__**Heading**__</c> — underlined AND bold. A heading that is only underlined while
///         the details beneath it are bold inverts the hierarchy, which was the exact complaint:
///         the scannable structure was the quietest thing on the page.</item>
///   <item><c>==highlight==</c> stays scarce — at most one per body, or it stops meaning
///         "read this one thing" (§675).</item>
/// </list>
/// </summary>
public class TaskCopyEmphasisRuleTests(ITestOutputHelper output)
{
    /// <summary>Every authored task body in the edition config, with a label for failure messages.</summary>
    private static IEnumerable<(string Source, string Title, string Body)> AllConfigTaskBodies()
    {
        var repo = FindRepoRoot();

        var sponsorPath = Path.Combine(repo, "config", "sponsor.eldk27.json");
        using (var doc = JsonDocument.Parse(File.ReadAllText(sponsorPath)))
        {
            if (doc.RootElement.TryGetProperty("taskSets", out var sets))
            {
                foreach (var set in sets.EnumerateObject())
                {
                    if (set.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var task in set.Value.EnumerateArray())
                    {
                        var title = task.TryGetProperty("title", out var t) ? t.GetString() : null;
                        var body = task.TryGetProperty("description", out var d) ? d.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            yield return ($"sponsor/{set.Name}", title ?? "(untitled)", body!);
                        }
                    }
                }
            }
        }

        var speakerPath = Path.Combine(repo, "config", "speaker-deadlines.eldk27.json");
        using (var doc = JsonDocument.Parse(File.ReadAllText(speakerPath)))
        {
            if (doc.RootElement.TryGetProperty("deadlines", out var deadlines))
            {
                foreach (var dl in deadlines.EnumerateArray())
                {
                    var title = dl.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var body = dl.TryGetProperty("description", out var d) ? d.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        yield return ("speaker-deadlines", title ?? "(untitled)", body!);
                    }
                }
            }
        }

        // §684.8/§686.2 — THE MIGRATED BODIES. As each task moves out of the JSON and into
        // config/tasks/<edition>/**.md, this enumerator has to follow it or §669's guarantee quietly
        // shrinks with every migration: the catalogue count would keep passing while covering fewer
        // and fewer tasks. §669's own complaint was that a sweep decays the moment the next task is
        // authored — a sweep that decays as tasks MOVE is the same failure wearing a different hat.
        var migratedRoot = Path.Combine(repo, "config", "tasks");
        if (Directory.Exists(migratedRoot))
        {
            foreach (var file in Directory
                         .EnumerateFiles(migratedRoot, "*.md", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var body = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(body)) continue;

                var role = Path.GetFileName(Path.GetDirectoryName(file)) ?? "tasks";
                yield return ($"migrated/{role}", Path.GetFileNameWithoutExtension(file), body);
            }
        }
    }

    /// <summary>
    /// The count §669 explicitly asked for — <i>"do not close this one without enumerating the full
    /// task catalogue and showing the count covered"</i>. Printed so the number is in the test log,
    /// not just asserted.
    /// </summary>
    [Fact]
    public void The_whole_authored_task_catalogue_is_covered()
    {
        var all = AllConfigTaskBodies().ToList();

        foreach (var (source, title, _) in all)
        {
            output.WriteLine($"{source,-22} {title}");
        }

        output.WriteLine($"TOTAL authored task bodies checked: {all.Count}");
        Assert.True(all.Count >= 19, $"expected the full catalogue, found only {all.Count}");
    }

    /// <summary>
    /// 🔒 The inversion he reported: <c>__STEP 1 — ORDER:__</c> underlined only, while the product
    /// names below it were bold.
    /// </summary>
    [Fact]
    public void Every_underlined_heading_is_also_bold()
    {
        // A heading is __...__ on its own; it must contain ** ... ** inside.
        var heading = new Regex(@"__(?<inner>.+?)__", RegexOptions.Singleline);

        var offences = new List<string>();

        foreach (var (source, title, body) in AllConfigTaskBodies())
        {
            foreach (Match m in heading.Matches(body))
            {
                var inner = m.Groups["inner"].Value;
                if (!inner.Contains("**", StringComparison.Ordinal))
                {
                    offences.Add($"[{source}] \"{title}\" → __{inner}__");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "§669 — these headings are underlined but NOT bold, which puts the structure below the "
            + "details in the visual hierarchy. Write them as __**Heading**__:\n  "
            + string.Join("\n  ", offences));
    }

    /// <summary>
    /// Money is the thing a sponsor most needs to see and the thing he named first
    /// (<i>"The cost is EURO 500 (excl. tax) — the PRICE, plain"</i>).
    /// </summary>
    [Fact]
    public void Every_price_is_bold()
    {
        // Matches "EURO 500" / "EURO {{attendeeBagPriceEur}}" and asks whether ** opens before it
        // on the same line and closes after it.
        var price = new Regex(@"EURO\s+(\{\{[^}]+\}\}|[\d.,]+)", RegexOptions.IgnoreCase);

        var offences = new List<string>();

        foreach (var (source, title, body) in AllConfigTaskBodies())
        {
            foreach (var line in body.Split('\n'))
            {
                foreach (Match m in price.Matches(line))
                {
                    if (!IsInsideBold(line, m.Index))
                    {
                        offences.Add($"[{source}] \"{title}\" → {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "§669 — a price is rendering plain. Wrap the amount (and its tax note) in **:\n  "
            + string.Join("\n  ", offences));
    }

    /// <summary>
    /// 🔒 §675's highlight only works while it is scarce. Two highlights in one body means neither
    /// is "the important one" — the same argument as bold being spent on everything.
    /// </summary>
    [Fact]
    public void No_body_uses_more_than_one_highlight()
    {
        var offences = new List<string>();

        foreach (var (source, title, body) in AllConfigTaskBodies())
        {
            var count = Regex.Matches(body, "==[^=]+==").Count;
            if (count > 1)
            {
                offences.Add($"[{source}] \"{title}\" uses {count} highlights");
            }
        }

        Assert.True(
            offences.Count == 0,
            "§669/§675 — highlight must stay scarce:\n  " + string.Join("\n  ", offences));
    }

    /// <summary>
    /// Counts the <c>**</c> markers before <paramref name="index"/> on this line: an odd number
    /// means the position sits inside an open bold span.
    /// </summary>
    private static bool IsInsideBold(string line, int index)
    {
        var before = line[..index];
        var opens = 0;
        for (var i = 0; i + 1 < before.Length; i++)
        {
            if (before[i] == '*' && before[i + 1] == '*')
            {
                opens++;
                i++;
            }
        }

        return opens % 2 == 1;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "config")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
