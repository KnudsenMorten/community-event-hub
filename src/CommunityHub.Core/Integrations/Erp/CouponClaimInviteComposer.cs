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
        string? RecipientFirstName = null,
        /// <summary>
        /// §1093 — the partner's own read-only usage page, or null when they have no monitor link.
        /// </summary>
        /// <remarks>
        /// 🔑 This mail is the ONE thing CEH sends the partner directly, so it is the only place the
        /// link can reach them without an organizer copying a URL by hand. Everything else in the
        /// coupon area is addressed to <c>info@</c>.
        /// </remarks>
        string? MonitorUrl = null,
        // ── §1117: the agreement, stated in the mail that starts it ─────────────────────────
        /// <summary>
        /// 🔴 §1117 — WHICH KIND OF AGREEMENT this is, in the customer's words.
        /// </summary>
        /// <remarks>
        /// <para>Operator 2026-08-21: *"the claim mail must include: type of ticket (prepaid, adhoc
        /// billing, free). Amount, cap (if any), billing frequency"*.</para>
        ///
        /// <para>🔑 <b><see cref="IsPrepaid"/> could not carry this.</b> A bool has two states and
        /// there are three kinds — prepaid, ad-hoc, and FREE — so "not prepaid" was silently
        /// rendering a free allocation as one that gets invoiced as tickets are claimed. Passing the
        /// real <see cref="CouponBillingType"/> makes the third case expressible instead of
        /// mis-stated. <c>IsPrepaid</c> stays for the existing layout branches, and
        /// <see cref="From"/> keeps the two in step.</para>
        /// </remarks>
        Domain.CouponBillingType? BillingType = null,
        /// <summary>§1117 — the agreed price per ticket (DKK, ex VAT). Null ⇒ the line is omitted.</summary>
        /// <remarks>
        /// 🔒 Omitted rather than guessed. Quoting a price we are not sure of, in the mail that opens
        /// a paying relationship, is the one error that costs more than saying nothing.
        /// </remarks>
        decimal? AgreedUnitPriceDkk = null,
        /// <summary>§1117 — the agreed ticket ceiling, when they asked for one. Null ⇒ uncapped.</summary>
        int? CapTickets = null,
        /// <summary>§1117 — what share of each ticket is invoiced to them (1–100). Null ⇒ all of it.</summary>
        int? InvoicedSharePercent = null);

    /// <summary>Build the subject + HTML body for one invite.</summary>
    public static (string Subject, string Html) Build(Invite i)
    {
        ArgumentNullException.ThrowIfNull(i);
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        // §1105 (operator 2026-08-20: *"make more room in the email between the paragraphs. very
        // cramped"*). 🔑 Line-height as well as margin: the gap between paragraphs only reads as a
        // break when it is clearly bigger than the gap between LINES, and at 16px/1.2 the two were
        // nearly equal, so a wrapped sentence looked like a new paragraph.
        static string P(string inner) =>
            $"<p style=\"margin:0 0 22px;line-height:1.6;\">{inner}</p>";

        var subject = string.Format(CultureInfo.InvariantCulture, SubjectTemplate, i.EventDisplayName);
        var url = ClaimUrl(i.TicketBaseUrl, i.CouponName);
        var link = $"<a href=\"{Enc(url)}\">{Enc(url)}</a>";
        var sb = new System.Text.StringBuilder();

        sb.Append(P($"Thank You again for your support for <strong>{Enc(i.EventDisplayName)}</strong>"));

        // --- Ticket claiming -------------------------------------------------------------
        sb.Append(P("<strong>Ticket Claiming:</strong>"));
        // 🔑 §1105 (operator 2026-08-20: *"make important things in bold in the emails"* — *"amount,
        // ticket class, event name"*). The facts a partner scans for are the quantity, which ticket
        // it is and which event; in flat prose they sit at the same weight as "You can now use the
        // below registration URL", so the reader has to parse a sentence to find a number.
        sb.Append(P(i.IsPrepaid && i.Quantity is { } qty
            // His wording, including "pre-paid claims" — a prepaid partner has already bought a
            // countable allocation, and the number is the thing they will check first.
            ? $"You can now use the below registration URL to start claiming the <strong>{qty}</strong> "
              + $"pre-paid claims for <strong>{Enc(HumanLabel.TicketClassText(i.TicketClassLabel))}</strong> for "
              + $"<strong>{Enc(i.EventDisplayName)}</strong>"
            // Ad-hoc has no number, because nothing has been bought yet — that is the difference
            // between the two agreements, and it is why the two mails exist.
            : $"You can now use the below registration URL to start claiming tickets for "
              + $"<strong>{Enc(HumanLabel.TicketClassText(i.TicketClassLabel))}</strong> for "
              + $"<strong>{Enc(i.EventDisplayName)}</strong>"));
        sb.Append(P(link));

        // --- §1117: Your agreement -------------------------------------------------------
        // 🔴 Operator 2026-08-21: *"the claim mail must include: type of ticket (prepaid, adhoc
        // billing, free). Amount, cap (if any), billing frequency"*.
        //
        // 🔑 <b>Why a TABLE and not more prose.</b> These four facts are what a customer's finance
        // person checks against their own record, and they are checked one at a time. Threaded
        // through sentences they were partly present and wholly unfindable: the cadence lived in the
        // invoice paragraph, the quantity in the claiming paragraph, and the price and the cap
        // nowhere at all — so the mail that OPENS the agreement never stated it.
        //
        // 🔒 Every row is DROPPED when its value is unknown, never rendered blank or guessed. A
        // price quoted wrongly in the first mail of a paying relationship costs more than a price
        // not quoted at all.
        var agreement = new List<(string Label, string Value)>();

        var kind = AgreementKind(i);
        if (kind is not null) agreement.Add(("Agreement", kind));

        if (i.IsPrepaid && i.Quantity is { } bought)
            agreement.Add(("Tickets bought", $"<strong>{bought}</strong>"));

        if (i.AgreedUnitPriceDkk is { } price and > 0m)
        {
            var per = $"<strong>DKK {Money(price)}</strong> per ticket (ex VAT)";
            // §1091 — a part-payment share is stated HERE too. A partner who agreed to 50% and reads
            // only the unit price will query the first invoice.
            if (i.InvoicedSharePercent is { } pct and > 0 and < 100)
                per += $", of which we invoice you <strong>{pct}%</strong>";
            agreement.Add(("Price", per));
        }

        // ⚠️ The cap is the customer's OWN ceiling, so it is described as theirs — it is the number
        // they asked for and the number they can raise, not a limit we imposed.
        if (i.CapTickets is { } cap and > 0)
            agreement.Add(("Your limit", $"<strong>{cap}</strong> ticket(s) — you can raise it at any time"));

        // Free coupons are never invoiced, so a cadence would be a promise to bill nobody.
        if (kind is not null && i.BillingType != Domain.CouponBillingType.NoInvoicing)
        {
            agreement.Add(("Invoicing", i.IsPrepaid
                // §1108 — a prepaid pool is paid for up front; "every 14 days" made no sense on it.
                ? "Paid up front — nothing further is invoiced as tickets are claimed"
                : $"<strong>{Cadence(i.IntervalDays)}</strong>, for the tickets claimed in that period"));
        }

        if (agreement.Count > 0)
        {
            sb.Append(P("<strong>Your agreement:</strong>"));
            sb.Append(
                "<table style=\"border-collapse:collapse;font-size:15px;margin:0 0 22px;\">"
                + string.Concat(agreement.Select(r =>
                    "<tr>"
                    + "<td style=\"padding:6px 16px 6px 0;color:#6b7280;vertical-align:top;\">"
                    + $"{Enc(r.Label)}</td>"
                    + $"<td style=\"padding:6px 0;\">{r.Value}</td>"
                    + "</tr>"))
                + "</table>");
        }

        // --- Extend (prepaid only) -------------------------------------------------------
        if (i.IsPrepaid && i.Quantity is { } q)
        {
            sb.Append(P("<strong>Extend with more tickets</strong>"));
            sb.Append(P(
                $"If needed, we can easily extend the <strong>{q}</strong> ticket to more later under "
                + "the same &lsquo;early bird&rsquo; terms "
                + $"(<strong>DKK {Money(i.ExtendPriceDkk)}/EURO{Money(i.ExtendPriceEur)}</strong>)"));
        }

        // --- Invoice ---------------------------------------------------------------------
        // 🔴 §1117 — A FREE COUPON GETS NO INVOICE PARAGRAPH AT ALL.
        //
        // ⚰️ This section keyed off `IsPrepaid` alone, so "not prepaid" fell through to *"We will
        // invoice you every 2 weeks when a ticket is claimed"* — told to a partner whose tickets are
        // free. The bool has two states and there are three kinds of agreement; the third one was
        // being described as the second. Caught by the test written for the §1117 summary block,
        // which is the only reason it was found before the next free coupon went out.
        var isFree = i.BillingType == Domain.CouponBillingType.NoInvoicing;
        var isUnmapped = i.BillingType == Domain.CouponBillingType.Unmapped;

        if (isFree)
        {
            sb.Append(P("<strong>Invoice:</strong>"));
            sb.Append(P("These tickets are <strong>free of charge</strong> — there is nothing to pay "
                        + "and you will not receive an invoice for them."));
        }
        else if (isUnmapped)
        {
            // No agreed billing exists yet. Saying nothing is right: the alternative is inventing a
            // commercial term, and the coupon still works meanwhile.
        }
        else if (i.IsPrepaid)
        {
            sb.Append(P("<strong>Invoice:</strong>"));
            // 🔒 The invoice paragraph is DROPPED rather than faked when there is no number. His
            // draft reads "I have sent invoice #170 to you today", and saying that with a blank —
            // or with a provisional draft number (§1013a) — sends a partner looking for a document
            // that does not exist under that number.
            if (!string.IsNullOrWhiteSpace(i.InvoiceNumber))
            {
                sb.Append(P(
                    $"I have sent invoice <strong>#{Enc(i.InvoiceNumber)}</strong> to you today"
                    + (string.IsNullOrWhiteSpace(i.Reference)
                        ? string.Empty
                        : $" with reference: <strong>{Enc(i.Reference)}</strong>")));
            }
        }
        else
        {
            sb.Append(P("<strong>Invoice:</strong>"));
            // §1016d — states the REAL cadence, from this coupon's own billing period. His draft
            // said "bi-weekly" while the job invoiced within minutes of every claim; the batching
            // now makes the sentence true, so it reads from the same number that enforces it.
            sb.Append(P(
                $"We will invoice you <strong>{Cadence(i.IntervalDays)}</strong> when a ticket is claimed."
                + (string.IsNullOrWhiteSpace(i.Reference)
                    ? string.Empty
                    : $" We will add the reference {Enc(i.Reference)} to the invoice")));
        }

        // --- Follow the usage (§1093) ----------------------------------------------------
        // 🔑 Placed AFTER the invoice paragraph on purpose: this link is how the partner checks the
        // invoice they have just been told about. Before it, it reads as a curiosity; after it, it
        // is the answer to "how do I know what you are billing me for?".
        if (!string.IsNullOrWhiteSpace(i.MonitorUrl))
        {
            var monitor = Enc(i.MonitorUrl!);
            sb.Append(P("<strong>Follow who has signed up:</strong>"));
            sb.Append(P(
                "You can see who has used your code at any time — no login needed, and the page "
                + "updates by itself as tickets are claimed. It covers <em>every</em> code we have "
                + "issued you, including any we add later."));
            sb.Append(P($"<a href=\"{monitor}\">{monitor}</a>"));
            // ⚠️ Said plainly because the page shows other people's names and e-mail addresses. A
            // partner forwarding this link should know what they are forwarding.
            sb.Append(P(
                "<span style=\"color:#6b7280;font-size:13px;\">Please treat this link as "
                + "confidential — anyone who has it can see the names and e-mail addresses of the "
                + "people who used your code.</span>"));
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
    /// <remarks>
    /// 🔴 §1105 — <b>"bi-weekly" is gone.</b> In English it means BOTH "twice a week" and "every two
    /// weeks", and this sentence tells a customer how often we will invoice them. Ambiguity about
    /// billing frequency, in a mail to a partner whose first language is not English, is the kind of
    /// thing that produces a reply rather than an understanding. Every arm now says plainly how
    /// often, in words that cannot be read two ways (operator 2026-08-20: *"we dont understand this
    /// wording"*).
    /// </remarks>
    internal static string Cadence(int days) => days switch
    {
        <= 0 => "for each claim",
        1 => "every day",
        7 => "every week",
        14 => "every 2 weeks",
        30 or 31 => "every month",
        _ => $"every {days.ToString(CultureInfo.InvariantCulture)} days",
    };

    /// <summary>
    /// §1117 — the agreement in the customer's words, or null when we do not know which it is.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>Null rather than a guess.</b> An unmapped coupon has no agreed billing yet; saying
    /// "billed as tickets are claimed" about one would be inventing a commercial term in the mail
    /// that opens the relationship. The whole block is then omitted and the mail reads exactly as it
    /// did before §1117.</para>
    ///
    /// <para>⚠️ Falls back to <see cref="Invite.IsPrepaid"/> when no type is supplied, so every
    /// existing caller keeps its old behaviour — but a caller passing the type gets the FREE case,
    /// which a bool cannot express.</para>
    /// </remarks>
    internal static string? AgreementKind(Invite i) => i.BillingType switch
    {
        Domain.CouponBillingType.AllocatedPrepaymentByCustomer =>
            "Prepaid — the tickets are bought and held for you",
        Domain.CouponBillingType.ClaimableAdHocPaymentByCustomer =>
            "Billed as used — you pay only for the tickets that are actually claimed",
        Domain.CouponBillingType.NoInvoicing =>
            "Free — these tickets are included, and you are not invoiced for them",
        // Unmapped: no agreed billing exists yet, so the mail says nothing about it.
        Domain.CouponBillingType.Unmapped => null,
        // No type supplied — the pre-§1117 callers. The bool still distinguishes the two BILLED
        // kinds correctly; it simply cannot see the free one, which is why the field was added.
        null => i.IsPrepaid
            ? "Prepaid — the tickets are bought and held for you"
            : "Billed as used — you pay only for the tickets that are actually claimed",
        _ => null,
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
        int? prepaidQuantity = null, string? invoiceNumber = null,
        // §1093 — the partner's usage link, when their billing customer has one.
        string? monitorUrl = null) =>
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
            ExtendPriceEur: extendEur,
            MonitorUrl: monitorUrl,
            // 🔴 §1117 — read straight off the rule, so the mail states the agreement that is
            // actually stored rather than one anybody re-typed. Both callers go through here, which
            // is why the new fields are filled HERE and not at each call site: a caller that forgot
            // one would produce a mail that is silently missing a commercial term.
            BillingType: rule.BillingType,
            AgreedUnitPriceDkk: rule.AgreedUnitPriceDkk,
            CapTickets: rule.ClaimCapTickets,
            InvoicedSharePercent: rule.InvoicedSharePercent);
}
