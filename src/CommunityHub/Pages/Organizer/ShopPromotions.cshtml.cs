using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1305 — choose the (up to) three products in the shop's COMMERCIAL CORNER.
/// </summary>
/// <remarks>
/// Operator 2026-09-26: <i>"a commercial corner in the webshop, where i can promote 3 products that i want to get sold,
/// i need an organizer page for that and info when one is sold so i can add a new one"</i>. A sold-out product leaves
/// the corner on its own; the sold-out mail says it was promoted and links here.
/// </remarks>
[Authorize]
public class ShopPromotionsModel : PageModel
{
    public const int Slots = 3;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public ShopPromotionsModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant, TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    public IReadOnlyList<ShopProduct> Candidates { get; private set; } = Array.Empty<ShopProduct>();
    public ShopProduct?[] Current { get; private set; } = new ShopProduct?[Slots];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        Candidates = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .Where(p => p.EventId == me.EventId && p.Status == ShopProductStatus.Published
                        && (p.PrimaryCategory == null || p.PrimaryCategory.Group != ShopGroup.OrganizerOnly))
            .OrderBy(p => p.PrimaryCategory!.Group).ThenBy(p => p.Name).ToListAsync(ct);
        foreach (var p in Candidates.Where(p => p.PromotedSlot is >= 1 and <= Slots))
            Current[p.PromotedSlot!.Value - 1] = p;
    }

    [Audit("Chose the sponsor shop's commercial corner", Category = AuditCategory.Admin, Action = "webshop.promotions", TargetType = "ShopProduct")]
    public async Task<IActionResult> OnPostAsync(int?[] slot, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var chosen = (slot ?? Array.Empty<int?>()).Take(Slots).ToArray();
        var ids = chosen.Where(i => i is not null).Select(i => i!.Value).ToList();
        if (ids.Count != ids.Distinct().Count()) { Message = "Choose three different products."; return RedirectToPage(); }

        var now = _clock.GetUtcNow();
        var all = await _db.ShopProducts.Where(p => p.EventId == me.EventId && (p.PromotedSlot != null || ids.Contains(p.Id))).ToListAsync(ct);
        foreach (var p in all) { p.PromotedSlot = null; p.UpdatedAt = now; }
        for (var n = 0; n < chosen.Length; n++)
        {
            if (chosen[n] is not int id) continue;
            var p = all.FirstOrDefault(x => x.Id == id);
            if (p is not null) p.PromotedSlot = n + 1;
        }
        await _db.SaveChangesAsync(ct);
        Message = ids.Count == 0 ? "The commercial corner is empty now." : $"The commercial corner now shows {ids.Count} product(s).";
        return RedirectToPage();
    }
}
