using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §370 — the shared confirm dialog is only a safeguard if it is actually WIRED. Two ways it
/// silently is not, both found in shipped code:
///
/// <list type="number">
///   <item><b>A trigger pointing at a modal id that does not exist.</b> The layout JS is
///   deliberately progressive-enhancement — <c>if (!modal) return;</c> lets the native action
///   proceed — so a typo'd or missing id does not throw, it just performs the destructive action
///   on the FIRST click with no dialog. That is how the wizard's "Give up my Master Class seat"
///   button shipped: the operator's whole point was <i>"i am worried people cancel by mistake"</i>.</item>
///   <item><b>A trigger whose handler lives in <c>formaction</c>.</b> Covered by the second test:
///   the replay must pass the submitter, or the post lands on the form's default handler.</item>
/// </list>
///
/// <para>Neither is visible in a unit test of a page model — the wiring is markup + script — so it
/// is asserted over the shipped <c>.cshtml</c> source instead.</para>
/// </summary>
public sealed class ConfirmModalWiringTests
{
    // data-ceh-confirm="someId"
    private static readonly Regex Trigger =
        new(@"data-ceh-confirm=""(?<id>[^""]+)""", RegexOptions.Compiled);

    // The modal declares its id as the FIRST ConfirmModalModel argument, positionally
    // (new ConfirmModalModel("mc-giveup-step", …) or with the Id: label.
    private static readonly Regex Declaration =
        new(@"ConfirmModalModel\(\s*(?:Id:\s*)?""(?<id>[^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void Every_confirm_TRIGGER_has_a_modal_declared_on_the_same_page()
    {
        var pages = FindPagesDir();
        Assert.True(pages is not null, "src/CommunityHub/Pages was not found — has it moved?");

        var problems = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pages!, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // The shared partial and the layout script DESCRIBE the mechanism (docs + the JS
            // selector); they are not pages carrying a trigger.
            var name = Path.GetFileName(file);
            if (name is "_ConfirmModal.cshtml" or "_LayoutClientScripts.cshtml") continue;

            var declared = Declaration.Matches(text)
                .Select(m => m.Groups["id"].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match m in Trigger.Matches(text))
            {
                var id = m.Groups["id"].Value;
                if (!declared.Contains(id))
                {
                    problems.Add($"{Path.GetFileName(file)} → data-ceh-confirm=\"{id}\" has no matching _ConfirmModal");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "A confirm trigger with no modal performs its action on the first click, with no dialog:\n  "
            + string.Join("\n  ", problems));
    }

    [Fact]
    public void The_modal_replay_preserves_a_trigger_FORMACTION()
    {
        // asp-page-handler on a <button> renders as formaction, not name/value. A bare
        // requestSubmit() drops the submitter, so the confirmed post goes to the form's DEFAULT
        // handler — which on Broadcast / TestDataCleanup / PreselectionQueue does not exist, and on
        // the Get Started wizard is "save this step" rather than "give up the seat". The dialog
        // would look like it worked. Pin the one line that prevents it.
        var pages = FindPagesDir();
        Assert.True(pages is not null);

        var js = File.ReadAllText(Path.Combine(pages!, "Shared", "_LayoutClientScripts.cshtml"));

        Assert.Contains("hasAttribute('formaction')", js, StringComparison.Ordinal);
        Assert.Contains("form.requestSubmit(trigger)", js, StringComparison.Ordinal);
    }

    /// <summary>Walk up from the test binaries to the web project's Pages folder.</summary>
    private static string? FindPagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
