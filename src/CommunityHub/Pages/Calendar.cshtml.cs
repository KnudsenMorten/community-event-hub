using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
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

    /// <summary>One "add to calendar" row, pre-built with OPEN-in-calendar links.</summary>
    public sealed record CalRow(
        string Title, DateTimeOffset StartsAt, string? Location,
        string GoogleUrl, string OutlookUrl, string? IcsUrl);

    /// <summary>Entries grouped by month ("MMMM yyyy") for the restructured Key Dates list.</summary>
    public IReadOnlyList<IGrouping<string, CalRow>> Months { get; private set; } =
        Array.Empty<IGrouping<string, CalRow>>();

    /// <summary>
    /// Role-appropriate description of what the subscribe feed includes, so the
    /// copy never offers a speaker "shifts" (volunteer-only) or a volunteer
    /// "sessions" (operator 2026-06-23).
    /// </summary>
    public string SyncItemsPhrase { get; private set; } = "tasks";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        CalendarSyncEnabled = await _db.Events
            .Where(e => e.Id == me.EventId).Select(e => e.CalendarSyncEnabled)
            .FirstOrDefaultAsync(ct);

        Entries = await _schedule.GetForRoleAsync(me.EventId, me.Role, ct);

        // Build per-entry OPEN-in-calendar links (Google + Outlook), grouped by month.
        // The .ics link is only offered for a real persisted entry (Id > 0) so we never
        // emit /schedule/0.ics (which 404s). End time defaults to +1h when unset.
        Months = Entries
            .OrderBy(e => e.StartsAt)
            .Select(e =>
            {
                var end = e.EndsAt ?? e.StartsAt.AddHours(1);
                var details = $"Experts Live Denmark — {e.Title}";
                return new CalRow(
                    e.Title, e.StartsAt, e.Location,
                    CalendarLinkBuilder.GoogleUrl(e.Title, e.StartsAt, end, details, e.Location),
                    CalendarLinkBuilder.OutlookUrl(e.Title, e.StartsAt, end, details, e.Location),
                    e.Id > 0 ? $"/schedule/{e.Id}.ics" : null);
            })
            .GroupBy(r => r.StartsAt.ToString("MMMM yyyy"))
            .ToList();

        SyncItemsPhrase = me.Role switch
        {
            ParticipantRole.Speaker => "sessions and tasks",
            ParticipantRole.Volunteer => "shifts and tasks",
            ParticipantRole.Organizer or ParticipantRole.Media or ParticipantRole.EventPartner
                => "sessions, tasks and shifts",
            _ => "tasks",
        };

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
