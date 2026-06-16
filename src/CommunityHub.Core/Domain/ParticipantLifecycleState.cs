namespace CommunityHub.Core.Domain;

/// <summary>
/// The onboarding lifecycle state of a participant — the pre-selection gate that
/// the onboarding flow turns on. A person enters the hub in a holding state, an
/// organizer validates the data and advances them, and only an
/// <see cref="Active"/> participant can sign in.
///
/// This is DISTINCT from <see cref="Participant.IsActive"/>: that boolean stays
/// the deactivation / cancellation switch (a withdrawn person is
/// <c>IsActive = false</c> no matter what lifecycle state they reached). Login
/// requires BOTH <c>IsActive</c> AND <c>LifecycleState == Active</c>, so a
/// not-yet-activated queue entry can never sign in.
///
/// The states are ordered and the flow only ever moves FORWARD through the queue
/// (Inactive → Preselected → Active); the comparable int values let a bulk
/// "advance" no-op for rows already at or beyond the target.
/// </summary>
public enum ParticipantLifecycleState
{
    /// <summary>
    /// Default. Just landed in the pre-selection queue (e.g. a Sessionize sync
    /// result or a volunteer interest-form sign-up) and not yet reviewed.
    /// Cannot sign in.
    /// </summary>
    Inactive = 0,

    /// <summary>
    /// An organizer has shortlisted this person but not yet fully activated them
    /// (data still being validated). Still cannot sign in.
    /// </summary>
    Preselected = 1,

    /// <summary>
    /// Fully activated by an organizer. Combined with <c>IsActive = true</c> this
    /// is the only state in which the person can sign in and run onboarding.
    /// </summary>
    Active = 2,
}
