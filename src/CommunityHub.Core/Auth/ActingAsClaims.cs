using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The acting-as session contract, expressed without any web dependency so it
/// is unit-testable: the claim-type names an impersonation / secretary sign-in
/// writes, and the pure parser that turns those raw claim values into an
/// <see cref="ActingAsContext"/>. The web-layer <c>CurrentParticipant</c>
/// delegates here, so the marker semantics (target identity + who is really
/// acting + "no nested impersonation") have one source of truth.
/// </summary>
public static class ActingAsClaims
{
    /// <summary>Claim type carrying the <see cref="ImpersonationActorKind"/>.</summary>
    public const string ActorKind = "ActingAsKind";

    /// <summary>Claim type carrying the acting organizer's participant id (organizer kind only).</summary>
    public const string ActorParticipantId = "ActingAsActorId";

    /// <summary>Claim type carrying the human label of the actor (banner + audit).</summary>
    public const string ActorLabel = "ActingAsActorLabel";

    /// <summary>
    /// Parse the raw acting-as claim values into a context, or null when this is
    /// a normal (non-impersonated) session (no/invalid <paramref name="kind"/>).
    /// </summary>
    public static ActingAsContext? Parse(string? kind, string? actorParticipantId, string? actorLabel)
    {
        if (!Enum.TryParse<ImpersonationActorKind>(kind, out var parsedKind))
        {
            return null;
        }
        int? actorPid = int.TryParse(actorParticipantId, out var ap) ? ap : null;
        return new ActingAsContext(
            parsedKind, actorPid,
            string.IsNullOrWhiteSpace(actorLabel) ? "(unknown)" : actorLabel);
    }
}

/// <summary>
/// Who is really driving an acting-as session (the actor), while the surrounding
/// signed-in identity is the target being acted upon.
/// </summary>
/// <param name="Kind">Organizer "switch to user", or a secretary-token session.</param>
/// <param name="ActorParticipantId">The organizer's participant id (null for a secretary token).</param>
/// <param name="ActorLabel">Human label of the actor for the banner + audit.</param>
public sealed record ActingAsContext(
    ImpersonationActorKind Kind, int? ActorParticipantId, string ActorLabel);
