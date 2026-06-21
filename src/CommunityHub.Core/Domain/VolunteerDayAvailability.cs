namespace CommunityHub.Core.Domain;

/// <summary>
/// A volunteer's per-day availability for an edition: for each event day, how
/// much of that day they can work as a volunteer versus are blocked (e.g.
/// attending sessions themselves). Coordinators use this to assign shifts only
/// inside a volunteer's available windows — some volunteers work the full day,
/// others split (work ~half, attend ~half), others are fully blocked on a day.
/// One row per (event, participant, day). Distinct from
/// <see cref="VolunteerAvailability"/> (which captures preferred shift names).
/// </summary>
public class VolunteerDayAvailability
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>The event day this row covers (pre-day or a main day).</summary>
    public DateOnly Day { get; set; }

    /// <summary>How available the volunteer is on this day.</summary>
    public VolunteerAvailabilityLevel Level { get; set; } = VolunteerAvailabilityLevel.Full;

    /// <summary>Optional note (e.g. "blocked 13:00-15:00 for a session I want to attend").</summary>
    public string? Note { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// How much of an event day a volunteer is available to work. The integer value
/// IS the percentage of the day available for volunteer work, so coordinators
/// can sum/compare capacity directly.
/// </summary>
public enum VolunteerAvailabilityLevel
{
    /// <summary>Not available to work this day — attending only.</summary>
    Blocked = 0,

    /// <summary>Roughly half the day available (split: work part, attend part).</summary>
    Half = 50,

    /// <summary>Fully available to work this day.</summary>
    Full = 100,
}
