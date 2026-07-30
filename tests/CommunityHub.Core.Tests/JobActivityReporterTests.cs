using CommunityHub.Core.Diagnostics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §545(b) INACTIVE — the per-run "I ran and deliberately did nothing, and here is why" scratchpad.
/// </summary>
/// <remarks>
/// <para><b>The incident, verbatim (§545/§544).</b> The session push was switched off by a setting
/// and said so <i>every run for weeks</i> — <i>"session sync direction is stage 1 … CEH→Zoho push
/// inactive"</i> — but only at Information level, in App Insights, which the operator cannot reach.
/// From his side the Jobs page showed a healthy job with a recent "last run", so the system looked
/// fine while doing nothing, until 1,500 attendees had no agenda.</para>
///
/// <para>These tests pin the three rules that decide whether the alert is trustworthy: first reason
/// wins, work beats inactive, and silence is never mistaken for either.</para>
/// </remarks>
public class JobActivityReporterTests
{
    [Fact]
    public void A_reporter_that_was_never_told_anything_says_NOTHING()
    {
        // 🔒 The rule that makes an incremental rollout safe: an un-instrumented job is UNKNOWN, and
        // the middleware must skip it entirely rather than record it as healthy OR as inactive.
        var r = new JobActivityReporter();

        Assert.False(r.Reported);
        Assert.Null(r.InactiveReason);
    }

    [Fact]
    public void The_FIRST_reason_wins_because_it_is_the_OUTERMOST_gate()
    {
        // A job checks "is Zoho on?" before "is there anything to do?". If the later, blander reason
        // overwrote the first, the alert would say "nothing to do" when the truth is "switched off"
        // — and he would go looking for missing data instead of a setting.
        var r = new JobActivityReporter();

        r.ReportInactive("Zoho is switched off, so nothing is pushed.");
        r.ReportInactive("Nothing to do.");

        Assert.Equal("Zoho is switched off, so nothing is pushed.", r.InactiveReason);
    }

    [Fact]
    public void Real_WORK_beats_an_earlier_inactive_report()
    {
        // SessionBackstagePushJob has two halves. If speakers are gated but sessions actually push,
        // the run DID something and must not count towards an inactive streak.
        var r = new JobActivityReporter();

        r.ReportInactive("Speaker push: the sync direction excludes it.");
        r.ReportWork();

        Assert.True(r.Reported);
        Assert.Null(r.InactiveReason);
    }

    [Fact]
    public void Reporting_work_FIRST_and_then_a_gate_still_counts_as_inactive_for_that_gate()
    {
        // The reverse order is a genuine partial: something worked, then a later stage was turned
        // away. The later reason stands, because the reporter's job is to surface the gate.
        var r = new JobActivityReporter();

        r.ReportWork();
        r.ReportInactive("Session push: the sync direction excludes it.");

        Assert.Equal("Session push: the sync direction excludes it.", r.InactiveReason);
    }

    [Fact]
    public void Reporting_work_alone_leaves_no_reason_to_alert_on()
    {
        var r = new JobActivityReporter();
        r.ReportWork();

        Assert.True(r.Reported);
        Assert.Null(r.InactiveReason);
    }

    // ---------- §545 NO-OP STREAK: examined ZERO records ----------

    /// <summary>
    /// §585 in miniature: <c>halls</c> was missing from the paging reject list, so every read came
    /// back EMPTY and no session could get a room — no error, no failure count, every gate healthy.
    /// A sync that gets through and finds nothing is a THIRD failure shape, distinct from failing
    /// and from being turned away.
    /// </summary>
    [Fact]
    public void Examining_ZERO_records_counts_as_a_no_op()
    {
        var r = new JobActivityReporter();
        r.ReportExamined(0, "attendees in Zoho");

        Assert.NotNull(r.InactiveReason);
        Assert.Contains("attendees in Zoho", r.InactiveReason);
    }

    [Fact]
    public void The_zero_record_reason_rules_OUT_a_switched_off_feature()
    {
        // The reason has to send him somewhere useful. "Nothing is switched off; the data itself is
        // not arriving" points at the integration, not at the Settings page.
        var r = new JobActivityReporter();
        r.ReportExamined(0, "orders in Zoho");

        Assert.Contains("nothing is switched off", r.InactiveReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Examining_even_ONE_record_is_real_work()
    {
        var r = new JobActivityReporter();
        r.ReportExamined(1, "attendees in Zoho");

        Assert.True(r.Reported);
        Assert.Null(r.InactiveReason);
    }

    /// <summary>
    /// 🔒 An OUTER gate still wins. If Zoho is switched off the job never got to look, and "found no
    /// attendees" would send him hunting for missing data instead of at the switch he flipped.
    /// </summary>
    [Fact]
    public void A_gate_reported_EARLIER_still_wins_over_a_zero_count()
    {
        var r = new JobActivityReporter();
        r.ReportInactive("Zoho is switched off, so nothing is pulled.");
        r.ReportExamined(0, "attendees in Zoho");

        Assert.Equal("Zoho is switched off, so nothing is pulled.", r.InactiveReason);
    }
}
