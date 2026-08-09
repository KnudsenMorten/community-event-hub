namespace CommunityHub.Core.Domain;

/// <summary>
/// §787 — how a Zoho Backstage coupon is billed. The three values are the operator's own, carried
/// over verbatim from the retired script's settings file so the words on the page are the words he
/// already uses.
/// </summary>
public enum CouponBillingType
{
    /// <summary>
    /// 🔒 THE DEFAULT, AND IT IS DELIBERATELY THE ONE THAT DOES NOTHING. A coupon CEH has seen
    /// claimed but which nobody has mapped yet. It is never invoiced and never guessed at; it is
    /// surfaced on <c>/Organizer/CouponInvoicing</c> as needing attention.
    ///
    /// <para>⚠️ This value does not exist in the retired CSV — the script simply had no row. Making
    /// "unmapped" an explicit, STORABLE state is the whole reason the mapping moved into a table: a
    /// missing row cannot be shown, chased, or acted on, and a claimed-but-unmapped coupon is
    /// exactly the case the operator needs to see.</para>
    /// </summary>
    Unmapped = 0,

    /// <summary>
    /// The customer claims an amount, uses plus-addressing and SECURES the ticket (allocated).
    /// Invoiced to <see cref="CouponInvoicingSetting.ErpCustomerNumber"/>.
    /// </summary>
    AllocatedPrepaymentByCustomer = 1,

    /// <summary>
    /// The code can be claimed by anyone who has it, first come first served — the ticket is NOT
    /// allocated. The partner pays if it IS claimed, so billing is ad hoc. Invoiced to
    /// <see cref="CouponInvoicingSetting.ErpCustomerNumber"/>.
    /// </summary>
    ClaimableAdHocPaymentByCustomer = 2,

    /// <summary>Free / included. NEVER invoiced, and not a gap — a deliberate decision that must be
    /// distinguishable from <see cref="Unmapped"/>.</summary>
    NoInvoicing = 3,
}

/// <summary>
/// §787.17 — how OFTEN a partner is invoiced. Their claims are bundled into one invoice per window.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"i must also define cadence for frequency to bill pr customer"* …
/// *"so it can bundle so partner get invoice every week,day,month"* … *"individually agreed"*.</para>
///
/// <para>🔒 A claim belongs to the period it was CLAIMED in, and a period is invoiced only once it
/// has CLOSED (§787.17c) — see <see cref="Integrations.Erp.CouponBillingWindow"/>.</para>
/// </remarks>
public enum CouponBillingCadence
{
    /// <summary>
    /// 🔒 THE DEFAULT, AND IT IS TODAY'S BEHAVIOUR. One invoice per claimed order, due immediately,
    /// keyed on the order-based reference CEH already writes.
    ///
    /// <para>⚠️ Default on purpose: a partner nobody has agreed a cadence with must keep billing the
    /// way they do now, rather than silently having their tickets held in a bundle nobody
    /// configured.</para>
    /// </summary>
    PerClaim = 0,

    /// <summary>One invoice per calendar day of claims, sent once the day has ended.</summary>
    Daily = 1,

    /// <summary>One invoice per ISO week of claims, sent once the week has ended (Monday start).</summary>
    Weekly = 2,

    /// <summary>One invoice per calendar month of claims, sent once the month has ended.</summary>
    Monthly = 3,
}

/// <summary>
/// §787 — one coupon's invoicing rule. Replaces a row of
/// <c>Settings\ZohoBackstageCoupons_Invoicing.csv</c> on the operator's VM
/// (<c>Couponname;CouponBillingDetails;ErpCustomerNumberInvoicing</c>).
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this is a table and not a config file</b> (operator decision 2026-08-04): the
/// script already mails <i>"ACTION REQUIRED: Coupon missing ERP mapping"</i> the moment an unmapped
/// coupon is claimed, and that alert is only useful if the person receiving it can act on it. A
/// config JSON would have meant the alert arrives about something only a deploy can fix, with the
/// ticket uninvoiced until then. The alert and the fix belong in the same place.</para>
///
/// <para>🔒 <b>Per EDITION.</b> The CSV had no edition column, but coupon names repeat across years
/// and an <c>ErpCustomerNumber</c> from 2026 must not silently bill a 2027 claim. Scoping to
/// <see cref="EventId"/> is reversible; a global table is not.</para>
/// </remarks>
public class CouponInvoicingSetting
{
    public int Id { get; set; }

