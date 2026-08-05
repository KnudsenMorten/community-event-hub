using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §797 — one coupon CEH has just discovered, and everything it knows about it.
/// </summary>
/// <param name="CouponName">The promo code, as Backstage reports it.</param>
/// <param name="LiveClaims">Claims that still exist.</param>
/// <param name="CancelledClaims">Claims that were cancelled. ⚠️ They still prove the code was used.</param>
/// <param name="ValueDkk">What the live claims are worth, from <c>base_price</c> (§787.6).</param>
/// <param name="TicketClasses">The class names seen on its claims, for the reader — never matched on.</param>
public sealed record DiscoveredCoupon(
    string CouponName,
    int LiveClaims,
    int CancelledClaims,
    decimal ValueDkk,
    IReadOnlyList<string> TicketClasses);

/// <summary>
/// §797 — detects coupons CEH has never seen, gives them a rule row, and says so once.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"it could be great with an automatic rule creation detecting new
/// coupons and their relevant status. i need a reminder for that when new are detected"*.</para>
///
/// <para>🔴 <b>§787 already auto-creates an unmapped row — from inside the invoicing sweep, which is
/// where it fails.</b> Measured on PROD 2026-08-04: every coupon claim in the mirror is CANCELLED, so
/// <c>CouponDraftInvoiceService</c> returns at <c>claims.Count == 0</c> <b>before</b> its auto-create
/// block, and the coupon is invisible for ever. A claim that was cancelled still proves the code
/// exists — and the next claim on it is billable, so it still needs a decision.</para>
///
/// <para>🔒 <b>It runs BEFORE the <c>coupon-erp-invoicing</c> gate</b>, like §795.2 and §796:
/// discovering that a coupon exists is not the same act as writing an invoice, and a detection that
/// depends on an invoicing switch is [[ceh-two-switch-trap]] waiting to happen.</para>
///
/// <para>⚠️ <b>Detection is CLAIM-DRIVEN by construction and the mail says so.</b> Zoho Backstage has
/// no coupon API (§787.14, proven), so CEH cannot list the codes that exist over there. A coupon
/// nobody has used yet is undiscoverable, and a reminder that implied otherwise would be read as a
/// complete list.</para>
///
/// <para>🔒 <b>The status is never guessed.</b> A new row is <see cref="CouponBillingType.Unmapped"/>
/// — *"nobody has said who pays"* — because inventing a customer is worse than not invoicing (§787).
/// What the mail carries instead is what CEH actually knows: live claims, cancelled claims, value and
/// ticket classes, next to the decision it needs.</para>
/// </remarks>
public sealed class CouponDiscoveryService
{
    private readonly CommunityHubDbContext _db;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponDiscoveryService>? _log;

