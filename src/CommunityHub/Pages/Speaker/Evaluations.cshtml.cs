using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
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
    private readonly SpeakerSessionsService _sessions;
    private readonly SessionEvalsQrService _qr;

    public EvaluationsModel(
        ICurrentParticipantAccessor participant,
        SpeakerEvaluationsService svc,
        PublicSessionsService publicSessions,
        SpeakerSessionsService sessions,
        SessionEvalsQrService qr)
    {
        _participant = participant;
        _svc = svc;
        _publicSessions = publicSessions;
        _sessions = sessions;
        _qr = qr;
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

    /// <summary>§124: the speaker's own sessions (id + title + room), shown in the
    /// "evaluation QR" card regardless of whether ratings exist yet.</summary>
    public IReadOnlyList<MySpeakerSession> MySessions { get; private set; } =
        System.Array.Empty<MySpeakerSession>();

    /// <summary>§124: sessionId → the matched per-room session-evaluation QR file
    /// (only sessions whose room matched a QR file are present).</summary>
    public IReadOnlyDictionary<int, SessionEvalQrFile> RoomQr { get; private set; } =
        new System.Collections.Generic.Dictionary<int, SessionEvalQrFile>();

    /// <summary>§124: true once the QR folder is wired (the QR card is shown).</summary>
    public bool QrConfigured { get; private set; }

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

        // §124: the speaker's sessions + the per-room evaluation QR for each (inert
        // until the QR folder is configured). Own-row scoped by the sessions service.
        MySessions = await _sessions.GetMySessionsAsync(me.EventId, me.ParticipantId, me.Role, ct);
        QrConfigured = _qr.CanRead;
        if (QrConfigured)
        {
            RoomQr = await _qr.MatchSessionsAsync(
                MySessions.Select(s => new SessionRoomRef(s.SessionId, s.Room)), ct);
        }

        return Page();
    }

    /// <summary>
    /// §124: stream the session-evaluation QR PNG for the ROOM of one of the speaker's
    /// OWN sessions. Own-scope enforced — the session must be in the speaker's own list;
    /// otherwise (or when nothing matches / not configured) a 404.
    /// </summary>
    public async Task<IActionResult> OnGetQrAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) return NotFound();

        var mine = await _sessions.GetMySessionsAsync(me.EventId, me.ParticipantId, me.Role, ct);
        var session = mine.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null) return NotFound();

        var qr = await _qr.DownloadForRoomAsync(session.Room, ct);
        if (qr is null) return NotFound();

        return File(qr.Content, qr.ContentType, qr.FileName);
    }
}
