using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// §838 — SOCIAL MEDIA ANNOUNCEMENTS, for a speaker.
///
/// <para>Operator 2026-08-05: <i>"also add similar page with Social media announcements for
/// speakers"</i> — the §837 page, over a speaker's own session and track posts.</para>
///
/// <para>🔒 Scoping and the approved-only rule live in <see cref="SoMeAnnouncementQuery"/>. A
/// speaker sees only their own posts, and only approved ones (§834.1).</para>
/// </summary>
[Authorize]
public class AnnouncementsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeAnnouncementQuery _query;

    public AnnouncementsModel(ICurrentParticipantAccessor participant, SoMeAnnouncementQuery query)
    {
        _participant = participant;
        _query = query;
    }

    public bool AccessDenied { get; private set; }
    public SpeakerAnnouncements? Announcements { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        Announcements = await _query.ForSpeakerAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
