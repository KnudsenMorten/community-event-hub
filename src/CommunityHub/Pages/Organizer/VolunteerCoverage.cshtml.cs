using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1380 — "Coverage &amp; shortage": how many volunteers we are short, and when, WITHOUT running auto-assign.
/// Leth 2026-11-01: <i>"Overblik over vores nuværende fordeling samt hvor mange volunteers vi mangler"</i>.
/// Read-only; organizer-only; its own page (the Volunteers hub stays a tile landing, §1146e).
/// </summary>
[Authorize]
public class VolunteerCoverageModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerCoverageService _coverage;

    public VolunteerCoverageModel(ICurrentParticipantAccessor participant, VolunteerCoverageService coverage)
    {
        _participant = participant;
        _coverage = coverage;
    }

    public bool AccessDenied { get; private set; }
    public CoverageReport? Report { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }
        Report = await _coverage.LoadAsync(me.EventId, ct);
        return Page();
    }
}
