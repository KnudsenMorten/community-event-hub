using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer view of the DELTA-APPROVAL QUEUE (REQUIREMENTS §59). Sync engines (today the
/// §38e Zoho→CEH session change-detection engine + Sessionize disappearances) ENQUEUE
/// detected changes here as Pending; the operator reviews the old→new field diffs and
/// APPROVES (apply + notify) or REJECTS (keep current) each one. Approving a Disappeared
/// item only acknowledges it — CEH never auto-deletes. Mirrors the
/// <see cref="PreselectionQueueModel"/> auth + post-redirect-get pattern.
/// </summary>
[Authorize]
public class SyncQueueModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SyncDeltaQueueService _queue;

    public SyncQueueModel(ICurrentParticipantAccessor participant, SyncDeltaQueueService queue)
    {
        _participant = participant;
        _queue = queue;
    }

    public IReadOnlyList<SyncDelta> Pending { get; private set; } = new List<SyncDelta>();
    public IReadOnlyList<SyncDelta> RecentlyDecided { get; private set; } = new List<SyncDelta>();
    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }

    /// <summary>Carries a result banner across the post-redirect-get.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Msg { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!string.IsNullOrEmpty(Msg)) ActionMessage = Msg;
        Pending = await _queue.ListPendingAsync(me.EventId, ct);
        RecentlyDecided = await _queue.ListRecentlyDecidedAsync(me.EventId, 25, ct);
        return Page();
    }

    /// <summary>Approve one pending delta — applies it (Update) or acknowledges it (Disappeared).</summary>
    public async Task<IActionResult> OnPostApproveAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var delta = await _queue.GetAsync(id, ct);
        if (delta is null || delta.EventId != me.EventId)
        {
            return RedirectToPage(new { Msg = "That item could not be found in this edition." });
        }

        var result = await _queue.ApproveAsync(id, me.Email, ct);
        return RedirectToPage(new { Msg = result.Message });
    }

    /// <summary>Reject one pending delta with a reason — keeps the current CEH value.</summary>
    public async Task<IActionResult> OnPostRejectAsync(int id, string? reason, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var delta = await _queue.GetAsync(id, ct);
        if (delta is null || delta.EventId != me.EventId)
        {
            return RedirectToPage(new { Msg = "That item could not be found in this edition." });
        }

        var result = await _queue.RejectAsync(id, me.Email, reason, ct);
        return RedirectToPage(new { Msg = result.Message });
    }
}
