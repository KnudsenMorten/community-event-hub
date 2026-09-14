using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §796 — warns before a prepaid pool runs out.
/// </summary>
/// <remarks>
/// <para>§794.1(4), operator 2026-08-04: *"Warn before it runs out"* … *"build the low-balance
/// alert"*.</para>
///
/// <para>🔴 <b>Why this is urgent rather than tidy: CEH CANNOT STOP A CLAIM.</b> Zoho Backstage has
/// no coupon API (§787.14), so an exhausted pool does not close the promo code — the code keeps
/// working and the next person through claims a ticket <b>nobody has paid for</b>. This alert is the
/// only thing between "the pool ran out" and "an attendee found out". It is the same reasoning that
/// lets <see cref="CouponPoolBalance.Remaining"/> go negative instead of clamping at zero.</para>
///
/// <para>🔒 <b>Never about a pool closed by hand</b> (§795.1) — that is a decision somebody already
/// made, and <see cref="CouponPoolBalance.IsLow"/> excludes it at the source so this service and the
/// page can never disagree about what "low" means.</para>
///
/// <para>🔒 <b>Not gated on <c>coupon-erp-invoicing</c></b>, for the same reason as §795.2: that
/// switch governs whether CEH WRITES invoices to e-conomic, and a pool balance is not an invoice.</para>
/// </remarks>
public sealed class CouponPrepaidLowBalanceAlertService
{
    /// <summary>How long a pool stays quiet after being warned about at the same level or better.</summary>
    /// <remarks>
    /// ⚠️ 24h, matching §787's unmapped-coupon alert: both are conditions that stay true until a
    /// human acts, and both cost money while they do. <see cref="RemindsWhenWorse"/> is what keeps
    /// this from being the wrong number.
    /// </remarks>
    public static readonly TimeSpan ReAlertAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// 🔴 §796.2 — the quiet period is BROKEN BY DETERIORATION. Documented as a constant so the rule
    /// is visible from the call site: a pool alerted at "5 left" that is now "-2" re-alerts
    /// immediately, because that is a different fact and not a repeat of the first.
    /// </summary>
    public const bool RemindsWhenWorse = true;

    private readonly CommunityHubDbContext _db;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponPrepaidLowBalanceAlertService>? _log;

    public CouponPrepaidLowBalanceAlertService(
        CommunityHubDbContext db,
        EngineAlertSender alerts,
        TimeProvider clock,
        ILogger<CouponPrepaidLowBalanceAlertService>? log = null)
    {
        _db = db;
        _alerts = alerts;
        _clock = clock;
        _log = log;
    }

    /// <summary>One pool that is worth warning about, and the row it came from.</summary>
    private sealed record Candidate(CouponPrepaidAllocation Row, CouponPoolBalance Balance, string Coupon);

    /// <summary>
    /// §1094c — the AD-HOC twin: a coupon with an agreed cap that is nearly used up.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"make alerting, emails the same for any coupon no matter if adhoc
    /// or prepaid, if cap is applied include also available/remaining in mails/alerting"*.</para>
    ///
    /// <para>🔑 <b>Same alert, same mail, same rules — because the failure is identical.</b> Prepaid
    /// or capped ad-hoc, CEH cannot stop a claim (§787.14), so passing the agreed number means
    /// somebody takes a ticket outside the agreement either way. Two separate alerts would have been
    /// two chances to tune one of them wrong, and two mails for one problem.</para>
    ///
    /// <para>⚠️ An UNCAPPED ad-hoc coupon is never a candidate: there is no number to be near.</para>
    /// </remarks>
    private sealed record CapCandidate(CouponInvoicingSetting Row, int Claimed, int Cap)
    {
        public string Coupon => Row.CouponName;
        public int Remaining => Cap - Claimed;
        public bool IsOverCap => Remaining < 0;
        public bool IsExhausted => Remaining == 0;
        public bool IsLow => Remaining <= CouponPrepaidBalance.DefaultLowBalanceThreshold;
    }

    /// <summary>
    /// Warn about prepaid pools at or below their threshold. Returns how many were named in the
    /// mail; 0 when every pool is healthy or still inside its quiet period.
    /// </summary>
    public async Task<int> AlertAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var pools = await _db.CouponPrepaidAllocations
            .Where(a => a.EventId == eventId)
            .Include(a => a.CouponInvoicingSetting)
            // §798.4 — the purchased figure is the SUM of the top-ups, so they have to be loaded or
            // every pool would balance against zero bought and read as oversubscribed.
            .Include(a => a.Purchases)
            .ToListAsync(ct);

        // 🔒 Only pools on a coupon that is STILL prepaid — an allocation left behind on a rule
        // switched to another billing type is not a pool anyone is claiming against.
        var live = pools.Where(a => a.CouponInvoicingSetting?.IsPrepaid == true).ToList();

        // §1094c — capped AD-HOC coupons are watched by the same alert. Uncapped ones are skipped:
        // there is no agreed number to be near.
        var capped = await _db.CouponInvoicingSettings
            .Where(c => c.EventId == eventId
                        && c.BillingType == CouponBillingType.ClaimableAdHocPaymentByCustomer
                        && c.ClaimCapTickets != null
                        && c.ClaimCapTickets > 0)
            .ToListAsync(ct);

