using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER results dashboard for the per-session attendee EVALUATIONS (HappyOrNot-style
/// ratings collected from the public evaluate page / room QR). Shows per-session and
/// per-room aggregates — average score, count, comments — filterable by session type and
/// room. Read-only; the aggregation lives in <see cref="SessionEvaluationService"/> so the
/// dashboard never re-implements the math. Mobile-first (~360px) + a11y.
/// </summary>
[Authorize]
public class SessionEvaluationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionEvaluationService _svc;

    public SessionEvaluationsModel(
        ICurrentParticipantAccessor participant,
        SessionEvaluationService svc)
    {
        _participant = participant;
        _svc = svc;
    }

    public bool AccessDenied { get; private set; }

    // --- Filters (querystring) ---------------------------------------------
    [BindProperty(SupportsGet = true)] public SessionType? FilterType { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterRoom { get; set; }

    // --- View state --------------------------------------------------------
    public SessionEvaluationService.DashboardResult? Dashboard { get; private set; }
    public List<string> Rooms { get; private set; } = new();

    public SelectList TypeOptions => new(
        Enum.GetValues<SessionType>().Select(t => new { Value = t, Text = Display(t) }),
        "Value", "Text");

    public static string Display(SessionType t) => t switch
    {
        SessionType.CommunityMasterClass => "Community Master Class",
        SessionType.CommunityTechSession => "Community Tech Session",
        SessionType.SponsorSession => "Sponsor Session",
        _ => t.ToString(),
    };

    /// <summary>Render a 1–5 rating as a smiley (for the average / per-comment display).</summary>
    public static string Face(double? rating)
    {
        if (rating is null) return "—";
        return (int)Math.Round(rating.Value) switch
        {
            <= 1 => "😞",
            2 => "🙁",
            3 => "😐",
            4 => "🙂",
            _ => "😀",
        };
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Rooms = await _svc.ListRoomsAsync(me.EventId, ct);
        Dashboard = await _svc.BuildDashboardAsync(me.EventId, FilterType, FilterRoom, ct);
        return Page();
    }
}
