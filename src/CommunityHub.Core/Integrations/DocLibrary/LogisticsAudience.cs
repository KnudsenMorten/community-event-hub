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
/// <para>🔒 <b>§1257 — a LINKED DUPLICATE does not count.</b> A row whose
/// <see cref="Participant.SamePersonAsId"/> is set is the same human as another row (two upstream
/// systems owning one person under two addresses), and the primary represents them. Counting both
/// orders two hotel rooms, two polos and two dinners for one person. <c>OrderCountService</c> has
/// applied exactly this rule since it was written; putting it HERE is what makes the hotel, swag,
/// lunch, food and expo files — and the Excel exports — agree with it, instead of one consumer
/// de-duplicating and five not.</para>
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
        participants.Where(p => p.EventId == eventId && p.IsActive && !p.IsTestUser
                                && p.SamePersonAsId == null);

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
        p.EventId == eventId && p.IsActive && !p.IsTestUser
        && p.SamePersonAsId == null;

    /// <summary>
    /// §1258 — the CREW: countable participants who are NOT already represented by a live attendee
    /// ticket. This is the participant side of any count that also adds the attendee side.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>The defect this fixes, measured in PROD 2026-09-20.</b> The main-day lunch tile
    /// read <c>260 crew &amp; speakers + 163 attendees = 423</c>. But 137 of those 260 were
    /// ATTENDEE-ROLE participant rows, and every one of them also held a live ticket — so 137
    /// people were counted twice and the caterer would have been ordered ~137 lunches too many.
    /// The honest figure was 123 + 163 = 286.</para>
    ///
    /// <para>🔑 <b>The assumption was true when it was written and stopped being true later.</b>
    /// This class says the two sides are disjoint because <i>"crew do not have tickets"</i>
    /// (operator 2026-08-14) — and that still holds: not one speaker, sponsor or organizer holds a
    /// ticket. What changed is that the ticket launch began PROVISIONING A PARTICIPANT LOGIN for
    /// every 2-day buyer, so the participant table stopped being crew-only. ⇒ The disjointness is
    /// now ENFORCED here rather than assumed, and it cannot silently lapse again.</para>
    ///
    /// <para>⚠️ Matched on e-mail, not on <c>Role == Attendee</c>: the rule is "this human is
    /// already counted by their ticket", which is about the ticket, not about the label on the row.
    /// A speaker who buys a ticket is counted once for the same reason.</para>
    ///
    /// <para>🔒 <b>MAIN-DAY ONLY — any live ticket, any class.</b> The main day admits every ticket
    /// holder, so any active ticket is a second count and must remove its holder from the crew
    /// side. The PRE-DAY is different and this method must NOT be used for it: only a 2-day ticket
    /// admits you to the Master Class day (<see cref="PreDayAttendees"/>), so a crew member holding
    /// a 1-DAY ticket is not counted on the pre-day attendee side at all — excluding them from the
    /// pre-day crew would drop a real head and under-order a meal. The pre-day figure is built from
    /// sign-ups and the auto-counted always-on-site roles instead, and stays that way.</para>
    ///
    /// <para>ℹ️ No live exposure to that edge today: measured 2026-09-20, zero crew hold a ticket
    /// of either class. The rule is written for the day one does.</para>
    /// </remarks>
    public static IQueryable<Participant> CountableCrew(
        IQueryable<Participant> participants, IQueryable<Attendee> attendees, int eventId) =>
        Countable(participants, eventId)
            .Where(p => !attendees.Any(a => a.EventId == eventId
                                            && a.MirrorState == MirrorState.Active
                                            && a.Email == p.Email));

    /// <summary>
    /// §1257 — the rows this rule EXCLUDES because they are a linked duplicate of another row:
    /// active, real, in this edition, and pointing at a primary via
    /// <see cref="Participant.SamePersonAsId"/>.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>This is why no separate de-duplication report was built.</b> Every logistics
    /// surface already asks <see cref="Countable"/> who counts; asking it a second question —
    /// "and who did you leave out?" — keeps ONE definition of a duplicate. A report with its own
    /// query would be a second definition, and the day the two disagree the numbers stop adding up
    /// with nothing to say which is right (the §366/§1081 shape).</para>
    ///
    /// <para>The pair (raw = countable + excluded) is what an order sheet needs to show: the number
    /// being ordered, and the people deliberately not in it.</para>
    /// </remarks>
    public static IQueryable<Participant> ExcludedDuplicates(
        IQueryable<Participant> participants, int eventId) =>
        participants.Where(p => p.EventId == eventId && p.IsActive && !p.IsTestUser
                                && p.SamePersonAsId != null);

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
