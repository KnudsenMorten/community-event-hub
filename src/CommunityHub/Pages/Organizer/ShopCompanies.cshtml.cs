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
/// §1309 — the company directory for this edition: who is a SPONSOR and who is only a POTENTIAL sponsor, the pre-load from
/// the old webshop's company module, and the join requests that wait for an organizer.
/// </summary>
[Authorize]
public class ShopCompaniesModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopCompanyDirectoryService _directory;

    public ShopCompaniesModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopCompanyDirectoryService directory)
    {
        _db = db;
        _participant = participant;
        _directory = directory;
    }

    public sealed record Row(ShopCompany Company, int HubPeople, int KnownPeople);

    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    public IReadOnlyList<Row> Companies { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<(ShopJoinRequest Request, string Company)> Pending { get; private set; } = Array.Empty<(ShopJoinRequest, string)>();
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    /// <summary>§1312 — search (operator 2026-09-30: "where is the search for sponsor"): legal / public name, company #, e-conomic #, CVR.</summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public static bool Matches(ShopCompany c, string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        var term = q.Trim().TrimStart('#');
        return new[] { c.LegalName, c.PublicName, c.SponsorCompanyId, c.ErpCustomerNumber, c.Cvr }
            .Any(v => v is not null && v.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        var eventId = me.EventId;

        // §1312 — a WITHDRAWN company is not listed (operator 2026-09-29, on the duplicate Master Packager #110: "withdraw it
        // or set as inactive" · "dont delete anything"). The row stays; it is only hidden.
        var withdrawn = (await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EventId == eventId && s.Status == SponsorStatus.Withdrawn)
                .Select(s => s.SponsorCompanyId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var companies = (await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == eventId).ToListAsync(ct))
            .Where(c => !withdrawn.Contains(c.SponsorCompanyId)).ToList();
        var hub = await _db.SponsorCompanyContacts.AsNoTracking().Where(l => l.EventId == eventId)
            .GroupBy(l => l.SponsorCompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var known = await _db.ShopKnownContacts.AsNoTracking().Where(k => k.EventId == eventId)
            .GroupBy(k => k.SponsorCompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var h = hub.ToDictionary(x => x.Key, x => x.N);
        var k = known.ToDictionary(x => x.Key, x => x.N);
        Companies = companies
            .Where(c => Show switch { "sponsors" => c.State == ShopCompanyState.Sponsor, "potential" => c.State != ShopCompanyState.Sponsor, _ => true })
            .Where(c => Matches(c, Q))
            .Select(c => new Row(c, h.GetValueOrDefault(c.SponsorCompanyId), k.GetValueOrDefault(c.SponsorCompanyId)))
            .OrderBy(r => r.Company.State == ShopCompanyState.Sponsor ? 0 : 1)
            .ThenBy(r => r.Company.LegalName, StringComparer.OrdinalIgnoreCase).ToList();

        var names = companies.ToDictionary(c => c.SponsorCompanyId, c => c.OrganizerLabel);
        Pending = (await _db.ShopJoinRequests.AsNoTracking().Where(r => r.EventId == eventId && r.Approved == null)
                .OrderBy(r => r.RequestedAt).ToListAsync(ct))
            .Select(r => (r, names.GetValueOrDefault(r.SponsorCompanyId) ?? r.SponsorCompanyId)).ToList();
    }

    /// <summary>
    /// §1312 — set a company's PUBLIC name from the table (operator 2026-09-29: "remember to add a place in ceh, where i can
    /// edit this field"). Once set, nothing automatic changes it; it reaches SoMe, Zoho, the event site and the app.
    /// </summary>
    [Audit("Set a company's public name", Category = AuditCategory.Admin, Action = "webshop.company-public-name", TargetType = "ShopCompany")]
    public async Task<IActionResult> OnPostPublicNameAsync(string companyId, string? publicName, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _directory.SetPublicNameAsync(me.EventId, companyId, publicName, ct)).Message;
        return RedirectToPage(new { Show, Q });
    }

    [Audit("Decided a join request", Category = AuditCategory.Admin, Action = "webshop.join-decide", TargetType = "ShopJoinRequest")]
    public async Task<IActionResult> OnPostDecideAsync(int requestId, bool approve, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _directory.DecideAsync(me.EventId, requestId, approve, me.Email, note, ct)).Message;
        return RedirectToPage();
    }
}
