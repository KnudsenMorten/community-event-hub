using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1311 — one company, for organizers: e-conomic data (read-only), CEH data, the default signer and default event
/// coordinator, and the linked people. Operator 2026-09-27: <i>"i need a place where i can set the default signer and
/// default event coordinator for a potential sponsor, similar to what i have in cm today"</i>.
/// </summary>
[Authorize]
public class ShopCompanyModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopCompanyDirectoryService _directory;

    private readonly ShopRegistrationService? _registration;

    public ShopCompanyModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopCompanyDirectoryService directory,
        ShopRegistrationService? registration = null)
    {
        _registration = registration;
        _db = db;
        _participant = participant;
        _directory = directory;
    }

    public sealed record Linked(int ParticipantId, string Name, string Email, bool IsSigner, bool IsCoordinator);

    [BindProperty(SupportsGet = true)] public string? Id { get; set; }
    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    public ShopCompany? Company { get; private set; }
    /// <summary>§1312 — tag the company's people (signer, coordinators) in its SoMe posts.</summary>
    public bool SoMeTagContacts { get; private set; } = true;
    public IReadOnlyList<ShopPersonOption> People { get; private set; } = Array.Empty<ShopPersonOption>();
    public IReadOnlyList<Linked> LinkedPeople { get; private set; } = Array.Empty<Linked>();
    public IReadOnlyList<ShopKnownContact> NotLinked { get; private set; } = Array.Empty<ShopKnownContact>();
    public string? CurrentSignerKey { get; private set; }
    public string? CurrentCoordinatorKey { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return Page();
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        Company = await _db.ShopCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.EventId == me.EventId && c.SponsorCompanyId == Id, ct);
        SoMeTagContacts = await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.EventId == me.EventId && i.SponsorCompanyId == Id).Select(i => (bool?)i.SoMeTagContacts).FirstOrDefaultAsync(ct) ?? true;
        if (Company is null) return NotFound();

        var links = await _db.SponsorCompanyContacts.AsNoTracking().Where(l => l.EventId == me.EventId && l.SponsorCompanyId == Id)
            .Select(l => new Linked(l.ParticipantId, l.Participant.FullName, l.Participant.Email, l.IsSigner, l.IsEventCoordinator))
            .ToListAsync(ct);
        LinkedPeople = links.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        People = await _directory.PeopleForAsync(me.EventId, Id!, ct);
        CurrentSignerKey = links.FirstOrDefault(l => l.IsSigner) is { } s ? $"p:{s.ParticipantId}" : null;
        CurrentCoordinatorKey = links.FirstOrDefault(l => l.IsCoordinator) is { } c ? $"p:{c.ParticipantId}" : null;
        var inHub = links.Select(l => l.Email.ToLowerInvariant()).ToHashSet();
        NotLinked = (await _db.ShopKnownContacts.AsNoTracking().Where(k => k.EventId == me.EventId && k.SponsorCompanyId == Id).ToListAsync(ct))
            .Where(k => !inHub.Contains(k.Email.ToLowerInvariant())).OrderBy(k => k.FullName).ToList();
        return Page();
    }

    /// <summary>§1312 — create (or link by CVR) the e-conomic customer of a registered company that has none.</summary>
    [Audit("Created a company in e-conomic", Category = AuditCategory.Admin, Action = "webshop.company-erp-create", TargetType = "ShopCompany")]
    public async Task<IActionResult> OnPostErpAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (_registration is null) { Message = "Not available on this server."; return RedirectToPage(new { Id }); }
        Message = (await _registration.EnsureInErpAsync(me.EventId, Id!, ct)).Message;
        return RedirectToPage(new { Id });
    }

    [Audit("Edited a company's CEH data", Category = AuditCategory.Admin, Action = "webshop.company-edit", TargetType = "ShopCompany")]
    public async Task<IActionResult> OnPostDataAsync(string? publicName, string? website, string? linkedIn, string? twitter,
        string? billingReference, string? notes, bool soMeTagContacts, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _directory.UpdateCehDataAsync(me.EventId, Id!, publicName, website, linkedIn, twitter, billingReference, notes, ct, soMeTagContacts)).Message;
        return RedirectToPage(new { Id });
    }

    [Audit("Set a company's default signer and event coordinator", Category = AuditCategory.Admin, Action = "webshop.company-defaults", TargetType = "ShopCompany")]
    public async Task<IActionResult> OnPostDefaultsAsync(string? signerKey, string? coordinatorKey, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _directory.SetDefaultsAsync(me.EventId, Id!, signerKey, coordinatorKey, ct)).Message;
        return RedirectToPage(new { Id });
    }

    [Audit("Linked a person to a company", Category = AuditCategory.Admin, Action = "webshop.company-link", TargetType = "ShopCompany")]
    public async Task<IActionResult> OnPostLinkAsync(string? knownKey, string? email, string? name, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _directory.LinkPersonAsync(me.EventId, Id!, knownKey, email, name, ct)).Message;
        return RedirectToPage(new { Id });
    }

    [Audit("Removed a person from a company", Category = AuditCategory.Admin, Action = "webshop.company-unlink", TargetType = "ShopCompany")]
    public async Task<IActionResult> OnPostUnlinkAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _directory.UnlinkPersonAsync(me.EventId, Id!, participantId, ct)).Message;
        return RedirectToPage(new { Id });
    }
}
