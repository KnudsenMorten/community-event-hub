using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Domain;

/// <summary>
/// The persisted per-edition kill switch for ONE customizable capability
/// (REQUIREMENTS §23). One row per (edition, feature key). When no row exists for
/// a feature the catalog default applies, so a fresh edition behaves exactly as
/// the catalog declares (advanced features OFF) without seeding every key.
///
/// This holds only the on/off state. The feature's metadata (name, group, tier,
/// default, dependencies) lives in the immutable
/// <c>CommunityHub.Core.Settings.FeatureCatalog</c>; per-feature endpoints/ids
/// stay in their own typed settings rows (e.g. <see cref="SoMeSettings"/>,
/// <see cref="SessionizeEndpointSetting"/>). No secrets are ever stored here.
/// </summary>
public class FeatureSetting
{
    public int Id { get; set; }

    /// <summary>The edition this kill switch belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The catalog feature key (kebab-case, e.g. <c>backstage-sync</c>). Matches a
    /// <c>FeatureCatalog</c> descriptor key; a stale key (a feature removed from
    /// the catalog) is simply ignored by the gate.
    /// </summary>
    public string FeatureKey { get; set; } = string.Empty;

    /// <summary>Whether this capability is enabled for this edition.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The RELEASE RING this feature is currently released to for this edition
    /// (REQUIREMENTS §23 progressive rollout). A feature is active for a resource
    /// iff it is <see cref="Enabled"/> (not killed) AND the resource's effective
    /// ring is ≤ this ring. Lower released-ring = released to fewer (earlier)
    /// rings only.
    ///
    /// Defaults to <see cref="Ring.Broad"/> (general availability) so a feature
    /// with no explicit released-ring is visible to EVERYONE — today's behaviour
    /// is unchanged. When no <see cref="FeatureSetting"/> row exists the catalog
    /// descriptor's default released-ring applies.
    /// </summary>
    public Ring ReleasedToRing { get; set; } = Rings.Default;

    /// <summary>
    /// The per-feature ring OVERRIDE (REQUIREMENTS §23a group-ring model). When set,
    /// this feature is released to exactly this ring regardless of its group's ring —
    /// the "special ring" exception. When <c>null</c> the feature INHERITS its
    /// (effective) group's ring (or its catalog default if the group ring is unset).
    /// The legacy <see cref="ReleasedToRing"/> column above is retained for back-compat
    /// and migrated into this override on upgrade; the gate now reads this.
    /// </summary>
    public Ring? ReleasedToRingOverride { get; set; }

    /// <summary>
    /// The per-edition GROUP override (REQUIREMENTS §23a "graduate by re-homing").
    /// When set, this feature is treated as belonging to this group instead of its
    /// catalog home group — so it adopts the destination group's lifecycle ring.
    /// <c>null</c> = use the catalog home group. This is how a feature graduates out
    /// of Incubation into a real group without a code change.
    /// </summary>
    public FeatureGroup? GroupOverride { get; set; }

    /// <summary>
    /// The DATE this feature auto-activates for the BROAD rings (ring 2 + ring 3) — the
    /// §38e date gate (operator 2026-06-25: "auto-enabled by a DATE: 1 Dec 2026 for ring
    /// 2+3; ring 0/1 is NOT date-limited so we can test now"). When set, a ring-2/ring-3
    /// participant is only inside the feature BEFORE this date if... no — they are only
    /// inside it ONCE <c>now &gt;= ActiveFromForBroadRings</c>. Ring 0 and ring 1 ignore
    /// this entirely (always active once the kill switch + released-ring gate pass), so a
    /// ring-1 tester exercises the feature immediately. <c>null</c> = no date gate (broad
    /// rings follow only the normal kill-switch + released-ring rules). Only consulted by
    /// features that opt into a date gate (today: <c>session-change-alerts</c>).
    /// </summary>
    public DateTimeOffset? ActiveFromForBroadRings { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last changed this switch (audit; nullable).</summary>
    public string? LastUpdatedByEmail { get; set; }
}
