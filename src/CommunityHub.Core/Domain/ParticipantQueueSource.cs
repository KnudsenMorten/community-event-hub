namespace CommunityHub.Core.Domain;

/// <summary>
/// Where a participant row in the pre-selection queue came from. Recorded so the
/// organizer queue can show / filter by inbound source. Most participants are
/// created directly by an organizer (<see cref="Manual"/>); the onboarding queue
/// is fed by the two automated inbound sources.
/// </summary>
public enum ParticipantQueueSource
{
    /// <summary>Created directly by an organizer (the default for hand-added rows).</summary>
    Manual = 0,

    /// <summary>
    /// Landed from the Sessionize-API speaker sync (the accepted-speaker results).
    /// </summary>
    SessionizeSync = 1,

    /// <summary>
    /// Landed from the public volunteer interest form / sign-up.
    /// </summary>
    VolunteerInterestForm = 2,

    /// <summary>
    /// Media / crew (video + camera) interest sign-up.
    /// </summary>
    MediaTeamSignup = 3,
}
