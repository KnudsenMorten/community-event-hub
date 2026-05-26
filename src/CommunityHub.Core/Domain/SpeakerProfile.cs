namespace CommunityHub.Core.Domain;

/// <summary>
/// Per-participant speaker profile. Used for Speaker / MasterclassSpeaker
/// roles only. Splits into:
///  - "Hub-collected" fields (the participant fills the speaker form):
///      Accreditation, IsFirstTimeSpeaker, Country, Gender
///  - "Sessionize-imported" fields (organizer uploads the Sessionize export):
///      Blog, LinkedIn, Twitter, Tagline, Biography
/// The Sessionize import only overwrites the *imported* fields. The
/// Hub-collected fields are authoritative -- a Sessionize import never
/// touches them, even when the Sessionize export ships its own Country /
/// Gender columns.
/// </summary>
public class SpeakerProfile
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    // --- Hub-collected (authoritative; never touched by Sessionize import) -
    /// <summary>One of: "Microsoft Employee", "Microsoft Expert", "Microsoft MVP", "Microsoft Regional Director", "None".</summary>
    public string? Accreditation { get; set; }

    /// <summary>True if this is the participant's first time speaking at this event series.</summary>
    public bool? IsFirstTimeSpeaker { get; set; }

    public string? Country { get; set; }

    /// <summary>"Male" / "Female" / "Non-binary" / "Prefer not to say".</summary>
    public string? Gender { get; set; }

    /// <summary>True if the speaker is delivering a session on Pre-day (Master Class / workshop day).</summary>
    public bool SpeakingPreDay { get; set; }

    /// <summary>True if the speaker is delivering a session on the main conference day.</summary>
    public bool SpeakingMainDay { get; set; }

    // --- Sessionize-imported (overwritten on each Sessionize import) -------
    public string? Tagline { get; set; }
    public string? Biography { get; set; }
    public string? Blog { get; set; }
    public string? LinkedIn { get; set; }
    public string? Twitter { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastSessionizeImportAt { get; set; }
}