    /// <summary>The edition this rule belongs to.</summary>
    public int EventId { get; set; }

    /// <summary>
    /// The Zoho promo code, as Backstage reports it. Matched case-insensitively and trimmed —
    /// a coupon typed with a stray space must not read as a different, unmapped coupon.
    /// </summary>
    public string CouponName { get; set; } = string.Empty;

    /// <summary>How this coupon is billed. Defaults to <see cref="CouponBillingType.Unmapped"/>.</summary>
    public CouponBillingType BillingType { get; set; } = CouponBillingType.Unmapped;

    /// <summary>
    /// The e-conomic customer to invoice. NULLABLE on purpose: <see cref="CouponBillingType.NoInvoicing"/>
    /// has nobody to bill, and an <see cref="CouponBillingType.Unmapped"/> row exists precisely
    /// BECAUSE this is not known yet. ⚠️ The invoicing service must refuse to invoice a row whose
    /// billing type needs a customer but has none, rather than defaulting to anyone.
    /// </summary>
    public int? ErpCustomerNumber { get; set; }

    /// <summary>
    /// §798.1 — the REQUESTER: the e-conomic contact who asked for this coupon, and who becomes the
    /// invoice's <b>Att</b> person (<c>recipient.attention.customerContactNumber</c>).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"i also need to add a requester per coupon (dropdown from erp
    /// contacts) which is the att person for the invoice"*.</para>
    ///
    /// <para>🔑 <b>Until now a coupon invoice passed no attention at all</b> and fell back to
    /// whatever the CUSTOMER record happened to point at — which is why nobody could steer it. §786.1
    /// solved the same problem for webshop invoices by resolving the company's DEFAULT SIGNER; a
    /// coupon has no company behind it, so the person is chosen per coupon instead.</para>
    ///
    /// <para>⚠️ <b>It must belong to <see cref="ErpCustomerNumber"/>.</b> A contact number from a
    /// different customer is one e-conomic will reject — or worse, accept against another account.
    /// The page only ever offers contacts of this coupon's own customer, and changing the customer
    /// clears it.</para>
    ///
    /// <para>🔒 Null is fine and is the shipped default: the invoice then behaves exactly as it did
    /// before, falling back to the customer's own attention contact.</para>
    /// </remarks>
    public int? RequesterContactNumber { get; set; }

    /// <summary>
    /// The requester's name as last seen in e-conomic — DISPLAY only, so the page can name the person
    /// when e-conomic is unreachable. ⚠️ Never matched on; the number is the identity.
    /// </summary>
    public string? RequesterName { get; set; }

    /// <summary>Free-text note for the organizer (who the partner is, what was agreed).</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When CEH first saw this coupon CLAIMED. Set by the invoicing sweep when it auto-creates an
    /// <see cref="CouponBillingType.Unmapped"/> row, so the page can say how long a ticket has been
    /// waiting to be billed — the number that turns a list into a queue.
    /// </summary>
    public DateTimeOffset? FirstSeenClaimedAt { get; set; }

    /// <summary>When the organizer was last alerted about this coupon being unmapped. Null = never.
    /// Stops the alert becoming hourly noise while still letting it repeat if ignored.</summary>
    public DateTimeOffset? LastAlertedAt { get; set; }

    /// <summary>
    /// 🔴 §1016d — when this coupon was last INVOICED, so claims are batched into one invoice per
    /// billing period instead of one invoice per claim.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"the claim gets registered so fx a ticket claim for 2 tickets
    /// decreases from 20 to 18. But we dont want to invoice customer for every single claim; that
    /// creates to many invoices. therefore you must batch them to every 2 weeks and remember when
    /// the last invoice was sent for this coupon, so you know the 'catch-up' to invoice."*</para>
    ///
    /// <para>🔑 <b>The claim and the invoice were already separate — only the CADENCE was wrong.</b>
    /// The balance has always moved the moment a ticket is claimed (that is the pool arithmetic, and
    /// it is unaffected by any of this). What ran too often was the INVOICE: the job passes every
    /// ~10 minutes, and each pass invoiced whatever was new, so a coupon claimed on ten different
    /// days produced ten invoices.</para>
    ///
    /// <para>🔒 <b>The "catch-up" needs no separate bookkeeping, and that is why this is one field.</b>
    /// Every claim already carries its own <c>CouponTicket-{id}</c> reference and the run skips any
    /// reference already present on a booked or draft invoice (§787's idempotency interlock). So
    /// "everything since the last invoice" is simply "everything not yet invoiced" — which the
    /// existing scan computes exactly. This timestamp only decides WHEN to send, never WHAT.</para>
    ///
    /// <para>⚠️ Null on a coupon never invoiced ⇒ the window is measured from
    /// <see cref="FirstSeenClaimedAt"/>, so the FIRST batch accumulates too. Otherwise claim #1
    /// would get an invoice to itself and only the rest would ever be batched.</para>
    ///
    /// <para>🔒 A DRY RUN must never stamp this — nothing was created, and stamping it would push
    /// the next real invoice out by a fortnight.</para>
    /// </remarks>
    public DateTimeOffset? LastInvoicedAt { get; set; }

