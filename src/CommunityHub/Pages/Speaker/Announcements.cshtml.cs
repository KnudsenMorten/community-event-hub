using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

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
    private readonly CommunityHub.Core.Data.CommunityHubDbContext _db;

    public AnnouncementsModel(
        ICurrentParticipantAccessor participant, SoMeAnnouncementQuery query,
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        _participant = participant;
        _query = query;
        _db = db;
    }

    public bool AccessDenied { get; private set; }
    public SpeakerAnnouncements? Announcements { get; private set; }

    /// <summary>
    /// §1060(f) — this person's LinkedIn mention-resolution outcome, which is what the follow card
    /// reads. Passed through RAW rather than reduced to a bool here: the partial is the one place
    /// that decides what each of the five statuses is allowed to CLAIM, and collapsing it to
    /// "follows / does not follow" at this layer would throw away the distinction between a real
    /// negative and a lookup that never answered.
    /// </summary>
    public string? LinkedInStatus { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        Announcements = await _query.ForSpeakerAsync(me.EventId, me.ParticipantId, ct);
        LinkedInStatus = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.LinkedInPersonUrnStatus)
            .FirstOrDefaultAsync(ct);
        return Page();
    }
}
