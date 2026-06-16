using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// PUBLIC, no-login sessions overview (REQUIREMENTS § session management — public
/// filters). Lists the active edition's sessions with their linked speaker(s),
/// type, length, room and scheduled time. Filterable by session <b>type</b> and
/// <b>length</b> (and room), with a free-text search; each row deep-links to the
/// session's master-class logistics page (master classes only) and its public
/// "ask a question" page. Read-only — there is no write path to abuse.
///
/// Mobile-first (~360px) + a11y (semantic table, labelled filter controls,
/// <c>role="status"</c> result count). Empty state when no event is active or no
/// session matches the filters.
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly PublicSessionsService _svc;

    public IndexModel(PublicSessionsService svc) => _svc = svc;

    // --- Filters (querystring, GET-bound) ----------------------------------
    [BindProperty(SupportsGet = true)] public SessionType? FilterType { get; set; }
    [BindProperty(SupportsGet = true)] public SessionLength? FilterLength { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterRoom { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    // --- View state --------------------------------------------------------
    public PublicSessionsView? View { get; private set; }

    /// <summary>True when there is no active event (distinct from "no match").</summary>
    public bool NoActiveEvent { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await _svc.BuildAsync(FilterType, FilterLength, FilterRoom, Search, ct);
        NoActiveEvent = View is null;
        return Page();
    }

    // --- Display helpers (shared labels with the organizer page) ------------
    public static string Display(SessionType t) => t switch
    {
        SessionType.CommunityMasterClass => "Community Master Class",
        SessionType.CommunityTechSession => "Community Tech Session",
        SessionType.SponsorSession => "Sponsor Session",
        _ => t.ToString(),
    };

    public static string Display(SessionLength l) => l switch
    {
        SessionLength.FullDay => "Full day",
        SessionLength.TwentyMin => "20 min",
        SessionLength.FiftyMin => "50 min",
        SessionLength.SixtyMin => "60 min",
        _ => l.ToString(),
    };
}
