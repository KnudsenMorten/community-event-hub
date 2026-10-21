using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// §837 — SOCIAL MEDIA ANNOUNCEMENTS, for a sponsor.
///
/// <para>Operator 2026-08-05: <i>"make a page with Social media announcement where they can preview
/// it and see when it runs on linkedin. both sponsor categories, individual and sessions if they
/// bought"</i> and <i>"when and who it should say. with preview"</i>.</para>
///
/// <para>🔒 <b>Scoping and the approved-only rule are enforced in
/// <see cref="SoMeAnnouncementQuery"/>, not here.</b> A sponsor sees their own posts and only
/// APPROVED ones (§834.1: <i>"only show approved posts"</i>) — a planned post's wording may still
/// change, and showing it would publish a promise the organizer then edits.</para>
/// </summary>
[Authorize]
public class AnnouncementsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeAnnouncementQuery _query;
    private readonly CommunityHub.Core.Data.CommunityHubDbContext _db;

    public AnnouncementsModel(
        ICurrentParticipantAccessor participant,
        SoMeAnnouncementQuery query,
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        _participant = participant;
        _query = query;
        _db = db;
    }

    public bool AccessDenied { get; private set; }
    public bool NoCompanyLink { get; private set; }
    public SponsorAnnouncements? Announcements { get; private set; }

    /// <summary>
    /// §1060(f) — the raw mention-resolution status. Passed through unreduced: only the shared
    /// follow-card partial decides what each of the five values may claim, and flattening it here to
    /// a bool would lose the difference between "confirmed non-follower" and "we could not look".
    /// </summary>
    public string? LinkedInStatus { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        // The company lives on the Participant row, not on the auth accessor — the same way every
        // other sponsor page resolves it.
        // §1060(f) — the follow card's status comes off the SAME row, so it costs no extra query.
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(
                _db.Participants
                    .Where(p => p.Id == me.ParticipantId)
                    .Select(p => new { p.SponsorCompanyId, p.LinkedInPersonUrnStatus }),
                ct);

        var companyId = row?.SponsorCompanyId;
        LinkedInStatus = row?.LinkedInPersonUrnStatus;

        if (string.IsNullOrWhiteSpace(companyId))
        {
            NoCompanyLink = true;
            return Page();
        }

        // §1236 — the company the person is ACTING for (a contact of several companies switches), as every sponsor page.
        var acting = HttpContext.RequestServices.GetService(typeof(CommunityHub.Sponsors.SponsorActingCompanyService)) as CommunityHub.Sponsors.SponsorActingCompanyService;
        if (acting is not null && await acting.CurrentAsync(me.ParticipantId, me.EventId, ct) is { } actingId) companyId = actingId;

        Announcements = await _query.ForSponsorAsync(me.EventId, companyId!, ct);

        // §1312 — WHY nothing shows (operator 2026-09-27: "it should state why so sponsor knows it is him we are waiting
        // for"). Posts appear only once the sponsor is eligible — their social-media text delivered (§1028). A company with
        // planned posts but no text is told exactly that, with the dates and where to add it.
        if (Announcements.Total == 0)
        {
            var key = "sponsor:" + companyId;
            PlannedDates = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                _db.SoMePosts.Where(p => p.EventId == me.EventId && p.SubjectKey == key && !p.IsDeleted && p.PublishedAtUtc == null)
                    .OrderBy(p => p.ScheduledAtUtc).Select(p => p.ScheduledAtUtc), ct);
            var intro = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(_db.SponsorInfos)
                    .Where(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId).Select(s => s.SocialMediaIntro), ct);
            MissingSocialText = string.IsNullOrWhiteSpace(intro);
        }
        return Page();
    }

    /// <summary>§1312 — when the company's posts are planned but not shown yet.</summary>
    public IReadOnlyList<DateTimeOffset> PlannedDates { get; private set; } = Array.Empty<DateTimeOffset>();

    /// <summary>§1312 — the sponsor has not delivered their social-media text (what the posts wait for).</summary>
    public bool MissingSocialText { get; private set; }
}
