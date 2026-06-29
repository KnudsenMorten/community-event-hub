using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// ERP→webshop reconcile (the C# port of Sync-ERP-Contacts-to-Webshop.ps1). For
/// every group-1 (sponsor) e-conomic customer it:
///   - ensures every ERP contact (with an email) exists as a Company Manager user
///     and is linked to the webshop company;
///   - sets the company's DEFAULT SIGNER + DEFAULT EVENT COORDINATOR from the first
///     ERP contact holding Role 1 / Role 2 (only when the webshop default is empty,
///     so a curated value is never overwritten);
///   - INVARIANT: a sponsor must always have a default signer AND a default event
///     coordinator. When the ERP contacts can't supply one (no Role:1 / Role:2),
///     it emails an alert to the organizer so they fix it in e-conomic.
/// e-conomic is the master; this only writes to the webshop. Idempotent.
/// </summary>
public sealed class ErpWebshopContactSyncService
{
    private readonly EconomicContactAdminService _erp;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly IEmailSender _email;
    private readonly ILogger<ErpWebshopContactSyncService> _log;

    /// <summary>Where the "fix this in e-conomic" alerts go.</summary>
    public const string AlertEmail = "mok@expertslive.dk";
    private const int SponsorGroup = 1;

    public ErpWebshopContactSyncService(
        EconomicContactAdminService erp, CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        IEmailSender email, ILogger<ErpWebshopContactSyncService> log)
    {
        _erp = erp;
        _cm = cm;
        _cmOptions = cmOptions;
        _email = email;
        _log = log;
    }

    public bool CanRun => _erp.CanWrite && _cmOptions.Enabled;

    public sealed record SyncResult(
        bool Enabled, int Customers, int UsersCreated, int DefaultsSet, int Alerts, List<string> AlertNotes);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (!CanRun) return new(false, 0, 0, 0, 0, notes);

        var customers = await _erp.ListCustomersAsync(null, SponsorGroup, ct);
        var companies = await _cm.ListCompaniesAsync(ct);
        var byErp = companies
            .Where(c => !string.IsNullOrWhiteSpace(c.ErpCustomerNumber))
            .GroupBy(c => c.ErpCustomerNumber.Trim())
            .ToDictionary(g => g.Key, g => g.First());

        int usersCreated = 0, defaultsSet = 0;