        if (live.Count == 0 && capped.Count == 0)
        {
            _log?.LogInformation(
                "§796/§1094c: no prepaid pools and no capped ad-hoc coupons — nothing to warn about.");
            return 0;
        }

        // 🔑 The balances come from the SAME derivation the page and the partner see
        // (CouponPrepaidBalance), read from the current state of the order mirror. An alert computed
        // its own way would eventually disagree with the number the organizer is looking at.
        var raw = await _db.Orders
            .Where(o => o.EventId == eventId)
            .Select(o => o.RawJson)
            .ToListAsync(ct);

        var claimsByCoupon = raw
            .SelectMany(j => CouponClaimExtractor.FromOrderJson(j))
            .GroupBy(c => c.CouponName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CouponClaim>)g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var candidates = new List<Candidate>();
        foreach (var row in live)
        {
            var coupon = row.CouponInvoicingSetting!.CouponName;
            claimsByCoupon.TryGetValue(coupon, out var claims);
            var balance = CouponPrepaidBalance.For(row, claims ?? Array.Empty<CouponClaim>());

            // 🔒 IsLow already excludes a hand-closed pool (§795.1). Asking the balance rather than
            // re-deriving "low" here is what keeps this service and the page in agreement.
            if (balance.IsLow) candidates.Add(new Candidate(row, balance, coupon));
        }

        var due = candidates.Where(c => IsDue(c, now)).ToList();

        // §1094c — the ad-hoc side, built the same way and judged by the same rules.
        var capCandidates = new List<CapCandidate>();
        foreach (var rule in capped)
        {
            claimsByCoupon.TryGetValue(rule.CouponName, out var claims);
            var claimed = (claims ?? Array.Empty<CouponClaim>()).Count(c => !c.IsCancelled);
            var c = new CapCandidate(rule, claimed, rule.ClaimCapTickets!.Value);
            if (c.IsLow) capCandidates.Add(c);
        }

        var capDue = capCandidates.Where(c => IsCapDue(c, now)).ToList();

        if (due.Count == 0 && capDue.Count == 0)
        {
            // ⚠️ Silent. "Nothing is low" and "the job did not run" must not both produce a mail.
            _log?.LogInformation(
                "§796/§1094c: {Low} low pool(s) of {Total} and {CapLow} capped coupon(s), none due "
                + "an alert (quiet period, and none got worse).",
                candidates.Count, live.Count, capCandidates.Count);
            return 0;
        }

        var over = due.Where(c => c.Balance.IsOversubscribed).ToList();
        var capOver = capDue.Where(c => c.IsOverCap).ToList();
        await SendAsync(due, over, capDue, capOver, ct);

