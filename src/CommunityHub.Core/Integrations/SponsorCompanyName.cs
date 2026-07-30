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
        UnresolvedName(companyId);

    /// <summary>
    /// §528b — the last-resort label when NO name has been captured for a company.
    ///
    /// <para>It used to read <c>"Company 30"</c>, which is indistinguishable from a company
    /// genuinely called that: the operator read it as corrupt data on a real sponsor (System Center
    /// Dudes) rather than as a missing sync. Worse, it hid a FUNCTIONAL fault — the name is
    /// captured onto <c>SponsorUploadLocation.CompanyName</c> when the order pull provisions the
    /// company's SharePoint folders, so a missing name means that provisioning never completed,
    /// and the sponsor therefore has no upload folders either.</para>
    ///
    /// <para>Naming it honestly makes that visible everywhere at once — this is the single fallback
    /// every surface funnels through, which is why fixing it at one page (as §528 first tried) did
    /// nothing: <c>ResolveAllAsync</c> already applies this chain, so the caller never sees a miss.</para>
    /// </summary>
    public static string UnresolvedName(string companyId) => $"(name not synced — CM id {companyId})";
}
