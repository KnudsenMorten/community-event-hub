using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §420 — a <c>&lt;button type="submit"&gt;</c> must live inside a form.
///
/// <para><b>Why this needs a test.</b> The Master Class notification toggle emitted a bare submit
/// button on the assumption it was "rendered inside the page's ONE form". On
/// <c>/MasterClassPage</c> it was not — both toggles sit ABOVE the page's only form — so clicking
/// them did <b>nothing at all</b>: no request, no error, no console message, no clue. The operator
/// found it by pressing a control that looked completely normal (2026-07-27: <i>"nothing happens
/// when i click mail notifications on/off"</i>).</para>
///
/// <para>That is the failure mode worth guarding: a submit button outside a form is silently inert.
/// Nothing in the build, the tests, or the browser complains — the only detector is a person
/// clicking it and noticing the absence of a result.</para>
///
/// <para>Scoped to PARTIALS deliberately. A partial cannot see its host page, so "the caller will
/// have wrapped me in a form" is an assumption it can never verify — which is precisely how this
/// broke. A full page's own markup is checkable by reading the file; a partial's is not.</para>
/// </summary>
public sealed class SubmitButtonHasAFormTests
{
    private static readonly Regex SubmitButton =
        new(@"<button[^>]*type=""submit""", RegexOptions.IgnoreCase);

    private static readonly Regex FormOpen = new(@"<form\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Partials that legitimately emit a submit button for a form the HOST owns, and that are only
    /// ever rendered inside one. Each entry is a deliberate exemption, not an oversight — the
    /// wizard step-fields partials are the clearest case: the wizard host wraps every step in a
    /// single form by design (§148), and giving each field-set its own form would nest them (§304b).
    /// </summary>
    private static readonly HashSet<string> HostOwnsTheForm = new(StringComparer.OrdinalIgnoreCase)
    {
        "_DeadlinesFields.cshtml",
        "_MasterClassFields.cshtml",
        "_MasterClassWaitlistFields.cshtml",
        "_PartyFields.cshtml",
        "_HotelFields.cshtml",
        "_DinnerFields.cshtml",
        "_SignalFields.cshtml",
        "_SponsorSessionFields.cshtml",
    };

    [Fact]
    public void A_partial_with_a_submit_button_either_owns_a_form_or_is_a_declared_wizard_field_set()
    {
        var offences = new List<string>();

        foreach (var file in PartialFiles())
        {
            var name = Path.GetFileName(file);
            if (HostOwnsTheForm.Contains(name)) continue;

            var text = File.ReadAllText(file);
            if (!SubmitButton.IsMatch(text)) continue;
            if (FormOpen.IsMatch(text)) continue;

            offences.Add($"{name}: has a submit button but no <form>. Outside a form it does "
                         + "NOTHING when clicked — silently. Give it its own <form>, or add it to "
                         + "HostOwnsTheForm if the host genuinely wraps it.");
        }

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
    }

    [Fact]
    public void The_notification_toggle_owns_its_form()
    {
        // The specific control that broke, pinned by name so a future edit cannot quietly return it
        // to relying on its host.
        var file = PartialFiles().Single(f =>
            Path.GetFileName(f).Equals("_McSubscribeToggle.cshtml", StringComparison.OrdinalIgnoreCase));
        var text = File.ReadAllText(file);

        Assert.Matches(@"<form\b[^>]*method=""post""", text);
        Assert.Contains("</form>", text);
    }

    [Fact]
    public void The_sweep_actually_found_partials()
    {
        // Without this, a wrong path turns both tests above into always-green no-ops.
        Assert.True(PartialFiles().Count >= 10);
    }

    private static List<string> PartialFiles()
    {
        var pages = Path.Combine(FindRepoRoot(), "src", "CommunityHub", "Pages");
        return Directory.EnumerateFiles(pages, "_*.cshtml", SearchOption.AllDirectories).ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
