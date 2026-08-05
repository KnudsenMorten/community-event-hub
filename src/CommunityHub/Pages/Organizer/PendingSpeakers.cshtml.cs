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

    /// <summary>
    /// §877 — <c>?approveAll=community|guest|sponsor</c>, the one-click bulk approval the
    /// pending-speaker mail links to.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? approveAll { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // 🔒 §877 — A STATE CHANGE ON A GET, DELIBERATELY, AND THIS IS WHY IT IS SAFE.
        //
        // Operator 2026-08-05: *"it takes 1 click away"* — he rejected a confirm page, and he is
        // right that a button which opens a page to ask "are you sure" is the click it replaced.
        // A mail cannot POST, so the action has to run on the link.
        //
        // ⚠️ THE RISK IS PREFETCH, NOT CSRF: Outlook/Defender Safe Links, Brevo click-tracking and
        // most mail scanners fetch every URL in a message before a human sees it. A bare mutating
        // link would approve the whole queue unread.
        //
        // 🔒 THE MITIGATION: this page is [Authorize]d AND checks IsRealOrganizer, and scanners
        // fetch ANONYMOUSLY — a prefetch lands on the login redirect above and changes nothing.
        // ❗ THEREFORE: the mail's buttons must stay PLAIN links. CEH mints one-tap sign-in grants
        // for mail (§436/welcome); if one ever rode on these URLs the prefetch would authenticate
        // and this entire protection would vanish silently. Never add a token to them.
        //
        // Unrecognised values approve nobody (TryParseApproveAll returns false).
        if (SpeakerApprovalService.TryParseApproveAll(approveAll, out var bulkCategory))
        {
            var result = await _approval.ApproveAllPendingAsync(me.EventId, bulkCategory, ct);
            Message = result.Count == 0
                // Safe to click twice: the queue is simply empty the second time.
                ? "Nothing to approve — no speakers are pending right now."
                : $"Approved {result.Count} speaker(s) as {bulkCategory}, ring 3, activated: "
                  + string.Join(", ", result.Names)
                  + ". The next Zoho pass picks them up — you can still change any single one below.";
        }

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
