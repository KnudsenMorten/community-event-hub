namespace CommunityHub.Core.Domain;

/// <summary>§1305 — where a company stands with the event, as the shop sees it.</summary>
public enum ShopCompanyState
{
    /// <summary>Registered, not yet a sponsor. Operator: "This is not equal to an order".</summary>
    Candidate = 0,

    /// <summary>Has signed at least one contract (or was a sponsor through the old webshop).</summary>
    Sponsor = 1,
}

/// <summary>§1305 — how a shop company came to exist.</summary>
public enum ShopCompanySource
{
    /// <summary>Registered through the CEH registration wizard. Its id is CEH-issued (<c>ceh-…</c>).</summary>
    Registered = 0,

    /// <summary>An existing sponsor from the old webshop's company plugin, first seen in the CEH shop.</summary>
    CompanyManager = 1,
}

/// <summary>
/// §1305 — one company in the CEH sponsor shop: its legal and billing identity, and whether it is a
/// candidate or a sponsor.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The company id is the same value space as <see cref="SponsorInfo.SponsorCompanyId"/></b>:
/// the old webshop's numeric company id for existing sponsors, <c>ceh-xxxxxxxx</c> for a company that
/// registered in CEH. Everything already keyed on a sponsor company id — contacts (§1235), the company
/// switcher, tasks, SponsorInfo — therefore works for a CEH-registered company unchanged.</para>
///
/// <para>Operator 2026-09-26: <i>"It should be a state on the sponsor row, where they can be flipped
/// from sponsor (candidate …) to sponsor (order)."</i> The state lives here AND is mirrored onto
/// <see cref="SponsorInfo.IsSponsor"/>, whose query filter keeps a candidate out of every sponsor list,
/// mail and job until it signs.</para>
/// </remarks>
public class ShopCompany
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string SponsorCompanyId { get; set; } = string.Empty;
    public ShopCompanySource Source { get; set; }
    public ShopCompanyState State { get; set; }

    /// <summary>Legal name (as registered / in e-conomic).</summary>
    public string LegalName { get; set; } = string.Empty;

    /// <summary>The name used in announcements ("Company Name that should be used in announcement").</summary>
    public string? PublicName { get; set; }

    /// <summary>The VAT / CVR number, normalised, without country prefix.</summary>
    public string? Cvr { get; set; }

    public string? Address { get; set; }
    public string? Postcode { get; set; }
    public string? City { get; set; }

    /// <summary>ISO-3166 alpha-2.</summary>
    public string CountryIso2 { get; set; } = "DK";

    public string? CompanyEmail { get; set; }
    public string? InvoiceEmail { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? TwitterUrl { get; set; }

    /// <summary>Invoice currency (DKK / EUR / USD). Prices are EUR; see the contract's currency note.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>e-conomic VAT zone (1 domestic, 2 EU, 3 abroad, 4 domestic exempt). 0 = unknown.</summary>
    public int VatZone { get; set; }

    /// <summary>The e-conomic customer number, once the customer exists there.</summary>
    public string? ErpCustomerNumber { get; set; }

    /// <summary>Why the ERP customer could not be created, when it could not. Cleared on success.</summary>
    public string? ErpError { get; set; }

    /// <summary>§1305 — how the VAT number was checked at registration ("VIES: valid", "VIES unavailable", …).</summary>
    public string? VatCheck { get; set; }

    /// <summary>
    /// §1312 — what does not agree between CEH and e-conomic about this company's people and roles (one finding per line), set
    /// by <see cref="Integrations.Webshop.ShopErpRoleSyncService"/>; null when all agrees. Shown on the company page.
    /// </summary>
    public string? ErpRoleWarning { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? RegisteredByEmail { get; set; }
    public DateTimeOffset? BecameSponsorAt { get; set; }

    /// <summary>§1311 — the customer's purchase-order / billing reference (the old webshop's `billing_reference`).</summary>
    public string? BillingReference { get; set; }

    /// <summary>§1311 — organizers' internal notes / special agreements (the old webshop's `notes`).</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// The name used in the ORDERING process — registration, switch company, cart, contract, reservation, orders, invoice:
    /// the LEGAL (billing) company and its country. Operator 2026-09-27: <i>"show the billing company here, as it is
    /// impossible to see them apart when you use the public name fx arrow"</i> · <i>"it is very important that the contract,
    /// reservation, registration, billing goes to the correct legal entity"</i> · <i>"once products are bought and we start
    /// in the social media branding, exposure, etc. then we use the public name - but not in the ordering process"</i>.
    /// </summary>
    public string LegalLabel => LegalName + (string.IsNullOrWhiteSpace(CountryIso2) ? "" : $" ({CountryIso2})");

    /// <summary>
    /// For ORGANIZER lists: the legal label plus the e-conomic customer number — the unique key the old webshop also shows
    /// (operator 2026-09-27: "but here you can see we also use erp id").
    /// </summary>
    public string OrganizerLabel => LegalLabel + (string.IsNullOrWhiteSpace(ErpCustomerNumber) ? "" : $" · e-conomic {ErpCustomerNumber}");

    /// <summary>
    /// 🔴 §1312 — the name for everything PUBLIC (SoMe, Zoho, the event site, the app): the public name, else the legal name
    /// WITHOUT its legal form — never "Master Packager SIA" (operator 2026-09-29: "public name is for SoMe, Zoho, etc
    /// everything public"). The legal name stays on the contract and the invoice.
    /// </summary>
    public string PublicLabel => !string.IsNullOrWhiteSpace(PublicName)
        ? PublicName.Trim()
        : Integrations.CompanyLegalForm.Strip(LegalName);
}

/// <summary>§1305 — the lifecycle of a shop order.</summary>
/// <summary>
/// §1309 — a person known for a company from the old webshop's company module (or e-conomic): a LOOKUP row only.
/// </summary>
/// <remarks>
/// 🔒 Not a participant: nobody can sign in with it and nobody is mailed because of it (operator 2026-09-27: "we will not
/// onboard all these companies as sponsors and send emails"). It only fills the registration dropdown — "pull already
/// registrered people for the company … so we dont get the same person registrered twice" — and becomes a participant
/// when a verified registrant picks it.
/// </remarks>
public class ShopKnownContact
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public string SponsorCompanyId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public bool IsSigner { get; set; }
    public bool IsEventCoordinator { get; set; }
    /// <summary>"CompanyManager" or "Economic".</summary>
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; set; }
}

