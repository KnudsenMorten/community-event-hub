namespace CommunityHub.Core.Domain;

/// <summary>
/// What an <see cref="AllocationScenario"/> reshuffles (REQUIREMENTS §129). The
/// scenario is the GENERIC "stage → simulate → commit" wrapper that generalizes the
/// proven per-organizer volunteer task-allocation draft → commit and the hotel/room
/// placement, so a big reshuffle is staged + proven safe before anything touches the
/// live tables.
/// </summary>
public enum AllocationScenarioKind
{
    /// <summary>People → <see cref="VolunteerTask"/> coverage (the move's
    /// <see cref="AllocationScenarioMove.TargetRole"/> says volunteer vs organizer queue).</summary>
    VolunteerAllocation = 0,

    /// <summary>People → <see cref="Hotel"/> placement (room-block math). PHASE 2 —
    /// the model carries <see cref="AllocationScenarioMove.HotelId"/> so this is additive.</summary>
    HotelAllocation = 1,

    /// <summary>A volunteer/organizer re-plan auto-seeded from a DROPPED person's now-
    /// uncovered tasks (REQUIREMENTS §129.2). Simulated + committed exactly like a
    /// <see cref="VolunteerAllocation"/>; the distinct kind records WHY it exists.</summary>
    DropOutBackfill = 2,
}

/// <summary>Lifecycle of a scenario (REQUIREMENTS §129). Committed == the persisted
/// truth (there is no separate save step); Discarded never touched the live tables.</summary>
public enum AllocationScenarioStatus
{
    Draft = 0,
    Committed = 1,
    Discarded = 2,
}

/// <summary>One staged move's direction: ADD an allocation or REMOVE an existing one.</summary>
public enum AllocationMoveOp
{
    Assign = 0,
    Unassign = 1,
}

/// <summary>
/// A staged bulk-allocation plan (REQUIREMENTS §129): an organizer builds it (from a
/// proposal or by hand), SIMULATES it read-only (coverage gaps / conflicts / capacity
/// breaches / before-after diff / "what breaks"), then COMMITS it atomically (applying
/// every staged move to the live assignment/hotel tables in one transaction + an audit
/// row) or DISCARDS it (no effect). It sits ON TOP of the existing
/// <see cref="VolunteerTask"/>/<see cref="VolunteerTaskAssignment"/>, <see cref="Hotel"/>/
/// <see cref="HotelBooking"/> and <see cref="TaskAllocationDraft"/> models — no parallel
/// allocation stack — and reuses the red/green gap + room-block authorities.
///
/// <para>Edition-scoped + owned by one organizer so simultaneous planning sessions stay
/// isolated. Distinct from <see cref="TaskAllocationDraft"/> (the live per-organizer queue
/// that drives the inline coverage simulation): a scenario is a NAMED, persisted, auditable
/// reshuffle a planner can stage, leave, return to, and commit/discard as a unit.</para>
/// </summary>
public class AllocationScenario
{
    public int Id { get; set; }

    // --- Edition scope + owner ---------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The organizer who owns this planning scenario.</summary>
    public int OwnerParticipantId { get; set; }
    public Participant OwnerParticipant { get; set; } = null!;

    // --- What + state ------------------------------------------------------
    public AllocationScenarioKind Kind { get; set; } = AllocationScenarioKind.VolunteerAllocation;
    public AllocationScenarioStatus Status { get; set; } = AllocationScenarioStatus.Draft;

    /// <summary>A short human label ("Backfill: Friday registration desk", etc.).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional free-text note (e.g. "auto-seeded after Alex dropped out").</summary>
    public string? Notes { get; set; }

    /// <summary>For <see cref="AllocationScenarioKind.DropOutBackfill"/>: the participant whose
    /// drop seeded this scenario (the gaps to refill came from their cancelled allocations).
    /// Null for hand-built scenarios.</summary>
    public int? DroppedParticipantId { get; set; }
    public Participant? DroppedParticipant { get; set; }

    // --- Audit trail (REQUIREMENTS §129.4) ---------------------------------
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CommittedAt { get; set; }
    public string? CommittedByEmail { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }

    public ICollection<AllocationScenarioMove> Moves { get; set; } = new List<AllocationScenarioMove>();
}

/// <summary>
/// One staged move inside an <see cref="AllocationScenario"/> — a single person ↔ target
/// change that is applied (or removed) only on commit. The target is a task
/// (<see cref="TaskId"/>) for volunteer/organizer kinds or a hotel (<see cref="HotelId"/>)
/// for the hotel kind; exactly one is set per move per the scenario's
/// <see cref="AllocationScenario.Kind"/>. Unique per (ScenarioId, ParticipantId, TaskId,
/// HotelId) so the same move is staged at most once.
/// </summary>
public class AllocationScenarioMove
{
    public int Id { get; set; }

    public int ScenarioId { get; set; }
    public AllocationScenario Scenario { get; set; } = null!;

    /// <summary>Denormalized edition id (matches the parent) for simple edition-scoped queries.</summary>
    public int EventId { get; set; }

    /// <summary>The person being (un)allocated.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>ADD an allocation (the default) or REMOVE an existing live one.</summary>
    public AllocationMoveOp Op { get; set; } = AllocationMoveOp.Assign;

    /// <summary>Task target (volunteer/organizer/drop-out kinds). Null for hotel moves.</summary>
    public int? TaskId { get; set; }
    public VolunteerTask? Task { get; set; }

    /// <summary>Hotel target (hotel kind, phase 2). Null for task moves.</summary>
    public int? HotelId { get; set; }
    public Hotel? Hotel { get; set; }

    /// <summary>For task moves: which coverage queue this counts toward — volunteer vs
    /// organizer (mirrors <see cref="TaskAllocationDraft.TargetRole"/>). Ignored for hotel.</summary>
    public ParticipantRole TargetRole { get; set; } = ParticipantRole.Volunteer;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
