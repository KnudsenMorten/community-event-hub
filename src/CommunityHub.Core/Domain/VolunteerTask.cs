namespace CommunityHub.Core.Domain;

/// <summary>
/// The work-state of a <see cref="VolunteerTask"/> in the volunteer structure.
/// Deliberately distinct from <see cref="TaskState"/> (the personal/sponsor task
/// list) so the two task models can evolve independently.
/// </summary>
public enum VolunteerTaskStatus
{
    /// <summary>Not started — needs a volunteer / awaiting work.</summary>
    Open = 0,
    /// <summary>Work is under way.</summary>
    InProgress = 1,
    /// <summary>Finished.</summary>
    Done = 2,
    /// <summary>No longer needed (kept for the record, not deleted).</summary>
    Cancelled = 3,
}

/// <summary>
/// How important a <see cref="VolunteerTask"/> is. Mirrors the two bands used in
/// the real ELDK plan ("Need-to-have" / "Nice-to-have") plus an explicit
/// <see cref="Unspecified"/> default for tasks created before a band is chosen.
/// </summary>
public enum VolunteerTaskCriticality
{
    /// <summary>No criticality recorded yet.</summary>
    Unspecified = 0,
    /// <summary>Nice-to-have — desirable but the event survives without it.</summary>
    NiceToHave = 1,
    /// <summary>Need-to-have — the event is impacted if this is not done.</summary>
    NeedToHave = 2,
}

/// <summary>
/// The LOWEST level of the volunteer work structure: a concrete piece of work
/// within one <see cref="VolunteerSubcategory"/> (e.g. "Staff badge desk 08:00–10:00").
/// Volunteers are linked to tasks many-to-many via
/// <see cref="VolunteerTaskAssignment"/>; a task rolls up
/// Task → Subcategory → Category. Carries <see cref="EventId"/> denormalized from
/// its subcategory so a task list can scope by edition without a two-hop join.
/// </summary>
public class VolunteerTask
{
    public int Id { get; set; }

    // --- Edition scope (denormalized up the tree) ---------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Parent -------------------------------------------------------------
    public int SubcategoryId { get; set; }
    public VolunteerSubcategory Subcategory { get; set; } = null!;

    // --- Content ------------------------------------------------------------
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Optional hard deadline (date only — matches the rest of the hub).</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>
    /// Optional free-text shift / time window (e.g. "Day 1, 08:00–10:00"). Kept
    /// as text because the shift catalogue is config-driven elsewhere in the hub
    /// (VolunteerAvailability uses the same approach).
    /// </summary>
    public string? Shift { get; set; }

    public VolunteerTaskStatus Status { get; set; } = VolunteerTaskStatus.Open;

    // --- Buckets / plan fields (added 2026-06-15) ---------------------------
    /// <summary>
    /// Optional free-text END time of the work window (e.g. "12:00"). The plan
    /// CSV carries a "Time End" column; kept as text for the same reason as
    /// <see cref="Shift"/> (the shift catalogue is config-driven elsewhere).
    /// </summary>
    public string? TimeEnd { get; set; }

    /// <summary>How important this task is (Need-to-have / Nice-to-have).</summary>
    public VolunteerTaskCriticality Criticality { get; set; } = VolunteerTaskCriticality.Unspecified;

    /// <summary>The team responsible (e.g. "BeFree", "Photo", "BC-F&amp;B").
    /// In the plan this is the section band that also drives the Bucket grouping.</summary>
    public string? ResponsibleTeam { get; set; }

    /// <summary>
    /// The ELDK lead for THIS task — the go-to person who owns it and may mark it
    /// Completed. Free text (a name) because the lead is not necessarily a hub
    /// <see cref="Participant"/> (matches the plan's "ELDK Lead Task" column).
    /// </summary>
    public string? EldkLeadName { get; set; }

    /// <summary>How many people this task needs (the resource budget). Compared
    /// against the assigned count to drive the red/green gap indicator.</summary>
    public int ResourcesNeeded { get; set; }

    /// <summary>What must be true before the task can start (free text). May be
    /// AI-generated from the title via <c>ITaskGuidanceGenerator</c> when blank.</summary>
    public string? Prerequisites { get; set; }

    /// <summary>What "done" looks like — the expected outcome (free text). May be
    /// AI-generated from the title via <c>ITaskGuidanceGenerator</c> when blank.</summary>
    public string? Expectations { get; set; }

    /// <summary>Per-task instructions shown to the assigned volunteers.</summary>
    public string? Instructions { get; set; }

    /// <summary>Set when the task's ELDK lead (or an organizer) marks it Completed.
    /// Distinct from <see cref="Status"/> so "the lead signed it off" is auditable.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Email of whoever marked it Completed, for audit.</summary>
    public string? CompletedByEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    public ICollection<VolunteerTaskAssignment> Assignments { get; set; } = new List<VolunteerTaskAssignment>();
    public ICollection<VolunteerHelpRequest> HelpRequests { get; set; } = new List<VolunteerHelpRequest>();
    public ICollection<TaskAllocationDraft> AllocationDrafts { get; set; } = new List<TaskAllocationDraft>();

    /// <summary>
    /// The resource gap = needed minus assigned. Negative/zero = covered (green);
    /// positive = short by that many (red). Computed in code (LINQ-to-objects) off
    /// loaded <see cref="Assignments"/>; not mapped to the DB.
    /// </summary>
    public int ResourceShortfall(int assignedCount) => ResourcesNeeded - assignedCount;
}
