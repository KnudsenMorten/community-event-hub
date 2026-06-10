namespace CommunityHub.Core.Domain;

/// <summary>State of a task in the hub.</summary>
public enum TaskState
{
    Open = 0,
    InProgress = 1,
    Done = 2
}

/// <summary>
/// A task or deliverable shown on a participant's hub. Covers volunteer
/// shift-jobs, speaker deadlines, and sponsor deliverables alike - the
/// reminder engine (Stage 6) reads these. Scoped to an edition by EventId.
/// </summary>
public class ParticipantTask
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Assignment ---------------------------------------------------------
    /// <summary>
    /// The participant responsible. Null = unassigned (e.g. a sponsor-company
    /// task not yet given to a specific contact - see CONTEXT.md section 9).
    /// </summary>
    public int? AssignedParticipantId { get; set; }
    public Participant? AssignedParticipant { get; set; }

    // --- Task content -------------------------------------------------------
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Optional due date - drives deadline reminders.</summary>
    public DateOnly? DueDate { get; set; }

    public TaskState State { get; set; } = TaskState.Open;

    /// <summary>
    /// Mandatory vs optional. Mandatory tasks are part of the agreed
    /// deliverables (logo upload, booth layout, session description, etc.);
    /// optional ones are paid add-ons / nice-to-haves (attendee-bag insert,
    /// TV rental, app-game prize, etc.). UI surfaces an "Optional" badge
    /// when false; reminder cadence may differ in future. Default true so
    /// pre-existing tasks (which never had this flag) keep the safer
    /// "treat as deliverable" semantics.
    /// </summary>
    public bool IsMandatory { get; set; } = true;

    /// <summary>
    /// Optional source tag, e.g. which task-set generated it ("allSponsors",
    /// "boothPlatinum", a volunteer import). Useful for idempotent re-runs.
    /// </summary>
    public string? SourceKey { get; set; }

    /// <summary>
    /// For a sponsor task (one created from a WooCommerce order): the
    /// company id (the order's _cm_company_id). Lets a sponsor contact see
    /// and edit only their own company's tasks. Null for non-sponsor tasks.
    /// </summary>
    public string? SponsorCompanyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
