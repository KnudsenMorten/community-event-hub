namespace CommunityHub.Core.Domain;

/// <summary>
/// A participant's swag preferences for an edition (polo / jacket / gift).
/// Only collected for roles that receive swag (Volunteer, Speaker,
/// MasterclassSpeaker, Organizer) — never for Sponsor / Attendee.
/// Stores the participant's STATED preference. The organizer-set final
/// allocation count lives on a separate entitlement table (added later).
/// </summary>
public class SwagPreference
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    // --- Polo ---------------------------------------------------------------
    /// <summary>True = the participant wants a polo. False = "I wear my own clothes" / not interested.</summary>
    public bool WantsPolo { get; set; }
    /// <summary>One of the labels in PoloSizes (e.g. "M (men)"). Null if WantsPolo is false.</summary>
    public string? PoloSize { get; set; }

    // --- Jacket -------------------------------------------------------------
    public bool WantsJacket { get; set; }
    public string? JacketSize { get; set; }

    // --- Appreciation award (formerly "Gift") -------------------------------
    /// <summary>Default-on. Speaker/volunteer/organizer engraved appreciation award.</summary>
    public bool WantsGift { get; set; } = true;

    // --- Credly badge -------------------------------------------------------
    /// <summary>Default-on. Digital Credly badge.</summary>
    public bool WantsCredlyBadge { get; set; } = true;

    // --- Notes --------------------------------------------------------------
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
