using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The generic per-role "Calendar" (operator 2026-06-21). Shows the schedule
/// entries that apply to the signed-in participant's role (appreciation dinner,
/// group photo, party, pre-day/main-day, …) with a per-entry "add to calendar"
/// (.ics) link, plus the one-click subscribe (webcal) URL so the whole feed —
/// the role events PLUS the participant's own sessions/tasks/shifts — stays in
/// sync. Reuses <see cref="ScheduleService"/> + <see cref="CalendarFeedTokenService"/>;
/// nothing new server-side.
/// </summary>
[Authorize]
public class CalendarModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ScheduleService _schedule;
    private readonly CalendarFeedTokenService _tokens;

    public CalendarModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ScheduleService schedule,
        CalendarFeedTokenService tokens)
    {
        _db = db;
        _participant = participant;
        _schedule = schedule;
        _tokens = tokens;
    }

    public List<ScheduleEntry> Entries { get; private set; } = new();
    public bool CalendarSyncEnabled { get; private set; }
    public string WebcalUrl { get; private set; } = string.Empty;
    public string HttpsUrl { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        CalendarSyncEnabled = await _db.Events
            .Where(e => e.Id == me.EventId).Select(e => e.CalendarSyncEnabled)
            .FirstOrDefaultAsync(ct);

        Entries = await _schedule.GetForRoleAsync(me.EventId, me.Role, ct);

        if (CalendarSyncEnabled)
        {
            try
            {
                var token = await _tokens.EnsureTokenAsync(me.ParticipantId, ct);
                var host = Request.Host.Value ?? string.Empty;
                HttpsUrl = $"{Request.Scheme}://{host}/cal/{token}.ics";
                WebcalUrl = $"webcal://{host}/cal/{token}.ics";
            }
            catch { /* subscribe card simply hides if the token can't be minted */ }
        }
        return Page();
    }
}
