namespace CommunityHub.Core.Domain;

/// <summary>
/// Where a <see cref="TaskAllocationDraft"/> row came from — the lifecycle
/// stage marker that lets the SAME queue table hold both the engine's
/// stage-1 <i>proposed</i> rows and the lead/organizer's stage-2 <i>queued</i>
/// edits (§150). The review UI can badge engine proposals distinctly.
/// </summary>
public enum DraftSource
{
    /// <summary>Hand-added/edited by a lead or organizer in the queue (stage 2 = queued).</summary>
    Manual = 0,
    /// <summary>Auto-proposed by the availability auto-assign engine (stage 1 = proposed).</summary>
    EngineProposed = 1,
}

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

    /// <summary>The person proposed for the task (a volunteer or, for the
    /// organizer queue, an organizer — see <see cref="TargetRole"/>).</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The role this draft queues FOR — the discriminator that lets the one
    /// draft table back BOTH the volunteer queue and the new organizer queue
    /// (§150 route-by-Responsible-Team) without a parallel entity.
    /// <see cref="ParticipantRole.Volunteer"/> = volunteer queue (the existing
    /// behaviour, so legacy rows default here); <see cref="ParticipantRole.Organizer"/>
    /// = organizer queue. Stored as int.
    /// </summary>
    public ParticipantRole TargetRole { get; set; } = ParticipantRole.Volunteer;

    /// <summary>
    /// Lifecycle stage marker (§150): <see cref="DraftSource.EngineProposed"/>
    /// for stage-1 engine output, <see cref="DraftSource.Manual"/> for stage-2
    /// lead/organizer queue edits. Defaults to Manual so existing rows (all
    /// hand-queued) keep their meaning; never an email trigger — only Commit mails.
    /// Stored as int.
    /// </summary>
    public DraftSource Source { get; set; } = DraftSource.Manual;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
