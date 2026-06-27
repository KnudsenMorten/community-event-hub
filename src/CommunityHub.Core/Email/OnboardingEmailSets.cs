using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Email;

/// <summary>
/// The five persona GROUPS the onboarding email sets are keyed on. A persona
/// group collapses the finer <see cref="ParticipantRole"/> values into the
/// audiences the requirement lists (volunteer / speaker / media-team / sponsor /
/// organizer). Speaker covers <see cref="ParticipantRole.Speaker"/>; media-team
/// covers <see cref="ParticipantRole.Media"/>. Event partners map to the media-team
/// (crew) set — like media-team they are housed/fed crew, not Organizer (who are not).
/// Attendees are deliberately NOT an onboarding-set persona (they get the
/// existing welcome flow, not a crew onboarding set).
/// </summary>
public enum PersonaGroup
{
    Organizer = 0,
    Speaker = 1,
    Volunteer = 2,
    MediaTeam = 3,
    Sponsor = 4,
    /// <summary>No onboarding set (e.g. attendee) — auto-send is a no-op.</summary>
    None = 99,
}

/// <summary>One onboarding email in a persona's set.</summary>
/// <param name="StepKey">
/// Stable key used in the <c>SentReminder</c> occasion (<c>onboarding:{StepKey}</c>)
/// so the set is idempotent per person and renaming the template never re-sends.
/// </param>
/// <param name="TemplateName">The branded content template to render.</param>
public sealed record OnboardingEmail(string StepKey, string TemplateName);

/// <summary>
/// The defined onboarding email SET per persona group (requirement 10a-1). The
/// sets are intentionally small + code-defined (no schema): editing them is a
/// code change reviewed like any other. Each persona's set is ordered; the
/// engine sends each not-yet-sent email and records it in the ledger.
/// </summary>
public static class OnboardingEmailSets
{
    /// <summary>The reminder-type recorded in the ledger for every onboarding email.</summary>
    public const string ReminderType = "onboarding";

    private static readonly OnboardingEmail GettingStarted =
        new("getting-started", "onboarding-getting-started");

    private static readonly IReadOnlyDictionary<PersonaGroup, IReadOnlyList<OnboardingEmail>> Sets =
        new Dictionary<PersonaGroup, IReadOnlyList<OnboardingEmail>>
        {
            [PersonaGroup.Organizer] = new[] { GettingStarted },
            [PersonaGroup.Speaker]   = new[] { GettingStarted },
            [PersonaGroup.Volunteer] = new[] { GettingStarted },
            [PersonaGroup.MediaTeam] = new[] { GettingStarted },
            [PersonaGroup.Sponsor]   = new[] { GettingStarted },
        };

    /// <summary>The ordered onboarding set for a persona (empty for <see cref="PersonaGroup.None"/>).</summary>
    public static IReadOnlyList<OnboardingEmail> For(PersonaGroup persona) =>
        Sets.TryGetValue(persona, out var set) ? set : Array.Empty<OnboardingEmail>();

    /// <summary>Collapse a role into its onboarding persona group.</summary>
    public static PersonaGroup PersonaFor(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => PersonaGroup.Organizer,
        ParticipantRole.Speaker => PersonaGroup.Speaker,
        ParticipantRole.Volunteer => PersonaGroup.Volunteer,
        ParticipantRole.Media => PersonaGroup.MediaTeam,
        ParticipantRole.EventPartner => PersonaGroup.MediaTeam,
        ParticipantRole.Sponsor => PersonaGroup.Sponsor,
        _ => PersonaGroup.None,
    };
}
