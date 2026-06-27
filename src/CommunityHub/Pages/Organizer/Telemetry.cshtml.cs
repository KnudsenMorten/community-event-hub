using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// AUTHENTICATED, in-area attendee telemetry for ORGANIZERS (§55). The same
/// aggregate "who's coming" analytics as the public <c>/attendee-telemetry</c> page
/// — the ranked breakdown tables + segment + topic-filter dropdowns rendered from
/// the shared <c>_AttendeeTelemetryPanel</c> partial via
/// <see cref="AttendeeTelemetryService"/> — surfaced inside the organizer (org-admin)
/// interface. Server-side role gate: Organizer only.
/// </summary>
[Authorize]
public class TelemetryModel : PageModel
{
    private readonly AttendeeTelemetryService _svc;
    private readonly ICurrentParticipantAccessor _participant;

    public TelemetryModel(AttendeeTelemetryService svc, ICurrentParticipantAccessor participant)
    {
        _svc = svc;
        _participant = participant;
    }

    /// <summary>Non-organizer reached the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Segment { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterKey { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterValue { get; set; }

    public AttendeeTelemetry? Data { get; private set; }
    public IReadOnlyList<TelemetrySegment> Segments => AttendeeTelemetryService.Segments;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // Organizer surface — build the OrganizerOnly aggregates (e.g. "Top companies").
        Data = await _svc.GetAsync(Segment, FilterKey, FilterValue, isOrganizer: true, ct);
        return Page();
    }
}
