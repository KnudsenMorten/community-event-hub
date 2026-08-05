using CommunityHub.Core.Config;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §783.6 — a POST-event task deadline anchors on the edition's LAST day, not its first.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03: <i>"Task 'Download leads and inquiries' has wrong deadline (10th feb
/// 2027). It must be 11 feb 2027 as it must be the day after the event"</i>.</para>
///
/// <para>🔒 <b>Why the old rule looked right.</b> It was <c>eventMinus</c> with <c>days: -1</c>, i.e.
/// <i>start + 1</i>, and its own <c>_doc</c> proudly explained the double negative. For ELDK27 that
/// lands on 2027-02-10 — which reads as "after the event" only because the edition happens to be two
/// days long. It is actually the MAIN CONFERENCE DAY: the task tells a sponsor to download their
/// leads while the event is still running and the leads are still being collected.</para>
///
/// <para>⚠️ The failure is silent and scales with the edition: a three-day event would put the task
/// squarely mid-event, and nothing would report it. Anchoring on <c>EndDate</c> makes the rule mean
/// what it says in every edition.</para>
/// </remarks>
public sealed class PostEventDeadlineTests
{
    // ELDK27: pre-day (master classes) 9 Feb, main conference day 10 Feb.
    private static readonly DateOnly Start = new(2027, 2, 9);
    private static readonly DateOnly End = new(2027, 2, 10);

    private static SponsorTaskExpander ExpanderWith(string basis, int days)
    {
        var config = new SponsorConfig();
        config.DeadlineRulesRaw["downloadLeads"] = System.Text.Json.JsonDocument
            .Parse($"{{\"basis\":\"{basis}\",\"days\":{days}}}").RootElement.Clone();
        return new SponsorTaskExpander(config);
    }

    [Fact]
    public void The_download_leads_deadline_is_the_day_AFTER_the_last_day()
    {
        var due = ExpanderWith("eventEndPlus", 1)
            .ResolveDeadlineRule("downloadLeads", Start, End, firstOrderDate: null, today: Start);

        Assert.Equal(new DateOnly(2027, 2, 11), due);
    }

    [Fact]
    public void The_OLD_rule_is_shown_to_land_ON_the_main_conference_day()
    {
        // Kept as documentation of the defect: this is what shipped, and it is NOT post-event.
        var due = ExpanderWith("eventMinus", -1)
            .ResolveDeadlineRule("downloadLeads", Start, End, firstOrderDate: null, today: Start);

        Assert.Equal(End, due);                       // 2027-02-10 — the event is still running
        Assert.NotEqual(new DateOnly(2027, 2, 11), due);
    }

    [Fact]
    public void The_end_anchored_rule_survives_an_edition_that_is_not_two_days_long()
    {
        // The real reason to fix the ANCHOR rather than just the number: a start-anchored "+1" is
        // only ever correct by coincidence.
        var threeDayEnd = new DateOnly(2027, 2, 11);

        var endAnchored = ExpanderWith("eventEndPlus", 1)
            .ResolveDeadlineRule("downloadLeads", Start, threeDayEnd, null, Start);
        Assert.Equal(new DateOnly(2027, 2, 12), endAnchored);   // still the day after

        var startAnchored = ExpanderWith("eventMinus", -1)
            .ResolveDeadlineRule("downloadLeads", Start, threeDayEnd, null, Start);
        Assert.Equal(new DateOnly(2027, 2, 10), startAnchored);  // now MID-event, silently
    }

    [Fact]
    public void The_shipped_ELDK27_config_actually_uses_the_end_anchored_rule()
    {
        // 🔒 The rule above is only true if the CONFIG says so — the arithmetic living in code
        // proves nothing about the value that ships. This is the half that would rot.
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !Directory.Exists(Path.Combine(repoRoot.FullName, "config")))
            repoRoot = repoRoot.Parent;
        Assert.NotNull(repoRoot);

        var json = File.ReadAllText(
            Path.Combine(repoRoot!.FullName, "config", "sponsor.eldk27.json"));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var rule = doc.RootElement.GetProperty("deadlineRules").GetProperty("downloadLeads");

        Assert.Equal("eventEndPlus", rule.GetProperty("basis").GetString());
        Assert.Equal(1, rule.GetProperty("days").GetInt32());
    }
}
