namespace CommunityHub.Core.Integrations;

/// <summary>
/// The single source of truth for the sponsor public-company-name fallback
/// chain (DESIGN §6 / REQUIREMENTS §15): <c>company_name_public → legal →
/// billing → "Company {id}"</c>. Every sponsor-facing reference (ERP customer
/// name, webshop sync, emails) resolves the name through this so the chain
/// never drifts between call sites.
/// </summary>
public static class SponsorCompanyName
{
    /// <summary>
    /// Resolve the public company name. The first non-blank of: Company Manager
    /// public name, Company Manager legal name, webshop billing company,
    /// then the "Company {id}" fallback so something always renders.
    /// </summary>
    public static string Resolve(
        string? publicName,
        string? legalName,
        string? billingName,
        string companyId) =>
        !string.IsNullOrWhiteSpace(publicName)  ? publicName!.Trim()  :
        !string.IsNullOrWhiteSpace(legalName)   ? legalName!.Trim()   :
        !string.IsNullOrWhiteSpace(billingName) ? billingName!.Trim() :
        $"Company {companyId}";
}
