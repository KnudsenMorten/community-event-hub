namespace CommunityHub.Core.Domain;

/// <summary>The lifecycle of a <see cref="VolunteerHelpRequest"/>.</summary>
public enum VolunteerHelpStatus
{
    /// <summary>Raised by the volunteer, not yet handled.</summary>
    Open = 0,
    /// <summary>The supervisor has replied.</summary>
    Answered = 1,
    /// <summary>The matter is closed (resolved).</summary>
    Resolved = 2,
}

/// <summary>
/// The HELP CHANNEL: a volunteer working a task can ask their category's
/// supervisor for help. The request names the <see cref="TaskId"/> the volunteer
/// is on (its category is resolved for the supervisor) and carries a free-text
/// <see cref="Message"/>. The supervisor (and, for oversight, the category's
/// organizer lead) sees it; the supervisor answers, moving it Open → Answered,
/// and either party can mark it Resolved. Edition-scoped via
/// <see cref="EventId"/>; <see cref="CategoryId"/> is denormalized from the task
/// so a supervisor's "help for my category" query needs no join.
/// </summary>
public class VolunteerHelpRequest
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Where the help is needed -------------------------------------------
    /// <summary>The task the volunteer is working when they ask for help.</summary>
    public int TaskId { get; set; }
    public VolunteerTask Task { get; set; } = null!;

    /// <summary>The owning category (denormalized from the task), so the
    /// supervisor's category-scoped help inbox is a single-column filter.</summary>
    public int CategoryId { get; set; }
    public VolunteerCategory Category { get; set; } = null!;

    // --- Who raised it ------------------------------------------------------
    public int RequestedByParticipantId { get; set; }
    public Participant RequestedByParticipant { get; set; } = null!;

    public string Message { get; set; } = string.Empty;

    // --- Resolution ---------------------------------------------------------
    public VolunteerHelpStatus Status { get; set; } = VolunteerHelpStatus.Open;

    /// <summary>The supervisor's reply (set when the status moves to Answered/Resolved).</summary>
    public string? Response { get; set; }

    /// <summary>Email of whoever answered/resolved (supervisor or lead), for audit.</summary>
    public string? RespondedByEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
