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
    // §299.8/b7: the per-edition length quick-picks (label + minutes) for the
    // config-driven length filter. Pure config, no DB — the page stays fast.
    private readonly CommunityHub.Core.Config.SessionOptionsService _options;

    public IndexModel(
        PublicSessionsService svc, CommunityHub.Core.Config.SessionOptionsService options)
    {
        _svc = svc;
        _options = options;
    }

    // --- Filters (querystring, GET-bound) ----------------------------------
    [BindProperty(SupportsGet = true)] public SessionType? FilterType { get; set; }
    /// <summary>§299.8/b7 — the length filter is MINUTES now (the config quick-pick
    /// values), replacing the retired enum-bucket filter.</summary>
    [BindProperty(SupportsGet = true)] public int? FilterLength { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterRoom { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterTimeslot { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterTrack { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterLevel { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    /// <summary>§299.8/b7 — the config length quick-picks driving the filter options.</summary>
    public IReadOnlyList<CommunityHub.Core.Config.SessionLengthOption> LengthQuickPicks =>
        _options.LengthQuickPicks;

    // --- View state --------------------------------------------------------
    public PublicSessionsView? View { get; private set; }

    /// <summary>True when there is no active event (distinct from "no match").</summary>
    public bool NoActiveEvent { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await _svc.BuildAsync(
            FilterType, FilterLength, FilterRoom, Search, FilterTimeslot, FilterTrack, FilterLevel, ct);
        NoActiveEvent = View is null;
        return Page();
    }

    // --- Display helpers (shared labels with the organizer page) ------------
    public static string Display(SessionType t) => t switch
    {
        SessionType.Keynote => "Keynote",
        SessionType.TechnicalSession => "Technical Session",
        SessionType.MasterClass => "Master Class",
        SessionType.AskTheExperts => "Ask the Experts",
        SessionType.PanelDiscussion => "Panel Discussion",
        SessionType.Welcome => "Welcome",
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
