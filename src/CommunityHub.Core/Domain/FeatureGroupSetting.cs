using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Domain;

/// <summary>
/// The persisted per-edition GROUP lifecycle ring (REQUIREMENTS §23a). One row per
/// (edition, <see cref="FeatureGroup"/>). It is the PRIMARY control in the group-ring
/// model: every feature in the group whose <see cref="FeatureSetting.ReleasedToRingOverride"/>
/// is null inherits this ring. Promoting a whole group through the cycle (Ring0 →
/// Ring1 → Ring2 → Broad) is one change here.
///
/// When no row exists for a group the gate falls back to the FEATURE's own catalog
/// default (so behaviour is unchanged until an organizer sets a group ring); the
/// GUI shows <see cref="FeatureCatalog.GroupDefaultRing"/> as the starting point.
/// Holds no secrets.
/// </summary>
public class FeatureGroupSetting
{
    public int Id { get; set; }

    /// <summary>The edition this group ring belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The feature group this lifecycle ring applies to.</summary>
    public FeatureGroup Group { get; set; }

    /// <summary>
    /// The ring this group is currently released to. A feature in this group with no
    /// per-feature override is active for a resource iff the resource's effective ring
    /// is ≤ this ring. Defaults to <see cref="Ring.Broad"/>.
    /// </summary>
    public Ring ReleasedToRing { get; set; } = Rings.Default;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last changed this group ring (audit; nullable).</summary>
    public string? LastUpdatedByEmail { get; set; }
}
