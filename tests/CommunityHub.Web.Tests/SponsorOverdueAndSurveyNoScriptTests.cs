using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Static Razor-source guards (same approach as <see cref="BreadcrumbWiringTests"/>
/// / MicroPolishWiringTests — no app host) for two P2 accessibility/clarity fixes:
///
///   FIX 1 — the Sponsor Tasks row (_SponsorTaskRow.cshtml) flags an OVERDUE
///   task with a red "Overdue" badge, mirroring the shared _ChecklistCard, so an
///   overdue deliverable no longer looks identical to a not-yet-due one. A
///   future-dated task still gets only the neutral "due" badge.
///
///   FIX 2 — the anonymous Survey wizard (Survey/Index.cshtml) renders a
///   &lt;noscript&gt; fallback (notice + progressive-enhancement CSS) so a
///   visitor with JavaScript disabled is told what to do AND the wizard degrades
///   to a single scrollable form (no controls left disabled-by-markup).
/// </summary>
public sealed class SponsorOverdueAndSurveyNoScriptTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    private static string Page(params string[] parts) =>
        File.ReadAllText(Path.Combine(PagesDir(), Path.Combine(parts)));

    // ---- FIX 1: sponsor overdue badge -------------------------------------

    [Fact]
    public void SponsorTaskRow_computes_overdue_with_the_same_rule_as_the_checklist_card()
    {
        var row = Page("Sponsor", "_SponsorTaskRow.cshtml");

        // Past-due + not-done, in UTC DateOnly terms — the same inputs the
        // shared _ChecklistCard uses (today = DateOnly.FromDateTime(DateTime.UtcNow)).
        Assert.Contains("DateOnly.FromDateTime(DateTime.UtcNow)", row);
        Assert.Contains("Model.State != TaskState.Done", row);
        Assert.Contains("daysOverdue", row);
    }

    [Fact]
    public void SponsorTaskRow_renders_an_overdue_badge_when_past_due_and_not_done()
    {
        var row = Page("Sponsor", "_SponsorTaskRow.cshtml");

        // The overdue branch is gated on the computed count and emits the
        // shared "Overdue" label in the red treatment (#c0392b — the same
        // overdue colour the _ChecklistCard uses).
        Assert.Contains("else if (daysOverdue > 0)", row);
        Assert.Contains("Hub.Overdue", row);
        Assert.Contains("#c0392b", row);
    }

    [Fact]
    public void SponsorTaskRow_keeps_a_neutral_due_badge_for_a_not_yet_due_task()
    {
        var row = Page("Sponsor", "_SponsorTaskRow.cshtml");

        // A future/within-due task falls through to the plain neutral "due"
        // badge (amber #fff3cd) — NOT the overdue red. The overdue branch must
        // come FIRST so it wins when past due, and the neutral branch remain.
        Assert.Contains("TaskRow.Due", row);
        Assert.Contains("#fff3cd", row);

        var overdueIdx = row.IndexOf("else if (daysOverdue > 0)", StringComparison.Ordinal);
        var dueIdx = row.IndexOf("else if (Model.DueDate is not null)", StringComparison.Ordinal);
        Assert.True(overdueIdx >= 0 && dueIdx > overdueIdx,
            "The overdue branch must precede the neutral due branch so overdue wins.");
    }

    // ---- FIX 2: survey <noscript> fallback --------------------------------

    [Fact]
    public void SurveyPage_renders_a_noscript_block()
    {
        var page = Page("Survey", "Index.cshtml");

        Assert.Contains("<noscript>", page);
        Assert.Contains("</noscript>", page);
        // The notice copy is localized (English-default resx key).
        Assert.Contains("Survey.NoScriptNotice", page);
    }

    [Fact]
    public void SurveyPage_noscript_reveals_steps_and_shows_submit()
    {
        var page = Page("Survey", "Index.cshtml");

        var start = page.IndexOf("<noscript>", StringComparison.Ordinal);
        var end = page.IndexOf("</noscript>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Expected a <noscript> block.");
        var block = page.Substring(start, end - start);

        // Degrade to a single scrollable form: every step visible, JS-only step
        // pills hidden, and the submit button shown.
        Assert.Contains(".step-section { display: block !important; }", block);
        Assert.Contains(".stepbar { display: none !important; }", block);
        Assert.Contains("#submitBtn { display: inline-block !important; }", block);
    }

    [Fact]
    public void SurveyPage_does_not_bake_disabled_into_the_wizard_controls()
    {
        var page = Page("Survey", "Index.cshtml");

        // The controls must NOT ship with a hard `disabled` attribute (which CSS
        // can't undo for a no-JS visitor). JS gates them at startup instead.
        Assert.DoesNotContain("id=\"nextBtn\" disabled", page);
        Assert.DoesNotContain("id=\"submitBtn\" style=\"display:none;\" disabled", page);
        Assert.Contains("gateInitialControls", page);
    }
}
