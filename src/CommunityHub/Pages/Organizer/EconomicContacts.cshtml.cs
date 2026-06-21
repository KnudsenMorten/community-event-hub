using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
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

    public bool AccessDenied { get; private set; }
    public bool NotConfigured { get; private set; }
    public string? Message { get; private set; }

    public string? Search { get; private set; }
    public int? SelectedCustomer { get; private set; }
    public string? SelectedCustomerName { get; private set; }

    public IReadOnlyList<EconomicCustomerRow> Customers { get; private set; } = Array.Empty<EconomicCustomerRow>();
    public IReadOnlyList<EconomicContactAdminService.ContactView> Contacts { get; private set; }
        = Array.Empty<EconomicContactAdminService.ContactView>();

    public static readonly ErpContactRole[] Roles =
        { ErpContactRole.Contact, ErpContactRole.Signer, ErpContactRole.EventCoordinator };

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
            var match = await _admin.ListCustomersAsync(cid.ToString(), ct);
            SelectedCustomerName = match.FirstOrDefault(c => c.CustomerNumber == cid)?.Name;
        }
        else
        {
            Customers = await _admin.ListCustomersAsync(search, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        int customer, string name, string email, string? phone,
        int role, string? notes, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }
        if (string.IsNullOrWhiteSpace(name))
            return RedirectToPage(new { customer, msg = "Name is required." });

        await _admin.CreateAsync(customer,
            new EconomicContactInput(name.Trim(), (email ?? "").Trim(), phone?.Trim()),
            (ErpContactRole)role, notes, me.Email, ct);
        return RedirectToPage(new { customer, msg = "Contact added." });
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        int customer, int contact, string name, string email, string? phone,
        int role, string? notes, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }

        await _admin.UpdateAsync(customer, contact,
            new EconomicContactInput((name ?? "").Trim(), (email ?? "").Trim(), phone?.Trim()),
            (ErpContactRole)role, notes, me.Email, ct);
        return RedirectToPage(new { customer, msg = "Contact updated." });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int customer, int contact, CancellationToken ct)
    {
        var me = Guard();
        if (_participant.Current is null) return RedirectToPage("/Login");
        if (me is null) return Page();
        if (!_admin.CanWrite) { NotConfigured = true; return Page(); }

        await _admin.DeleteAsync(customer, contact, ct);
        return RedirectToPage(new { customer, msg = "Contact deleted." });
    }
}