        foreach (var c in due)
        {
            c.Row.LastLowBalanceAlertAt = now;
            // §796.2 — the NUMBER, not a flag: it is what lets the next pass tell "worse" from
            // "still low" and from "recovered".
            c.Row.LastLowBalanceAlertRemaining = c.Balance.Remaining;
        }
        foreach (var c in capDue)
        {
            c.Row.LastCapAlertAt = now;
            c.Row.LastCapAlertRemaining = c.Remaining;
        }
        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§796/§1094c: warned about {Count} low pool(s) ({Over} oversubscribed) and {CapCount} "
            + "capped coupon(s) ({CapOver} over cap).",
            due.Count, over.Count, capDue.Count, capOver.Count);
        return due.Count + capDue.Count;
    }

    /// <summary>§1094c — the ad-hoc twin of <see cref="IsDue"/>, with identical rules.</summary>
    private static bool IsCapDue(CapCandidate c, DateTimeOffset now)
    {
        if (c.Row.LastCapAlertAt is not { } last) return true;
        if (now - last >= ReAlertAfter) return true;

        return c.Row.LastCapAlertRemaining is { } before && c.Remaining < before;
    }

    /// <summary>
    /// 🔴 §796.2 — due when it has never been alerted, when the quiet period has passed, OR when it
    /// has got WORSE since the last alert.
    /// </summary>
    /// <remarks>
    /// The third case is the one that matters: a pool alerted at "5 left" and now at "-2" is a
    /// different fact, and a plain 24-hour suppression would silence exactly the transition this
    /// alert exists to catch. ⚠️ RECOVERY is deliberately not a trigger — a cancellation putting
    /// tickets back is good news, and nobody needs a mail about it.
    /// </remarks>
    private static bool IsDue(Candidate c, DateTimeOffset now)
    {
        if (c.Row.LastLowBalanceAlertAt is not { } last) return true;
        if (now - last >= ReAlertAfter) return true;

        return c.Row.LastLowBalanceAlertRemaining is { } before && c.Balance.Remaining < before;
    }

    private async Task SendAsync(
        IReadOnlyList<Candidate> due, IReadOnlyList<Candidate> over,
        IReadOnlyList<CapCandidate> capDue, IReadOnlyList<CapCandidate> capOver,
        CancellationToken ct)
    {
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        // ⚠️ Worst first — an oversubscribed pool is money already gone, where "3 left" is a
        // heads-up. The subject says so too, because the two must never read the same.
        var ordered = due.OrderBy(c => c.Balance.Remaining)
            .ThenBy(c => c.Coupon, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = string.Join("", ordered.Select(c =>
        {
            var b = c.Balance;
            var cls = string.IsNullOrWhiteSpace(b.Label) ? b.TicketClassId : b.Label;
            var colour = b.IsOversubscribed ? "#c8322a" : b.IsExhausted ? "#b45309" : "#b45309";
            var state = b.IsOversubscribed
                ? $"<strong style=\"color:#c8322a;\">{-b.Remaining} beyond what was paid for</strong>"
                : b.IsExhausted ? "<strong>used in full</strong>" : "running low";

            return "<tr>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{Enc(c.Coupon)}</td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{Enc(cls)}</td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;\">"
                 + $"<strong style=\"color:{colour};font-size:16px;\">{b.Remaining}</strong>"
                 + $"<span style=\"color:#6b7280;\"> of {b.Purchased}</span></td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{b.Claimed} claimed</td>"
                 + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{state}</td>"
                 + "</tr>";
        }));

        // §1094c — the ad-hoc rows, in the SAME table. Operator 2026-08-19: *"make alerting, emails
        // the same for any coupon no matter if adhoc or prepaid"*. The "of N" column carries the
        // remaining/available number he asked to see for both kinds.
        var capRows = string.Join("", capDue
            .OrderBy(c => c.Remaining)
            .ThenBy(c => c.Coupon, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var colour = c.IsOverCap ? "#c8322a" : "#b45309";
                var state = c.IsOverCap
                    ? $"<strong style=\"color:#c8322a;\">{-c.Remaining} beyond the agreed cap</strong>"
                    : c.IsExhausted ? "<strong>cap reached</strong>" : "near the cap";

                return "<tr>"
                     + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{Enc(c.Coupon)}</td>"
                     + "<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">"
                     + "<em>ad-hoc cap</em></td>"
                     + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;\">"
                     + $"<strong style=\"color:{colour};font-size:16px;\">{c.Remaining}</strong>"
                     + $"<span style=\"color:#6b7280;\"> of {c.Cap}</span></td>"
                     + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{c.Claimed} claimed</td>"
                     + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{state}</td>"
                     + "</tr>";
            }));

        rows += capRows;

        var totalDue = due.Count + capDue.Count;
        var totalOver = over.Count + capOver.Count;

        // 🔑 "coupon(s)", not "prepaid pool(s)": one mail now covers both kinds, and a subject that
        // still said prepaid would make a capped ad-hoc warning look misfiled.
        var subject = totalOver > 0
            ? $"ACTION REQUIRED: {totalOver} coupon(s) OVER their agreed number"
              + (totalDue > totalOver ? $", {totalDue - totalOver} running low" : string.Empty)
            : $"ACTION REQUIRED: {totalDue} coupon(s) running low";

        var lead = totalOver > 0
            ? "<p><strong>At least one partner has claimed more tickets than was agreed.</strong> "
              + "This is not a warning about the future — those tickets exist, and for a prepaid "
              + "pool nobody has been billed for them.</p>"
            : "<p><strong>These coupons are nearly used up.</strong></p>";

        var body =
            lead
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + "<tr>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Coupon</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Ticket class</th>"
            + "<th style=\"text-align:right;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Left</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Used</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">State</th>"
            + "</tr>"
            + rows
            + "</table>"
            // 🔴 The operational fact that makes this urgent, said where it is read.
            + "<p style=\"background:#fff4e5;border-left:4px solid #d97706;padding:10px 12px;"
            + "border-radius:4px;\"><strong>An empty pool does not stop the promo code.</strong> "
            + "Zoho Backstage has no coupon API, so CEH cannot close it — the code keeps working and "
            + "the next person to use it gets a ticket nobody has paid for. Closing the code is a "
            + "manual step in Backstage.</p>"
            + "<p>On <strong>Organizer &rarr; Setup &rarr; Coupon invoicing</strong> you can agree "
            + "more tickets with the partner and raise the allocation, or close the allocation if "
            + "the agreement is over. Cancelled tickets return to the pool on their own.</p>"
            + "<p style=\"color:#6b7280;font-size:13px;\">This repeats once a day while a pool is "
            + "low &mdash; and immediately if it drops further, because &quot;5 left&quot; and "
            + "&quot;-2&quot; are different problems. A pool you have closed is never warned about.</p>";

        // 🔒 throttleKey null — the per-pool stamp is the throttle, and it has to be, or one low
        // pool would silence an alert about a different one that just went negative.
        //
        // §808 — an ISSUE, so it goes to mok@ (the EngineAlertSender default): a pool about to hand
        // out tickets nobody paid for is his to act on, and "oversubscribed" is not queue work.
        await _alerts.AlertAsync(subject, body, ct, throttleKey: null);
    }
}
