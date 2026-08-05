using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §723 — the PER-ROLE RINGS block on /Organizer/Settings must be OPEN by default.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Written because a collapsed control hid a live misconfiguration.</b> Operator
/// 2026-07-31: <i>"bug: these must be expand by default - i missed the misconfig earlier due to
/// this"</i>. In §721 the per-mail rings for <c>welcome-speaker</c> / <c>welcome-sponsor</c> were set
/// to Ring 3 while the rings that actually governed delivery sat folded away behind this summary —
/// so the page looked correct and no welcome reached anyone at Ring 3.</para>
///
/// <para>§718 makes this page the authority on audience. A per-role ring is not an "advanced"
/// detail, it IS the audience, and an audience control you must click to see is one you can be
/// wrong about without knowing. A static source check, because the thing under test is one HTML
/// attribute that no unit test would otherwise ever look at.</para>
/// </remarks>
public class SettingsPerRoleRingsVisibleTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string SettingsPage() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "CommunityHub", "Pages", "Organizer", "Settings.cshtml"));

    [Fact]
    public void The_per_role_rings_block_is_open_by_default()
    {
        var html = SettingsPage();

        // The <details> that immediately precedes the "Per-role rings" summary.
        var m = Regex.Match(html, @"<details\b([^>]*)>\s*<summary>\s*Per-role rings", RegexOptions.Singleline);
        Assert.True(m.Success, "Could not find the 'Per-role rings' <details> block in Settings.cshtml.");

        Assert.True(
            Regex.IsMatch(m.Groups[1].Value, @"\bopen\b"),
            "The 'Per-role rings' block must carry `open`. It is the AUDIENCE control (§718), and "
            + "collapsing it is what hid the §721 misconfiguration — the page read as correct while "
            + "no Ring-3 speaker or sponsor was reachable.");
    }

    [Fact]
    public void The_rare_structural_advanced_block_stays_collapsed()
    {
        // The other <details> on this page is §326bz's "move this feature to another chapter" — a
        // rare structural act that hides NO audience configuration. Deliberately still folded, so
        // "expand everything" is not the rule; "never hide what decides who gets mail" is.
        var html = SettingsPage();
        var m = Regex.Match(html, @"<details\b([^>]*)>\s*<summary>@Localizer\[""Settings\.Advanced""\]");
        Assert.True(m.Success, "Could not find the Settings.Advanced <details> block.");
        Assert.False(
            Regex.IsMatch(m.Groups[1].Value, @"\bopen\b"),
            "The structural 'Advanced' block should stay collapsed — it governs no audience.");
    }
}