/// <summary>
/// §1309 — a registrant asking to JOIN a company that already exists, when the match is NOT proven by their e-mail
/// domain (CVR only, or a gmail-type address). Waits for an organizer (operator 2026-09-27: "i dont want edit capability
/// in the registration form, as then anyone cna change any company"). Nothing about the company can be changed by it.
/// </summary>
public class ShopJoinRequest
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public string SponsorCompanyId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    /// <summary>How the company was found: "cvr" or "domain".</summary>
    public string MatchedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedByEmail { get; set; }
    public bool? Approved { get; set; }
    public string? DecisionNote { get; set; }
}

public enum ShopOrderStatus
{
    /// <summary>Being filled. Holds NO stock: an abandoned cart must never keep a booth off the market.</summary>
    Cart = 0,

    /// <summary>
    /// A "handshake" (deck slide 103): the items are held — stock taken in the webshop — but nothing is
    /// signed. Operator: "Reservation vs. Confirmed – we need to support the concept of reservation".
    /// </summary>
    Reserved = 1,

    /// <summary>Sent to the signer. Stock is held.</summary>
    AwaitingSignature = 2,

    /// <summary>Signed: the order is confirmed and onboarding starts (§1305 item 7b).</summary>
    Signed = 3,

    /// <summary>Cancelled; any held stock was given back to the webshop.</summary>
    Cancelled = 4,
}

/// <summary>§1305 — how a contract was signed.</summary>
public enum ShopSignatureKind
{
    None = 0,
    /// <summary>Drawn on screen.</summary>
    Drawn = 1,
    /// <summary>An uploaded picture of a signature.</summary>
    Uploaded = 2,
    /// <summary>Generated from the signer's typed name (operator: "generate a digital signature with the signers name").</summary>
    Typed = 3,
    /// <summary>§1312 — signed the sponsor's own way (printed / PDF) and confirmed by an organizer.</summary>
    Offline = 4,
}

