using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §304 "Pending speakers" — the organizer admin surface for speakers HELD from the
/// Zoho flow (imported from Sessionize they arrive Ring 3 / inactive / uncategorized).
/// One row per pending speaker with the exact blockers and a ONE-CLICK approve that
/// sets the category, places the ring and activates — so the speaker "flows to zoho
/// fast" (the next hourly push picks them up; new speakers adopt their existing
/// Backstage record by e-mail when one exists). The Sessionize import job mails
/// info@ IMMEDIATELY when new pending speakers appear, linking here.
/// </summary>
[Authorize]
public class PendingSpeakersModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerApprovalService _approval;

    public PendingSpeakersModel(
        ICurrentParticipantAccessor participant, SpeakerApprovalService approval)
    {
        _participant = participant;
        _approval = approval;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public SpeakerApprovalService.PendingResult Pending { get; private set; } =
        new(Ring.Ring0, false, Array.Empty<SpeakerApprovalService.PendingSpeaker>());

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        Pending = await _approval.PendingAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(
        int participantId, SpeakerCategory category, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var ok = await _approval.ApproveAsync(me.EventId, participantId, category, ring, ct);
        if (!ok)
        {
            IsError = true;
            Message = "That speaker could not be found in this event.";
        }
        else
        {
            Message = "Speaker approved — category set, ring placed, activated. "
                + "The next hourly Zoho pass picks them up.";
        }

        Pending = await _approval.PendingAsync(me.EventId, ct);
        return Page();
    }

    private static bool IsRealOrganizer(CurrentParticipant me) =>
        me.Role == ParticipantRole.Organizer && !me.IsActingAs;
}
