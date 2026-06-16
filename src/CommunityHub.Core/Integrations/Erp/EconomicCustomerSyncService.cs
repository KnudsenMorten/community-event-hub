using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Orchestrates e-conomic ERP customer create/sync, CVR validation on create,
/// and ERP contact/role create + webshop sync (REQUIREMENTS §7a). It maps a
/// Company Manager company → an <see cref="ErpCustomer"/> (name always resolved
/// through <see cref="SponsorCompanyName"/>), validates the CVR via
/// <see cref="ICvrValidator"/>, then drives the <see cref="IEconomicErpClient"/>
/// seam — recording the outcome + an idempotency link in <see cref="ErpCustomerLink"/>.
///
/// When the ERP client cannot write (TESTMODE, or live creds not wired) it
/// records <see cref="ErpSyncOutcome.WouldCreate"/> rather than faking a call.
/// </summary>
public sealed class EconomicCustomerSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly IEconomicErpClient _erp;
    private readonly ICvrValidator _cvr;
    private readonly TimeProvider _clock;
    private readonly ILogger<EconomicCustomerSyncService> _log;

    public EconomicCustomerSyncService(
        CommunityHubDbContext db,
        IEconomicErpClient erp,
        ICvrValidator cvr,
        TimeProvider clock,
        ILogger<EconomicCustomerSyncService> log)
    {
        _db = db;
        _erp = erp;
        _cvr = cvr;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Map a Company Manager company into the ERP customer shape the hub can
    /// resolve. Name ALWAYS resolves through the public→legal→billing→"Company"
    /// chain. Pure mapping — no I/O.
    /// </summary>
    public static ErpCustomer MapCustomer(
        string companyId, CompanyManagerCompany? cm, string? billingName, string? contactEmail)
    {
        var name = SponsorCompanyName.Resolve(cm?.PublicName, cm?.Name, billingName, companyId);
        return new ErpCustomer(
            CompanyId: companyId,
            Name: name,
            CorporateIdentificationNumber: NullIfBlank(cm?.CorporateIdentificationNumber),
            Currency: NullIfBlank(cm?.Currency),
            VatZone: NullIfBlank(cm?.VatZone),
            Email: NullIfBlank(contactEmail),
            ErpCustomerNumber: NullIfBlank(cm?.ErpCustomerNumber));
    }

    /// <summary>
    /// Validate + create/sync one sponsor company in e-conomic. CVR validation
    /// is a hard gate on FIRST create: a company that has never been linked is
    /// not created in the ERP with an invalid CVR. A company that is already
    /// linked re-syncs even if its CVR is now blank (the create gate already ran).
    /// </summary>
    public async Task<ErpCustomerResult> SyncCustomerAsync(
        int eventId, ErpCustomer customer, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var link = await _db.ErpCustomerLinks.SingleOrDefaultAsync(
            l => l.EventId == eventId && l.SponsorCompanyId == customer.CompanyId, ct);

        // --- CVR validation (REQUIREMENTS §7a — validation on sponsor create) -
        var alreadyLinked = link is { ErpCustomerNumber.Length: > 0 };
        var cvrResult = await _cvr.ValidateAsync(customer.CorporateIdentificationNumber, ct);

        link ??= new ErpCustomerLink
        {
            EventId = eventId,
            SponsorCompanyId = customer.CompanyId,
            CreatedAt = now,
        };
        link.CompanyName = customer.Name;
        link.Cvr = string.IsNullOrEmpty(cvrResult.Normalized) ? null : cvrResult.Normalized;
        link.CvrValid = cvrResult.IsValid;
        link.CvrValidationReason = cvrResult.Reason;
        link.CvrOfflineOnly = cvrResult.IsValid && !cvrResult.RegistryChecked;
        link.LastSyncedAt = now;
        if (link.Id == 0) _db.ErpCustomerLinks.Add(link);

        if (!alreadyLinked && !cvrResult.IsValid)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(
                "ERP customer create blocked for '{Company}': CVR invalid ({Reason}).",
                customer.Name, cvrResult.Reason);
            return new ErpCustomerResult(customer, ErpSyncOutcome.Failed, null,
                $"cvr-invalid:{cvrResult.Reason}");
        }

        // --- ERP customer create / update ------------------------------------
        if (!_erp.CanWrite)
        {
            await _db.SaveChangesAsync(ct);
            return new ErpCustomerResult(customer,
                alreadyLinked ? ErpSyncOutcome.AlreadyExists : ErpSyncOutcome.WouldCreate,
                link.ErpCustomerNumber.Length > 0 ? link.ErpCustomerNumber : null,
                _erp.CanWrite ? null : "erp-write-disabled");
        }

        try
        {
            // Prefer the local link, then a Company-Manager-supplied number,
            // then an ERP-side lookup — so a re-run never double-creates.
            var existing = link.ErpCustomerNumber.Length > 0
                ? link.ErpCustomerNumber
                : customer.ErpCustomerNumber
                  ?? await _erp.FindCustomerNumberAsync(customer, ct);

            ErpSyncOutcome outcome;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                await _erp.UpdateCustomerAsync(existing!, customer, ct);
                link.ErpCustomerNumber = existing!;
                outcome = ErpSyncOutcome.Updated;
            }
            else
            {
                var number = await _erp.CreateCustomerAsync(customer, ct);
                link.ErpCustomerNumber = number;
                outcome = ErpSyncOutcome.Created;
            }

            link.LastSyncedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return new ErpCustomerResult(customer, outcome, link.ErpCustomerNumber, null);
        }
        catch (Exception ex)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogError(ex, "ERP customer sync failed for '{Company}'.", customer.Name);
            return new ErpCustomerResult(customer, ErpSyncOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>
    /// Create/update one ERP contact (with its role) + mirror it to the webshop.
    /// Requires the company to already be linked (have an ERP customer number).
    /// </summary>
    public async Task<ErpContactResult> SyncContactAsync(
        int eventId, ErpContact contact, CancellationToken ct = default)
    {
        var link = await _db.ErpCustomerLinks.SingleOrDefaultAsync(
            l => l.EventId == eventId && l.SponsorCompanyId == contact.CompanyId, ct);

        if (link is null || link.ErpCustomerNumber.Length == 0)
        {
            return new ErpContactResult(contact, ErpSyncOutcome.Failed, "no-erp-customer-link");
        }

        if (!_erp.CanWrite)
        {
            return new ErpContactResult(contact, ErpSyncOutcome.WouldCreate, "erp-write-disabled");
        }

        try
        {
            await _erp.CreateOrUpdateContactAsync(link.ErpCustomerNumber, contact, ct);
            return new ErpContactResult(contact, ErpSyncOutcome.Created, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ERP contact sync failed for {Email}.", contact.Email);
            return new ErpContactResult(contact, ErpSyncOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Map a Company Manager user into an ERP contact, role derived from the company's signer/coordinator ids.</summary>
    public static ErpContact MapContact(
        string companyId, CompanyManagerUser user, CompanyManagerCompany? company)
    {
        var role = ErpContactRole.Contact;
        if (company is not null)
        {
            if (user.UserId == company.DefaultSignerUserId) role = ErpContactRole.Signer;
            else if (user.UserId == company.EventCoordinationDefaultContactUserId) role = ErpContactRole.EventCoordinator;
        }
        var name = !string.IsNullOrWhiteSpace(user.FullName) ? user.FullName : user.DisplayName;
        return new ErpContact(companyId, user.Email, name, role);
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
