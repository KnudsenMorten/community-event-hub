using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// PUBLIC, no-login detail page for a single session (<c>/Sessions/{id}</c>,
/// REQUIREMENTS §21 PUBLIC). Shareable/SEO-friendly per-session page: title,
/// abstract, type/length/room/time, the linked speaker(s) (each cross-linked to
/// the public speaker page when that speaker is published — the same hard gate as
/// the lineup), and the session's deep-links: its master-class logistics page
/// (master class only) and its public "ask a question" page. Read-only.
///
/// Mobile-first (~360px) + a11y. 404s when there is no active event, the id is not
/// in the active edition, or it is a service session (breaks/lunch).
/// </summary>
[AllowAnonymous]
public class DetailModel : PageModel
{
    private readonly PublicSessionsService _svc;

    public DetailModel(PublicSessionsService svc) => _svc = svc;

    public PublicSessionDetail? Session { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Session = await _svc.GetByIdAsync(id, ct);
        if (Session is null) return NotFound();
        return Page();
    }

    // --- Display helpers (shared labels with the overview page) -------------
    public static string Display(SessionType t) => IndexModel.Display(t);
    public static string Display(SessionLength l) => IndexModel.Display(l);
}
