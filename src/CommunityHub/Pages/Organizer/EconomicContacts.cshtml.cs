using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer GUI to manage the CONTACTS on an existing e-conomic customer
/// (REQUIREMENTS §6) — a grid of all e-conomic customers, then per customer the
/// live add / edit / delete of contacts, so the organizer never logs into
/// e-conomic. Email/name/phone write live to e-conomic; role + notes are kept
/// hub-side. Organizer-gated; every write checks the live client is configured.
/// </summary>
[Authorize]
public class EconomicContactsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EconomicContactAdminService _admin;

    public EconomicContactsModel(
        ICurrentParticipantAccessor participant, EconomicContactAdminService admin)
    {
        _participant = participant;
        _admin = admin;
    }

    /// <summary>
    /// §501b (operator: <i>"verify also the functionality matches the new method where you sync as
    /// well?"</i>) — it did NOT. §482b/§493b pushed an ERP contact change downstream when a SPONSOR
    /// edited contacts on Company Details, but this ORGANIZER page wrote to the ERP and triggered
    /// nothing. An organizer adding a contact here hit exactly the bug the sponsor page had already
    /// been fixed for: correct in the ERP, invisible in the hub until a scheduled run.
    ///
    /// <para>Queued, never awaited (§493b): these are several sequential external calls and must not
    /// sit in front of the page load. Its OWN DI scope, because the request scope and its DbContext
    /// are disposed the moment the response is written.</para>
    ///
    /// <para>Fail-soft: the ERP write is the system of record and has already succeeded, so a sync
    /// hiccup is logged and the scheduled reconcile catches up. It must never turn a saved change
    /// into an error.</para>
    /// </summary>
    private void PushContactChangeDownstream(int erpCustomerNumber)
    {
        var scopeFactory = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var logger = HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger("EconomicContacts");

        _ = Task.Run(async () =>
        {
            // NOT the request token — it is cancelled as the response completes, which would abort
            // the work we just deliberately moved off the request.
            var ct = CancellationToken.None;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;

                // Leg 1 — ERP → Company Manager, scoped to THIS customer (§482b).
                var erpToCm = sp.GetService<CommunityHub.Core.Integrations.Erp.ErpWebshopContactSyncService>();
                if (erpToCm is not null)
                {
                    await erpToCm.SyncAsync(onlyCustomerNumber: erpCustomerNumber, ct: ct);
                }

                // Leg 2 — Company Manager → hub, for the company carrying this ERP number.
                var cm = sp.GetService<CommunityHub.Core.Integrations.CompanyManagerClient>();
                var cmToHub = sp.GetService<CommunityHub.Core.Integrations.SponsorContactSyncService>();
                var db = sp.GetService<CommunityHub.Core.Data.CommunityHubDbContext>();
                if (cm is null || cmToHub is null || db is null) return;

                var eventId = await db.Events.Where(e => e.IsActive)
                    .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
                if (eventId is null) return;

                var companies = await cm.ListCompaniesAsync(ct);
                var match = companies.FirstOrDefault(c =>
                    int.TryParse(c.ErpCustomerNumber, out var n) && n == erpCustomerNumber);
                if (match is not null)
                {
                    await cmToHub.SyncCompanyAsync(eventId.Value, match.Id, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Background contact sync failed for ERP customer {Erp}; the scheduled reconcile will catch up.",
                    erpCustomerNumber);
            }
        });
    }

    public bool AccessDenied { get; private set; }
    public bool NotConfigured { get; private set; }
    public string? Message { get; private set; }

    public string? Search { get; private set; }
    public int? SelectedCustomer { get; private set; }
    public string? SelectedCustomerName { get; private set; }

    public IReadOnlyList<EconomicCustomerRow> Customers { get; private set; } = Array.Empty<EconomicCustomerRow>();
    public IReadOnlyList<EconomicContactAdminService.ContactView> Contacts { get; private set; }
        = Array.Empty<EconomicContactAdminService.ContactView>();

    /// <summary>Sponsors live in e-conomic customer group 1.</summary>
    public const int SponsorCustomerGroup = 1;

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null || me.Role != ParticipantRole.Organizer) { AccessDenied = me is not null; return null; }
        return me;
    }

    public async Task<IActionResult> OnGetAsync(
        string? search, int? customer, string? msg, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();   // AccessDenied

        Message = msg;
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }

        Search = search;
        SelectedCustomer = customer;

        if (customer is int cid)
        {
            Contacts = await _admin.ListContactsAsync(cid, ct);
            var match = await _admin.ListCustomersAsync(cid.ToString(), SponsorCustomerGroup, ct);
            SelectedCustomerName = match.FirstOrDefault(c => c.CustomerNumber == cid)?.Name;
        }
        else
        {
            // Sponsors only — e-conomic customer group 1.
            Customers = await _admin.ListCustomersAsync(search, SponsorCustomerGroup, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        int customer, string name, string? email, string? phone,
        bool signer, bool coordinator, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        // Writes require a REAL organizer — acting-as / secretary sessions carry
        // Role==Organizer but must never mutate (§234 / OrganizerAuth).
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }
        if (string.IsNullOrWhiteSpace(name))
            return RedirectToPage(new { customer, msg = "Name is required." });

        try { await _admin.CreateAsync(customer, name, email, phone, signer, coordinator, ct); }
        catch (CommunityHub.Core.Integrations.Erp.EconomicApiException ex)
        { return RedirectToPage(new { customer, msg = ex.Message }); }
        PushContactChangeDownstream(customer);   // §501b — same downstream push as the sponsor page
        return RedirectToPage(new { customer, msg = "Contact added in backend." });
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        int customer, int contact, string name, string? email, string? phone,
        bool signer, bool coordinator, string? notes, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }

        // §643 — a REJECTED EDIT IS NOT A CRASHED SERVER. e-conomic answering 400 used to escape
        // as an unhandled HttpRequestException and render HTTP 500, so a routine data problem
        // looked like the hub was broken and said nothing about the cause.
        try
        {
            await _admin.UpdateAsync(customer, contact, name ?? string.Empty, email, phone,
                signer, coordinator, notes, ct);
        }
        catch (CommunityHub.Core.Integrations.Erp.EconomicApiException ex)
        { return RedirectToPage(new { customer, msg = ex.Message }); }
        PushContactChangeDownstream(customer);   // §501b
        return RedirectToPage(new { customer, msg = "Contact updated in backend." });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int customer, int contact, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }

        try { await _admin.DeleteAsync(customer, contact, ct); }
        catch (CommunityHub.Core.Integrations.Erp.EconomicApiException ex)
        { return RedirectToPage(new { customer, msg = ex.Message }); }
        PushContactChangeDownstream(customer);   // §501b
        return RedirectToPage(new { customer, msg = "Contact deleted." });
    }
}
