namespace CommunityHub.Core.Domain;

/// <summary>
/// The volunteer's own decision about a shift they are assigned to. This is the
/// self-service signal a volunteer raises on their OWN assignment — it never
/// removes the assignment (a coordinator reassigns), it records intent so the
/// coordinator sees "this shift needs attention". Distinct from
/// <see cref="VolunteerTaskStatus"/> (the work-state of the task itself).
/// </summary>
public enum ShiftDecisionStatus
{
    /// <summary>Default — the volunteer has not flagged anything; they intend to work it.</summary>
    None = 0,
    /// <summary>The volunteer confirmed they can take this shift (availability = yes).</summary>
    Confirmed = 1,
    /// <summary>The volunteer cannot take this shift and has declined it — needs reassignment.</summary>
    Declined = 2,
    /// <summary>The volunteer asked to swap this shift (offer it back / to someone) — needs reassignment.</summary>
    SwapRequested = 3,
}

/// <summary>
/// The many-to-many link between a <see cref="VolunteerTask"/> and a volunteer
/// <see cref="Participant"/>: one row = "this volunteer is assigned to this task".
/// A task can have several volunteers; a volunteer can be on several tasks. The
/// pair (TaskId, ParticipantId) is unique so the same volunteer is never linked
/// to one task twice. <see cref="EventId"/> is carried so a "my tasks" query for
/// a volunteer is a single edition-scoped lookup.
/// </summary>
public class VolunteerTaskAssignment
{
    public int Id { get; set; }

    // --- Edition scope (denormalized) ---------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- The pair -----------------------------------------------------------
    public int TaskId { get; set; }
    public VolunteerTask Task { get; set; } = null!;

    /// <summary>The volunteer assigned. The service layer enforces this is a
    /// <see cref="ParticipantRole.Volunteer"/> in the same edition.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Who created the assignment (organizer lead or category supervisor email), for audit.</summary>
    public string? AssignedByEmail { get; set; }

    // --- Volunteer self-service shift decision (added 2026-06-16) ------------
    /// <summary>
    /// The assigned volunteer's own decision about this shift — confirm
    /// availability, decline, or request a swap. Set ONLY by the assigned
    /// volunteer (server-enforced); a coordinator reads it to know which shifts
    /// need reassigning. Default <see cref="ShiftDecisionStatus.None"/>.
    /// </summary>
    public ShiftDecisionStatus DecisionStatus { get; set; } = ShiftDecisionStatus.None;

    /// <summary>Optional free-text reason the volunteer gave when declining /
    /// requesting a swap (e.g. "clashes with my own talk"). For the coordinator.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>When the volunteer last set <see cref="DecisionStatus"/>, for audit.</summary>
    public DateTimeOffset? DecisionAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
