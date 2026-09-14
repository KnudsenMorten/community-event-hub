using CommunityHub.Core.Attendees;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 — WHO COUNTS in a logistics calculation. One rule, one place.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Operator 2026-08-02: <i>"in general inactive is not counted"</i></b> — and every
/// logistics file exists to order or pay for something. A withdrawn person in the breakfast count is
/// a breakfast bought; in the polo file, a shirt printed; in the hotel file, a room blocked. §253 G5
/// already stated this for the swag downloads (<i>"a drop-out's row would be paid for and printed"</i>);
/// §6.4 makes it the rule for every file the venue and the suppliers receive.</para>
///
/// <para>🔑 <b>The inputs are the Get Started form results</b> (operator, same message: <i>"the
/// different get started forms results goes to calculation, like hotel party food etc"</i>) — the
/// party RSVP, the hotel booking, the dietary capture, the swag preferences. Those rows outlive a
/// deactivation on purpose (§209 keeps a cancelled RSVP row for audit and re-asking), so filtering
/// on the FORM ROW existing is not the same as filtering on the person still coming. The join to an
/// ACTIVE participant is what makes a count honest.</para>
///
/// <para>⚠️ <b>This is a filter, not a lookup.</b> It is deliberately expressed over
/// <see cref="IQueryable{T}"/> so it composes into the database query — a producer that pulled every
/// row and filtered in memory would be correct and would also load the whole edition to count
/// breakfasts.</para>
/// </remarks>
public static class LogisticsAudience
{
    /// <summary>
    /// The participants a logistics file may count: this edition, ACTIVE, real people.
    /// </summary>
    public static IQueryable<Participant> Countable(IQueryable<Participant> participants, int eventId) =>
        participants.Where(p => p.EventId == eventId && p.IsActive && !p.IsTestUser);

    /// <summary>
    /// Does this participant count? The in-memory twin of <see cref="Countable"/>, for a producer
    /// that already holds the row.
    /// </summary>
    /// <remarks>
    /// ⚠️ Two expressions of one rule is the shape that drifts (§767). They are kept together, and
    /// a test asserts they agree — because the day they disagree, one file counts a person the next
    /// file does not, and the venue gets two different head-counts for the same event.
    /// </remarks>
    public static bool Counts(Participant p, int eventId) =>
        p.EventId == eventId && p.IsActive && !p.IsTestUser;

    // ---- ATTENDEES ---------------------------------------------------------------------------
    //
    // 🔴 §1086 (operator 2026-08-14): *"logistics lunch preday must contain any attendees that
    // booked a 2-day ticket. i think it is missing in the calculation and excel export"* — and it
    // was. This class only knew about PARTICIPANTS, so every logistics file counted the crew and
    // silently left out the paying audience.
    //
    // ⚠️ The rule itself was not missing from CEH: /Organizer/Lunch has counted 2-day ticket
    // holders into the pre-day since §326bv, written for exactly this reason (*"i need to trust
    // this, so i order correct amount"*). What was missing was the FILE agreeing with the PAGE.
    // One question with two answers is the §366/§1081 shape, and it is why the rules live HERE now
    // rather than in whichever surface remembered them.

    /// <summary>
    /// The attendees a logistics file may count: this edition, and still holding their ticket.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <see cref="MirrorState.Active"/> is the whole filter, and it is the §326as rule: a
    /// cancelled or reassigned-away ticket keeps its ROW (that is the audit trail) but stops being a
    /// person who eats. A count that ignored it would order lunch for everyone who ever bought.</para>
    ///
    /// <para>⚠️ There is no <c>IsTestUser</c> on <see cref="Attendee"/> — a test ticket would have to
    /// be a real Backstage order — so, unlike the participant rule, there is nothing to exclude. The
    /// same is true of the organizer page this mirrors.</para>
    /// </remarks>
    public static IQueryable<Attendee> CountableAttendees(
        IQueryable<Attendee> attendees, int eventId) =>
        attendees.LiveIn(eventId);

    /// <summary>
    /// The attendees who are on site for the PRE-DAY: holders of a 2-day ticket.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The pre-day IS the Master Class day, and only a 2-day ticket admits you to it</b> — so
    /// this is not a catering guess, it is the ticket. A 1-day holder is not in the building.
    /// <see cref="Attendee.TicketStatus"/> is maintained by the Backstage sync from the stable
    /// <c>ticket_class_id</c> (§707.35b — never the editable class NAME).
    /// </remarks>
    public static IQueryable<Attendee> PreDayAttendees(
        IQueryable<Attendee> attendees, int eventId) =>
        CountableAttendees(attendees, eventId)
            .Where(a => a.TicketStatus == TicketStatus.TwoDay);
}
