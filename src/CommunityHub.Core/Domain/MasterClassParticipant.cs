namespace CommunityHub.Core.Domain;

/// <summary>
/// One participant booked into a master class, synced ONE-WAY from Zoho Booking
/// (REQUIREMENTS § 6c). Links the master-class <see cref="Session"/> to the
/// <see cref="Participant"/> the booking resolves to (created/looked-up by email
/// within the edition). Zoho Booking is the source of truth for the booking
/// CONTENT (who booked, status); the hub owns the link + the lifecycle gate.
///
/// <b>Idempotent:</b> the sync upserts by (EventId, SessionId, BookingRecordId)
/// — re-pulling the same booking updates the row in place rather than
/// duplicating. A booking the hub has already linked is never re-created.
///
/// <b>Lifecycle:</b> a freshly-booked participant is created
/// <see cref="ParticipantLifecycleState.Inactive"/> (and the link's
/// <see cref="IsActive"/> follows the booking status); the sync NEVER activates
/// a participant for login — an organizer validates them through the normal
/// pre-selection queue, consistent with the rest of the hub.
/// </summary>
public class MasterClassParticipant
{
    public int Id { get; set; }

    /// <summary>The edition this booking link belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The master-class session the participant booked.</summary>
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>The hub participant the booking resolved to (matched/created by email).</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The Zoho Booking record / appointment id — the idempotency key. A
    /// re-sync of the same booking updates this row in place. Unique within a
    /// (event, session).
    /// </summary>
    public string BookingRecordId { get; set; } = string.Empty;

    /// <summary>The booked person's email as it came from Zoho Booking (lower-cased).</summary>
    public string BookedEmail { get; set; } = string.Empty;

    /// <summary>The booked person's display name as it came from Zoho Booking.</summary>
    public string BookedName { get; set; } = string.Empty;

    /// <summary>
    /// The raw booking status from Zoho Booking (e.g. <c>upcoming</c> /
    /// <c>completed</c> / <c>cancelled</c>). Drives <see cref="IsActive"/>.
    /// </summary>
    public string BookingStatus { get; set; } = string.Empty;

    /// <summary>
    /// False once the booking is cancelled in Zoho (a re-sync flips this) — the
    /// link stays for history rather than being hard-deleted, mirroring the
    /// reconciliation model elsewhere in the hub.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this link was last touched by a Booking sync (upsert stamp).</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }
}