/// <summary>
/// §1305 — one order (cart → reservation → contract → signed) for one company.
/// </summary>
public class ShopOrder
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Human order number, e.g. <c>ELDK27-0042</c>. Set on first save.</summary>
    public string? Number { get; set; }

    public ShopOrderStatus Status { get; set; }

    /// <summary>Purchase order / reference printed on the contract and invoice.</summary>
    public string? PurchaseOrderRef { get; set; }

    /// <summary>The person who signs. Chosen from the company's contacts.</summary>
    public int? SignerParticipantId { get; set; }

    /// <summary>The event coordinator for this order.</summary>
    public int? CoordinatorParticipantId { get; set; }

    /// <summary>
    /// Organizer-only special conditions ("Agreed Terms" on the contract): payment terms, invoice timing,
    /// other written terms. Operator: "These information must be stored, so the later ordering takes the
    /// billing terms into consideration."
    /// </summary>
    public string? SpecialTerms { get; set; }

    /// <summary>When set, invoicing must not happen before this date (a special condition, machine-readable).</summary>
    public DateOnly? InvoiceNotBefore { get; set; }

    /// <summary>Payment days after invoice, when the organizer agreed something other than the standard 30.</summary>
    public int? PaymentDays { get; set; }

    /// <summary>Currency the order is invoiced in (copied from the company when the order is created).</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>VAT rate applied, in percent (0 for non-Danish customers).</summary>
    public decimal VatRatePercent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ReservedAt { get; set; }
    public DateTimeOffset? SentForSignatureAt { get; set; }

    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedByName { get; set; }
    public string? SignedByEmail { get; set; }
    public int? SignedByParticipantId { get; set; }
    public ShopSignatureKind SignatureKind { get; set; }

    /// <summary>The signature picture (PNG). Small by construction: resized before it is stored.</summary>
    public byte[]? SignatureImage { get; set; }
    public string? SignedFromIp { get; set; }

    /// <summary>The signed contract PDF in SharePoint.</summary>
    public string? ContractPath { get; set; }
    public string? ContractItemId { get; set; }

    /// <summary>True once this signed order has been through the onboarding pipeline.</summary>
    public DateTimeOffset? OnboardedAt { get; set; }

    /// <summary>
    /// §1305 item 13 — the swag-catalogue credit (euro value an organizer granted) applied to this order's swag
    /// lines, frozen when the order is reserved.
    /// </summary>
    public decimal SwagCreditEur { get; set; }

    /// <summary>§1305 — the e-conomic draft invoice for this order: its reference, number, and when it was made.</summary>
    public string? ErpInvoiceReference { get; set; }
    public int? ErpDraftInvoiceNumber { get; set; }
    public DateTimeOffset? InvoicedAt { get; set; }

    /// <summary>Why the last invoicing attempt did not create a draft (shown to organizers). Cleared on success.</summary>
    public string? InvoiceProblem { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledByEmail { get; set; }
    public string? CancelReason { get; set; }

    // ── §1308 — an ORGANIZER reservation for this edition ─────────────────────────────────────────────────────────
    // Operator 2026-09-27: "I have multiple companies like Red Hat, that have reserved a booth … weekly email where they
    // will have a deadline date … Organizers must be able to control the deadline on a per reservation basis … I must
    // have a requester that will get email … define the cadence … byweekly or disabled until after a specific date."
    // An order is an organizer reservation when ReservationRequesterEmail is set. It holds stock like any reserved
    // order (booth shown RESERVED, not selectable) and gets ITS OWN reminder instead of the daily cart reminder.

    /// <summary>§1308 — who gets the reservation reminders (need not have a hub account).</summary>
    public string? ReservationRequesterName { get; set; }
    public string? ReservationRequesterEmail { get; set; }

    /// <summary>§1308 — the deadline shown in the reminder; after it the requester is no longer mailed and the organizers are told once.</summary>
    public DateOnly? ReservationDeadline { get; set; }

    /// <summary>§1308 — remind every N weeks (1 = weekly, 2 = every other week); 0 = no reminders.</summary>
    public int ReservationReminderWeeks { get; set; } = 1;

    /// <summary>§1308 — no reminder before this date ("disabled until after a specific date").</summary>
    public DateOnly? ReservationRemindFrom { get; set; }

    /// <summary>§1308 — the organizer's note on the reservation (internal).</summary>
    public string? ReservationNote { get; set; }

    /// <summary>
    /// §1312 — a SPONSOR asked the organizers to reserve this cart (operator 2026-09-27: "in the offer can you make 2
    /// buttons: reservation request (which i can approve) and sign/order" · "only thing i have to do is to accept or deny
    /// reservation request and i can add deadline, reminder, etc"). Null = no open request. Nothing is reserved while it
    /// waits (first come, first served); Accept turns the cart into an organizer reservation, Deny clears it.
    /// </summary>
    public DateTimeOffset? ReservationRequestedAt { get; set; }

    /// <summary>§1312 — who asked for the reservation (a contact of the company).</summary>
    public int? ReservationRequestedByParticipantId { get; set; }

    /// <summary>§1312 — true while a sponsor's reservation request waits for the organizers.</summary>
    public bool HasOpenReservationRequest => ReservationRequestedAt is not null && Status == ShopOrderStatus.Cart;

    /// <summary>
    /// §1312 — the requester asked for MORE TIME on a reservation (operator 2026-09-27: "add a extension request button in
    /// the reminder mail where they select a date and give reason - and it is sent to me for approval"). Null = none open.
    /// </summary>
    public DateOnly? ExtensionRequestedDeadline { get; set; }
    public string? ExtensionRequestReason { get; set; }
    public DateTimeOffset? ExtensionRequestedAt { get; set; }
    public int? ExtensionRequestedByParticipantId { get; set; }

    /// <summary>§1312 — true while an extension request waits for the organizers.</summary>
    public bool HasOpenExtensionRequest => ExtensionRequestedDeadline is not null && CanAskForMoreTime;

    /// <summary>§1312 — a held order with a deadline (reserved, or sent for signature) can be given more time.</summary>
    public bool CanAskForMoreTime =>
        ReservationDeadline is not null && Status is ShopOrderStatus.Reserved or ShopOrderStatus.AwaitingSignature;

    /// <summary>§1308 — true for an organizer reservation (it has a requester).</summary>
    public bool IsOrganizerReservation => !string.IsNullOrWhiteSpace(ReservationRequesterEmail);

    public List<ShopOrderLine> Lines { get; set; } = new();

    /// <summary>True while the order holds stock in the webshop.</summary>
    public bool HoldsStock =>
        Status is ShopOrderStatus.Reserved or ShopOrderStatus.AwaitingSignature or ShopOrderStatus.Signed;
}

