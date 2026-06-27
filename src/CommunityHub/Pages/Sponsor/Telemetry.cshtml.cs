using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// AUTHENTICATED, in-area attendee telemetry for SPONSORS (§55). Same aggregate
/// "who's coming" analytics as the public <c>/attendee-telemetry</c> page — the
/// ranked breakdown tables + segment + topic-filter dropdowns rendered from the
/// shared <c>_AttendeeTelemetryPanel</c> partial via <see cref="AttendeeTelemetryService"/>
/// — but reached from inside the sponsor hub (so the sponsor keeps their session
/// and the hub chrome). Server-side role gate: Sponsor only.
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

    /// <summary>Non-sponsor reached the page (server-side gate, not CSS).</summary>
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
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        // Sponsor surface — never assemble the OrganizerOnly aggregates (defense-in-depth, §69).
        Data = await _svc.GetAsync(Segment, FilterKey, FilterValue, isOrganizer: false, ct);
        return Page();
    }
}
