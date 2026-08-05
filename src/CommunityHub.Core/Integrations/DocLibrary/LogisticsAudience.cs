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
}
