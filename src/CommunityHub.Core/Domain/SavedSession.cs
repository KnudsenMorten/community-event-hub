namespace CommunityHub.Core.Domain;

/// <summary>
/// One session a signed-in participant has SAVED to their personal plan ("My plan").
/// A self-service bookmark: it records only that this person wants this talk in
/// their personal running order — it never books a seat or reserves capacity (that
/// stays in Zoho Bookings) and it is never shown publicly.
///
/// Edition-scoped and own-row scoped: a row links one <see cref="Participant"/> to
/// one <see cref="Session"/> within one <see cref="Event"/>. The (Event, Participant,
/// Session) triple is unique so saving the same session twice is a no-op (idempotent
/// toggle). Distinct from the attendee's reconciled Master Class (which is matched
/// from their Zoho booking) — this is the attendee's own free choice across the
/// whole public agenda.
/// </summary>
public class SavedSession
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>When the participant saved this session (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
