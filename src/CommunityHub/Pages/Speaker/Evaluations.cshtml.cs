using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// SPEAKER self-service view of the post-session attendee EVALUATIONS for THEIR
/// sessions — the same HappyOrNot-style 1–5 ratings + anonymous comments that
/// already feed the organizer dashboard and the organizer-triggered results
/// email, now available to the speaker on demand for their OWN sessions only
/// (scope enforced server-side in <see cref="SpeakerEvaluationsService"/>).
/// Read-only; mobile-first.
///
/// Only Speaker / MasterclassSpeaker reach the content; any other role gets a
/// friendly message rather than a 403 so the nav stays simple.
/// </summary>
[Authorize]
public class EvaluationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerEvaluationsService _svc;

    public EvaluationsModel(ICurrentParticipantAccessor participant, SpeakerEvaluationsService svc)
    {
        _participant = participant;
        _svc = svc;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
    };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }

    public MySpeakerEvaluationsResult Result { get; private set; } =
        new(System.Array.Empty<MySpeakerSessionRatings>(), 0, null);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        Result = await _svc.GetMyEvaluationsAsync(me.EventId, me.ParticipantId, me.Role, ct);
        return Page();
    }
}
