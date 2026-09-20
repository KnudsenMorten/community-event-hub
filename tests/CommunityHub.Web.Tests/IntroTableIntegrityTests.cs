using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1228 — an HTML comment between two table rows breaks the table on the public /About page.
/// </summary>
/// <remarks>
/// <para>Found on PROD 2026-09-15 right after the intro refresh: the quick-links row "Run this for my own
/// community" rendered as literal <c>| … |</c> text. It sat after an <c>internal-only</c> block inside
/// the table; the public view strips that block, the gap ends the table, and every later row becomes a
/// paragraph. The same happens with a <c>covers:</c> marker between rows (fixed in the same refresh).</para>
///
/// <para>🔑 Checked on the SOURCE, the way the renderer sees it: a comment line whose previous content
/// line is a table row must not be followed — after the comment or the internal-only block closes — by
/// another table row.</para>
/// </remarks>
public sealed class IntroTableIntegrityTests
{
    private const string IntroPath = "config/content/eldk27/ceh-introduction.md";

    [Fact]
    public void No_comment_or_internal_only_block_sits_between_two_table_rows()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), IntroPath.Replace('/', Path.DirectorySeparatorChar)));
        var problems = new List<string>();

        for (var i = 1; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("<!--", StringComparison.Ordinal)) continue;
            if (!IsTableRow(lines[i - 1])) continue;

            // Skip the whole comment run (and an internal-only block up to its end marker).
            var j = i;
            var inInternal = lines[i].Contains("internal-only:start", StringComparison.Ordinal);
            while (j < lines.Length)
            {
                var t = lines[j].TrimStart();
                if (inInternal)
                {
                    if (t.Contains("internal-only:end", StringComparison.Ordinal)) { j++; break; }
                    j++;
                    continue;
                }
                if (t.StartsWith("<!--", StringComparison.Ordinal))
                {
                    while (j < lines.Length && !lines[j].Contains("-->", StringComparison.Ordinal)) j++;
                    j++;
                    if (j < lines.Length && lines[j].Contains("internal-only:start", StringComparison.Ordinal))
                        inInternal = true;
                    continue;
                }
                break;
            }

            if (j < lines.Length && IsTableRow(lines[j]))
                problems.Add($"line {i + 1}: comment between table rows (row continues at line {j + 1})");
        }

        Assert.True(problems.Count == 0,
            "A comment or internal-only block splits a table, so the rows after it render as raw text on "
            + "/About:\n" + string.Join("\n", problems));
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith("|", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "FEATURES.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