/// <summary>§1305 — one product on an order.</summary>
public class ShopOrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public ShopOrder Order { get; set; } = null!;

    public int ProductId { get; set; }
    public ShopProduct Product { get; set; } = null!;

    public int Quantity { get; set; } = 1;

    /// <summary>
    /// §1305 item 13 — pieces per unit. 1 for ordinary products. For a swag-catalogue item it is the BAG quantity:
    /// the webshop stocks the item once per sponsor (one sponsor takes the item), while the price is per piece ×
    /// every bag. Stock uses <see cref="Quantity"/>; money uses <see cref="Quantity"/> × this.
    /// </summary>
    public int PiecesPerUnit { get; set; } = 1;

    /// <summary>
    /// The list price PER UNIT when frozen (at reservation) — for a swag line that is price per piece × pieces.
    /// </summary>
    public decimal UnitPriceEur { get; set; }

    /// <summary>
    /// §1305 — the NORMAL unit price, frozen with <see cref="UnitPriceEur"/> when the line was discounted, so the cart
    /// and the contract can keep showing "normal price → your price". Null when there was no discount.
    /// </summary>
    public decimal? NormalUnitPriceEur { get; set; }

    /// <summary>
    /// §1312 — a price agreed with THIS company for THIS line (organizer-set), replacing the product's price for them only;
    /// the product's normal price shows crossed out beside it. Operator 2026-09-27: "i must be able to add a discount for a
    /// product in webshop (individual) if i want to give red hat a discount of 3000 euro for a 60 mn session. it is only for
    /// them". Null = the product's own price. Frozen into <see cref="UnitPriceEur"/> like any price.
    /// </summary>
    public decimal? AgreedUnitPriceEur { get; set; }

    /// <summary>Late-order surcharge applied, in percent (0 or e.g. 50).</summary>
    public decimal SurchargePercent { get; set; }

    /// <summary>Name as it read when added — for the contract and history, not for matching.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// True once this line's units were taken off the webshop's stock. Kept per line so a cancellation
    /// gives back exactly what was taken, and an unlimited product (nothing taken) gives back nothing.
    /// </summary>
    public bool StockTaken { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// §1305 — an e-mail being verified before a registration: the registrant proves they own the address
/// with a one-time code BEFORE any company or person exists in CEH.
/// </summary>
/// <remarks>
/// Operator: <i>"a secure login with pin code so we have an authenticated registration, also with
/// captcha"</i>. The existing PIN login needs an existing participant, so registration has its own
/// short-lived code record; once the wizard completes, the people it creates sign in with the normal PIN.
/// </remarks>
public class ShopRegistration
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>A random token handed to the browser after verification; the wizard posts it back.</summary>
    public string? SessionTokenHash { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? SponsorCompanyId { get; set; }
    public string? RequestIp { get; set; }
}

/// <summary>
/// §1305 item 10 — something a company had in a previous edition and may reserve again in this one.
/// </summary>
/// <remarks>
/// Operator: <i>"For ELDK28, we want to open up, so sponsors can reserve the same options, they had for
/// ELDK27 related to booths and sessions (timeslots). They must do this within 1 month … I need to have
/// an import module related to this."</i> Imported from the previous edition's signed orders (or its
/// webshop orders); matched to this edition's products by booth id / timeslot / name.
/// </remarks>
public class ShopEntitlement
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public string SponsorCompanyId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>The edition it came from, e.g. <c>ELDK27</c>.</summary>
    public string SourceEdition { get; set; } = string.Empty;

    /// <summary>What they had, as it read then.</summary>
    public string ProductName { get; set; } = string.Empty;
    public string? BoothId { get; set; }
    public string? Timeslot { get; set; }

    /// <summary>The product in THIS edition it maps to, when one was found.</summary>
    public int? MatchedProductId { get; set; }
    public ShopProduct? MatchedProduct { get; set; }

    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>Set when the company reserved it (the order line that took it).</summary>
    public int? ClaimedOrderLineId { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Set when the company said it does not want it this time — stops the reminders for it.</summary>
    public DateTimeOffset? DeclinedAt { get; set; }
}

/// <summary>
/// §1305 item 10b — one page of the public sponsor catalogue (the replacement of the sponsor PowerPoint).
/// </summary>
public class ShopCatalogPage
{
    public int Id { get; set; }
    public int EventId { get; set; }

    /// <summary>URL-safe key (<c>/sponsor-catalog/{slug}</c>). Unique per edition.</summary>
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>The TOC chapter this page sits under (e.g. "About", "Sponsor opportunities").</summary>
    public string? Chapter { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Body in Markdown. Rendered sanitised.</summary>
    public string? BodyMarkdown { get; set; }

    /// <summary>A picture or video URL shown at the top (absolute https URL).</summary>
    public string? MediaUrl { get; set; }

    /// <summary>When set, the page lists that group's products with LIVE prices and availability.</summary>
    public ShopGroup? ProductGroup { get; set; }

    /// <summary>Show the booth map with live availability on this page.</summary>
    public bool ShowBoothMap { get; set; }

    /// <summary>
    /// §1312 — the words that pick this page's products (every word in the product's name or category, e.g. "Gold Exhibitor"):
    /// the page shows their LIVE price and availability. The deck import fills it with the page title.
    /// </summary>
    public string? ProductMatch { get; set; }

    /// <summary>
    /// §1312 — a live table of every package (the deck's "Actual Reservations" slides), in one of two views. Operator:
    /// <i>"show both available and reserved/confirmed - 2 views"</i>.
    /// </summary>
    public ShopCatalogAvailabilityView AvailabilityView { get; set; }

    public bool IsPublished { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedByEmail { get; set; }
}

/// <summary>§1312 — which live package table a catalogue page shows.</summary>
public enum ShopCatalogAvailabilityView
{
    None = 0,
    /// <summary>What can still be bought: remaining of the maximum, per package.</summary>
    Available = 1,
    /// <summary>What is taken: reserved and confirmed, per package.</summary>
    Taken = 2,
    /// <summary>
    /// Both in one table — price, maximum, remaining, reserved and confirmed with who (operator 2026-09-27: "i need a
    /// availability &amp; booked section … what who, amounts, price … it is the summary" · "many sponsors ask: what do you
    /// have remaining"). The booked amount in euro is shown to organizers only.
    /// </summary>
    Summary = 3,
}

/// <summary>
/// §1305 item 11 — one booth drawn over the expo layout picture, in percent of the picture's size so the
/// map scales to any screen.
/// </summary>
public class ShopBoothSpot
{
    public int Id { get; set; }
    public int EventId { get; set; }

    /// <summary>The booth id as it appears in product names (<c>E-20A</c>).</summary>
    public string BoothId { get; set; } = string.Empty;

    public decimal XPercent { get; set; }
    public decimal YPercent { get; set; }
    public decimal WidthPercent { get; set; }
    public decimal HeightPercent { get; set; }
}
