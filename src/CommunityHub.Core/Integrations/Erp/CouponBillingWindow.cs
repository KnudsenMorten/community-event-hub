using System.Globalization;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §787.17 — how a partner's claims are BUNDLED into invoices, and when a bundle becomes due.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"i must also define cadence for frequency to bill pr customer"* …
/// *"so it can bundle so partner get invoice every week,day,month"* … *"individually agreed"* …
/// *"bill by claim date, closed windows only"*.</para>
///
/// <para>🔒 <b>Pure and I/O-free on purpose.</b> This decides which invoice a partner's money lands
/// on and when it is sent, so it is testable without a database, a clock or e-conomic.</para>
/// </remarks>
public static class CouponBillingWindow
{
    /// <summary>
    /// The period key a claim belongs to — part of the invoice reference, so it is also the
    /// idempotency key.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Derived from the CLAIM's own timestamp</b>, never from "now" (§787.17c). The job
    /// can be late, retried, run twice or miss a day, and the same claim still resolves to the same
    /// period and therefore to the same invoice.</para>
    ///
    /// <para>⚠️ <b>ISO week, and the ISO YEAR with it.</b> 1 Jan can fall in week 52 of the PREVIOUS
    /// year — using the calendar year there would file a January claim under a week that already
    /// invoiced in the old year, and the reference would collide with a real one.</para>
    /// </remarks>
    public static string PeriodKey(CouponBillingCadence cadence, DateTimeOffset claimedAt)
    {
        var d = claimedAt.UtcDateTime;
        return cadence switch
        {
            CouponBillingCadence.Daily => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CouponBillingCadence.Weekly => IsoWeekKey(d),
            CouponBillingCadence.Monthly => d.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            // PerClaim does not bundle, so it has no period — the caller keeps the order-based
            // reference it has always used.
            _ => string.Empty,
        };
    }

    private static string IsoWeekKey(DateTime d)
    {
        var week = ISOWeek.GetWeekOfYear(d);
        var year = ISOWeek.GetYear(d);   // NOT d.Year — see the remarks above.
        return string.Create(CultureInfo.InvariantCulture, $"{year:D4}-W{week:D2}");
    }

    /// <summary>
    /// The instant a claim's window CLOSES — exclusive. The window is billable once <c>now</c> has
    /// reached it.
    /// </summary>
    public static DateTimeOffset WindowEnd(CouponBillingCadence cadence, DateTimeOffset claimedAt)
    {
        var d = claimedAt.UtcDateTime;
        return cadence switch
        {
            CouponBillingCadence.Daily =>
                new DateTimeOffset(d.Date.AddDays(1), TimeSpan.Zero),

            CouponBillingCadence.Weekly =>
                // ISO weeks start Monday, so the window ends at the start of the NEXT Monday.
                new DateTimeOffset(d.Date.AddDays(7 - ((int)d.DayOfWeek + 6) % 7), TimeSpan.Zero),

            CouponBillingCadence.Monthly =>
                new DateTimeOffset(new DateTime(d.Year, d.Month, 1).AddMonths(1), TimeSpan.Zero),

            // PerClaim is due immediately — its "window" closed the moment the claim existed.
            _ => claimedAt,
        };
    }

    /// <summary>
    /// Is this claim's window CLOSED, i.e. may it be invoiced yet?
    /// </summary>
    /// <remarks>
    /// ⚠️ An OPEN window is <b>not an error</b>. Its claims are not due yet, and a caller must report
    /// them as held rather than skipped — "3 claims skipped" reads as a fault, "3 claims held, this
    /// week closes Sunday" reads as the truth (§787.17c).
    /// </remarks>
    public static bool IsClosed(
        CouponBillingCadence cadence, DateTimeOffset claimedAt, DateTimeOffset now) =>
        now >= WindowEnd(cadence, claimedAt);

    /// <summary>
    /// The invoice reference for a bundle. <c>PerClaim</c> keeps the ORDER-based reference the
    /// retired script used and CEH already writes; the bundling cadences key on the period instead.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>PerClaim's reference is unchanged on purpose.</b> It is the marker the booked+draft scan
    /// already matches, so rows that never adopt a cadence keep working exactly as they do today —
    /// which is what makes this change safe to deploy against partners nobody has re-agreed terms
    /// with.
    /// </remarks>
    public static string InvoiceReference(
        CouponBillingCadence cadence, string couponName, string orderId, DateTimeOffset claimedAt) =>
        cadence == CouponBillingCadence.PerClaim
            ? CouponClaimExtractor.BuildReference(couponName, orderId)
            : $"{couponName.Trim()}-{PeriodKey(cadence, claimedAt)}";

    /// <summary>Human wording for a held bundle, so a log line explains itself.</summary>
    public static string DescribeHold(
        CouponBillingCadence cadence, DateTimeOffset claimedAt) =>
        $"{cadence.ToString().ToLowerInvariant()} bundle "
        + $"{PeriodKey(cadence, claimedAt)} is still open until "
        + $"{WindowEnd(cadence, claimedAt).UtcDateTime:yyyy-MM-dd HH:mm}Z";
}
