using System.Globalization;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// 🔴 §1016c — the mail that tells a PARTNER their promo code is live and they can start claiming.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"can you also make a button which will notify the requester, that he
/// can now use the coupon code — i have prepared 2 sample mails, one for each scenario"*. His copy
/// is the SPEC and is reproduced as written; it is not improved, tightened or re-voiced. He wrote it
/// to a partner he has a relationship with, and the register is part of the message.</para>
///
/// <para>🔑 <b>This is the FIRST outward-facing coupon mail.</b> Every other one — the promo-code
/// reminder, the draft-invoice notice, the unmapped-coupon chase — goes to <c>info@</c> or
/// <c>mok@</c> and tells an ORGANIZER to do something. Nothing ever reached the person actually
/// waiting to claim, so after the pool was recorded, the invoice raised and the code created, the
/// partner was told by hand or not at all.</para>
///
/// <para>🔒 <b>Pure and static on purpose.</b> A mail that quotes a real invoice number and a live
/// claim URL to a paying customer must be assertable without sending anything, so every branch is
/// exercised by test rather than by a send.</para>
/// </remarks>
public static class CouponClaimInviteComposer
{
    /// <summary>His subject, verbatim for both variants.</summary>
    public const string SubjectTemplate = "Coupon-link for {0} Ticket claim";

    /// <summary>
    /// The claim URL the partner follows. ⚠️ The <c>#</c> matters: Backstage's buy flow is a
    /// fragment route, so the promo code rides INSIDE the fragment and never reaches a server.
    /// </summary>
    public static string ClaimUrl(string ticketBaseUrl, string couponName) =>
        $"{(ticketBaseUrl ?? string.Empty).TrimEnd('/')}#/buyTickets?promoCode={Uri.EscapeDataString(couponName ?? string.Empty)}";

    /// <summary>Everything the mail needs, so the composer never reaches for a database.</summary>
    /// <param name="IsPrepaid">Chooses the variant: prepaid (a pool was bought) vs ad-hoc (billed as claimed).</param>
    /// <param name="Quantity">Prepaid only — how many tickets the pool holds.</param>
    /// <param name="InvoiceNumber">Prepaid only — the invoice already sent. Null ⇒ the invoice paragraph is omitted.</param>
    /// <param name="Reference">The coupon's Notes, which is also the invoice's sub-heading, so both say the same thing.</param>
    /// <param name="IntervalDays">Ad-hoc only — the agreed billing period, so the mail states the real cadence (§1016d).</param>
    public sealed record Invite(
        string EventDisplayName,
        string CouponName,
        string TicketClassLabel,
        string TicketBaseUrl,
        bool IsPrepaid,
        int? Quantity = null,
        string? InvoiceNumber = null,
        string? Reference = null,
        int IntervalDays = 14,
        decimal ExtendPriceDkk = 3000m,
        decimal ExtendPriceEur = 390m,
        string? RecipientFirstName = null);