    public CouponDiscoveryService(
        CommunityHubDbContext db,
        EngineAlertSender alerts,
        TimeProvider clock,
        ILogger<CouponDiscoveryService>? log = null)
    {
        _db = db;
        _alerts = alerts;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Create rules for coupons CEH has never seen and mail info@ about them. Returns what was
    /// discovered; empty when nothing is new.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredCoupon>> DiscoverAsync(
        int eventId, CancellationToken ct = default)
    {
        // 🔑 The mirror, not Zoho (§525): an hourly job that needs no token cannot contribute to the
        // refresh-grant limit that once took every sync down.
        var raw = await _db.Orders
            .Where(o => o.EventId == eventId)
            .Select(o => o.RawJson)
            .ToListAsync(ct);

        var claims = raw.SelectMany(j => CouponClaimExtractor.FromOrderJson(j)).ToList();
        if (claims.Count == 0)
        {
            _log?.LogInformation("§797: no coupon claims in the mirror — nothing to discover.");
            return Array.Empty<DiscoveredCoupon>();
        }

        var known = (await _db.CouponInvoicingSettings
                .Where(c => c.EventId == eventId)
                .Select(c => c.CouponName)
                .ToListAsync(ct))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ⚠️ Grouped case-insensitively, exactly as the sweep matches rules — a coupon typed with a
        // different case is the SAME coupon, and inserting a second row would trip the
        // (EventId, CouponName) unique index or, worse, split one partner's billing in two.
        var discovered = claims
            .GroupBy(c => c.CouponName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !known.Contains(g.Key))
            .Select(g => new DiscoveredCoupon(
                CouponName: g.Key,
                LiveClaims: g.Count(c => !c.IsCancelled),
                // 🔴 Cancelled claims are COUNTED, not filtered away. They are the whole reason this
                // service exists: the invoicing sweep drops them and the coupon then never gets a row.
                CancelledClaims: g.Count(c => c.IsCancelled),
                ValueDkk: g.Where(c => !c.IsCancelled).Sum(c => c.UnitPriceDkk),
                TicketClasses: g.Select(c => c.TicketClassName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderByDescending(d => d.LiveClaims)
            .ThenBy(d => d.CouponName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (discovered.Count == 0)
        {
            _log?.LogInformation(
                "§797: {Claims} coupon claim(s) examined, no new coupons.", claims.Count);
            return Array.Empty<DiscoveredCoupon>();
        }

        var now = _clock.GetUtcNow();
        foreach (var d in discovered)
        {
            _db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
            {
                EventId = eventId,
                CouponName = d.CouponName,
                // 🔒 Unmapped. The one honest status for "nobody has said who pays".
                BillingType = CouponBillingType.Unmapped,
                FirstSeenClaimedAt = now,
                CreatedAt = now,
                // §797.5 — stamped as alerted BY THIS MAIL, so the §787 "still unmapped" chase does
                // not send a second mail about the same coupon within the hour. One event, one mail.
                LastAlertedAt = now,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A race with the invoicing sweep's own auto-create is the realistic cause. Nothing is
            // lost: the row exists either way, and the next pass finds nothing new. ⚠️ Never mail
            // about rows that did not save — the mail would name coupons this pass did not create.
            _log?.LogWarning(ex,
                "§797: {Count} new coupon(s) could not be saved (most likely created concurrently). "
                + "No mail sent; the next pass re-checks.", discovered.Count);
            return Array.Empty<DiscoveredCoupon>();
        }

        await SendAsync(discovered, ct);

        _log?.LogInformation(
            "§797: {Count} new coupon(s) detected and given an Unmapped rule: {Names}.",
            discovered.Count, string.Join(", ", discovered.Select(d => d.CouponName)));

        return discovered;
    }

    private async Task SendAsync(IReadOnlyList<DiscoveredCoupon> discovered, CancellationToken ct)
    {
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        var rows = string.Join("", discovered.Select(d =>
        {
            // ⚠️ "2 claimed" and "2 claimed, all cancelled" are different situations and must not
            // render the same — the second one is why this coupon was invisible until now.
            var used = d.LiveClaims > 0
                ? $"{d.LiveClaims} live claim(s)"
                  + (d.CancelledClaims > 0 ? $", {d.CancelledClaims} cancelled" : string.Empty)
                : $"<em>none live</em> — {d.CancelledClaims} cancelled";

            var classes = d.TicketClasses.Count > 0
                ? Enc(string.Join(", ", d.TicketClasses))
                : "<span style=\"color:#6b7280;\">unknown</span>";

            return "<tr>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\"><strong>{Enc(d.CouponName)}</strong></td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{used}</td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;\">{d.ValueDkk:0.##} DKK</td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{classes}</td>"
                 + "<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">not mapped</td>"
                 + "</tr>";
        }));

        var anyCancelledOnly = discovered.Any(d => d.LiveClaims == 0 && d.CancelledClaims > 0);

        var body =
            $"<p><strong>CEH has seen {discovered.Count} coupon code(s) it had no rule for, and has "
            + "created one for each.</strong> They are set to <em>not mapped</em> — CEH does not guess "
            + "who pays.</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + "<tr>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Coupon</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Used</th>"
            + "<th style=\"text-align:right;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Value</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Ticket class</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Status</th>"
            + "</tr>"
            + rows
            + "</table>"
            + "<p>Set the billing type and the e-conomic customer on "
            + "<strong>Organizer &rarr; Setup &rarr; Coupon invoicing</strong>. Until then any claim "
            + "on these codes is a ticket nobody can be billed for.</p>"
            + (anyCancelledOnly
                ? "<p style=\"background:#fff4e5;border-left:4px solid #d97706;padding:10px 12px;"
                  + "border-radius:4px;\">Some of these have <strong>only cancelled claims</strong>. "
                  + "That is not a reason to ignore them &mdash; the code exists and works, so the "
                  + "next person to use it produces a real, billable ticket. It is also why they were "
                  + "not picked up before: the invoicing sweep skips cancelled tickets entirely.</p>"
                : string.Empty)
            // ⚠️ The limit of the feature, stated where it is read. A list that looks complete and
            // is not is worse than no list.
            + "<p style=\"color:#6b7280;font-size:13px;\">CEH can only discover a coupon once somebody "
            + "<strong>uses</strong> it &mdash; Zoho Backstage has no API for listing promo codes, so "
            + "this is not the full list of codes that exist in Backstage. A code created there and "
            + "never claimed stays unknown until its first claim. You can always add one here by hand "
            + "before it is used, which is the case where nothing ever waits.</p>";

        // 🔒 throttleKey null — this is a "records created" notification, not a recurring condition.
        // Each mail names different coupons, and a blanket window would drop the second batch.
        // §808 — an ISSUE (it ends in "they need a billing decision"), so mok@ rather than the
        // actionable mailbox. The one invoice mail that still goes to info@ is the DRAFT-CREATED
        // notice, which is the "new pending invoice" he named.
        await _alerts.AlertAsync(
            $"{discovered.Count} new coupon code(s) detected — they need a billing decision",
            body, ct, throttleKey: null);
    }
}
