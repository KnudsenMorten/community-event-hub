using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// §1040 — THE SHARED PAGE: who has bought a ticket, for the company that bought the block.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"i can send to persons where they can follow who bought a ticket"</i>.
/// No login — the URL is the credential.</para>
///
/// <para>🔴 <b>This is the one page in the hub that shows real people's names and e-mail addresses to
/// an anonymous caller.</b> Four things make that defensible, and all four are load-bearing:</para>
/// <list type="number">
///   <item>the token is 256 bits of cryptographic randomness — the URL cannot be guessed;</item>
///   <item>a monitor scopes to ONE domain or ONE coupon, so a forwarded link cannot widen;</item>
///   <item>it is revocable, and it EXPIRES by itself (15 Feb 2027 by his instruction);</item>
///   <item>it is <b>read-only</b> — there is no action on this page at all.</item>
/// </list>
///
/// <para>🔒 <c>noindex</c> is set in the view: a link pasted into a public thread must not become a
/// search result. That is defence against the accident, not against an attacker.</para>
/// </remarks>
[AllowAnonymous]
public class MonitorModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly AttendeeMonitorQuery _query;
    private readonly TimeProvider _clock;
    private readonly MonitorPrepaidBalanceQuery? _billing;
    private readonly CommunityHub.Core.Integrations.Erp.CouponCapRequestNotifier? _capRequests;
    private readonly CommunityHub.Core.Integrations.Erp.CouponPrepaidInvoiceService? _prepaidInvoices;

    public MonitorModel(
        CommunityHubDbContext db, AttendeeMonitorQuery query, TimeProvider clock,
        // §1094 — all optional so every existing test that constructs this page still compiles,
        // and so the page degrades to exactly its old behaviour if any is unregistered.
        MonitorPrepaidBalanceQuery? billing = null,
        CommunityHub.Core.Integrations.Erp.CouponCapRequestNotifier? capRequests = null,
        // §1104 — a partner buying more prepaid tickets raises the invoice for the difference.
        CommunityHub.Core.Integrations.Erp.CouponPrepaidInvoiceService? prepaidInvoices = null)
    {
        _db = db;
        _query = query;
        _clock = clock;
        _billing = billing;
        _capRequests = capRequests;
        _prepaidInvoices = prepaidInvoices;
    }

    public AttendeeMonitor? Monitor { get; private set; }
    public IReadOnlyList<MonitoredAttendee> Rows { get; private set; } = Array.Empty<MonitoredAttendee>();
    public string? EventName { get; private set; }

    /// <summary>§1094 — the billing picture: prepaid pools and ad-hoc coupons with their caps.</summary>
    public MonitoredBilling Billing { get; private set; } = MonitoredBilling.Empty;

    /// <summary>§1094 — set after a successful "I need more tickets" request.</summary>
    public string? RequestMessage { get; private set; }

    /// <summary>§1094 — set when the request could not be accepted, with the reason.</summary>
    public string? RequestError { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        var monitor = await ResolveAsync(token, ct);
        if (monitor is null) return NotFound();

        // §1040 — record the read so an organizer can see whether a link is in use before revoking
        // it, and notice one being opened that should not be.
        monitor.ViewCount++;
        monitor.LastViewedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        Monitor = monitor;
        Rows = await _query.RunAsync(monitor, ct);
        // §1094 — fail-soft: the billing panel is an addition to this page, and a coupon-side
        // problem must not take away the attendee list the link has always shown.
        Billing = await SafeBillingAsync(monitor, ct);
        EventName = await _db.Events.Where(e => e.Id == monitor.EventId)
            .Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        return Page();
    }

    /// <summary>
    /// §1094 — "I need more tickets", from the partner's own page.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This records a REQUEST and mails the operator. It changes no limit.</b> The cap
    /// that actually stops a claim is Zoho's, and Backstage has no coupon API (§787.14) — so raising
    /// CEH's number here would have the hub promise tickets the partner still cannot claim.</para>
    ///
    /// <para>🔒 <b>Throttled to one request an hour per link, and that is the only thing between an
    /// anonymous endpoint and the ops inbox.</b> The page is reachable by anyone holding the URL; a
    /// refresh loop must not become a mail loop.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostRequestMoreAsync(
        string token, string couponName, int requestedTotal, string? note,
        string? requestedByName, string? requestedByEmail, CancellationToken ct)
    {
        var monitor = await ResolveAsync(token, ct);
        if (monitor is null) return NotFound();

        Monitor = monitor;
        Rows = await _query.RunAsync(monitor, ct);
        Billing = await SafeBillingAsync(monitor, ct);
        EventName = await _db.Events.Where(e => e.Id == monitor.EventId)
            .Select(e => e.DisplayName).FirstOrDefaultAsync(ct);

        if (requestedTotal <= 0)
        {
            RequestError = "Enter how many tickets you need in total.";
            return Page();
        }

        // 🔴 §1096 — THE COUPON NAME COMES FROM AN ANONYMOUS FORM POST, so it is matched against the
        // lines this link actually shows. Trusting it would let anyone holding one partner's URL
        // raise a request against ANOTHER customer's coupon — the one failure the whole monitor
        // design is built to make impossible ("a token can never widen to another company").
        var wanted = (couponName ?? string.Empty).Trim();
        var pool = Billing.Pools.FirstOrDefault(
            p => string.Equals(p.CouponName, wanted, StringComparison.OrdinalIgnoreCase));
        var adHoc = Billing.AdHoc.FirstOrDefault(
            a => string.Equals(a.CouponName, wanted, StringComparison.OrdinalIgnoreCase));

        if (pool is null && adHoc is null)
        {
            RequestError = "That is not one of your codes — please reload the page and try again.";
            return Page();
        }

        var now = _clock.GetUtcNow();
        // 🔒 The rate limit is per LINK, not per coupon: it protects an anonymous endpoint, and a
        // partner holding three codes must not get three times the access to the ops inbox.
        if (monitor.LastCapRequestAt is { } last && now - last < TimeSpan.FromHours(1))
        {
            RequestError =
                "We have already received your request — we will be in touch shortly. "
                + "Please email us if it is urgent.";
            return Page();
        }

        if (_capRequests is null)
        {
            RequestError = "Requests cannot be sent from this environment. Please email us instead.";
            return Page();
        }

        // §1096 — no guessing any more: the LINE the partner pressed decides which agreement this
        // is, so a customer holding both a pool and a capped coupon gets the right mail for each.
        var isPrepaid = pool is not null;
        var currentCap = isPrepaid ? pool!.Purchased : adHoc!.CapTickets ?? 0;
        var claimed = isPrepaid ? pool!.Claimed : adHoc!.Claimed;
        var extra = requestedTotal - currentCap;

        if (extra <= 0)
        {
            RequestError =
                $"You already have {currentCap} on {wanted}. Enter a HIGHER total to increase it.";
            return Page();
        }

        // 🔴 §1104 — CEH APPLIES THE CHANGE ITSELF (operator 2026-08-20: *"why do i have to do that
        // manually"*). Raising a cap and invoicing a difference are both arithmetic the hub already
        // holds; making him retype them was work with no judgement in it.
        //
        // 🔒 GUARDED BY A SANITY CEILING, because this endpoint is ANONYMOUS and, for a prepaid pool,
        // now spends money. A mistyped 4000 would otherwise raise an invoice for ~12M DKK on a
        // partner's behalf. Above the ceiling it falls back to asking, which is the old behaviour and
        // the safe one — reported plainly in the mail rather than silently.
        var autoApplied = extra <= MaxSelfServiceIncrease;
        string? appliedNote = null;

        if (autoApplied)
        {
            appliedNote = await ApplySelfServiceIncreaseAsync(
                monitor.EventId, wanted, isPrepaid, requestedTotal, extra, ct);
            if (appliedNote is null) autoApplied = false;   // could not apply — fall back to asking
        }

        await _capRequests.NotifyAsync(
            customerLabel: monitor.Name,
            erpCustomerNumber: monitor.Kind == AttendeeMonitorKind.ErpCustomer
                               && int.TryParse(monitor.Value, out var cn) ? cn : null,
            kind: isPrepaid
                ? CommunityHub.Core.Integrations.Erp.CouponCapRequestNotifier.RequestKind.PrepaidTopUp
                : CommunityHub.Core.Integrations.Erp.CouponCapRequestNotifier.RequestKind.AdHocCap,
            currentCap: currentCap > 0 ? currentCap : null,
            requestedTotal: requestedTotal,
            claimedSoFar: claimed,
            note: note,
            ct: ct,
            couponName: wanted,
            appliedNote: appliedNote,
            requestedByName: requestedByName,
            requestedByEmail: requestedByEmail,
            contactOnFile: await _db.CouponInvoicingSettings
                .AsNoTracking()
                .Where(c => c.EventId == monitor.EventId && c.CouponName == wanted)
                .Select(c => c.ClaimInviteSentToEmail ?? c.RequesterName)
                .FirstOrDefaultAsync(ct));

        // 🔒 Stamped AFTER the mail is away, so a send that threw does not silence the next attempt.
        monitor.LastCapRequestAt = now;

        // §1096 — the ask itself lives on the COUPON, so two lines can be waiting at once and the
        // organizer's page can show each against the coupon it belongs to.
        var rule = await _db.CouponInvoicingSettings.FirstOrDefaultAsync(
            c => c.EventId == monitor.EventId && c.CouponName == wanted, ct);
        if (rule is not null)
        {
            rule.RequestedCapTickets = requestedTotal;
            rule.RequestedCapAt = now;
        }

        await _db.SaveChangesAsync(ct);

        // §1104 — say what actually happened. "We have passed your request on" would be untrue when
        // the hub has already applied it, and the partner would not know their tickets are live.
        RequestMessage = autoApplied
            ? $"Thank you — {extra} more ticket(s) have been added to {wanted}, bringing you to "
              + $"{requestedTotal}. The organizer team has been notified and will make sure the code "
              + "itself allows the new total."
            : $"Thank you — we have passed your request for {extra} more ticket(s) on {wanted} "
              + "to the organizer team. They will confirm by email.";

        // Re-read so the tables show the new numbers rather than the ones from before the change.
        Billing = await SafeBillingAsync(monitor, ct);
        return Page();
    }

    /// <summary>
    /// §1104 — the largest increase a partner may apply to themselves in one go.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This endpoint is anonymous and, for a prepaid pool, it raises a real invoice.</b>
    /// A mistyped 4000 on a 3000 DKK ticket would bill a partner ~12M DKK before anyone read the
    /// mail. Above this the request is still sent, and still reaches him — it just is not applied
    /// automatically, which is exactly the behaviour that shipped before and the safe one.</para>
    ///
    /// <para>🔒 <b>100 is the operator's number, confirmed 2026-08-20 (*"keep the guard at 100"*)</b>
    /// after he was shown what the guard produces when it fires. It is not a placeholder to tune.</para>
    ///
    /// <para>⚠️ It is a TYPO guard, not a credit limit: a partner who genuinely wants 200 more asks
    /// twice, or the organizer does it on the page. Raising it to "be helpful" trades a real
    /// financial blast radius for the convenience of a case that has a working path already.</para>
    /// </remarks>
    private const int MaxSelfServiceIncrease = 100;

    /// <summary>
    /// §1104 — do what the partner asked: raise the cap, or buy and invoice the difference.
    /// Returns a sentence describing what was done, or null when it could not be applied.
    /// </summary>
    private async Task<string?> ApplySelfServiceIncreaseAsync(
        int eventId, string couponName, bool isPrepaid, int newTotal, int extra, CancellationToken ct)
    {
        var rule = await _db.CouponInvoicingSettings.FirstOrDefaultAsync(
            c => c.EventId == eventId && c.CouponName == couponName, ct);
        if (rule is null) return null;

        var now = _clock.GetUtcNow();

        // ── ad-hoc: a ceiling, no money ────────────────────────────────────────────────────
        if (!isPrepaid)
        {
            rule.ClaimCapTickets = newTotal;
            rule.RequestedCapTickets = null;     // applied, so nothing is left waiting on him
            rule.UpdatedAt = now;
            rule.LastUpdatedByEmail = SelfServiceAuthor;
            await _db.SaveChangesAsync(ct);

            return $"the cap on <strong>{couponName}</strong> was raised by <strong>{extra}</strong> "
                 + $"to <strong>{newTotal}</strong>. No invoice — a capped ad-hoc coupon is billed as "
                 + "tickets are claimed.";
        }

        // ── prepaid: a purchase, and an invoice for the difference ─────────────────────────
        var pool = await _db.CouponPrepaidAllocations
            .Include(a => a.Purchases)
            .FirstOrDefaultAsync(a => a.EventId == eventId && a.CouponInvoicingSettingId == rule.Id, ct);
        if (pool is null) return null;

        // 🔑 The price comes from what they last agreed, never from a default: this is an invoice
        // raised without a human, so inventing a price would be inventing an agreement.
        var unitPrice = pool.Purchases
            .Where(p => p.UnitPriceDkk is > 0m)
            .OrderByDescending(p => p.Id)
            .Select(p => p.UnitPriceDkk)
            .FirstOrDefault();
        if (unitPrice is not > 0m) return null;

        var purchase = new CommunityHub.Core.Domain.CouponPrepaidPurchase
        {
            Quantity = extra,
            UnitPriceDkk = unitPrice,
            Notes = "Bought by the partner from their own status page.",
            CreatedAt = now,
            CreatedByEmail = SelfServiceAuthor,
        };
        pool.Purchases.Add(purchase);
        pool.UpdatedAt = now;
        rule.RequestedCapTickets = null;

        // 🔒 Saved BEFORE the invoice, exactly as the organizer path does (§990): the purchase id is
        // the invoice's reference, and the tickets were agreed whether or not e-conomic answers.
        await _db.SaveChangesAsync(ct);

        var bought = pool.Purchases.Sum(p => p.Quantity);
        var money = (extra * unitPrice.Value).ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

        if (_prepaidInvoices is null)
        {
            return $"<strong>{extra}</strong> more ticket(s) were added to "
                 + $"<strong>{couponName}</strong> ({bought} total). ⚠️ No invoice was raised — "
                 + "invoicing is not configured on this host, so it needs one by hand.";
        }

        var label = pool.TicketClassLabel ?? pool.TicketClassId;
        var result = await _prepaidInvoices.CreateForPurchaseAsync(
            rule, label, purchase.Id, extra, unitPrice.Value, ct);

        return result.Created
            ? $"<strong>{extra}</strong> more ticket(s) added to <strong>{couponName}</strong> "
              + $"({bought} total) and e-conomic draft invoice <strong>{result.DraftNumber}</strong> "
              + $"raised for <strong>{money} DKK</strong>. "
              // ⚠️ §1013a — a draft is renumbered when booked, so the number above is provisional.
              + "It is a DRAFT until you book it, and its number changes when you do."
            : $"<strong>{extra}</strong> more ticket(s) were added to <strong>{couponName}</strong> "
              + $"({bought} total), but the invoice could NOT be created: {result.Problem}. "
              + "Raise it by hand on /Organizer/CouponInvoicing.";
    }

    /// <summary>Marks rows the PARTNER changed, so an audit can tell them from an organizer's.</summary>
    private const string SelfServiceAuthor = "partner (self-service, §1104)";

    private async Task<MonitoredBilling> SafeBillingAsync(AttendeeMonitor monitor, CancellationToken ct)
    {
        if (_billing is null) return MonitoredBilling.Empty;
        try { return await _billing.RunAsync(monitor, ct); }
        catch { return MonitoredBilling.Empty; }
    }

    /// <summary>
    /// §1040 — the Excel export he asked for: <i>"i also need them to have an export to excel
    /// button"</i>.
    /// </summary>
    /// <remarks>
    /// 🔑 Runs the SAME query as the page, so the file and the screen can never disagree — a export
    /// built from its own query is a second definition of "who counts", and the one that drifts is
    /// the one somebody forwards to their finance team.
    /// </remarks>
    public async Task<IActionResult> OnGetExportAsync(string token, CancellationToken ct)
    {
        var monitor = await ResolveAsync(token, ct);
        if (monitor is null) return NotFound();

        var rows = await _query.RunAsync(monitor, ct);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tickets");
        // §1097 (operator 2026-08-20): *"list of all sign-ups with relevant details like name,
        // email, company, ticket id, ticket class, date/time when bought"*.
        var headers = new[]
        {
            "Bought", "First name", "Last name", "E-mail", "Company",
            "Ticket class", "Ticket id", "Order id",
            "Ordered by", "Ordered by e-mail",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var a in rows)
        {
            // ⚠️ The DATE as a real date cell, not a string: a spreadsheet people sort by ticket
            // date is the whole point, and text sorts 1 May before 2 April.
            if (a.BoughtAt is { } b)
            {
                ws.Cell(r, 1).Value = b.UtcDateTime;
                // §1097 — DATE AND TIME, as he asked. Two tickets bought the same day are otherwise
                // indistinguishable in a sort, and on a busy day that is most of them.
                ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            }
            ws.Cell(r, 2).Value = a.FirstName;
            ws.Cell(r, 3).Value = a.LastName;
            ws.Cell(r, 4).Value = a.Email;
            ws.Cell(r, 5).Value = a.Company ?? string.Empty;
            ws.Cell(r, 6).Value = a.TicketClassName ?? string.Empty;
            // 🔒 The ticket id as TEXT: Backstage ids are long numeric strings, and Excel turns
            // those into floats in scientific notation — 1.488e+16 is not something anyone can look
            // up. Same reason the invoice references are strings.
            ws.Cell(r, 7).SetValue(a.TicketId ?? string.Empty);
            ws.Cell(r, 7).Style.NumberFormat.Format = "@";
            ws.Cell(r, 8).SetValue(a.OrderId);
            ws.Cell(r, 8).Style.NumberFormat.Format = "@";
            ws.Cell(r, 9).Value = a.BuyerName ?? string.Empty;
            ws.Cell(r, 10).Value = a.BuyerEmail ?? string.Empty;
            r++;
        }
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var safe = string.Join("-", monitor.Name.Split(Path.GetInvalidFileNameChars()));
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"tickets-{safe}-{_clock.GetUtcNow():yyyyMMdd}.xlsx");
    }

    /// <summary>
    /// The token → monitor lookup, and the ONLY gate on this page.
    /// </summary>
    /// <remarks>
    /// 🔒 Returns null for unknown, revoked AND expired alike, and the caller answers <b>404</b> for
    /// all three. ⚠️ Deliberately not "this link has expired": distinguishing them would confirm to
    /// a stranger that a token is real, which is the one fact the URL's secrecy is protecting.
    /// </remarks>
    private async Task<AttendeeMonitor?> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var monitor = await _db.AttendeeMonitors
            .FirstOrDefaultAsync(m => m.Token == token, ct);

        if (monitor is null) return null;
        if (monitor.RevokedAt is not null) return null;
        if (monitor.IsExpired(_clock.GetUtcNow())) return null;
        return monitor;
    }
}
