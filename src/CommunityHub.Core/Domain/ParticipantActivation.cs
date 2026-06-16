using System.Linq.Expressions;

namespace CommunityHub.Core.Domain;

/// <summary>
/// The single, lifecycle-correct definition of an <b>active</b> participant
/// (issue #39): a person is active only when BOTH the withdrawal switch
/// <see cref="Participant.IsActive"/> is true AND the onboarding gate
/// <see cref="Participant.LifecycleState"/> has reached
/// <see cref="ParticipantLifecycleState.Active"/>. A withdrawn person
/// (<c>IsActive=false</c>) or a not-yet-activated queue entry
/// (<c>LifecycleState != Active</c>) is NOT active and drops out of the active
/// views — matching exactly the combined login gate the PIN flow enforces.
///
/// Both forms are provided so callers never re-spell the rule:
///   - <see cref="IsActive(Participant)"/> for an in-memory check;
///   - <see cref="IsActiveExpr"/> for an EF <c>Where(...)</c> that translates to SQL.
/// </summary>
public static class ParticipantActivation
{
    /// <summary>In-memory: is this participant active (lifecycle-correct)?</summary>
    public static bool IsActive(Participant p) =>
        p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active;

    /// <summary>
    /// EF-translatable predicate for the same rule, e.g.
    /// <c>db.Participants.Where(ParticipantActivation.IsActiveExpr)</c>.
    /// </summary>
    public static readonly Expression<Func<Participant, bool>> IsActiveExpr =
        p => p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active;
}
