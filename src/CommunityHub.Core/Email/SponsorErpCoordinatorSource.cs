using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Email;

/// <summary>
/// Resolves the e-conomic event-coordinator (Role 2) email set for one sponsor
/// company (REQUIREMENTS §7c). It bridges the hub's sponsor company id to
/// e-conomic by reading the company's e-conomic customer number from CEH's own
/// company record (<see cref="Domain.ShopCompany.ErpCustomerNumber"/> — §1312, Company
/// Manager is retired), then asks the read-only <see cref="IEconomicRoleClient"/> for
/// that customer's contact roles.
///
/// <para>This is the PRIMARY audience source for sponsor email; the
/// <see cref="SponsorRecipientResolver"/> falls back to the hub's
/// role flags (set from e-conomic by the role sync, or by hand) when this returns
/// <c>null</c> (ERP roles disabled / unreachable / no data).</para>
///
/// <para>STRICTLY READ-ONLY and FAIL-SOFT — any failure yields <c>null</c>
/// ("unavailable, fall back"), never an exception, never a write.</para>
/// </summary>
public interface ISponsorErpCoordinatorSource
{
    /// <summary>Whether ERP role resolution is enabled (gates the resolver's primary path).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Return the set of event-coordinator (Role 2) contact emails for the
    /// sponsor company, or <c>null</c> when ERP roles are unavailable
    /// (disabled / company not mapped to an ERP customer / read failed / empty)
    /// so the caller falls back to the hub role flags. Emails are
    /// returned verbatim from e-conomic; matching to participants is the
    /// resolver's job. Signer-only contacts (Role 1 without Role 2) are excluded
    /// here; a both-roles contact is included.
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetCoordinatorEmailsAsync(
        string sponsorCompanyId, CancellationToken ct = default);
}

/// <summary>Disabled no-op source — always returns null so the resolver uses the hub role flags. The default.</summary>
public sealed class NullSponsorErpCoordinatorSource : ISponsorErpCoordinatorSource
{
    public bool IsEnabled => false;

    public Task<IReadOnlyCollection<string>?> GetCoordinatorEmailsAsync(
        string sponsorCompanyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);
}

/// <summary>
/// Live implementation: CEH's company record (company id → e-conomic customer number)
/// + the read-only e-conomic role client (customer → coordinator emails). Both
/// reads are best-effort; on any gap or error it returns <c>null</c> so the
/// resolver falls back to the hub's own role flags.
/// </summary>
public sealed class SponsorErpCoordinatorSource : ISponsorErpCoordinatorSource
{
    private readonly Data.CommunityHubDbContext _db;
    private readonly IEconomicRoleClient _roleClient;
    private readonly ILogger<SponsorErpCoordinatorSource> _log;

    public SponsorErpCoordinatorSource(
        Data.CommunityHubDbContext db,
        IEconomicRoleClient roleClient,
        ILogger<SponsorErpCoordinatorSource> log)
    {
        _db = db;
        _roleClient = roleClient;
        _log = log;
    }

    public bool IsEnabled => _roleClient.IsEnabled;

    public async Task<IReadOnlyCollection<string>?> GetCoordinatorEmailsAsync(
        string sponsorCompanyId, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(sponsorCompanyId))
        {
            return null;
        }

        try
        {
            var id = sponsorCompanyId.Trim();
            var erpCustomerNumber = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                _db.ShopCompanies.Where(c => c.SponsorCompanyId == id && c.ErpCustomerNumber != null)
                    .OrderByDescending(c => c.EventId).Select(c => c.ErpCustomerNumber), ct);
            if (string.IsNullOrWhiteSpace(erpCustomerNumber))
            {
                return null; // not mapped to an ERP customer -> fall back
            }

            var contactRoles = await _roleClient.GetContactRolesAsync(erpCustomerNumber, ct);
            if (contactRoles.Count == 0)
            {
                return null; // no e-conomic role data -> fall back
            }

            var coordinatorEmails = contactRoles
                .Where(c => c.IsEventCoordinator) // signer-only excluded; both-roles kept
                .Select(c => c.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();

            // No Role-2 contact at all -> let the caller fall back rather than
            // returning an empty "authoritative" set.
            return coordinatorEmails.Count > 0 ? coordinatorEmails : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "ERP coordinator resolution for sponsor company {Company} failed; falling back to the hub role flags.",
                sponsorCompanyId);
            return null;
        }
    }
}
