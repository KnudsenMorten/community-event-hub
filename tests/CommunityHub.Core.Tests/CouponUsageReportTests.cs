using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1094 — the partner's fortnightly status mail, and the cap it reports.
/// </summary>
/// <remarks>
/// Operator 2026-08-19: *"they must get status per mail every 2 weeks of usage, cap (remaining) if
/// set"* · *"ad hoc can in some cases also have a cap, so both scenarios we must be able to report
/// on"* · *"basically i want self service so customer can see status, usage, extend and order more"*.
/// </remarks>
public sealed class CouponUsageReportTests
{
    private const string Url = "https://hub.example.test/monitor/tok123";

    private static MonitoredBilling Billing(
        IEnumerable<MonitoredPoolBalance>? pools = null,
        IEnumerable<MonitoredAdHocStatus>? adHoc = null) =>
        new(
            (pools ?? Array.Empty<MonitoredPoolBalance>()).ToList(),
            (adHoc ?? Array.Empty<MonitoredAdHocStatus>()).ToList());

    private static MonitoredAdHocStatus AdHoc(
        int claimed = 10, int? cap = null, int share = 50, decimal billed = 15000m) =>
        new("ARROW-2027", claimed, share, null, billed, null, "bi-weekly", cap);

    // ---------------------------------------------------------------------
    //  The cap, both ways
    // ---------------------------------------------------------------------

    /// <summary>A capped coupon reports how many are left — the number he asked for by name.</summary>
    [Fact]
    public void A_capped_ad_hoc_coupon_reports_the_remaining_tickets()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Arrow ECS", attendeeCount: 10,
            Billing(adHoc: new[] { AdHoc(claimed: 10, cap: 50) }), Url);

        Assert.Contains("10 claimed", html);
        Assert.Contains("50", html);
        Assert.Contains("40", html);      // 50 − 10 left
    }

    /// <summary>
    /// 🔑 An UNCAPPED coupon must not report a "left" figure. Printing "0 left" for an agreement
    /// that never had a ceiling invents a limit the partner never agreed, and the first thing they
    /// would do is stop claiming.
    /// </summary>
    [Fact]
    public void An_uncapped_ad_hoc_coupon_reports_no_limit_rather_than_zero_left()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Arrow ECS", attendeeCount: 10,
            Billing(adHoc: new[] { AdHoc(claimed: 10, cap: null) }), Url);

        Assert.Contains("no agreed limit", html);
        Assert.DoesNotContain("left", html);
    }

    /// <summary>
    /// ⚠️ Over the cap is STATED, not floored. CEH cannot stop a claim (Backstage has no coupon
    /// API), so exceeding an agreed limit is a real state — and one both sides need to see.
    /// </summary>
    [Fact]
    public void Going_over_the_cap_is_said_out_loud()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Arrow ECS", attendeeCount: 55,
            Billing(adHoc: new[] { AdHoc(claimed: 55, cap: 50) }), Url);

        Assert.Contains("above the agreed limit", html);
        Assert.Contains("-5", html);
    }

    [Fact]
    public void A_prepaid_pool_reports_bought_used_and_left()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Globeteam", attendeeCount: 12,
            Billing(pools: new[] { new MonitoredPoolBalance("GLOBE", "2-day", 20, 12, false) }), Url);

        Assert.Contains("20 bought", html);
        Assert.Contains("12 used", html);
        Assert.Contains("8", html);
    }

    [Fact]
    public void An_oversubscribed_pool_says_more_was_used_than_bought()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Globeteam", attendeeCount: 25,
            Billing(pools: new[] { new MonitoredPoolBalance("GLOBE", "2-day", 20, 25, false) }), Url);

        Assert.Contains("more than was bought", html);
    }

    // ---------------------------------------------------------------------
    //  The link and what the mail does NOT contain
    // ---------------------------------------------------------------------

    [Fact]
    public void The_mail_carries_the_self_service_link_and_the_extend_prompt()
    {
        var (subject, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Arrow ECS", attendeeCount: 10,
            Billing(adHoc: new[] { AdHoc(cap: 50) }), Url);

        Assert.Contains("ELDK 2027", subject);
        Assert.Contains(Url, html);
        Assert.Contains("Need more", html);
        Assert.Contains("confidential", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔴 <b>SUMMARY IN THE MAIL, DETAIL ON THE PAGE.</b> The report says how many, never who. Names
    /// and e-mails stay behind the token, where access can be withdrawn — a mail cannot be unsent,
    /// and this one goes out every fortnight to what may well be a shared inbox.
    /// </summary>
    [Fact]
    public void The_mail_never_lists_the_attendees_themselves()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Arrow ECS", attendeeCount: 3,
            Billing(adHoc: new[] { AdHoc(claimed: 3, cap: 50) }), Url);

        // The count is there...
        Assert.Contains("3", html);
        // ...but nothing that could carry a person.
        Assert.DoesNotContain("@arrowpartner", html);
        Assert.DoesNotContain("<table", html);
    }

    /// <summary>Money is invariant-formatted, like every other amount CEH puts in front of a customer.</summary>
    [Fact]
    public void The_billed_total_is_culture_independent()
    {
        var (_, html) = CouponUsageReportComposer.Build(
            "ELDK 2027", "Arrow ECS", attendeeCount: 10,
            Billing(adHoc: new[] { AdHoc(billed: 15000m) }), Url);

        Assert.Contains("15000.00 DKK", html);
        Assert.DoesNotContain("15000,00", html);
    }

    // ---------------------------------------------------------------------
    //  Cadence
    // ---------------------------------------------------------------------

    /// <summary>Fortnightly, per his instruction — pinned so nobody "tidies" it to weekly.</summary>
    [Fact]
    public void The_report_interval_is_two_weeks()
        => Assert.Equal(TimeSpan.FromDays(14), CouponUsageReportService.Interval);
}
