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
/// §1305 item 10 — the reservation-rights import module: export what each company had in this edition, import it in
/// the next edition's environment, fix unmatched items, and see who has reserved.
/// </summary>
[Authorize]
public class ShopReservationsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopEntitlementService _rights;
    private readonly ShopSettings _settings;

    public ShopReservationsModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopEntitlementService rights, ShopSettings settings)
    {
        _db = db;
        _participant = participant;
        _rights = rights;
        _settings = settings;
    }

    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    public IReadOnlyList<ShopEntitlement> Rights { get; private set; } = Array.Empty<ShopEntitlement>();
    public IReadOnlyList<ShopProduct> Products { get; private set; } = Array.Empty<ShopProduct>();
    public Core.Config.ShopReservationWindow Window => _settings.Shop().Reservation;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        Rights = await _db.ShopEntitlements.AsNoTracking().Include(e => e.MatchedProduct)
            .Where(e => e.EventId == me.EventId).OrderBy(e => e.CompanyName).ThenBy(e => e.ProductName).ToListAsync(ct);
        Products = await _db.ShopProducts.AsNoTracking()
            .Where(p => p.EventId == me.EventId && p.Status == ShopProductStatus.Published).OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var code = _settings.Shop().Event.Name;
        var json = await _rights.ExportAsync(me.EventId, string.IsNullOrWhiteSpace(code) ? "edition" : code, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"reservation-rights-{code}.json");
    }

    [Audit("Imported reservation rights from the previous edition", Category = AuditCategory.Admin, Action = "webshop.reservations-import", TargetType = "ShopEntitlement")]
    public async Task<IActionResult> OnPostImportAsync(IFormFile? file, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (file is null || file.Length == 0 || file.Length > 10 * 1024 * 1024) { Message = "Choose the exported JSON file."; return RedirectToPage(); }
        using var reader = new StreamReader(file.OpenReadStream());
        try
        {
            var r = await _rights.ImportAsync(me.EventId, await reader.ReadToEndAsync(ct), ct);
            Message = $"Imported {r.Items} item(s) for {r.Companies} company(ies) — {r.CompaniesCreated} company(ies) and {r.People} person(s) created, "
                      + $"{r.Matched} matched to a product." + (r.Unmatched.Count > 0 ? $" Not matched: {string.Join("; ", r.Unmatched.Take(20))}" : "");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            Message = "That file is not a reservation-rights export.";
        }
        return RedirectToPage();
    }

    [Audit("Matched a reservation right to a product", Category = AuditCategory.Admin, Action = "webshop.reservations-match", TargetType = "ShopEntitlement")]
    public async Task<IActionResult> OnPostMatchAsync(int id, int? productId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var right = await _db.ShopEntitlements.FirstOrDefaultAsync(e => e.Id == id && e.EventId == me.EventId, ct);
        if (right is null) return NotFound();
        right.MatchedProductId = productId;
        await _db.SaveChangesAsync(ct);
        Message = "Saved.";
        return RedirectToPage();
    }
}
