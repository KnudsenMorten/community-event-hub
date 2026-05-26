namespace CommunityHub.Core.Domain;

/// <summary>
/// A participant's volunteer-availability submission for an edition: which
/// shifts they can work. One per participant per event. The catalogue of
/// shift names is config-driven (content.&lt;edition&gt;.json).
/// </summary>
public class VolunteerAvailability
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The shifts the participant can work, stored as a delimited list of
    /// shift names (the catalogue is config-driven so a relational child
    /// table would couple the schema to one edition's shift set).
    /// </summary>
    public string SelectedShifts { get; set; } = string.Empty;

    /// <summary>Preferred role, free text (e.g. "Registration", "A/V").</summary>
    public string? PreferredRole { get; set; }

    /// <summary>Max hours per day the participant is willing to work.</summary>
    public int MaxHoursPerDay { get; set; } = 8;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
