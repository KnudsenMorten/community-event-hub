using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §364 (operator 2026-07-26, screenshot) — the overdue badge read
/// <i>"⚠ Overdue (47 day(s)) · was due 09/06/2026"</i>. Two defects in one string: the awkward
/// <c>day(s)</c> pluralisation, and a NUMERIC date — <c>09/06/2026</c> is 9 June to a Dane and
/// 6 September to an American, on a badge whose entire job is to tell someone how late they are.
/// It now reads <i>"⚠ Overdue — was due 9 Jun 2026 (47 days ago)"</i>.
///
/// <para>Pinned on all THREE task-row partials, because the badge is duplicated per role and a fix
/// applied to the one in the screenshot would leave the other two reading the old way — which is
/// how the operator meets the same bug twice.</para>
/// </summary>
public sealed class OverdueBadgeWordingTests
{
    private static readonly string[] TaskRowPartials =
    {
        Path.Combine("Tasks", "_ParticipantTaskRow.cshtml"),
        Path.Combine("Speaker", "_SpeakerTaskRow.cshtml"),
        Path.Combine("Sponsor", "_SponsorTaskRow.cshtml"),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_overdue_badge_spells_the_date_out_unambiguously(int partial)
    {
        var row = ReadPartial(TaskRowPartials[partial]);

        // "9 Jun 2026" — a month NAME, so it cannot be read back-to-front.
        Assert.Contains("\"d MMM yyyy\"", row, StringComparison.Ordinal);
        Assert.Contains("TaskRow.WasDue", row, StringComparison.Ordinal);

        // …and never a numeric day/month format, which is the ambiguity itself.
        Assert.DoesNotContain("\"dd/MM/yyyy\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MM/dd/yyyy\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString(\"d\")", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_overdue_badge_picks_a_real_singular_or_plural(int partial)
    {
        var row = ReadPartial(TaskRowPartials[partial]);

        // One day overdue says "1 day ago", not "1 day(s) ago".
        Assert.Contains("daysOverdue == 1 ? \"TaskRow.OverdueDay\" : \"TaskRow.OverdueDays\"",
            row, StringComparison.Ordinal);
        Assert.DoesNotContain("day(s)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_overdue_resource_string_carries_the_bracketed_plural()
    {
        // The badge is only as good as the string behind it — a "day(s)" here would put the
        // parenthesis straight back on the page regardless of the branch above.
        var resx = ReadSharedResx();

        foreach (var key in new[] { "TaskRow.OverdueDay", "TaskRow.OverdueDays" })
        {
            var m = Regex.Match(
                resx,
                @"<data name=""" + Regex.Escape(key) + @"""[^>]*>\s*<value>(?<v>[^<]*)</value>");
            Assert.True(m.Success, $"{key} is missing from SharedResource.resx");
            Assert.DoesNotContain("(s)", m.Groups["v"].Value, StringComparison.Ordinal);
        }
    }

    private static string ReadPartial(string relative) =>
        File.ReadAllText(Path.Combine(FindDir("src", "CommunityHub", "Pages"), relative));

    private static string ReadSharedResx() =>
        File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub.Core", "Resources"), "SharedResource.resx"));

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate {Path.Combine(parts)} from {AppContext.BaseDirectory}");
    }
}
