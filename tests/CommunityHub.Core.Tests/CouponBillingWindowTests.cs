using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §787.17 — the bundling windows: which invoice a partner's claims land on, and when it is due.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"so it can bundle so partner get invoice every week,day,month"* …
/// *"bill by claim date, closed windows only"*.</para>
///
/// <para>⚠️ Every test here is a BOUNDARY. Bundling is only ever wrong at an edge — the last minute
/// of a month, the Sunday/Monday flip, the January week that belongs to last year — and each of those
/// mistakes shows up as a partner invoiced twice or not at all.</para>
/// </remarks>
public sealed class CouponBillingWindowTests
{
    private static DateTimeOffset Utc(int y, int m, int d, int hh = 0, int mm = 0) =>
        new(y, m, d, hh, mm, 0, TimeSpan.Zero);

    // ---- period keys -------------------------------------------------------------------

    [Theory]
    [InlineData(CouponBillingCadence.Daily, 2027, 1, 20, "2027-01-20")]
    [InlineData(CouponBillingCadence.Monthly, 2027, 1, 20, "2027-01")]
    // ISO week: 20 Jan 2027 is a Wednesday in week 3.
    [InlineData(CouponBillingCadence.Weekly, 2027, 1, 20, "2027-W03")]
    // PerClaim does not bundle, so it has no period.
    [InlineData(CouponBillingCadence.PerClaim, 2027, 1, 20, "")]
    public void The_period_key_is_derived_from_the_claim_date(
        CouponBillingCadence cadence, int y, int m, int d, string expected)
        => Assert.Equal(expected, CouponBillingWindow.PeriodKey(cadence, Utc(y, m, d)));

    /// <summary>
    /// 🔴 THE ISO-YEAR TRAP. 1 January 2027 is a Friday, and it falls in ISO week 53 of <b>2026</b>.
    /// </summary>
    /// <remarks>
    /// Using the calendar year here would file it as <c>2027-W53</c> — a week that does not exist in
    /// 2027 — and a claim from the following December could collide with it. The reference IS the
    /// idempotency key, so a collision means one partner's invoice suppressing another's.
    /// </remarks>
    [Fact]
    public void A_January_claim_in_last_years_ISO_week_uses_last_years_ISO_year()
    {
        Assert.Equal("2026-W53", CouponBillingWindow.PeriodKey(CouponBillingCadence.Weekly, Utc(2027, 1, 1)));
        // ...and the first claim of ISO week 1 rolls over correctly.
        Assert.Equal("2027-W01", CouponBillingWindow.PeriodKey(CouponBillingCadence.Weekly, Utc(2027, 1, 4)));
    }

    // ---- when a window closes ----------------------------------------------------------

    [Fact]
    public void A_daily_window_closes_at_midnight_after_the_claim()
    {
        var claim = Utc(2027, 1, 20, 23, 58);
        Assert.Equal(Utc(2027, 1, 21), CouponBillingWindow.WindowEnd(CouponBillingCadence.Daily, claim));

        Assert.False(CouponBillingWindow.IsClosed(CouponBillingCadence.Daily, claim, Utc(2027, 1, 20, 23, 59)));
        Assert.True(CouponBillingWindow.IsClosed(CouponBillingCadence.Daily, claim, Utc(2027, 1, 21, 0, 0)));
    }

    [Theory]
    // Every day of ISO week 3 (Mon 18th – Sun 24th Jan 2027) closes at the same instant: Mon 25th.
    [InlineData(18)] [InlineData(20)] [InlineData(24)]
    public void A_weekly_window_closes_on_the_following_Monday(int day)
        => Assert.Equal(
            Utc(2027, 1, 25),
            CouponBillingWindow.WindowEnd(CouponBillingCadence.Weekly, Utc(2027, 1, day, 12, 0)));

    [Fact]
    public void A_monthly_window_closes_at_the_start_of_the_next_month()
    {
        var claim = Utc(2027, 1, 31, 23, 58);
        Assert.Equal(Utc(2027, 2, 1), CouponBillingWindow.WindowEnd(CouponBillingCadence.Monthly, claim));

        // 🔴 THE CASE THE DECISION WAS MADE FOR. A claim at 23:58 on the 31st, met by a run at 00:05
        // on the 1st: the window HAS closed, and the claim still belongs to JANUARY — not to the
        // month the run happened in. Billing by run date would put it in February and our accounts
        // would disagree with the partner's.
        Assert.True(CouponBillingWindow.IsClosed(CouponBillingCadence.Monthly, claim, Utc(2027, 2, 1, 0, 5)));
        Assert.Equal("2027-01", CouponBillingWindow.PeriodKey(CouponBillingCadence.Monthly, claim));
    }

    [Fact]
    public void An_open_window_is_not_yet_billable()
    {
        var claim = Utc(2027, 1, 20, 9, 0);
        // Same week, two days later — still open.
        Assert.False(CouponBillingWindow.IsClosed(CouponBillingCadence.Weekly, claim, Utc(2027, 1, 22)));
        Assert.False(CouponBillingWindow.IsClosed(CouponBillingCadence.Monthly, claim, Utc(2027, 1, 31, 23, 59)));
    }

    /// <summary>PerClaim is due immediately — it does not bundle, so there is nothing to wait for.</summary>
    [Fact]
    public void PerClaim_is_billable_at_once()
        => Assert.True(CouponBillingWindow.IsClosed(
            CouponBillingCadence.PerClaim, Utc(2027, 1, 20, 9, 0), Utc(2027, 1, 20, 9, 0)));

    // ---- the reference, which is also the idempotency key --------------------------------

    /// <summary>
    /// 🔒 PerClaim keeps the ORDER-based reference CEH already writes, so a partner nobody has
    /// re-agreed terms with is billed exactly as they are today.
    /// </summary>
    [Fact]
    public void PerClaim_keeps_the_existing_order_based_reference()
        => Assert.Equal(
            CouponClaimExtractor.BuildReference("PARTNER-X", "9001"),
            CouponBillingWindow.InvoiceReference(
                CouponBillingCadence.PerClaim, "PARTNER-X", "9001", Utc(2027, 1, 20)));

    /// <summary>
    /// A bundling cadence keys on the PERIOD, so every claim in the window resolves to the SAME
    /// reference — which is what makes one invoice cover many orders, and what stops a re-run
    /// creating a second one.
    /// </summary>
    [Fact]
    public void A_bundled_reference_is_the_same_for_every_claim_in_the_window()
    {
        var monday = CouponBillingWindow.InvoiceReference(
            CouponBillingCadence.Weekly, "PARTNER-X", "9001", Utc(2027, 1, 18));
        var sunday = CouponBillingWindow.InvoiceReference(
            CouponBillingCadence.Weekly, "PARTNER-X", "9999", Utc(2027, 1, 24));

        Assert.Equal("PARTNER-X-2027-W03", monday);
        Assert.Equal(monday, sunday);          // different ORDERS, one invoice

        // ...and the next week is a different invoice.
        Assert.NotEqual(monday, CouponBillingWindow.InvoiceReference(
            CouponBillingCadence.Weekly, "PARTNER-X", "10001", Utc(2027, 1, 25)));
    }

    /// <summary>Two partners in the same window never share a reference.</summary>
    [Fact]
    public void Two_partners_in_the_same_window_get_different_references()
        => Assert.NotEqual(
            CouponBillingWindow.InvoiceReference(CouponBillingCadence.Monthly, "PARTNER-X", "1", Utc(2027, 1, 5)),
            CouponBillingWindow.InvoiceReference(CouponBillingCadence.Monthly, "PARTNER-Y", "2", Utc(2027, 1, 5)));
}