    /// <summary>Build the subject + HTML body for one invite.</summary>
    public static (string Subject, string Html) Build(Invite i)
    {
        ArgumentNullException.ThrowIfNull(i);
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        static string P(string inner) => $"<p style=\"margin:0 0 16px;\">{inner}</p>";

        var subject = string.Format(CultureInfo.InvariantCulture, SubjectTemplate, i.EventDisplayName);
        var url = ClaimUrl(i.TicketBaseUrl, i.CouponName);
        var link = $"<a href=\"{Enc(url)}\">{Enc(url)}</a>";
        var sb = new System.Text.StringBuilder();

        sb.Append(P($"Thank You again for your support for {Enc(i.EventDisplayName)}"));

        // --- Ticket claiming -------------------------------------------------------------
        sb.Append(P("<strong>Ticket Claiming:</strong>"));
        sb.Append(P(i.IsPrepaid && i.Quantity is { } qty
            // His wording, including "pre-paid claims" — a prepaid partner has already bought a
            // countable allocation, and the number is the thing they will check first.
            ? $"You can now use the below registration URL to start claiming the {qty} pre-paid "
              + $"claims for {Enc(i.TicketClassLabel)} for {Enc(i.EventDisplayName)}"
            // Ad-hoc has no number, because nothing has been bought yet — that is the difference
            // between the two agreements, and it is why the two mails exist.
            : $"You can now use the below registration URL to start claiming tickets for "
              + $"{Enc(i.TicketClassLabel)} for {Enc(i.EventDisplayName)}"));
        sb.Append(P(link));

        // --- Extend (prepaid only) -------------------------------------------------------
        if (i.IsPrepaid && i.Quantity is { } q)
        {
            sb.Append(P("<strong>Extend with more tickets</strong>"));
            sb.Append(P(
                $"If needed, we can easily extend the {q} ticket to more later under the same "
                + $"&lsquo;early bird&rsquo; terms (DKK {Money(i.ExtendPriceDkk)}/EURO{Money(i.ExtendPriceEur)})"));
        }

        // --- Invoice ---------------------------------------------------------------------
        sb.Append(P("<strong>Invoice:</strong>"));
        if (i.IsPrepaid)
        {
            // 🔒 The invoice paragraph is DROPPED rather than faked when there is no number. His
            // draft reads "I have sent invoice #170 to you today", and saying that with a blank —
            // or with a provisional draft number (§1013a) — sends a partner looking for a document
            // that does not exist under that number.
            if (!string.IsNullOrWhiteSpace(i.InvoiceNumber))
            {
                sb.Append(P(
                    $"I have sent invoice #{Enc(i.InvoiceNumber)} to you today"
                    + (string.IsNullOrWhiteSpace(i.Reference)
                        ? string.Empty
                        : $" with reference: {Enc(i.Reference)}")));
            }
        }
        else
        {
            // §1016d — states the REAL cadence, from this coupon's own billing period. His draft
            // said "bi-weekly" while the job invoiced within minutes of every claim; the batching
            // now makes the sentence true, so it reads from the same number that enforces it.
            sb.Append(P(
                $"We will invoice you {Cadence(i.IntervalDays)} when a ticket is claimed."
                + (string.IsNullOrWhiteSpace(i.Reference)
                    ? string.Empty
                    : $" We will add the reference {Enc(i.Reference)} to the invoice")));
        }

        sb.Append(P("If you have any questions, please don't hesitate to reply back to this mail."));
        sb.Append(P("Best regards,<br/>Experts Live Denmark organizer-team"));
        return (subject, sb.ToString());
    }

    /// <summary>
    /// §1016d — the billing period in the words a customer uses. 🔑 Derived from the coupon's OWN
    /// interval so the promise and the mechanism can never drift: change the period, the sentence
    /// changes with it.
    /// </summary>
    internal static string Cadence(int days) => days switch
    {
        <= 0 => "for each claim",
        7 => "weekly",
        14 => "bi-weekly",
        30 or 31 => "monthly",
        1 => "daily",
        _ => $"every {days.ToString(CultureInfo.InvariantCulture)} days",
    };

    /// <summary>Whole kroner when it is whole, so "DKK 3000" never prints as "DKK 3000.00".</summary>
    private static string Money(decimal v) =>
        v == decimal.Truncate(v)
            ? decimal.Truncate(v).ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Build an <see cref="Invite"/> from the stored rows, so the page and the tests agree on where
    /// every value comes from.
    /// </summary>
    public static Invite From(
        CouponInvoicingSetting rule, string eventDisplayName, string ticketClassLabel,
        string ticketBaseUrl, int defaultIntervalDays,
        decimal extendDkk, decimal extendEur,
        int? prepaidQuantity = null, string? invoiceNumber = null) =>
        new(
            EventDisplayName: eventDisplayName,
            CouponName: rule.CouponName,
            TicketClassLabel: ticketClassLabel,
            TicketBaseUrl: ticketBaseUrl,
            IsPrepaid: rule.IsPrepaid,
            Quantity: prepaidQuantity,
            InvoiceNumber: invoiceNumber,
            // The COUPON's Notes (operator's choice 2026-08-09) — already the invoice sub-heading,
            // so the partner reads the same words in the mail and on the document.
            Reference: rule.Notes,
            IntervalDays: rule.InvoiceIntervalDays is { } d && d >= 0 ? d : defaultIntervalDays,
            ExtendPriceDkk: extendDkk,
            ExtendPriceEur: extendEur);
}
