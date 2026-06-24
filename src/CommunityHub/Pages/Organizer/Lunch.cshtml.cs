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

    public int EarlySetupDayCount { get; private set; }
    public int SetupDayCount { get; private set; }
    public int PreDayCount   { get; private set; }
    /// <summary>Pre-day headcount auto-counted (active crew + MC speakers).</summary>
    public int AutoCountedPreDayCount { get; private set; }
    /// <summary>Pre-day headcount from form declarations (non-auto-counted roles).</summary>
    public int DeclaredPreDayCount { get; private set; }
    public int TotalResponses { get; private set; }
    public string EarlySetupDayLabel { get; private set; } = "Setup day (Sun)";
    public string SetupDayLabel { get; private set; } = "Setup day (Mon)";
    public string PreDayLabel   { get; private set; } = "Pre-day";

    public List<Row> Rows { get; private set; } = new();
    public record Row(
        string Name, string Email, string Role,
        bool EarlySetupDay, bool SetupDay, bool PreDay, string? Notes);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var evt = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.StartDate })
            .FirstOrDefaultAsync(ct);
        if (evt is not null)
        {
            // StartDate IS the pre-day / Master Class; the two days before are setup.
            EarlySetupDayLabel = $"Setup day ({evt.StartDate.AddDays(-2):dddd, MMM d yyyy})";
            SetupDayLabel      = $"Setup day ({evt.StartDate.AddDays(-1):dddd, MMM d yyyy})";
            PreDayLabel        = $"Pre-day / Master Class ({evt.StartDate:dddd, MMM d yyyy})";
        }

        var rows = await _db.LunchSignups
            .Where(l => l.EventId == me.EventId)
            .Join(_db.Participants, l => l.ParticipantId, p => p.Id, (l, p) => new
            {
                p.Id, p.FullName, p.Email, p.Role,
                l.LunchEarlySetupDay, l.LunchSetupDay, l.LunchPreDay, l.Notes
            })
            .ToListAsync(ct);

        Rows = rows
            .OrderBy(r => r.Role.ToString())
            .ThenBy(r => r.FullName)
            .Select(r => new Row(r.FullName, r.Email, r.Role.ToString(),
                r.LunchEarlySetupDay, r.LunchSetupDay, r.LunchPreDay, r.Notes))
            .ToList();

        // Pre-day AUTO-COUNT (operator 2026-06-24): active crew (Organizer / Media /
        // Event-partner) + Master-class speakers are counted automatically — they
        // don't fill the form's pre-day. Add them to the pre-day headcount, and count
        // form pre-day declarations only from everyone else (no double-count).
        var autoCountedIds = (await _db.Participants
            .Where(ParticipantActivation.IsActiveExpr)
            .Where(p => p.EventId == me.EventId
                && (p.Role == ParticipantRole.Organizer
                    || p.Role == ParticipantRole.Media
                    || p.Role == ParticipantRole.EventPartner
                    || (p.Role == ParticipantRole.Speaker
                        && _db.SpeakerProfiles.Any(s =>
                            s.EventId == me.EventId && s.ParticipantId == p.Id && s.SpeakingPreDay))))
            .Select(p => p.Id)
            .ToListAsync(ct))
            .ToHashSet();

        TotalResponses = rows.Count;
        EarlySetupDayCount = rows.Count(r => r.LunchEarlySetupDay);
        SetupDayCount      = rows.Count(r => r.LunchSetupDay);
        AutoCountedPreDayCount = autoCountedIds.Count;
        DeclaredPreDayCount = rows.Count(r => r.LunchPreDay && !autoCountedIds.Contains(r.Id));
        PreDayCount = AutoCountedPreDayCount + DeclaredPreDayCount;

        return Page();
    }
}
