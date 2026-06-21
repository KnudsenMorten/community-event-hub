namespace CommunityHub.Core.Domain;

/// <summary>
/// The three editable configuration SECTIONS an organizer can override per
/// edition (Phase 1 of the admin-editable-config epic). Each value names one of
/// the shipped JSON config files whose defaults travel with the code release:
/// <c>config/event.&lt;edition&gt;.json</c>, <c>config/sponsor.&lt;edition&gt;.json</c>,
/// and <c>config/integrations.&lt;edition&gt;.json</c>. A persisted
/// <see cref="ConfigOverride"/> row carries a partial JSON FRAGMENT for one of
/// these sections that is deep-merged ON TOP of the shipped default at runtime.
/// </summary>
public enum ConfigSection
{
    /// <summary>Overrides for <c>config/event.&lt;edition&gt;.json</c>.</summary>
    Event = 0,

    /// <summary>Overrides for <c>config/sponsor.&lt;edition&gt;.json</c>.</summary>
    Sponsor = 1,

    /// <summary>Overrides for <c>config/integrations.&lt;edition&gt;.json</c>.</summary>
    Integrations = 2,
}

/// <summary>
/// A per-edition CONFIG OVERRIDE: a partial JSON fragment for one
/// <see cref="ConfigSection"/> that is deep-merged on top of the shipped JSON
/// default at runtime (HYBRID config model). The shipped JSON file remains the
/// template/default that travels with each code release and guarantees a fresh
/// edition/tenant bootstraps with nothing missing; this row holds only the bits
/// an organizer has changed for THIS edition.
///
/// One row per (edition, section) — upserted on save. When no row exists for a
/// section the effective config is byte-for-byte the shipped default, so a fresh
/// edition behaves exactly as today without seeding anything.
///
/// SECRETS ARE NEVER STORED HERE. <see cref="OverrideJson"/> may carry only
/// non-secret settings and, for secret-bearing fields, the Key Vault secret
/// NAME (never the value) — mirroring how the shipped JSON references secrets by
/// name. The existing Key Vault handling is unchanged.
/// </summary>
public class ConfigOverride
{
    public int Id { get; set; }

    /// <summary>The edition this override belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Which shipped config file this fragment overrides.</summary>
    public ConfigSection Section { get; set; }

    /// <summary>
    /// The partial JSON fragment for this section, deep-merged on top of the
    /// shipped default at runtime (override wins per key; an array in the
    /// override replaces the whole array). A blank/invalid fragment is ignored
    /// at merge time and the shipped default is used unchanged (fail-safe).
    /// Never contains secret VALUES — only settings and Key Vault secret names.
    /// </summary>
    public string OverrideJson { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last changed this override (audit; nullable).</summary>
    public string? UpdatedByEmail { get; set; }
}
