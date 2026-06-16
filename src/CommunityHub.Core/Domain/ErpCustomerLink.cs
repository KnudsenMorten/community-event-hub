namespace CommunityHub.Core.Domain;

/// <summary>
/// The hub-local link between a sponsor company and its e-conomic ERP customer
/// (REQUIREMENTS §7a — ERP customer create/sync). Scoped to
/// (EventId, SponsorCompanyId). This is the idempotency record: once a company
/// has an <see cref="ErpCustomerNumber"/>, a re-run updates the ERP customer
/// rather than creating a duplicate. It also records the last CVR validation
/// outcome so operators can see why a create was (or wasn't) allowed, without
/// re-hitting any external service.
///
/// This row holds NO secrets and no PII beyond the public company identity; the
/// e-conomic customer number is an opaque ERP key, not a secret.
/// </summary>
public class ErpCustomerLink
{
    public int Id { get; set; }

    public int EventId { get; set; }

    /// <summary>WooCommerce / Company Manager company id.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>The resolved public company name at sync time (audit only).</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>The e-conomic customer number, once created/linked. Empty = not yet in the ERP.</summary>
    public string ErpCustomerNumber { get; set; } = string.Empty;

    /// <summary>Normalized CVR last validated for this company (digits only).</summary>
    public string? Cvr { get; set; }

    /// <summary>True if the last CVR validation passed.</summary>
    public bool CvrValid { get; set; }

    /// <summary>Short reason when the last CVR validation failed (e.g. "checksum").</summary>
    public string? CvrValidationReason { get; set; }

    /// <summary>True if the offline gate alone validated the CVR (no external register call).</summary>
    public bool CvrOfflineOnly { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; set; }
}
