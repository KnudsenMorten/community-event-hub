namespace CommunityHub.Core.Domain;

/// <summary>
/// A participant's dinner / social-event signup for an edition. One per
/// participant per event. The list of dinner occasions and dietary options
/// is config-driven (content.&lt;edition&gt;.json).
/// </summary>
public class DinnerSignup
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Legacy bool. New code reads Rsvp instead.</summary>
    public bool Attending { get; set; }

    /// <summary>Three-state RSVP. NotAnswered = empty signup row.</summary>
    public DinnerRsvp Rsvp { get; set; } = DinnerRsvp.NotAnswered;

    /// <summary>Dietary preference, e.g. "None", "Vegetarian", "Vegan", "Gluten-free".</summary>
    public string? DietaryPreference { get; set; }

    /// <summary>Free-text allergy / extra notes.</summary>
    public string? AllergyNotes { get; set; }

    /// <summary>Legacy bool. Superseded by PlusOneCount.</summary>
    public bool PlusOne { get; set; }

    /// <summary>How many plus-one guests in addition to the participant (0..n).</summary>
    public int PlusOneCount { get; set; }

    /// <summary>Free-text comments (late arrival, etc.).</summary>
    public string? Comments { get; set; }

    /// <summary>Set when the engine has emailed the .ics calendar invitation for a Yes RSVP.</summary>
    public DateTimeOffset? CalendarInviteSentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public enum DinnerRsvp
{
    NotAnswered = 0,
    Yes = 1,
    No = 2,
    Maybe = 3
}
