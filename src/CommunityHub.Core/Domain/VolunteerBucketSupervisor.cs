namespace CommunityHub.Core.Domain;

/// <summary>
/// One supervisor of a Bucket (<see cref="VolunteerCategory"/>): the many-to-many
/// link that lets a Bucket have <b>one OR MORE</b> supervisors — volunteers acting
/// as the go-to person who runs the bucket day to day. This is the second tier of
/// the volunteer → supervisor → ELDK-lead model.
///
/// Appointing a supervisor (creating this row) ELEVATES that volunteer to
/// bucket-scoped management rights for THIS bucket only — exactly like the legacy
/// single <see cref="VolunteerCategory.SupervisorParticipantId"/> grant, which the
/// service treats as an implicit member of this set for back-compat. The pair
/// (CategoryId, ParticipantId) is unique so the same volunteer is never appointed
/// twice on one bucket.
/// </summary>
public class VolunteerBucketSupervisor
{
    public int Id { get; set; }

    // --- Edition scope (denormalized from the bucket) -----------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- The pair -----------------------------------------------------------
    /// <summary>The Bucket (category) being supervised.</summary>
    public int CategoryId { get; set; }
    public VolunteerCategory Category { get; set; } = null!;

    /// <summary>The supervising volunteer. The service enforces this is a
    /// <see cref="ParticipantRole.Volunteer"/> in the same edition.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Email of whoever appointed them (an organizer), for audit.</summary>
    public string? AppointedByEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
