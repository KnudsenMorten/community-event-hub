using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The set of onboarding wizard steps each <see cref="PersonaGroup"/> is REQUIRED
/// to complete. Steps differ per persona: a speaker must verify a bio + picture
/// and complete hotel/appreciation/swag, while a sponsor contact (no travel,
/// no public bio) only completes the appreciation + swag forms. A persona's
/// onboarding is "complete" when every step in its set is done — extra steps a
/// person happens to fill in beyond their set do not block, and steps NOT in the
/// set are not required.
///
/// Mirrors <see cref="OnboardingEmailSets"/> (same persona keys, same
/// reuse-the-PersonaGroup-mapping approach) so the two stay aligned, but it is a
/// separate concern: the email set is "what we mail you", this is "what you must
/// finish in the wizard". Code-defined (no schema) — editing a persona's set is
/// a reviewed code change.
/// </summary>
public static class OnboardingStepSets
{
    // The wizard renders steps in OnboardingStep enum order; each persona's set
    // is a SUBSET of the five, kept in that same order.
    private static readonly IReadOnlyList<OnboardingStep> SpeakerSteps = new[]
    {
        OnboardingStep.Bio,
        OnboardingStep.Picture,
        OnboardingStep.Hotel,
        OnboardingStep.Appreciation,
        OnboardingStep.Swag,
    };

    // Volunteers + media-team travel + get swag/appreciation but have no public
    // speaker bio/picture to verify.
    private static readonly IReadOnlyList<OnboardingStep> CrewSteps = new[]
    {
        OnboardingStep.Hotel,
        OnboardingStep.Appreciation,
        OnboardingStep.Swag,
    };

    // Organizers run the hub; their onboarding is the appreciation + swag forms.
    private static readonly IReadOnlyList<OnboardingStep> OrganizerSteps = new[]
    {
        OnboardingStep.Appreciation,
        OnboardingStep.Swag,
    };

    // Sponsor contacts are not housed/fed by us and have no public bio: swag +
    // appreciation only.
    private static readonly IReadOnlyList<OnboardingStep> SponsorSteps = new[]
    {
        OnboardingStep.Appreciation,
        OnboardingStep.Swag,
    };

    private static readonly IReadOnlyDictionary<PersonaGroup, IReadOnlyList<OnboardingStep>> Sets =
        new Dictionary<PersonaGroup, IReadOnlyList<OnboardingStep>>
        {
            [PersonaGroup.Speaker]   = SpeakerSteps,
            [PersonaGroup.Volunteer] = CrewSteps,
            [PersonaGroup.MediaTeam] = CrewSteps,
            [PersonaGroup.Organizer] = OrganizerSteps,
            [PersonaGroup.Sponsor]   = SponsorSteps,
        };

    /// <summary>The ordered required-step set for a persona group (empty for None).</summary>
    public static IReadOnlyList<OnboardingStep> For(PersonaGroup persona) =>
        Sets.TryGetValue(persona, out var set) ? set : Array.Empty<OnboardingStep>();

    /// <summary>The required-step set for a participant's role (via its persona group).</summary>
    public static IReadOnlyList<OnboardingStep> For(ParticipantRole role) =>
        For(OnboardingEmailSets.PersonaFor(role));

    /// <summary>True if <paramref name="step"/> is required for this persona.</summary>
    public static bool Requires(PersonaGroup persona, OnboardingStep step) =>
        For(persona).Contains(step);

    /// <summary>
    /// How many of the persona's REQUIRED steps the participant has completed.
    /// </summary>
    public static int DoneCount(Participant p) =>
        For(OnboardingEmailSets.PersonaFor(p.Role)).Count(step => IsStepDone(p, step));

    /// <summary>Total required steps for this participant's persona.</summary>
    public static int RequiredCount(Participant p) =>
        For(OnboardingEmailSets.PersonaFor(p.Role)).Count;

    /// <summary>
    /// True when the participant has completed EVERY step required for their
    /// persona. A persona with an empty required set (e.g. an attendee that
    /// somehow reaches the wizard) is trivially complete.
    /// </summary>
    public static bool IsComplete(Participant p)
    {
        var required = For(OnboardingEmailSets.PersonaFor(p.Role));
        return required.All(step => IsStepDone(p, step));
    }

    /// <summary>Completion percentage (0–100) over the persona's required steps.</summary>
    public static int PercentComplete(Participant p)
    {
        int total = RequiredCount(p);
        return total == 0 ? 100 : (int)Math.Round(100.0 * DoneCount(p) / total);
    }

    /// <summary>Read the completion bit for a given step off a participant.</summary>
    public static bool IsStepDone(Participant p, OnboardingStep step) => step switch
    {
        OnboardingStep.Bio          => p.OnboardingCompleted_Bio,
        OnboardingStep.Picture      => p.OnboardingCompleted_Picture,
        OnboardingStep.Hotel        => p.OnboardingCompleted_Hotel,
        OnboardingStep.Appreciation => p.OnboardingCompleted_Appreciation,
        OnboardingStep.Swag         => p.OnboardingCompleted_Swag,
        _ => false,
    };
}
