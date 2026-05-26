using CommunityHub.Core.Domain;

namespace CommunityHub.Branding;

/// <summary>
/// User-facing label for ParticipantRole. The DB enum keeps Speaker +
/// MasterclassSpeaker as separate values (for code routing), but the UI
/// always shows them as just "Speaker" -- the pre-day vs main-day
/// distinction now lives on SpeakerProfile.SpeakingPreDay / SpeakingMainDay
/// instead of on the role itself.
/// </summary>
public static class RoleDisplay
{
    public static string Name(ParticipantRole r) => r switch
    {
        ParticipantRole.Speaker             => "Speaker",
        ParticipantRole.MasterclassSpeaker  => "Speaker",
        ParticipantRole.Volunteer           => "Volunteer",
        ParticipantRole.Organizer           => "Organizer",
        ParticipantRole.Sponsor             => "Sponsor",
        ParticipantRole.Attendee            => "Attendee",
        ParticipantRole.Video               => "Video crew",
        ParticipantRole.Camera              => "Photo crew",
        _                                   => r.ToString(),
    };
}