        foreach (var cu in customers)
        {
          // PER-COMPANY GRACEFUL CATCH (mirrors SponsorOrderPullService's per-folder
          // pattern): a single company's failed call — e.g. a transient 503 from
          // GetCompanyUsersAsync that survives the HttpClient retry — becomes a logged
          // warning + an alert-note (Alerts++) and the loop CONTINUES to the next
          // company, instead of throwing out of the whole reconcile (the 2026-06-27
          // incident, where one 503 crashed every remaining company). A real host
          // shutdown (our ct) still propagates.
          try
          {
            var key = cu.CustomerNumber.ToString();
            if (!byErp.TryGetValue(key, out var company))
            {
                notes.Add($"{cu.Name} (e-conomic #{cu.CustomerNumber}): no matching webshop company (erp_customer_number).");
                continue;
            }

            var contacts = await _erp.ListContactsAsync(cu.CustomerNumber, ct);

            // Existing webshop users on this company, by lower-cased email.
            var wsUsers = await _cm.GetCompanyUsersAsync(company.Id, ct);
            var emailToUserId = wsUsers
                .Where(u => !string.IsNullOrWhiteSpace(u.Email))
                .GroupBy(u => u.Email.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().UserId);

            // Ensure each ERP contact with an email exists + is linked.
            foreach (var c in contacts)
            {
                if (string.IsNullOrWhiteSpace(c.Email)) continue;
                var em = c.Email.Trim().ToLowerInvariant();
                if (emailToUserId.ContainsKey(em)) continue;
                var (first, last) = SplitName(c.Name);
                try
                {
                    var uid = await _cm.CreateUserAsync(c.Email.Trim(), first, last, company.Id, ct);
                    if (uid > 0) { emailToUserId[em] = uid; usersCreated++; }
                    // uid <= 0 means Company Manager rejected the create — almost always
                    // because the email already exists as a user (a person can be linked to
                    // only ONE company in the webshop). That's expected, NOT an error: just
                    // skip it (no alert) per operator 2026-06-24. The org handles shared
                    // people via a manual override (e.g. FASTTRACK).
                    else _log.LogInformation(
                        "ERP sync: skipped {Email} for {Customer} — user already exists in the webshop (1-company limit).",
                        c.Email, cu.Name);
                }
                catch (Exception ex) { _log.LogInformation(ex, "ERP sync: skipped {Email} — webshop create rejected (already exists).", c.Email); }
            }

            // Resolve defaults from the FIRST contact holding each role (list order).
            int? signer = ResolveUserId(contacts.FirstOrDefault(c => c.IsSigner)?.Email, emailToUserId);
            int? coordinator = ResolveUserId(contacts.FirstOrDefault(c => c.IsEventCoordinator)?.Email, emailToUserId);

            // INVARIANT alerts — a role contact is missing in e-conomic.
            if (!contacts.Any(c => c.IsSigner))
                notes.Add($"{cu.Name} (e-conomic #{cu.CustomerNumber}): no contact with Signer role (Role:1) — add it in e-conomic.");
            if (!contacts.Any(c => c.IsEventCoordinator))
                notes.Add($"{cu.Name} (e-conomic #{cu.CustomerNumber}): no contact with Event Coordinator role (Role:2) — add it in e-conomic.");

            // Set defaults only when the webshop value is empty (never overwrite a curated one).
            var fields = new Dictionary<string, object?>();
            if (company.DefaultSignerUserId <= 0 && signer is int s) fields["default_signer_id"] = s;
            if (company.EventCoordinationDefaultContactUserId <= 0 && coordinator is int co) fields["event_coordination_default_contact_id"] = co;
            if (fields.Count > 0)
            {
                try { if (await _cm.UpdateCompanyAsync(company.Id, fields, ct)) defaultsSet++; }
                catch (Exception ex) { _log.LogWarning(ex, "ERP sync: set defaults failed for company {Co}.", company.Id); }
            }
          }
          catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
          {
            // One company failed (after the HttpClient already retried any transient
            // upstream error). Log + note it and KEEP GOING so the rest of the fleet
            // still reconciles; the next run reconverges this company.
            _log.LogWarning(ex,
                "ERP sync: company {Customer} (e-conomic #{Num}) failed; skipping and continuing.",
                cu.Name, cu.CustomerNumber);
            notes.Add($"{cu.Name} (e-conomic #{cu.CustomerNumber}): Company Manager call failed "
                + $"({ex.Message}); skipped this company — it will retry on the next run.");
          }
        }

        if (notes.Count > 0)
            await SendAlertAsync(notes, ct);

        return new SyncResult(true, customers.Count, usersCreated, defaultsSet, notes.Count, notes);
    }

    private async Task SendAlertAsync(List<string> notes, CancellationToken ct)
    {
        try
        {
            string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
            var items = string.Concat(notes.Select(n => $"<li>{Enc(n)}</li>"));
            var html = "<p>The ERP→webshop sponsor reconcile found items needing attention "
                + "(mostly contacts missing a Signer/Event-Coordinator role in e-conomic):</p>"
                + $"<ul>{items}</ul><p>Fix the role in e-conomic (contact notes <code>Role:1,2</code>) and the next sync will set the default.</p>";
            await _email.SendAsync(AlertEmail, "Sponsor ERP/webshop reconcile — action needed [ELDK27]", html, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "ERP sync: alert email to {To} failed.", AlertEmail); }
    }

    private static int? ResolveUserId(string? email, IReadOnlyDictionary<string, int> map)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        return map.TryGetValue(email.Trim().ToLowerInvariant(), out var id) && id > 0 ? id : null;
    }

    private static (string First, string Last) SplitName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0) return ("Contact", "-");
        var parts = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? (parts[0], "-") : (parts[0], string.Join(' ', parts.Skip(1)));
    }
}
