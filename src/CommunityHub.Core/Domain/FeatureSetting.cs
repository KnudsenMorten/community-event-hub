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
    /// RETIRED (§234, 2026-07-07): the §38e broad-rings "1 Dec 2026 auto-enable" DATE
    /// gate. It became dead code with §59 — session-change emails are no longer sent
    /// inline at detection time; they send only when an operator APPROVES the queued
    /// delta (and the sender still ring-gates each recipient), so nothing reads this
    /// value anymore. The COLUMN is retained solely to avoid a schema migration; do
    /// not wire new behavior to it.
    /// </summary>
    public DateTimeOffset? ActiveFromForBroadRings { get; set; }

    /// <summary>
    /// §742 — WHERE this feature's ops notice is e-mailed, when it sends one. <c>null</c> ⇒ the
    /// built-in <c>EngineAlertSender.Recipient</c> (<c>mok@expertslive.dk</c>), so behaviour is
    /// unchanged until an organizer sets it.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-31: <i>"it must go to mok@expertslive.dk and i need to be able to
    /// control where it goes and state in settings page"</i>, scoped by him to the Get Started
    /// 100%-completion notice.</para>
    ///
    /// <para>🔑 Lives on the FEATURE row because that is the exact grain: the on/off and the
    /// destination are two halves of one control, per edition. A switch that says it sends but not
    /// WHERE is the §694/§698 complaint — a control that does not state what it governs.</para>
    ///
    /// <para>🔒 Only meaningful where the descriptor sets <c>SendsOpsNotice</c>; the Settings page
    /// renders the box on those rows ONLY, so the field can never appear where it governs nothing.</para>
    /// </remarks>
    public string? NotificationRecipientEmail { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last changed this switch (audit; nullable).</summary>
    public string? LastUpdatedByEmail { get; set; }
}
