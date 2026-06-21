using CommunityHub.Core.Integrations.Erp;

namespace CommunityHub.Core.Domain;

/// <summary>
/// Hub-side metadata for an e-conomic customer contact — the <b>role</b>
/// (Signer / Event Coordinator) and free-text <b>notes</b>, neither of which
/// e-conomic stores on a contact. Keyed by the e-conomic
/// (customerNumber, contactNumber) so it travels with the live contact. Created /
/// updated / removed alongside the live e-conomic write by
/// <c>EconomicContactAdminService</c>. Global (e-conomic is not per-edition).
/// </summary>
public class EconomicContactAnnotation
{
    public int Id { get; set; }

    /// <summary>e-conomic customer number.</summary>
    public int CustomerNumber { get; set; }

    /// <summary>e-conomic customerContactNumber.</summary>
    public int ContactNumber { get; set; }

    /// <summary>The contact's role (0 = plain contact, 1 = Signer, 2 = Event Coordinator).</summary>
    public ErpContactRole Role { get; set; } = ErpContactRole.Contact;

    /// <summary>Hub-only free-text notes (e-conomic has no contact notes field).</summary>
    public string? Notes { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
