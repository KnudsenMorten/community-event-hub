namespace CommunityHub.Core.Domain;

/// <summary>§1382 — how a coupon sign-up is billed: paid up front, or invoiced as tickets are claimed.</summary>
public enum CouponSignupOrderType
{
    /// <summary>Prepaid — the company buys the tickets up front (a prepaid pool on /Organizer/CouponInvoicing).</summary>
    Prepaid = 1,

    /// <summary>Ad-hoc billing — the company is invoiced for each ticket that is actually claimed.</summary>
    AdHoc = 2,
}

/// <summary>§1382 — which ticket the coupon is for.</summary>
public enum CouponSignupTicketType
{
    OneDay = 1,
    TwoDay = 2,
}

/// <summary>
/// §1382 — one request from the PUBLIC coupon sign-up form (<c>/coupon-signup</c>): a company asking for a ticket
/// coupon, with everything the organizers need to set it up and invoice it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"a public coupon onboarding form with a captcha where a customer signs up for a ticket
/// coupon"</i> — on submit CEH creates the coupon, finds or creates the customer in e-conomic (customer group 2) and mails
/// the request to the event mailbox.</para>
///
/// <para>🔑 <b>Why a row of its own and not only the coupon.</b> The coupon (<see cref="CouponInvoicingSetting"/>) holds
/// the billing RULE. The request holds what the customer TYPED — VAT number, address, invoice e-mail or EAN, purchase
/// order, the requester's mobile — none of which the coupon has a column for, and all of which an organizer may need to
/// look up when the customer calls. It is also the rate-limit ledger for the anonymous form (<see cref="RequestIp"/>).</para>
///
/// <para>🔴 <b>The promo code itself lives in Zoho Backstage, which has no coupon API (§787.14).</b> CEH creates its own
/// coupon row with a PROPOSED code; an organizer still creates that code in Backstage by hand and confirms it on
/// /Organizer/CouponInvoicing (§1095). Nothing here writes to Backstage.</para>
/// </remarks>
public class CouponSignupRequest
{
    public int Id { get; set; }

    /// <summary>The edition the request was made for (the active one at the time).</summary>
    public int EventId { get; set; }

    public CouponSignupOrderType OrderType { get; set; }
    public CouponSignupTicketType TicketType { get; set; }
    public int TicketCount { get; set; }

    /// <summary>The VAT number as normalised by the form: upper-case, no spaces, with its country prefix (DK12345678).</summary>
    public string VatNumber { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;

    /// <summary>ISO 3166 two-letter code.</summary>
    public string CountryIso2 { get; set; } = string.Empty;

    public string RequesterName { get; set; } = string.Empty;
    public string RequesterEmail { get; set; } = string.Empty;
    public string RequesterMobile { get; set; } = string.Empty;

    /// <summary>Invoice by e-mail — exactly one of this and <see cref="EanNumber"/> is set.</summary>
    public string? InvoiceEmail { get; set; }

    /// <summary>Invoice by EAN (13 digits, the Danish public-sector e-invoice address) — exactly one of this and
    /// <see cref="InvoiceEmail"/> is set.</summary>
    public string? EanNumber { get; set; }

    /// <summary>
    /// The ATTENDEE's share of the ticket price in percent: 0 = the company pays 100 %, 50 = the company pays 50 % and the
    /// attendee pays the other 50 %. ⚠️ The coupon stores the COMPANY's share (<see cref="CouponInvoicingSetting.InvoicedSharePercent"/>)
    /// — the opposite reading — so the service converts it.
    /// </summary>
    public int SplitPercent { get; set; }

    /// <summary>Optional purchase-order text to quote on the invoice.</summary>
    public string? PurchaseOrder { get; set; }

    /// <summary>The e-conomic customer the request was matched to (by VAT number) or created as. Null = not resolved.</summary>
    public int? ErpCustomerNumber { get; set; }

    /// <summary>True when CEH CREATED the e-conomic customer for this request (false = an existing customer was found).</summary>
    public bool ErpCustomerCreated { get; set; }

    /// <summary>Why the e-conomic customer was NOT resolved — shown in red in the mail. Null when it was.</summary>
    public string? ErpError { get; set; }

    /// <summary>The CEH coupon row created for this request.</summary>
    public int? CouponInvoicingSettingId { get; set; }

    /// <summary>When the request mail reached the mail sender. Null = it failed (the row is still the record).</summary>
    public DateTimeOffset? MailSentAt { get; set; }

    /// <summary>The caller's IP — the anonymous form's rate limit counts on it.</summary>
    public string? RequestIp { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
