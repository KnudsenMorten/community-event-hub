namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §1350 — a PHYSICAL room: where a tablet actually stands. The schedule's rooms
/// (<see cref="EvaluationRoom"/>) are VIRTUAL — one per room name the sessions carry — and the venue
/// reuses one physical space under several of them.
/// </summary>
/// <remarks>
/// <para>Operator 2026-10-19: *"define rooms and link virtual rooms linked to sessions and link them to
/// physical rooms where the device is. fx. room 5-6-7 on preday will be split into 5 and 6-7 on main
/// day"*. So the mapping is many-to-many (<see cref="EvaluationRoomPlacement"/>): physical "Room 16"
/// covers "Room-16-…-MC" (pre-day) and "Room-16-…-Max 44" (main day); the combined pre-day room
/// "Room-5-7-…-MC" sits under BOTH "Room 5" and "Room 6-7".</para>
///
/// <para>🔑 The chain is <b>device → physical room → schedule rooms → sessions</b>, and the press time
/// picks the session (<see cref="CommunityHub.Core.Evaluation.EvaluationRoomMap"/>). Nothing here is
/// derived from names: a person decides what is the same space.</para>
/// </remarks>
public class EvaluationPhysicalRoom
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>What an organizer calls the space ("Room 16", "A1 Keynote"). Unique per event.</summary>
    public string Name { get; set; } = string.Empty;

    public List<EvaluationRoomPlacement> Placements { get; set; } = new();

    /// <summary>
    /// §1354 — the QR capture policy for every session held in this room, overriding the edition's default
    /// QR policy (operator chose per-room for QR, 2026-10-19). Null = default.
    /// </summary>
    public CommunityHub.Core.Evaluation.FeedbackCapturePolicy? QrCapturePolicy { get; set; }

    /// <summary>§1355 — the QR page's rating-button symbols for sessions held here (null = default).</summary>
    public CommunityHub.Core.Evaluation.RatingSymbols? QrRatingSymbols { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>§1350 — "schedule room X is held in physical room Y". Many-to-many.</summary>
public class EvaluationRoomPlacement
{
    public int PhysicalRoomId { get; set; }
    public EvaluationPhysicalRoom PhysicalRoom { get; set; } = null!;

    public int EvaluationRoomId { get; set; }
    public EvaluationRoom EvaluationRoom { get; set; } = null!;
}
