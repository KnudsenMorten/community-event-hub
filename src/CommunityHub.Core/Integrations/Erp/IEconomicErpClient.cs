namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// One ERP customer to create / update in e-conomic. Carries only the fields
/// the hub can resolve from Company Manager + the webshop. <see cref="Name"/>
/// is ALWAYS the resolved public company name (public → legal → billing →
/// "Company {id}"); never the legal/billing name as primary.
/// </summary>
public sealed record ErpCustomer(
    string CompanyId,
    string Name,
    string? CorporateIdentificationNumber, // CVR / tax id (validated upstream)
    string? Currency,
    string? VatZone,
    string? Email,
    string? ErpCustomerNumber);            // existing e-conomic customer number, if known

/// <summary>One ERP contact (person) to attach to an ERP customer, with a role.</summary>
public sealed record ErpContact(
    string CompanyId,
    string Email,
    string FullName,
    ErpContactRole Role);

/// <summary>
/// e-conomic contact roles the hub mirrors. Maps directly to the Company
/// Manager roles: Role 1 = Signer, Role 2 = Event Coordinator.
/// </summary>
public enum ErpContactRole
{
    /// <summary>Ordinary linked contact (no special role).</summary>
    Contact = 0,
    /// <summary>Company Manager Role 1 — the default signer.</summary>
    Signer = 1,
    /// <summary>Company Manager Role 2 — the event coordinator.</summary>
    EventCoordinator = 2,
}

/// <summary>One ERP order line to create from a webshop order line item.</summary>
public sealed record ErpOrderLine(
    long ProductId,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice);

/// <summary>
/// An ERP order to create in e-conomic from a webshop (WooCommerce) order.
/// The <see cref="Currency"/> is the order's own currency; the FX check is
/// performed by the order-creation service before this is sent.
/// </summary>
public sealed record ErpOrder(
    string CompanyId,
    long WebshopOrderId,
    string Currency,
    DateOnly OrderDate,
    IReadOnlyList<ErpOrderLine> Lines);

/// <summary>The outcome of one ERP customer / order operation.</summary>
public enum ErpSyncOutcome
{
    /// <summary>Already present in the ERP — nothing to do (idempotent skip).</summary>
    AlreadyExists,
    /// <summary>Was missing and has now been created in the ERP.</summary>
    Created,
    /// <summary>Was present and has been updated in the ERP.</summary>
    Updated,
    /// <summary>Was missing; the write was not performed (TESTMODE, or no live API).</summary>
    WouldCreate,
    /// <summary>The operation failed.</summary>
    Failed,
}

/// <summary>Result of one ERP customer create/sync.</summary>
public sealed record ErpCustomerResult(
    ErpCustomer Customer,
    ErpSyncOutcome Outcome,
    string? ErpCustomerNumber,
    string? Detail);

/// <summary>Result of one ERP contact create/sync.</summary>
public sealed record ErpContactResult(
    ErpContact Contact,
    ErpSyncOutcome Outcome,
    string? Detail);

/// <summary>Result of one ERP order create.</summary>
public sealed record ErpOrderResult(
    ErpOrder Order,
    ErpSyncOutcome Outcome,
    string? ErpOrderNumber,
    string? Detail);

/// <summary>
/// The e-conomic ERP + sponsor-webshop seam (REQUIREMENTS §7a). Two
/// implementations:
/// <list type="bullet">
///   <item>a TESTMODE one that performs NO real e-conomic / webshop calls, and</item>
///   <item>a live one against the e-conomic REST API + the sponsor webshop.</item>
/// </list>
/// The live implementation needs operator credentials + endpoints (e-conomic
/// app/grant tokens, webshop base URL) that are <b>not available in this repo</b>;
/// until they are wired it reports <see cref="CanWrite"/> = false and every
/// operation records <see cref="ErpSyncOutcome.WouldCreate"/>. NEVER hard-code
/// an endpoint or secret here — config carries secret NAMES only.
/// </summary>
public interface IEconomicErpClient
{
    /// <summary>
    /// Whether this implementation can perform a real write. False for
    /// TESTMODE, and false for the live client until its e-conomic + webshop
    /// credentials / endpoints are configured — callers then record
    /// <see cref="ErpSyncOutcome.WouldCreate"/> instead of <c>Created</c>.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Find an existing e-conomic customer number for this company, or null if
    /// none. (Idempotency: the sync service also keeps a local
    /// <c>ErpCustomerLink</c> so a re-run never double-creates.)
    /// </summary>
    Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct);

    /// <summary>Create the customer in e-conomic; returns the new customer number.</summary>
    Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct);

    /// <summary>Update an existing e-conomic customer (by its customer number).</summary>
    Task UpdateCustomerAsync(string erpCustomerNumber, ErpCustomer customer, CancellationToken ct);

    /// <summary>
    /// Create / update the contact in e-conomic with its role, and mirror it to
    /// the sponsor webshop.
    /// </summary>
    Task CreateOrUpdateContactAsync(string erpCustomerNumber, ErpContact contact, CancellationToken ct);

    /// <summary>Create the order in e-conomic; returns the new ERP order number.</summary>
    Task<string> CreateOrderAsync(string erpCustomerNumber, ErpOrder order, CancellationToken ct);
}
