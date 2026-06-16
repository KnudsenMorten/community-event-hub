namespace CommunityHub.Core.Domain;

/// <summary>
/// A PENDING (draft) allocation of a volunteer to a <see cref="VolunteerTask"/>,
/// held in a queue while the organizer plans coverage. This is the key piece of
/// the task-mapper: the organizer links people→tasks into a DRAFT and sees the
/// red/green gap update LIVE as a <i>simulation</i> — but NOTHING is actually
/// assigned. Only on <b>Commit</b> are these draft rows turned into real
/// <see cref="VolunteerTaskAssignment"/> rows (and the drafts cleared). A draft
/// can also be discarded/reset without ever touching assignments.
///
/// Deliberately a DISTINCT entity (not reusing <see cref="VolunteerTaskAssignment"/>)
/// so a draft is never mistaken for a real assignment by any "who is assigned?"
/// query: gap simulation counts assignments + drafts; the volunteer's "My tasks"
/// view counts assignments only. The pair (TaskId, ParticipantId) is unique so the
/// same person is queued onto one task at most once. Edition + organizer scoped so
/// two organizers' drafts never collide.
/// </summary>
public class TaskAllocationDraft
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The organizer whose draft session this belongs to. Drafts are
    /// per-planner so simultaneous planning sessions stay isolated; Commit/Discard
    /// operate on exactly this owner's queue.</summary>
    public int OwnerParticipantId { get; set; }
    public Participant OwnerParticipant { get; set; } = null!;

    // --- The proposed (not-yet-real) link -----------------------------------
    public int TaskId { get; set; }
    public VolunteerTask Task { get; set; } = null!;

    /// <summary>The volunteer proposed for the task.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