    /// <summary>
    /// §1016d — THIS coupon's billing period in days, overriding the edition default
    /// (<see cref="Integrations.Erp.InvoicingOptions.CouponInvoiceIntervalDays"/>, 14).
    /// Null ⇒ use the default.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"maybe the internal days could be a field that could be adjusted
    /// pr coupon"*. He is right, and the reason is in the agreements rather than in the code: a
    /// billing period is something negotiated with a partner, so two partners can perfectly well
    /// have different ones. A single global number forces the strictest partner's terms onto
    /// everybody.</para>
    ///
    /// <para>🔒 <b>0 means "invoice every pass"</b> for this coupon (no batching) — the same meaning
    /// the global setting gives it, so the two cannot be read differently. A NEGATIVE value is
    /// treated as unset rather than as an error: this is a number typed into a form, and a typo
    /// must fall back to the default rather than change the billing terms silently.</para>
    /// </remarks>
    public int? InvoiceIntervalDays { get; set; }

    /// <summary>
    /// §1016c — when the partner was last told their promo code is live, and at which address.
    /// </summary>
    /// <remarks>
    /// 🔑 Recorded because this is the ONE coupon mail that leaves the building. Every other one
    /// goes to <c>info@</c>, where a duplicate is noise; this one reaches a customer, where a
    /// duplicate is an organizer looking careless. The page shows it beside the button so nobody
    /// sends it twice by hand.
    /// </remarks>
    public DateTimeOffset? ClaimInviteSentAt { get; set; }

    /// <inheritdoc cref="ClaimInviteSentAt"/>
    public string? ClaimInviteSentToEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    /// <summary>
    /// True when this rule can actually produce an invoice.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>§794 — <see cref="CouponBillingType.AllocatedPrepaymentByCustomer"/> IS NOT
    /// INVOICEABLE, and this used to say it was.</b>
    ///
    /// <para>Operator 2026-08-04: *"lets say arrow dk buys 50 prepaid coupons"* — a prepaid partner
    /// has ALREADY PAID, up front, for an allocation. Invoicing them per claim bills them a SECOND
    /// time for tickets they already own. What they are owed is a BALANCE (how many are left), not
    /// an invoice, which is why *"partner must not get a credit note"* is satisfiable at all: the
    /// correction for a cancelled prepaid claim is returning the allocation, not a document.</para>
    ///
    /// <para>⚠️ This was a LIVE defect held back only by <c>Invoicing:DryRun</c> being true. It is
    /// fixed here rather than in the service so every caller — the sweep, the page's "billable"
    /// badge, any future report — agrees about what a prepaid coupon is.</para>
    ///
    /// <para>🔒 The customer number is still REQUIRED for a prepaid row: it is who the allocation
    /// belongs to. It simply does not get invoiced per claim.</para>
    /// </remarks>
    public bool IsInvoiceable =>
        BillingType == CouponBillingType.ClaimableAdHocPaymentByCustomer
        && ErpCustomerNumber is > 0;

    /// <summary>
    /// §794 — true when this coupon draws on a PREPAID allocation instead of being invoiced.
    /// </summary>
    public bool IsPrepaid => BillingType == CouponBillingType.AllocatedPrepaymentByCustomer;

    /// <summary>True when this coupon needs the organizer's attention on the page.</summary>
    public bool NeedsAttention =>
        BillingType == CouponBillingType.Unmapped
        || (BillingType is CouponBillingType.AllocatedPrepaymentByCustomer
                        or CouponBillingType.ClaimableAdHocPaymentByCustomer
            && ErpCustomerNumber is not > 0);
}
