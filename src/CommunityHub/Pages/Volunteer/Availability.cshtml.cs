using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// VOLUNTEER "My Availability" (operator 2026-06-21): a volunteer marks, per
/// event day, how much they can work — Full (whole day), Half (split: work part,
/// attend part) or Blocked (attending only). Coordinators read this when
/// assigning shifts so a volunteer is never scheduled outside their windows.
/// Editable any time; the "Validate my availability" prompt on My schedule links
/// here. Self-service only — a volunteer edits their own row via
/// <see cref="ICurrentParticipantAccessor"/>; the client never supplies the id.
/// </summary>
[Authorize]
public class AvailabilityModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ILogger<AvailabilityModel> _logger;

    public AvailabilityModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ILogger<AvailabilityModel> logger)
    {
        _db = db;
        _participant = participant;
        _logger = logger;
    }

    /// <summary>One editable row per event day, pre-filled with any saved value.</summary>
    public record DayRow(DateOnly Day, string Label, VolunteerAvailabilityLevel Level, string? Note);

    public IReadOnlyList<DayRow> Days { get; private set; } = Array.Empty<DayRow>();

    [BindProperty] public List<DayInput> Inputs { get; set; } = new();

    public class DayInput
    {
        public DateOnly Day { get; set; }
        public VolunteerAvailabilityLevel Level { get; set; }
        public string? Note { get; set; }
    }

    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Only accept days that genuinely belong to this edition — ignore anything
        // the client may have injected.
        var validDays = (await EventDaysAsync(me.EventId, ct)).Select(d => d.Day).ToHashSet();

        var existing = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == me.EventId && x.ParticipantId == me.ParticipantId)
            .ToListAsync(ct);

        foreach (var input in Inputs)
        {
            if (!validDays.Contains(input.Day)) continue;

            var note = string.IsNullOrWhiteSpace(input.Note)
                ? null
                : input.Note.Trim();
            if (note is { Length: > 500 }) note = note[..500];

            var row = existing.FirstOrDefault(x => x.Day == input.Day);
            if (row is null)
            {
                _db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
                {
                    EventId = me.EventId,
                    ParticipantId = me.ParticipantId,
                    Day = input.Day,
                    Level = input.Level,
                    Note = note,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                row.Level = input.Level;
                row.Note = note;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Volunteer {ParticipantId} saved availability for event {EventId}.",
            me.ParticipantId, me.EventId);

        Notice = "Your availability has been saved. Thank you — this helps us schedule you fairly.";
        return RedirectToPage();
    }

    private async Task LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var days = await EventDaysAsync(eventId, ct);

        var saved = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .ToDictionaryAsync(x => x.Day, ct);

        Days = days.Select(d =>
        {
            saved.TryGetValue(d.Day, out var row);
            return new DayRow(
                d.Day,
                d.Label,
                row?.Level ?? VolunteerAvailabilityLevel.Full,
                row?.Note);
        }).ToList();
    }

    /// <summary>Pre-day (if any) + every main day StartDate..EndDate, ordered, deduped.</summary>
    private async Task<IReadOnlyList<(DateOnly Day, string Label)>> EventDaysAsync(
        int eventId, CancellationToken ct)
    {
        var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return Array.Empty<(DateOnly, string)>();

        var set = new SortedSet<DateOnly>();
        if (ev.PreDayDate is { } pre) set.Add(pre);
        for (var d = ev.StartDate; d <= ev.EndDate; d = d.AddDays(1)) set.Add(d);

        return set.Select(d =>
        {
            var isPre = ev.PreDayDate == d || d < ev.StartDate;
            var label = d.ToString("dddd dd MMM") + (isPre ? " — pre-day" : string.Empty);
            return (d, label);
        }).ToList();
    }
}
