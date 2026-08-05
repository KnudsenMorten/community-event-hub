using System.Globalization;
using System.Text.Json;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §787 — one claimed coupon ticket, as the invoicing sweep needs it.
/// </summary>
/// <param name="Reference">
/// 🔒 The idempotency marker, <c>"{coupon}-{orderId}"</c>, ported EXACTLY from the retired script.
/// Note it is keyed on the ORDER, not the ticket: several tickets bought on one order under one
/// coupon share a reference and therefore land on ONE invoice, with a line each. Changing this to a
/// per-ticket key would re-invoice every multi-ticket order the script ever handled.
/// </param>
/// <param name="TicketClassId">
/// 🔒 §787.16 — the STABLE class identifier. Prices and prepaid pools key on this, never on
/// <paramref name="TicketClassName"/>: the live names carry double spaces and can be renamed in
/// Backstage, which would silently change who is billed what.
/// </param>
/// <param name="IsCancelled">
/// 🔴 §787.20/§794 — the ticket has been cancelled. A cancelled ticket must never be invoiced, and
/// for a PREPAID coupon it releases its allocation back to the partner's pool simply by no longer
/// counting.
/// </param>
public sealed record CouponClaim(
    string Reference,
    string CouponName,
    string OrderId,
    string TicketId,
    string Email,
    string FirstName,
    string LastName,
    string TicketClassName,
    decimal UnitPriceDkk,
    string TicketClassId = "",
    bool IsCancelled = false);

/// <summary>
/// §787 — pulls claimed-coupon tickets out of the raw Zoho Backstage order JSON that CEH already
/// mirrors into <c>Orders.RawJson</c>.
/// </summary>
/// <remarks>
/// <para>🔑 <b>No Zoho call.</b> <c>AttendeeBackstageSyncJob</c> already pulls the v3 <c>/orders</c>
/// feed and stores each order's complete raw JSON. Reading that mirror instead of calling Backstage
/// again matters for a specific reason: §525 — this host once had ~21 jobs each minting their own
/// Zoho token and tripping the refresh-grant rate limit, so every sync failed against a perfectly
/// valid credential. An hourly job that needs no token cannot contribute to that.</para>
///
/// <para>Pure and I/O-free, so the parsing rules below are tested against real Backstage payload
/// shapes rather than discovered in production.</para>
/// </remarks>
public static class CouponClaimExtractor
{
    /// <summary>Zoho reports these ticket prices in DKK. Not configurable — it is what the feed is.</summary>
    public const string SourceCurrency = "DKK";

    /// <summary>Builds the reference the invoice carries. Ported verbatim; see <see cref="CouponClaim"/>.</summary>
    public static string BuildReference(string couponName, string orderId) =>
        $"{Normalize(couponName)}-{orderId}";

    /// <summary>
    /// Every coupon-bearing ticket in one raw Zoho order. Returns empty for an order with no tickets,
    /// no coupon, or unparseable JSON — a malformed order must not stop the sweep for every other.
    /// </summary>
    public static IReadOnlyList<CouponClaim> FromOrderJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return Array.Empty<CouponClaim>();

        JsonElement order;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
            order = doc.RootElement;
        }
        catch (JsonException)
        {
            return Array.Empty<CouponClaim>();
        }

        using (doc)
        {
            if (order.ValueKind != JsonValueKind.Object) return Array.Empty<CouponClaim>();

            var orderId = Str(order, "id") ?? string.Empty;
            if (orderId.Length == 0) return Array.Empty<CouponClaim>();

            // The ORDER-level promo code, used when a ticket does not carry its own. Both shapes
            // occur in the live feed, which is why the script checked both.
            var orderPromo = order.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object
                ? Str(cost, "promo_code")
                : null;

            if (!order.TryGetProperty("tickets", out var tickets)
                || tickets.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CouponClaim>();
            }

            var claims = new List<CouponClaim>();

            foreach (var ticket in tickets.EnumerateArray())
            {
                if (ticket.ValueKind != JsonValueKind.Object) continue;

                // Ticket-level promo code wins; the order-level one is the fallback.
                //
                // ⚠️ A BLANK ticket-level code must fall back too, not just a MISSING one. The
                // retired script's test was `if ($ticket.promo_code)`, and in PowerShell that is
                // FALSE for an empty string — so a ticket carrying "" used the order's code. A C#
                // `??` only falls back on null, which would have silently dropped those tickets
                // from the invoice. Caught by a test built from a realistic payload, not in
                // production.
                var ticketPromo = Normalize(Str(ticket, "promo_code"));
                var promo = ticketPromo.Length > 0 ? ticketPromo : Normalize(orderPromo);
                if (promo.Length == 0) continue;   // not a coupon ticket — not our business

                var contact = ticket.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.Object
                    ? c : default;

                claims.Add(new CouponClaim(
                    Reference: BuildReference(promo, orderId),
                    CouponName: promo,
                    OrderId: orderId,
                    TicketId: Str(ticket, "id") ?? string.Empty,
                    Email: Normalize(contact.ValueKind == JsonValueKind.Object ? Str(contact, "email") : null),
                    FirstName: Normalize(contact.ValueKind == JsonValueKind.Object ? Str(contact, "first_name") : null),
                    LastName: Normalize(contact.ValueKind == JsonValueKind.Object ? Str(contact, "last_name") : null),
                    TicketClassName: Normalize(Str(ticket, "ticket_name")),
                    // base_price, NOT total: `total` is what the buyer paid AFTER the coupon, which
                    // for a fully-covered ticket is 0 — and invoicing the partner 0.00 is the exact
                    // failure this whole job exists to prevent.
                    //
                    // ✅ §787.19 — MEASURED: base_price survives a 100% discount intact. His own
                    // cancelled order shows base_price 3500 with discount 3500 and total 0, so the
                    // real cost is still readable even when the coupon covered the ticket entirely.
                    UnitPriceDkk: Money(ticket, "base_price"),
                    TicketClassId: Normalize(Str(ticket, "ticket_class_id")),
                    // 🔴 §787.20 — the status the whole sweep ignored. Both coupon tickets in the
                    // live mirror are "cancelled", so a run reported 5500 DKK of tickets that no
                    // longer exist; with dry run off it would have invoiced a partner for them.
                    IsCancelled: string.Equals(
                        Str(ticket, "status_string"), "cancelled", StringComparison.OrdinalIgnoreCase)));
            }

            return claims;
        }
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// ⚠️ Zoho returns money as a number on some fields and a quoted string on others. Both are
    /// accepted, and parsed with the INVARIANT culture — "1234.50" read under a Danish culture
    /// becomes 123450, which would invoice a partner a hundred times over.
    /// </summary>
    private static decimal Money(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0m;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(
                v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }
}
