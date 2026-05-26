using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class LunchModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public LunchModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    public int SetupDayCount { get; private set; }
    public int PreDayCount   { get; private set; }
    public int TotalResponses { get; private set; }
    public string SetupDayLabel { get; private set; } = "Setup day";
    public string PreDayLabel   { get; private set; } = "Pre-day";

    public List<Row> Rows { get; private set; } = new();
    public record Row(
        string Name, string Email, string Role,
        bool SetupDay, bool PreDay, string? Notes);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var evt = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        if (evt is not null)
        {
            var preDay   = evt.PreDayDate ?? evt.StartDate.AddDays(-1);
            var setupDay = preDay.AddDays(-1);
            SetupDayLabel = $"Setup day ({setupDay:dddd, MMM d yyyy})";
            PreDayLabel   = $"Pre-day / Master Class ({preDay:dddd, MMM d yyyy})";
        }

        var rows = await _db.LunchSignups
            .Where(l => l.EventId == me.EventId)
            .Join(_db.Participants, l => l.ParticipantId, p => p.Id, (l, p) => new
            {
                p.FullName, p.Email, p.Role,
                l.LunchSetupDay, l.LunchPreDay, l.Notes
            })
            .ToListAsync(ct);

        Rows = rows
            .OrderBy(r => r.Role.ToString())
            .ThenBy(r => r.FullName)
            .Select(r => new Row(r.FullName, r.Email, r.Role.ToString(),
                r.LunchSetupDay, r.LunchPreDay, r.Notes))
            .ToList();

        TotalResponses = rows.Count;
        SetupDayCount  = rows.Count(r => r.LunchSetupDay);
        PreDayCount    = rows.Count(r => r.LunchPreDay);

        return Page();
    }
}
