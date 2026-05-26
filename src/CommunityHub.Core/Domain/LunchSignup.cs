namespace CommunityHub.Core.Domain;

/// <summary>
/// Lunch-attendance preference for the days BEFORE the main conference
/// (Setup-day and Pre-day / Master-Class day). One row per participant per
/// edition. Collected for all roles EXCEPT Attendees and Sponsors -- they
/// have their own catering arrangement and are not in the org/speaker/
/// volunteer headcount the venue needs.
/// </summary>
public class LunchSignup
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>True = will join the lunch on Setup-day (day before Pre-day).</summary>
    public bool LunchSetupDay { get; set; }

    /// <summary>True = will join the lunch on Pre-day (Master Class day, day before main).</summary>
    public bool LunchPreDay { get; set; }

    /// <summary>Free-text notes (late arrival, dietary, etc.).</summary>
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
