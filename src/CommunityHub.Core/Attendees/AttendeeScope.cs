using System.Linq.Expressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Attendees;

/// <summary>
/// §1086 — <b>WHICH ATTENDEE ROWS ARE STILL A PERSON WHO IS COMING.</b> The attendee twin of
/// <see cref="Data.ParticipantLifecycleScope"/>: one rule, one place.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why it exists.</b> Operator 2026-08-14: <i>"the mechanism on the dashboard must use
/// the current calculation for attendees. it shows 100, but we have 97"</i>. The dashboard counted
/// every <see cref="Attendee"/> ROW in the edition; <c>/Organizer/Attendees</c> counts the ones
/// whose ticket is still live. The gap is exactly the cancelled and reassigned-away tickets.</para>
///
/// <para>🔑 <b>The rows are KEPT on purpose, and that is what made this a trap.</b> §326as / §707.23:
/// a cancellation stamps <see cref="Attendee.MirrorState"/> <c>Cancelled</c> and keeps the row, and a
/// reassignment keeps the previous holder's address — <i>"we keep old references when cancellations
/// or reassignments happen"</i>. So the table is deliberately a HISTORY, and any consumer that reads
/// it as a head-count is silently counting people who are not coming. Three did:
/// the organizer dashboard, the command centre and the reporting export.</para>
///
/// <para>⚠️ <b>There is no test-user flag to exclude here</b>, unlike participants — an attendee row
/// only exists because a real Backstage order created it.</para>
/// </remarks>
public static class AttendeeScope
{
    /// <summary>
    /// Still holding a ticket: the row has not been cancelled or reassigned away.
    /// </summary>
    public static readonly Expression<Func<Attendee, bool>> IsLiveExpr =
        a => a.MirrorState == MirrorState.Active;

    /// <summary>Only attendees still holding a ticket — composable onto any attendee query.</summary>
    public static IQueryable<Attendee> Live(this IQueryable<Attendee> attendees) =>
        attendees.Where(IsLiveExpr);

    /// <summary>This edition's live attendees, in one call.</summary>
    public static IQueryable<Attendee> LiveIn(this IQueryable<Attendee> attendees, int eventId) =>
        attendees.Where(a => a.EventId == eventId).Live();

    /// <summary>
    /// The in-memory twin, for a caller that already holds the row.
    /// ⚠️ Kept beside the expression so the two cannot drift (§867.1's lesson).
    /// </summary>
    public static bool IsLive(this Attendee a) => a.MirrorState == MirrorState.Active;
}
