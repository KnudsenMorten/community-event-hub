using CommunityHub.Core.Domain;

namespace CommunityHub.Branding;

/// <summary>
/// User-facing label for ParticipantRole. The pre-day vs main-day distinction
/// now lives on SpeakerProfile.SpeakingPreDay / SpeakingMainDay (and the order
/// entitlements) instead of on the role itself, so there is a single "Speaker"
/// label.
/// </summary>
public static class RoleDisplay
{
    public static string Name(ParticipantRole r) => r switch
    {
        ParticipantRole.Speaker             => "Speaker",
        ParticipantRole.Volunteer           => "Volunteer",
        ParticipantRole.Organizer           => "Organizer",
        ParticipantRole.Sponsor             => "Sponsor",
        ParticipantRole.Attendee            => "Attendee",
        ParticipantRole.Media               => "Media",
        ParticipantRole.EventPartner        => "Event partner",
        _                                   => r.ToString(),
    };
}
