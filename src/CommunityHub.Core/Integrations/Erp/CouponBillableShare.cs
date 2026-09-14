namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §1091 — what the LINKED COMPANY is invoiced for one claimed ticket, in DKK.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19: *"lets support an agreed price. if not set, then it takes the actual
/// price for the ticket from z if not filled out"* and *"i need to be able to add a percentage like
/// 50% or 100% with clear notice that this is the percentage the linked company will be invoiced
/// of"*.</para>
///
/// <para>🔑 <b>Two inputs, one answer, in one place.</b> The base is the agreed price when the coupon
/// carries one and the price the ticket actually sold for when it does not; the share is a
/// percentage of that base. This lives apart from the composer because it is the only arithmetic in
/// the coupon path that decides how much money a partner is asked for — the sort of thing that
/// should be readable and testable without an invoice, an HTTP client or an FX rate anywhere near
/// it.</para>
///
/// <para>🔴 <b>Both inputs are nullable and null is NEVER zero.</b> Every ad-hoc coupon that exists
/// today predates both fields. A missing percentage means 100 (bill the whole thing, as always) and
/// a missing agreed price means "use what the ticket cost". Reading either as 0 would invoice a
/// partner nothing while reporting success — the failure mode that is hardest to notice, because
/// nobody complains about not being billed.</para>
///
/// <para>⚠️ <b>DKK in, DKK out — this runs BEFORE any FX conversion.</b> The agreed price is agreed
/// in DKK, so the share must be taken in DKK and converted afterwards. Taking a percentage of an
/// already-converted figure would compound the conversion's rounding into the share.</para>
/// </remarks>
public static class CouponBillableShare
{
    /// <summary>The share billed when a coupon does not say otherwise: the whole ticket.</summary>
    public const int DefaultPercent = 100;

    /// <summary>
    /// The DKK amount to invoice the linked company for one claimed ticket.
    /// </summary>
    /// <param name="actualTicketPriceDkk">
    /// What the ticket actually sold for — Zoho's <c>base_price</c>, the LIST price rather than the
    /// discounted total (§788: a fully-covered ticket has a total of 0, which is why the list price
    /// is the one carried through).
    /// </param>
    /// <param name="agreedUnitPriceDkk">The coupon's agreed price per ticket, or null.</param>
    /// <param name="invoicedSharePercent">The company's share in percent, or null for 100.</param>
    public static decimal Dkk(
        decimal actualTicketPriceDkk,
        decimal? agreedUnitPriceDkk,
        int? invoicedSharePercent)
    {
        var basis = Basis(actualTicketPriceDkk, agreedUnitPriceDkk);
        var percent = Percent(invoicedSharePercent);

        if (percent == DefaultPercent) return basis;

        // Round at the END, to 2 decimals, matching what the composer does with the converted value.
        return Math.Round(basis * percent / 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The price the share is taken of: the agreed price when set, otherwise what the ticket cost.
    /// </summary>
    /// <remarks>
    /// 🔒 A NEGATIVE or ZERO agreed price is ignored rather than honoured. This is a number typed
    /// into a form; 0 would silently zero every invoice for the coupon, and a partner who is never
    /// billed raises no complaint to catch it by. Same defensive read the page already uses for
    /// <c>InvoiceIntervalDays</c>.
    /// </remarks>
    public static decimal Basis(decimal actualTicketPriceDkk, decimal? agreedUnitPriceDkk) =>
        agreedUnitPriceDkk is { } agreed && agreed > 0m ? agreed : actualTicketPriceDkk;

    /// <summary>
    /// The company's share in percent — null, out-of-range or nonsense falls back to 100.
    /// </summary>
    /// <remarks>
    /// ⚠️ Clamped rather than rejected here, because this runs inside the invoicing sweep where
    /// throwing would stop every OTHER coupon in the same pass. The FORM is where a bad percentage
    /// is refused and explained; this is the backstop, and its answer is the safe one — bill the
    /// whole ticket, which is both the old behaviour and the amount nobody loses money on.
    /// </remarks>
    public static int Percent(int? invoicedSharePercent) =>
        invoicedSharePercent is { } p && p is > 0 and <= 100 ? p : DefaultPercent;

    /// <summary>
    /// §1091 — the human sentence for the invoice line, or null at a plain 100% of the ticket price
    /// (where there is nothing to explain and a note would just be noise on every line).
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The partner's invoice has to say why it is not the ticket price.</b> A line reading
    /// "DKK 1,500.00" against a DKK 3,000 ticket is the kind of thing that starts an e-mail thread;
    /// "50% of DKK 3,000.00 (agreed price)" ends it before it starts. Mirrors the webshop's
    /// <c>ComposeDiscountNote</c> precedent.
    /// </remarks>
    public static string? ComposeShareNote(
        decimal actualTicketPriceDkk,
        decimal? agreedUnitPriceDkk,
        int? invoicedSharePercent)
    {
        var percent = Percent(invoicedSharePercent);
        var hasAgreed = agreedUnitPriceDkk is { } a && a > 0m;
        if (percent == DefaultPercent && !hasAgreed) return null;

        var basis = Basis(actualTicketPriceDkk, agreedUnitPriceDkk);
        var label = hasAgreed ? "agreed price" : "ticket price";

        // 🔒 INVARIANT CULTURE, like every other money string on these invoices
        // (`WebshopInvoiceLineComposer.ComposeDiscountNote`). Plain interpolation takes the SERVER's
        // culture, which on a Danish host renders 3000,00 — so the same coupon would print a comma
        // or a point on a partner's invoice depending on where the job happened to run. Caught by a
        // test only because this machine is da-DK; on an invariant CI box it would have shipped.
        var money = basis.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        return percent == DefaultPercent
            ? $"Invoiced: 100% of {money} DKK ({label})"
            : $"Invoiced: {percent}% of {money} DKK ({label})";
    }
}
