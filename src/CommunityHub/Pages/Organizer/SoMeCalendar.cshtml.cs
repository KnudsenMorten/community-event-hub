using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §835 — THE CALENDAR: what goes out, and when.
///
/// <para>Operator 2026-08-05: <i>"i also need a calendar where i can see when and how"</i>. The
/// <b>"and how"</b> is doing real work in that sentence — he wants to see the POST, not only a date,
/// so every entry carries its preview text and its graphic.</para>
///
/// <para>🔒 Organizer view ⇒ <b>held posts are shown too</b>. The approved-only rule (§834.1) is for
/// sponsors and speakers; hiding held posts here would make the approval queue invisible to the one
/// person who has to work it.</para>
/// </summary>
[Authorize]
public class SoMeCalendarModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeAnnouncementQuery _query;

    public SoMeCalendarModel(ICurrentParticipantAccessor participant, SoMeAnnouncementQuery query)
    {
        _participant = participant;
        _query = query;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>Every announcement, grouped by the day it goes out — the calendar's rows.</summary>
    public IReadOnlyList<IGrouping<DateOnly, SoMeAnnouncement>> Days { get; private set; } =
        Array.Empty<IGrouping<DateOnly, SoMeAnnouncement>>();

    public int Total { get; private set; }
    public int Held { get; private set; }

    /// <summary>
    /// §1206 — of the held posts, how many could not be approved even if he clicked.
    /// </summary>
    /// <remarks>
    /// 🔑 A subset of <see cref="Held"/>, not an addition to it: the rest are simply waiting for him.
    /// </remarks>
    public int NotReady { get; private set; }
    public int Approved { get; private set; }
    public int Published { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // 🔴 §1206 — with the readiness reason, so a held post says whether it is waiting for HIM or
        // waiting for someone else. Those are different jobs and looked identical.
        var all = await _query.ForEventWithReadinessAsync(me.EventId, ct);

        Total = all.Count;
        Held = all.Count(a => a.IsHeld);
        NotReady = all.Count(a => !string.IsNullOrWhiteSpace(a.Blocker));
        Approved = all.Count(a => a.IsApproved && !a.IsPublished);
        Published = all.Count(a => a.IsPublished);

        Days = all
            .GroupBy(a => a.Day)
            .OrderBy(g => g.Key)
            .ToList();

        return Page();
    }
}
