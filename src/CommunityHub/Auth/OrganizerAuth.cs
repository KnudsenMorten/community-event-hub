using CommunityHub.Core.Domain;

namespace CommunityHub.Auth;

/// <summary>
/// Shared authorization predicate for organizer-scoped <b>write</b> actions.
///
/// An acting-as session (organizer "switch to user", or a secretary-token
/// session) deliberately carries the TARGET participant's identity claims —
/// including <see cref="ParticipantRole"/> — so every page renders the target's
/// view and on-behalf writes land on the target's own rows. That means a plain
/// <c>me.Role == Organizer</c> check is satisfied even while acting-as into
/// another organizer, which would (a) grant the acting session full organizer
/// write access and (b) mis-attribute the audit trail to the impersonated
/// target rather than the real actor.
///
/// State-changing organizer handlers must therefore gate on
/// <see cref="IsRealOrganizer"/>: a genuine organizer who is NOT currently
/// acting-as. Read-only / view handlers may stay role-gated so a legitimate
/// acting-as organizer can still VIEW.
/// </summary>
public static class OrganizerAuth
{
    /// <summary>
    /// True when <paramref name="me"/> is a real organizer entitled to perform
    /// organizer write actions: role Organizer AND not in an acting-as session.
    /// An acting-as session — even one impersonating another organizer, and
    /// every secretary-token session — returns false.
    /// </summary>
    public static bool IsRealOrganizer(CurrentParticipant? me) =>
        me is not null && me.Role == ParticipantRole.Organizer && !me.IsActingAs;
}
