using System.Linq.Expressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Data;

/// <summary>
/// THE definition of the two ways a participant can be inactive — one predicate each, used by
/// every surface that asks the question.
///
/// <para>§499 (operator 2026-07-28): <i>"we have 2 scenarios of inactive state - a person comes in
/// from volunteer sign-up or from sessionize as inactive... but the other scenario is an active
/// person, that becomes inactive. how can we differentiate the 2?"</i> and then the third case:
/// <i>"a zoho ticket is cancelled or ticket is reassigned, so then the old attendee should become
/// inactive as well and not get reminders"</i>.</para>
///
/// <para><b>They are already distinguishable — each path leaves a different fingerprint:</b></para>
/// <list type="table">
///   <item><term>Sessionize import / volunteer sign-up</term>
///     <description><c>IsActive=false</c>, <c>LifecycleState</c> Inactive|Preselected, no
///     tombstone — <b>never was in</b>.</description></item>
///   <item><term>Ticket cancelled or reassigned</term>
///     <description><c>IsActive=false</c> but <c>LifecycleState</c> stays <b>Active</b> —
///     <see cref="Reminders.AttendeeTicketSyncService"/> flips only the login gate and leaves the
///     onboarding state untouched, which is precisely what marks them as someone who WAS in.
///     </description></item>
///   <item><term>Organizer deactivation</term>
///     <description><c>IsActive=false</c>, <c>LifecycleState=Inactive</c>, and the §253 G8
///     <see cref="Participant.DeactivatedByOrganizerAt"/> TOMBSTONE stamped.</description></item>
/// </list>
///
/// <para><b>Why one file.</b> The same question is asked by the reminder engine, the organizer
/// counts and the pending queues. Written out three times they drift, and the drift is invisible
/// until someone who cancelled their ticket gets chased for a master-class choice (§498). This is
/// the §428 lesson: do not add the missing clause in one more place — make there be one place.</para>
/// </summary>
public static class ParticipantLifecycleScope
{
    /// <summary>
    /// <b>WAS in, now OUT</b> — a cancelled or reassigned ticket, or an organizer withdrawal.
    ///
    /// <para>These people are gone: no reminders, excluded from counts, out of pending queues.
    /// Chasing them is worse than useless — it mails someone who already left.</para>
    ///
    /// <para>Two clauses because there are two exits: a ticket sync leaves
    /// <c>LifecycleState = Active</c> (it only closes the login gate), while an organizer
    /// deactivation drops the lifecycle to Inactive but stamps the tombstone.</para>
    /// </summary>
    public static Expression<Func<Participant, bool>> DroppedOutExpr =>
        p => !p.IsActive
             && (p.LifecycleState == ParticipantLifecycleState.Active
                 || p.DeactivatedByOrganizerAt != null);

    /// <summary>
    /// <b>NOT yet in</b> — arrived from a Sessionize sync or a volunteer interest form and is
    /// waiting to be reviewed.
    ///
    /// <para>These people BELONG in the pending queues: they are the organizer's work list, not
    /// noise. Hiding them would hide the queue's whole purpose.</para>
    /// </summary>
    public static Expression<Func<Participant, bool>> AwaitingOnboardingExpr =>
        p => !p.IsActive
             && p.LifecycleState != ParticipantLifecycleState.Active
             && p.DeactivatedByOrganizerAt == null;

    /// <summary>
    /// May this person be sent a reminder / chased? Only a fully signed-in-capable participant:
    /// <c>IsActive</c> AND <c>LifecycleState == Active</c> — the same pair
    /// <c>PinIdentityProvider</c> requires to log in.
    ///
    /// <para>Deliberately NOT "not dropped out": someone awaiting onboarding cannot sign in either,
    /// so a reminder telling them to go and do something in the hub would be equally useless.</para>
    /// </summary>
    public static Expression<Func<Participant, bool>> RemindableExpr =>
        p => p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active;

    // ---- IQueryable composition (EF-translatable) ---------------------------------------

    /// <summary>Only people who may be reminded — composable onto any participant query.</summary>
    public static IQueryable<Participant> Remindable(this IQueryable<Participant> participants) =>
        participants.Where(RemindableExpr);

    /// <summary>Only people who WERE in and are now out (cancelled / reassigned / withdrawn).</summary>
    public static IQueryable<Participant> DroppedOut(this IQueryable<Participant> participants) =>
        participants.Where(DroppedOutExpr);

    /// <summary>Only people still awaiting onboarding review (the pending queues' real content).</summary>
    public static IQueryable<Participant> AwaitingOnboarding(this IQueryable<Participant> participants) =>
        participants.Where(AwaitingOnboardingExpr);

    // ---- In-memory equivalents ----------------------------------------------------------
    // Same rules for a materialised row, so a page that already has the entity does not
    // hand-roll the check and quietly disagree with the query that fetched it.

    /// <summary>True when this person was in and is now out.</summary>
    public static bool IsDroppedOut(this Participant p) =>
        !p.IsActive
        && (p.LifecycleState == ParticipantLifecycleState.Active
            || p.DeactivatedByOrganizerAt != null);

    /// <summary>True when this person has not been activated yet (queue content, not a drop-out).</summary>
    public static bool IsAwaitingOnboarding(this Participant p) =>
        !p.IsActive
        && p.LifecycleState != ParticipantLifecycleState.Active
        && p.DeactivatedByOrganizerAt == null;

    /// <summary>True when this person may be reminded / chased.</summary>
    public static bool IsRemindable(this Participant p) =>
        p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active;
}
