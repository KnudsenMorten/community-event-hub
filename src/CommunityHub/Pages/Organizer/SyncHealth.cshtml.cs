using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer CEH⇄Zoho Sync-Health dashboard (REQUIREMENTS §132): a single read-only page
/// that proves the CEH mirror still tracks Zoho's ACTIVE set (the §125 trust goal). It
/// shows the last successful sync + last webhook, an In-sync / Stale status badge, the
/// active vs cancelled Order/Attendee counts, and local drift indicators — all computed
/// live by <see cref="SyncHealthService"/>. No writes; this page never posts back.
/// Organizer-gated server-side.
/// </summary>
[Authorize]
public class SyncHealthModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SyncHealthService _health;

    public SyncHealthModel(
        ICurrentParticipantAccessor participant,
        SyncHealthService health)
    {
        _participant = participant;
        _health = health;
    }

    public bool AccessDenied { get; private set; }
    public SyncHealthSnapshot? Snapshot { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Snapshot = await _health.BuildAsync(me.EventId, ct);
        return Page();
    }
}
