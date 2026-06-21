namespace CommunityHub.Core.Domain;

/// <summary>
/// One generic, role-tagged entry on the event SCHEDULE / key-dates calendar
/// (move-in, setup, pre-day, main day, party, group photo, appreciation dinner, …).
/// Organizer-managed (CRUD) and rendered role-filtered on the hub "Key dates" panel,
/// plus surfaced in each participant's subscribable iCal feed (sync all) and as a
/// per-entry .ics download (sync individual). Data-driven: add entries without code.
/// </summary>
public class ScheduleEntry
{
    public int Id { get; set; }

    /// <summary>The edition this entry belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Start (local event time). For an all-day entry only the date part is used.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>Optional end (local event time). Null = a point/short entry (feed gives it a default length).</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>True = an all-day entry (e.g. "Setup day"); false = a timed entry (e.g. Party 16:00).</summary>
    public bool AllDay { get; set; }

    /// <summary>The human title, e.g. "Pre-day / Master Class", "Appreciation Dinner".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional location/venue line.</summary>
    public string? Location { get; set; }

    /// <summary>
    /// Comma-separated role keywords this entry applies to (<see cref="ScheduleRoles"/>):
    /// <c>organizer</c>, <c>volunteer</c>, <c>speaker</c>, <c>media</c>, <c>attendee</c>,
    /// <c>sponsor</c>, or <c>all</c>. Drives the per-role display + per-role calendar sync.
    /// </summary>
    public string Roles { get; set; } = "all";

    /// <summary>Optional free-text note shown under the entry.</summary>
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
