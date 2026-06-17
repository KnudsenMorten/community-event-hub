using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer data-freshness panel (REQUIREMENTS §21 Organizer [M] "last synced
/// at"). A single read-only page that shows, per data feed, when it last produced
/// data, how long ago, and whether it has gone stale — computed live by
/// <see cref="DataFreshnessService"/>. No writes; this page never posts back.
/// Organizer-gated server-side.
/// </summary>
[Authorize]
public class DataFreshnessModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly DataFreshnessService _freshness;

    public DataFreshnessModel(
        ICurrentParticipantAccessor participant,
        DataFreshnessService freshness)
    {
        _participant = participant;
        _freshness = freshness;
    }

    public bool AccessDenied { get; private set; }
    public FreshnessSnapshot? Snapshot { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Snapshot = await _freshness.BuildAsync(me.EventId, ct);
        return Page();
    }
}
