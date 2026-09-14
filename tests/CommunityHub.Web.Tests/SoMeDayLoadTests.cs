using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1182 — SEEING WHAT ELSE IS ON A DAY BEFORE RESCHEDULING ONTO IT.
///
/// <para>Operator 2026-09-12: <i>"when i reschedule something, it would be great to see in the date
/// picker if other things have been planned for a date, so i dont overlap"</i>.</para>
///
/// <para>⚠️ <b>The native picker cannot be annotated.</b> There is no API to paint a count onto a day
/// cell of <c>&lt;input type="datetime-local"&gt;</c> — it is browser chrome. The occupancy is
/// therefore rendered BESIDE the field and updated as he picks, which is the same answer in the only
/// place the platform allows it to be drawn.</para>
/// </summary>
public sealed class SoMeDayLoadTests
{
    private static string Editor() =>
        File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub", "Pages"), "Organizer", "SoMePostEditor.cshtml"));

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(Path.Combine(parts));
    }

    [Fact]
    public void The_day_panel_sits_with_the_date_field()
    {
        var page = Editor();

        var field = page.IndexOf("id=\"when\"", StringComparison.Ordinal);
        var panel = page.IndexOf("id=\"dayinfo\"", StringComparison.Ordinal);

        Assert.True(field > 0, "The date field must still be there.");
        Assert.True(panel > field,
            "The occupancy panel must render AFTER the date field — the answer belongs where the "
            + "choice is made (§863.4 / §887).");
    }

    /// <summary>
    /// 🔒 It must describe the CURRENT date at rest, not only after he touches the control — a panel
    /// that is blank until interacted with shows nothing on the first look.
    /// </summary>
    [Fact]
    public void It_renders_without_waiting_for_an_interaction()
    {
        var page = Editor();

        Assert.Contains("render();", page, StringComparison.Ordinal);
        Assert.Contains("addEventListener('change', render)", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 Post labels are OPERATOR-ENTERED text. They travel as JSON in a non-executable script tag
    /// and are written with textContent — never interpolated into a JS string literal, and never
    /// through innerHTML, where a quote or a tag in a session title would break or inject.
    /// </summary>
    [Fact]
    public void Post_labels_are_never_written_as_markup_or_into_a_script_literal()
    {
        var page = Editor();

        Assert.Contains("<script type=\"application/json\" id=\"dayload\">", page, StringComparison.Ordinal);
        Assert.Contains("li.textContent = p;", page, StringComparison.Ordinal);

        // 🔑 Assert on ASSIGNMENTS, not on the word: an `innerHTML` in a comment is not a write, and
        // counting occurrences made this test fail on its own explanatory note.
        var assignments = System.Text.RegularExpressions.Regex
            .Matches(page, @"innerHTML\s*=\s*(?<rhs>[^;]+);")
            .Select(m => m.Groups["rhs"].Value.Trim())
            .ToList();

        Assert.All(assignments, rhs => Assert.True(
            rhs is "''" or "\"\"",
            $"innerHTML is assigned {rhs} — day-load content must be written with textContent."));
    }

    /// <summary>
    /// ⚠️ A broken or absent payload must leave the page working. The panel is an aid, not a
    /// dependency — an exception here would take the whole editor's scripting down with it.
    /// </summary>
    [Fact]
    public void A_broken_payload_degrades_to_no_panel()
    {
        var page = Editor();

        var start = page.IndexOf("id=\"dayload\"", StringComparison.Ordinal);
        var window = page[start..(start + 1400)];

        Assert.Contains("catch", window, StringComparison.Ordinal);
        Assert.Contains("return;", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔑 The per-day ceiling is the everyday rhythm (§843.3), not a hard stop — so a full day is
    /// FLAGGED and the date is still accepted. Refusing it would override a decision that is his.
    /// </summary>
    [Fact]
    public void A_full_day_is_flagged_and_not_refused()
    {
        var page = Editor();

        Assert.Contains("at your ' + cap + '-a-day limit", page, StringComparison.Ordinal);
        // No disabling, no validity message, no preventDefault around the date field.
        var start = page.IndexOf("id=\"dayinfo\"", StringComparison.Ordinal);
        var window = page[start..(start + 2600)];
        Assert.DoesNotContain("preventDefault", window, StringComparison.Ordinal);
        Assert.DoesNotContain("setCustomValidity", window, StringComparison.Ordinal);
    }

    /// <summary>It is announced to screen readers as it changes, since it changes without a reload.</summary>
    [Fact]
    public void The_panel_is_a_live_region()
    {
        var page = Editor();

        var start = page.IndexOf("id=\"dayinfo\"", StringComparison.Ordinal);
        var window = page[start..(start + 220)];
        Assert.Contains("aria-live=\"polite\"", window, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", window, StringComparison.Ordinal);
    }
}
