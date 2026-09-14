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
    /// §1091 — the share of each claimed ticket the <b>LINKED COMPANY</b> is invoiced, in percent.
    /// Null ⇒ <b>100</b>, which is what every ad-hoc coupon has always billed.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"right now ad-hoc coupons says that the linked company pays 100%
    /// of the ticket, but we need to support a split, like 50/50 so the partner pays 50% of the
    /// ticket … and then the linked company fx arrow gets a adhoc invoice of the other 50%"*.</para>
    ///
    /// <para>🔑 <b>It is the COMPANY's share, not the attendee's discount.</b> He asked for that to
    /// be said out loud — *"with clear notice that this is the percentage the linked company will be
    /// invoiced of"* — and the reason is that at 50/50 the two readings give the same number, so the
    /// ambiguity only bites the first time somebody enters 30.</para>
    ///
    /// <para>🔴 <b>CEH cannot set the attendee's half.</b> Zoho Backstage has no coupon API (§787.14),
    /// so the matching discount is a promo code the operator creates by hand in Backstage. This field
    /// drives the INVOICE only. The two sides are a commercial agreement, not an arithmetic identity
    /// — do not "reconcile" them against the order total, because the agreed price below means they
    /// are not required to sum to the ticket price.</para>
    /// </remarks>
    public int? InvoicedSharePercent { get; set; }

    /// <summary>
    /// §1091 — the agreed price per ticket for this coupon, in DKK. Null ⇒ use the price the ticket
    /// was actually sold at (Zoho's <c>base_price</c>), which is what has always been billed.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"remember to use the agreed price per coupon when calculating"* …
    /// *"i belive with set it in the coupon, but we can also use the actual price used"*. ⇒ Both, in
    /// that order: the agreed price when there is one, the actual price when there is not.</para>
    ///
    /// <para>🔒 <b>Nullable on purpose, and null is not zero.</b> Every ad-hoc coupon in existence
    /// predates this field; treating a missing price as 0 would invoice a partner nothing and look
    /// like success. Null means "no agreement recorded, bill what the ticket actually cost" — the
    /// behaviour those coupons already have. Same reasoning as
    /// <see cref="CouponPrepaidPurchase.UnitPriceDkk"/> (§992), which is the prepaid side's
    /// equivalent and where the phrase "agreed price" comes from.</para>
    /// </remarks>
    public decimal? AgreedUnitPriceDkk { get; set; }

    /// <summary>
    /// §1094 — the most tickets this AD-HOC coupon may be claimed for. Null ⇒ uncapped.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"ad hoc can in some cases also have a cap, so both scenarios we
    /// must be able to report on"* and *"we able to extend cap for ad-hoc if customer chose a
    /// cap"*.</para>
    ///
    /// <para>🔒 <b>A cap is ALWAYS IN TICKETS, NEVER IN KRONER — settled by the operator 2026-08-19:
    /// *"cap is always in tickets, newer kroner"*.</b> Do not add a monetary cap alongside this, and
    /// do not reinterpret it as an amount.</para>
    ///
    /// <para>🔑 Three reasons it is the right unit, so the decision survives without him: it is the
    /// direct analogue of a prepaid pool's quantity, it is the unit everything else in this feature
    /// reports in ("usage / sign-ups"), and it is the only one <b>Backstage can actually enforce</b>
    /// on a promo code. A spend cap would be enforceable nowhere and comparable to nothing.</para>
    ///
    /// <para>🔴 <b>CEH CANNOT ENFORCE IT.</b> Backstage has no coupon API (§787.14), so the limit
    /// only exists if the operator sets it on the promo code by hand — which is why raising the cap
    /// re-sends the Backstage instruction mail. The stored number is what we REPORT and INVOICE
    /// against; it is not a gate. This is the §796 shape exactly: an exhausted pool leaves the code
    /// working and the next claimant takes a ticket nobody agreed to.</para>
    ///
    /// <para>🔒 <b>Raising it is an ordinary edit</b> — there is no "top-up" row as prepaid has,
    /// because nothing was bought. The agreement simply changed, and the number carries no history
    /// beyond <c>UpdatedAt</c>.</para>
    /// </remarks>
    public int? ClaimCapTickets { get; set; }

    /// <summary>
    /// §1094c — when this CAPPED AD-HOC coupon was last warned about, and how many were left then.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"make alerting, emails the same for any coupon no matter if adhoc
    /// or prepaid, if cap is applied include also available/remaining in mails/alerting"*.</para>
    ///
    /// <para>🔑 <b>The prepaid twin of these lives on <see cref="CouponPrepaidAllocation"/></b>
    /// (<c>LastLowBalanceAlertAt</c> / <c>LastLowBalanceAlertRemaining</c>, §796.2) — a capped ad-hoc
    /// coupon has no allocation row, so it needs its own pair or it could never be throttled.
    /// Same meaning, same rules: the REMAINING number is stored, not a flag, because that is what
    /// lets the next pass tell "got worse" from "still low" from "recovered".</para>
    ///
    /// <para>⚠️ Null on every coupon until it first goes low — never treated as "alerted at 0".</para>
    /// </remarks>
    public DateTimeOffset? LastCapAlertAt { get; set; }

    /// <inheritdoc cref="LastCapAlertAt"/>
    public int? LastCapAlertRemaining { get; set; }

    /// <summary>
    /// ⚰️ §1111 — RETIRED. Nothing reads this; the column stays only because dropping it buys
    /// nothing. Do not add a new reader.
    /// </summary>
    /// <remarks>
    /// <para>§1098 gave the claim invite a tick alongside <see cref="IssueUsageLink"/> and
    /// <see cref="SendUsageStatusMail"/>. Operator 2026-08-21: *"i dont get the point of having this
    /// tick … if there is also a button"* — and he was right; it was a defect rather than a
    /// preference.</para>
    ///
    /// <para>🔑 <b>The other two gate things that happen WITHOUT him</b> — a link a provisioner
    /// issues, a mail on a timer — so an explicit opt-in is the only consent that exists for them.
    /// The claim invite is sent by pressing a button labelled "Send the claim link now", and that
    /// press IS the consent. A second, invisible consent could only make the visible one lie, which
    /// is exactly what it did: the button answered *"No mail was sent"*.</para>
    ///
    /// <para>The one-off-sale category (set up, invoiced, never written to) is unaffected — it is
    /// served by not pressing the button.</para>
    /// </remarks>
    public bool SendClaimInviteMail { get; set; }

    /// <summary>
    /// §1098 — include this coupon in the customer's secure usage link. Default OFF.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-20, on the one-off sale category: *"they dont need to have a secure
    /// link as well"* — *"some uses this as a method to buy via a invoice, as they dont have a
    /// credit card. so they typically make one order and then they dont buy more."*</para>
    ///
    /// <para>🔴 <b>The monitor is per CUSTOMER, so this works by SCOPE, not by suppression.</b> An
    /// unticked coupon is simply not part of any monitor: it does not appear on the page, is not in
    /// the balances and is not in the export. A customer whose coupons are all unticked gets no
    /// monitor created at all. Suppressing the whole link instead would punish the partner's OTHER
    /// agreements for the presence of a one-off.</para>
    /// </remarks>
    public bool IssueUsageLink { get; set; }

    /// <summary>
    /// §1098 — include this coupon in the fortnightly usage/status mail. Default OFF.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-20: *"i want to control if they should get a by-weekly status mail of
    /// usage by a tick button … we will let it run, but they dont get a status."*</para>
    ///
    /// <para>⚠️ Independent of <see cref="IssueUsageLink"/> on purpose, so an odd combination he
    /// actually wants — a link but no recurring mail, say — is expressible. A single "is this a
    /// portal customer" flag would have folded two decisions into one and made the rarer one
    /// impossible.</para>
    /// </remarks>
    public bool SendUsageStatusMail { get; set; }

    /// <summary>
    /// §1096 — the partner has asked for THIS coupon's ceiling to be raised to this many tickets.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-20: *"customer must see all the coupons they can for for each line,
    /// there must be a Increase max button so they can extend the cap"*.</para>
    ///
    /// <para>🔴 <b>PER COUPON, because a customer can hold several — and one shared field could not
    /// say which.</b> §1094 put the ask on the monitor row (one per customer), and Arrow Denmark is
    /// exactly the case that breaks it: a prepaid pool AND a capped ad-hoc coupon on one customer,
    /// where the request had to guess which agreement was meant and always guessed prepaid. A
    /// per-line button names its own coupon, so nothing has to be inferred.</para>
    ///
    /// <para>🔒 The rate limit stays on the MONITOR (<c>AttendeeMonitor.LastCapRequestAt</c>): it
    /// protects the anonymous endpoint, which is per link, not per coupon. Splitting them is
    /// deliberate — one partner should not be able to mail the ops box once per coupon per hour.</para>
    ///
    /// <para>⚠️ Cleared when the organizer applies it, so a non-null value means "still waiting".</para>
    /// </remarks>
    public int? RequestedCapTickets { get; set; }

    /// <inheritdoc cref="RequestedCapTickets"/>
    public DateTimeOffset? RequestedCapAt { get; set; }

    /// <summary>
    /// §1095 — when the organizer confirmed the promo code EXISTS in Zoho Backstage.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19, describing the flow he wants: *"c) i will get email to create
    /// coupons in zoho d) customer will get email with secure link to monitor usage (i need to click
    /// send mail). actually i prefer to have you send this automatically"*.</para>
    ///
    /// <para>🔴 <b>THIS IS THE ONE FACT CEH CANNOT DISCOVER, AND THE INVITE IS A LIE WITHOUT IT.</b>
    /// The claim invite tells the partner *"you can now use the below registration URL to start
    /// claiming"*. Backstage has no coupon API (§787.14), so CEH cannot check whether the code
    /// exists — and a mail sent the moment the coupon row is created will, more often than not,
    /// reach the partner BEFORE he has made the code. They then click a code that does not work, on
    /// a mail from us.</para>
    ///
    /// <para>🔑 So the send is automatic and the CONFIRMATION is the gate. It replaces a
    /// compose-preview-send ritual with a single click that records something true, and it is the
    /// only step in his (a)–(d) flow that a human genuinely has to supply.</para>
    ///
    /// <para>⚠️ Not a proxy for "the code is correct" — only that it exists. The limit and the
    /// discount still have to match what the hub reports, which is what the §1091b/§1094c
    /// instruction mails are for.</para>
    /// </remarks>
    public DateTimeOffset? BackstageCodeConfirmedAt { get; set; }

    /// <inheritdoc cref="BackstageCodeConfirmedAt"/>
    public string? BackstageCodeConfirmedByEmail { get; set; }

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
