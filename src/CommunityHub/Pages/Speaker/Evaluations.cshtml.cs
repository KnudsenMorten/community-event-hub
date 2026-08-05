using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// 🗑 §748.1 — the speaker's 1–5 RATINGS VIEW is gone; this page now exists for two things only:
/// it keeps the URL alive as a redirect, and it serves the per-session evaluation-QR download.
/// </summary>
/// <remarks>
/// <para><b>Why the body went, when §748.5 had said to keep it compiled "so C10 revives a working
/// page".</b> That reasoning does not survive §748.1 removing the 1–5 table: this body read
/// <c>SessionEvaluation</c>, and <b>C10 is the speaker view of the FOUR-POINT model</b>. §748 is
/// explicit that a 1–5 rating and a 1–4 forced choice are different instruments that cannot be
/// pooled — so reviving this aggregation would have produced numbers on the wrong scale. What C10
/// actually reuses is the PAGE (its scoping, its layout, its QR card), not the arithmetic, and that
/// is preserved here and in git. Keeping a compiled reader of a deleted table was not possible in
/// any case, and pretending it was ready-to-revive would have been the more expensive lie.</para>
///
/// <para>🔒 <b>Two things must NOT be "tidied away" here.</b></para>
/// <list type="number">
///   <item><b>The route.</b> The link sat in the speaker menu for weeks and may be in a sent mail or
///   a bookmark; a 404 would make a deliberate hiding look like a broken site (§748.5).</item>
///   <item><b>The <c>Qr</c> handler.</b> <c>/Speaker/Index</c> links straight to it for the
///   per-SESSION QR download (§749.2) — deleting this page would break a live speaker feature that
///   has nothing to do with the retired ratings.</item>
/// </list>
/// </remarks>
[Authorize]
public class EvaluationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerSessionsService _sessions;
    private readonly SessionEvalsQrService _qr;

    public EvaluationsModel(
        ICurrentParticipantAccessor participant,
        SpeakerSessionsService sessions,
        SessionEvalsQrService qr)
    {
        _participant = participant;
        _sessions = sessions;
        _qr = qr;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
    };

    /// <summary>
    /// Always a redirect until C10 (the speaker view of the four-point results) exists.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-31: <i>"hide the page until C10 exists"</i>. An empty ratings list reads to a
    /// speaker as <b>"nobody rated me"</b> rather than "not built yet" — which is why this redirects
    /// instead of rendering something honest-looking but blank. <c>/Speaker</c> is where the session
    /// cards already are.
    /// </remarks>
    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        return RedirectToPage("/Speaker/Index");
    }

    /// <summary>
    /// §124/§749.2: stream the session-evaluation QR PNG for one of the speaker's OWN sessions.
    /// Own-scope enforced — the session must be in the speaker's own list; otherwise a 404.
    /// </summary>
    public async Task<IActionResult> OnGetQrAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) return NotFound();

        var mine = await _sessions.GetMySessionsAsync(me.EventId, me.ParticipantId, me.Role, ct);
        var session = mine.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null) return NotFound();

        // §749.2 (operator 2026-07-31: "current is linked to room name but now we use sessionname").
        // 🔒 The own-scope check above already proved this session is the speaker's, so asking for
        // the SESSION's file is both correct and narrower than asking for its room's — a room's QR
        // would have been whoever else is scheduled in there.
        var qr = await _qr.DownloadForSessionAsync(sessionId, ct);
        if (qr is null) return NotFound();

        return File(qr.Content, qr.ContentType, qr.FileName);
    }
}
