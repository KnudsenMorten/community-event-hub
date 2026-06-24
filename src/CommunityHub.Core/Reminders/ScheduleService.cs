using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Loads + seeds the generic, role-tagged event SCHEDULE (<see cref="ScheduleEntry"/>).
/// One source of truth for the Key-dates panel, the organizer editor and the iCal
/// feed, so role visibility + dates are identical everywhere.
///
/// EFFECTIVE schedule: the persisted rows when any exist; otherwise a sensible
/// DEFAULT derived from the edition's StartDate (the pre-day / Master Class) and
/// EndDate (main day) — the move-in → main matrix plus the social events. The
/// organizer can SEED the defaults into the table (then edit them).
/// </summary>
public sealed class ScheduleService
{
    private readonly CommunityHubDbContext _db;
    public ScheduleService(CommunityHubDbContext db) => _db = db;

    // Event-local timezone (Europe/Copenhagen); robust across Windows/Linux, falls
    // back to UTC. Used to stamp entries with the correct offset for iCal UTC export.
    private static readonly TimeZoneInfo EventTz = ResolveTz();
    private static TimeZoneInfo ResolveTz()
    {
        foreach (var id in new[] { "Europe/Copenhagen", "Romance Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }
    /// <summary>A local wall-clock date/time as a DateTimeOffset in the event timezone.</summary>
    public static DateTimeOffset EventLocal(DateTime localWallClock) =>
        new(DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified),
            EventTz.GetUtcOffset(DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified)));

    /// <summary>All persisted entries for an edition, ordered by time (organizer editor).</summary>
    public async Task<List<ScheduleEntry>> GetAllAsync(int eventId, CancellationToken ct = default) =>
        await _db.ScheduleEntries.Where(s => s.EventId == eventId)
            .OrderBy(s => s.StartsAt).ToListAsync(ct);

    /// <summary>
    /// The EFFECTIVE schedule for an edition: persisted rows if any, else the derived
    /// default. Read-only (the default is not persisted until <see cref="EnsureSeededAsync"/>).
    /// </summary>
    public async Task<List<ScheduleEntry>> GetEffectiveAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await GetAllAsync(eventId, ct);
        if (rows.Count > 0) return rows;

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId).Select(e => new { e.StartDate, e.EndDate })
            .FirstOrDefaultAsync(ct);
        return ev is null ? new() : BuildDefault(eventId, ev.StartDate, ev.EndDate);
    }

    /// <summary>Effective schedule filtered to the dates a given role should see.</summary>
    public async Task<List<ScheduleEntry>> GetForRoleAsync(
        int eventId, ParticipantRole? role, CancellationToken ct = default)
    {
        var all = await GetEffectiveAsync(eventId, ct);
        return all.Where(s => ScheduleRoles.Applies(s.Roles, role))
                  .OrderBy(s => s.StartsAt).ToList();
    }

    /// <summary>Persist the derived default into the table when it is still empty (idempotent).</summary>
    public async Task<int> EnsureSeededAsync(int eventId, string? byEmail, CancellationToken ct = default)
    {
        if (await _db.ScheduleEntries.AnyAsync(s => s.EventId == eventId, ct)) return 0;

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId).Select(e => new { e.StartDate, e.EndDate })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return 0;

        var now = DateTimeOffset.UtcNow;
        var defaults = BuildDefault(eventId, ev.StartDate, ev.EndDate);
        foreach (var d in defaults) { d.CreatedAt = now; d.LastUpdatedByEmail = byEmail; }
        _db.ScheduleEntries.AddRange(defaults);
        await _db.SaveChangesAsync(ct);
        return defaults.Count;
    }

    /// <summary>
    /// The default ELDK schedule derived from StartDate (pre-day / Master Class) +
    /// EndDate (main day): four prep days, the pre-day, the social events (Party 16:00,
    /// Group photo 17:30 — all except sponsors, Appreciation Dinner 18:00), and main day.
    /// </summary>
    public static List<ScheduleEntry> BuildDefault(int eventId, DateOnly start, DateOnly end)
    {
        DateTime D(DateOnly d, int h = 0, int m = 0) => d.ToDateTime(new TimeOnly(h, m));
        ScheduleEntry E(DateTime when, string title, string roles, bool allDay, DateTime? endWhen = null) => new()
        {
            EventId = eventId, StartsAt = EventLocal(when), Title = title, Roles = roles, AllDay = allDay,
            EndsAt = endWhen is { } e ? EventLocal(e) : null,
        };
        return new List<ScheduleEntry>
        {
            E(D(start.AddDays(-4)),        "Move-in (Bella Center)",        "organizer", true),
            E(D(start.AddDays(-3)),        "Last-minute logistics & comms", "organizer", true),
            E(D(start.AddDays(-2)),        "Packing day",                   "organizer,volunteer", true),
            E(D(start.AddDays(-1)),        "Setup day",                     "organizer,volunteer,media", true),
            // Pre-day / Master Class runs 09:00–16:00; main day 07:00–17:15 (operator 2026-06-24).
            E(D(start, 9, 0),              "Pre-day / Master Class",        "organizer,volunteer,speaker,media", false, D(start, 16, 0)),
            E(D(start, 16, 0),             "Party",                         "all", false),
            E(D(start, 17, 30),            "Group photo",                   "organizer,volunteer,speaker,media,attendee", false),
            E(D(start, 18, 0),             "Appreciation Dinner",           "all", false),
            E(D(end, 7, 0),                "Main day",                      "organizer,volunteer,speaker,media", false, D(end, 17, 15)),
        };
    }
}
