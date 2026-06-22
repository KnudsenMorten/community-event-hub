namespace CommunityHub.Core.Domain;

/// <summary>
/// A per-participant manual override of one <see cref="OrderItem"/>'s
/// entitlement, layered ON TOP of the computed default set
/// (<see cref="Entitlements.OrderEntitlements"/>). An organizer uses these to
/// handle the exceptions the default rules cannot express:
/// <list type="bullet">
///   <item><see cref="Include"/> = <c>true</c> force-INCLUDES the item (give it
///   to this person even though the rules would not).</item>
///   <item><see cref="Include"/> = <c>false</c> force-EXCLUDES the item (do not
///   give it even though the rules would).</item>
/// </list>
/// One row per (EventId, ParticipantId, Item); upserted on edit.
/// </summary>
public class ParticipantOrderOverride
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>The order item this override applies to.</summary>
    public OrderItem Item { get; set; }

    /// <summary>True force-includes the item; false force-excludes it.</summary>
    public bool Include { get; set; }

    /// <summary>Optional free-text reason an organizer left for the override.</summary>
    public string? Reason { get; set; }

    /// <summary>The organizer email that last set this override.</summary>
    public string? SetByEmail { get; set; }

    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;
}
