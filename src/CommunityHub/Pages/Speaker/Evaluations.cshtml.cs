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
/// Only Speakers reach the content; any other role gets a
/// friendly message rather than a 403 so the nav stays simple.
/// </summary>
[Authorize]
public class EvaluationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerEvaluationsService _svc;
    private readonly PublicSessionsService _publicSessions;

    public EvaluationsModel(
        ICurrentParticipantAccessor participant,
        SpeakerEvaluationsService svc,
        PublicSessionsService publicSessions)
    {
        _participant = participant;
        _svc = svc;
        _publicSessions = publicSessions;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
    };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }

    public MySpeakerEvaluationsResult Result { get; private set; } =
        new(System.Array.Empty<MySpeakerSessionRatings>(), 0, null);

    /// <summary>
    /// Ids of the speaker's rated sessions whose PUBLIC page (<c>/Sessions/{id}</c>)
    /// would actually resolve — the same gate <see cref="PublicSessionsService.GetByIdAsync"/>
    /// applies (in the active edition, not a service session). The view links a
    /// session title to its public page iff its id is in this set; otherwise it
    /// renders the title as plain text (no link to a 404 / thin page).
    /// </summary>
    public IReadOnlySet<int> PubliclyViewableSessionIds { get; private set; } =
        new System.Collections.Generic.HashSet<int>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        Result = await _svc.GetMyEvaluationsAsync(me.EventId, me.ParticipantId, me.Role, ct);

        // Per-session public-visibility gate — the SAME signal the public page uses,
        // so we never link a title to a /Sessions/{id} that would 404 or render thin.
        PubliclyViewableSessionIds = await _publicSessions.GetPubliclyViewableSessionIdsAsync(
            Result.Sessions.Select(s => s.SessionId), ct);

        return Page();
    }
}
