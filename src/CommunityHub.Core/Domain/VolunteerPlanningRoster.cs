namespace CommunityHub.Core.Domain;

/// <summary>
/// §1297 — one volunteer on the <b>worker roster</b>: the curated pool the planner
/// auto-assigns from.
/// </summary>
/// <remarks>
/// <para>🔑 <b>There are two populations, and treating them as one is what made the
/// coverage-versus-fairness argument unresolvable.</b> Operator 2026-09-22: <i>"a handfull of
/// volunteers can work full day (not interested in conference) and then other group wants to
/// join the conference. we will only scope the first group."</i></para>
/// <list type="bullet">
/// <item><b>Workers</b> — here to work, not attending, can be loaded all day. This roster.</item>
/// <item><b>Attendees</b> — helping around sessions they want to be at. <b>Hand-placed</b>
/// through the manual assignment path, which the interval model makes safe.</item>
/// </list>
/// <para>Scoping the planner to the workers dissolves the fairness objection rather than
/// answering it: loading somebody heavily is only unfair if they wanted to be somewhere else,
/// and this group did not. So coverage-first, with hours-allocated as the tie-break, applied to
/// a pool that opted into exactly that.</para>
///
/// <para>🔒 <b>Membership IS the flag, and it is EXPLICIT — never derived.</b> "Answered Full
/// day on every day" looks like the same set, and it is a guess; a guess about who may be
/// worked flat out is the wrong thing to infer. Presence of this row = in scope, absence = not
/// auto-planned.</para>
///
/// <para>⚠️ <b>What membership does NOT do.</b> It does not promote, override or reinterpret a
/// member's stated availability — the matcher reads their answers like everyone else's.
/// Operator 2026-09-22: <i>"it will newer be an issues, as these people wil add full day by
/// default. so dont overengineer it."</i> The absence of an override rule is the decision; a
/// later reader finding none should leave it absent rather than "fix" it.</para>
/// </remarks>
public class VolunteerPlanningRosterEntry
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The volunteer in scope for auto-planning.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Email of the organizer who put them on the roster, for audit.</summary>
    public string? AddedByEmail { get; set; }

    /// <summary>Optional free-text reason ("works Mon–Wed, not attending").</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// §1297 — a volunteer this Bucket (<see cref="VolunteerCategory"/>) PREFERS: Leth's
/// "preferred volunteers per category are ranked higher".
/// </summary>
/// <remarks>
/// <para>🔑 <b>A preference, not a reservation.</b> It moves someone to the front of the
/// candidate order for this bucket's tasks; it never bypasses the interval checks, the
/// availability answer, the ring gate or the roster scope. A preferred volunteer who is not
/// free at that hour is still not proposed — otherwise the planner would produce assignments
/// that read as decided and are impossible.</para>
///
/// <para><see cref="Rank"/> orders several preferred people for one bucket, lowest first.
/// Equal ranks fall through to the ordinary fairness tie-break, so leaving every rank at 0 is
/// a valid way to say "these people, no particular order among them".</para>
/// </remarks>
public class VolunteerCategoryPreference
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The Bucket (category) that prefers this volunteer.</summary>
    public int CategoryId { get; set; }
    public VolunteerCategory Category { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Order among this bucket's preferred volunteers — lowest first, ties allowed.</summary>
    public int Rank { get; set; }

    /// <summary>Email of the organizer who recorded the preference, for audit.</summary>
    public string? AddedByEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
