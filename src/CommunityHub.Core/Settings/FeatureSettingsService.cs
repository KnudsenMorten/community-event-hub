using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Settings;

/// <summary>
/// The effective on/off state of one feature for one edition, joined with its
/// catalog metadata — the row the settings GUI renders.
/// </summary>
/// <param name="Descriptor">The immutable catalog metadata.</param>
/// <param name="Enabled">The effective state (persisted switch ?? catalog default).</param>
/// <param name="IsPersisted">True if an organizer has explicitly set this (a row exists).</param>
/// <param name="ReleasedToRing">
/// The EFFECTIVE released-to ring — exactly what the gate resolves
/// (<c>override ?? groupRing ?? catalogDefault</c>). A feature is active for a
/// resource only when the resource's effective ring ≤ this (REQUIREMENTS §23a).
/// </param>
/// <param name="OverrideRing">
/// The per-feature ring OVERRIDE if set (the "special ring"); <c>null</c> when the
/// feature INHERITS its (effective) group's ring.
/// </param>
/// <param name="EffectiveGroup">
/// The group this feature is treated as in for this edition = per-edition group
/// override ?? catalog home group (REQUIREMENTS §23a graduate-by-re-homing).
/// </param>
/// <param name="GroupRing">The effective group's ring (for display).</param>
public sealed record FeatureState(
    FeatureDescriptor Descriptor, bool Enabled, bool IsPersisted, Ring ReleasedToRing,
    Ring? OverrideRing, FeatureGroup EffectiveGroup, Ring GroupRing,
    // §514 — the outbound-email master ring, so the page can tell when it overrules this one.
    Ring? TransportRing = null,
    // §742 — the operator's stored "send this feature's notice to" address, or null when unset
    // (⇒ the built-in ops mailbox). Only meaningful when Descriptor.SendsOpsNotice.
    string? NotificationRecipientEmail = null)
{
    public string Key => Descriptor.Key;
    public bool IsAdvanced => Descriptor.IsAdvanced;

    /// <summary>True when the feature carries its own ring override (vs inheriting the group ring).</summary>
    public bool IsRingOverridden => OverrideRing.HasValue;

    /// <summary>True when the feature has been re-homed out of its catalog group (graduated/incubating).</summary>
    public bool IsReHomed => EffectiveGroup != Descriptor.Group;

    /// <summary>
    /// §514 — TRUE when this feature's mail is released BROADER than the outbound-email master
    /// switch, so the transport clamps it and the ring shown here is NOT the audience.
    ///
    /// <para><c>BrevoEmailSender</c> resolves the audience as
    /// <c>MIN(ring(outbound-email), ring(feature))</c> and, by its own comment, "never LOOSENS".
    /// Releasing a feature to Broad while the master sits at Ring 2 therefore changes nothing —
    /// the operator's example was *Speaker profile change alerts* at Ring 3 (broad) whose mail
    /// "will not arrive". Two screens, each truthful alone, together stating something false.</para>
    ///
    /// <para>The condition MIRRORS the transport's own (<c>IsRingScoped</c>) rather than the
    /// narrower e-mail grouping, so it cannot claim a cap the sender would not apply, nor miss one
    /// it would.</para>
    /// </summary>
    public bool IsCappedByTransport =>
        Descriptor.IsRingScoped
        && Key != FeatureCatalog.OutboundEmailKey
        && TransportRing is Ring cap
        && (int)ReleasedToRing > (int)cap;

    /// <summary>§514 — the ring really in force for this feature's mail, once the cap applies.</summary>
    public Ring EffectiveMailRing =>
        IsCappedByTransport ? TransportRing!.Value : ReleasedToRing;
}

/// <summary>The effective lifecycle ring of one feature GROUP for an edition (REQUIREMENTS §23a).</summary>
/// <param name="Group">The feature group.</param>
/// <param name="Ring">The effective group ring (persisted row ?? <see cref="FeatureCatalog.GroupDefaultRing"/>).</param>
/// <param name="IsPersisted">True if an organizer has explicitly set this group's ring.</param>
public sealed record GroupRingState(FeatureGroup Group, Ring Ring, bool IsPersisted);

/// <summary>
/// Loads + saves the per-edition feature kill switches for the organizer settings
/// GUI (REQUIREMENTS §23). Pure + EF-backed (constructor-injected context +
/// <see cref="TimeProvider"/>). The catalog is the source of truth for WHICH
/// features exist; this service overlays the persisted per-edition state.
/// </summary>
public sealed class FeatureSettingsService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public FeatureSettingsService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The effective state of EVERY catalog feature for an edition, in catalog
    /// (GUI) order. Advanced features with no persisted row fall back to the
    /// catalog default; core features are reported enabled (they are never gated).
    /// </summary>
    public async Task<IReadOnlyList<FeatureState>> GetAllAsync(
        int eventId, CancellationToken ct = default)
    {
        var persisted = await _db.FeatureSettings
            .Where(f => f.EventId == eventId)
            .ToDictionaryAsync(
                f => f.FeatureKey,
                // §742 — NotificationRecipientEmail joins the projection so the page can STATE
                // where each notice goes without a second query per row.
                f => new { f.Enabled, f.ReleasedToRingOverride, f.GroupOverride, f.NotificationRecipientEmail }, ct);

        var groupRings = await GetGroupRingMapAsync(eventId, ct);

        // §514 — the outbound-email master ring, resolved EXACTLY as the transport resolves it, so
        // a feature released above it can be shown as capped rather than silently clamped at send
        // time. Local function because the same resolution is needed twice (master + each feature).
        Ring ResolveRing(FeatureDescriptor d)
        {
            var has = persisted.TryGetValue(d.Key, out var row);
            var effGroup = (has ? row!.GroupOverride : null) ?? d.Group;
            return (has ? row!.ReleasedToRingOverride : null)
                ?? (groupRings.TryGetValue(effGroup, out var gr) ? gr : (Ring?)null)
                ?? d.DefaultReleasedToRing;
        }

        var master = FeatureCatalog.Find(FeatureCatalog.OutboundEmailKey);
        Ring? transportRing = master is null ? null : ResolveRing(master);

        return FeatureCatalog.All
            .Select(d =>
            {
                var has = persisted.TryGetValue(d.Key, out var row);
                var enabled = d.Tier == FeatureTier.Core
                    ? true
                    : has ? row!.Enabled : d.DefaultEnabled;

                var overrideRing = has ? row!.ReleasedToRingOverride : null;
                var effGroup = (has ? row!.GroupOverride : null) ?? d.Group;
                // The effective GROUP ring (persisted row ?? group default) — for display.
                var groupRing = groupRings.TryGetValue(effGroup, out var gr)
                    ? gr : FeatureCatalog.GroupDefaultRing(effGroup);
                // EFFECTIVE ring = exactly the gate's resolution:
                //   override ?? persisted-group-ring ?? catalog feature default.
                var effective = overrideRing
                    ?? (groupRings.TryGetValue(effGroup, out var grr) ? grr : (Ring?)null)
                    ?? d.DefaultReleasedToRing;

                return new FeatureState(d, enabled, IsPersisted: has,
                    ReleasedToRing: effective, OverrideRing: overrideRing,
                    EffectiveGroup: effGroup, GroupRing: groupRing,
                    TransportRing: transportRing,
                    // §742 — so the page can STATE where this feature's notice goes.
                    NotificationRecipientEmail: row?.NotificationRecipientEmail);
            })
            .ToList();
    }

    /// <summary>
    /// The effective states grouped by EFFECTIVE group (so a re-homed/graduated
    /// feature appears under its new group), groups in display order. Only groups
    /// that have features appear.
    /// </summary>
    public async Task<IReadOnlyList<IGrouping<FeatureGroup, FeatureState>>> GetByGroupAsync(
        int eventId, CancellationToken ct = default)
    {
        var all = await GetAllAsync(eventId, ct);
        return all.GroupBy(s => s.EffectiveGroup)
                  .OrderBy(g => (int)g.Key)
                  .ToList();
    }

    /// <summary>
    /// The effective lifecycle ring of every feature GROUP for an edition — the
    /// per-group control's state for the Rollout GUI (REQUIREMENTS §23a). Returns
    /// one entry per <see cref="FeatureGroup"/>: the persisted group ring if set,
    /// else <see cref="FeatureCatalog.GroupDefaultRing"/>.
    /// </summary>
    public async Task<IReadOnlyList<GroupRingState>> GetGroupRingsAsync(
        int eventId, CancellationToken ct = default)
    {
        var persisted = await _db.FeatureGroupSettings
            .Where(g => g.EventId == eventId)
            .ToDictionaryAsync(g => g.Group, g => g.ReleasedToRing, ct);

        return Enum.GetValues<FeatureGroup>()
            .OrderBy(g => (int)g)
            .Select(g => persisted.TryGetValue(g, out var r)
                ? new GroupRingState(g, r, IsPersisted: true)
                : new GroupRingState(g, FeatureCatalog.GroupDefaultRing(g), IsPersisted: false))
            .ToList();
    }

    private async Task<Dictionary<FeatureGroup, Ring>> GetGroupRingMapAsync(
        int eventId, CancellationToken ct) =>
        await _db.FeatureGroupSettings
            .Where(g => g.EventId == eventId)
            .ToDictionaryAsync(g => g.Group, g => g.ReleasedToRing, ct);

    /// <summary>
    /// Set one advanced feature's kill switch for an edition (upsert). Core
    /// features cannot be toggled (they are never gated) — a core key is ignored.
    /// Returns the effective state after the change.
    /// </summary>
    public async Task<bool> SetEnabledAsync(
        int eventId, string featureKey, bool enabled, string? byEmail,
        CancellationToken ct = default)
    {
        var descriptor = FeatureCatalog.Find(featureKey);
        if (descriptor is null || descriptor.Tier == FeatureTier.Core)
        {
            // Unknown or core key: nothing to persist, report the catalog default.
            return descriptor?.DefaultEnabled ?? true;
        }

        var row = await _db.FeatureSettings
            .FirstOrDefaultAsync(f => f.EventId == eventId && f.FeatureKey == featureKey, ct);
        if (row is null)
        {
            // A brand-new row seeds the released ring from the catalog default so
            // persisting the kill switch never silently narrows/widens the rollout.
            row = new FeatureSetting
            {
                EventId = eventId,
                FeatureKey = featureKey,
                ReleasedToRing = descriptor.DefaultReleasedToRing,
            };
            _db.FeatureSettings.Add(row);
        }

        row.Enabled = enabled;
        row.UpdatedAt = _clock.GetUtcNow();
        row.LastUpdatedByEmail = string.IsNullOrWhiteSpace(byEmail) ? null : byEmail.Trim();

        await _db.SaveChangesAsync(ct);
        return enabled;
    }

    /// <summary>
    /// Set one advanced feature's per-feature ring OVERRIDE for an edition (the
    /// "special ring" — REQUIREMENTS §23a). Upserts the feature row's
    /// <see cref="FeatureSetting.ReleasedToRingOverride"/>; the gate now reads this
    /// FIRST (before the group ring). Core/unknown keys are ignored. A new row seeds
    /// <see cref="FeatureSetting.Enabled"/> from the catalog default so setting the
    /// ring alone never flips the kill switch. Returns the set override ring.
    /// </summary>
    public async Task<Ring> SetReleasedRingAsync(
        int eventId, string featureKey, Ring releasedTo, string? byEmail,
        CancellationToken ct = default)
    {
        var descriptor = FeatureCatalog.Find(featureKey);
        if (descriptor is null || descriptor.Tier == FeatureTier.Core)
        {
            return descriptor?.DefaultReleasedToRing ?? Rings.Default;
        }

        var row = await UpsertRowAsync(eventId, featureKey, descriptor, ct);
        row.ReleasedToRingOverride = releasedTo;
        // Keep the legacy column tracking the override too (back-compat read path).
        row.ReleasedToRing = releasedTo;
        Stamp(row, byEmail);

        await _db.SaveChangesAsync(ct);
        return releasedTo;
    }

    /// <summary>
    /// Clear a feature's ring override so it INHERITS its (effective) group's ring
    /// again (REQUIREMENTS §23a "adopt the group's lifecycle"). No-op for core/unknown
    /// keys or a feature with no row. Returns the effective released ring afterwards.
    /// </summary>
    public async Task<Ring> ClearReleasedRingOverrideAsync(
        int eventId, string featureKey, string? byEmail, CancellationToken ct = default)
    {
        var descriptor = FeatureCatalog.Find(featureKey);
        if (descriptor is null || descriptor.Tier == FeatureTier.Core)
            return descriptor?.DefaultReleasedToRing ?? Rings.Default;

        var row = await _db.FeatureSettings
            .FirstOrDefaultAsync(f => f.EventId == eventId && f.FeatureKey == featureKey, ct);
        if (row is not null)
        {
            row.ReleasedToRingOverride = null;
            Stamp(row, byEmail);
            await _db.SaveChangesAsync(ct);
        }

        var all = await GetAllAsync(eventId, ct);
        return all.First(s => s.Key == featureKey).ReleasedToRing;
    }

    /// <summary>
    /// Set one feature GROUP's lifecycle ring for an edition (upsert) — the PRIMARY
    /// rollout control (REQUIREMENTS §23a). Every feature in this (effective) group
    /// without its own override inherits this ring. Returns the set ring.
    /// </summary>
    public async Task<Ring> SetGroupRingAsync(
        int eventId, FeatureGroup group, Ring releasedTo, string? byEmail,
        CancellationToken ct = default)
    {
        var row = await _db.FeatureGroupSettings
            .FirstOrDefaultAsync(g => g.EventId == eventId && g.Group == group, ct);
        if (row is null)
        {
            row = new FeatureGroupSetting { EventId = eventId, Group = group };
            _db.FeatureGroupSettings.Add(row);
        }
        row.ReleasedToRing = releasedTo;
        row.UpdatedAt = _clock.GetUtcNow();
        row.LastUpdatedByEmail = string.IsNullOrWhiteSpace(byEmail) ? null : byEmail.Trim();

        await _db.SaveChangesAsync(ct);
        return releasedTo;
    }

    /// <summary>
    /// RE-HOME / graduate a feature into a different group for an edition
    /// (REQUIREMENTS §23a). Sets the feature row's
    /// <see cref="FeatureSetting.GroupOverride"/> so it adopts the destination
    /// group's lifecycle ring; pass <paramref name="group"/> = the feature's catalog
    /// home group to clear the override. Core/unknown keys are ignored.
    /// </summary>
    public async Task SetFeatureGroupAsync(
        int eventId, string featureKey, FeatureGroup group, string? byEmail,
        CancellationToken ct = default)
    {
        var descriptor = FeatureCatalog.Find(featureKey);
        if (descriptor is null || descriptor.Tier == FeatureTier.Core) return;

        var row = await UpsertRowAsync(eventId, featureKey, descriptor, ct);
        // Re-homing to the catalog home group = no override (clears it).
        row.GroupOverride = group == descriptor.Group ? null : group;
        Stamp(row, byEmail);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §742 — set WHERE this feature's ops notice is e-mailed. Blank/null CLEARS it, restoring the
    /// built-in ops mailbox. Returns false when the feature does not send a notice at all.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-31: <i>"i need to be able to control where it goes and state in
    /// settings page"</i>, scoped to the Get Started 100% notice.</para>
    ///
    /// <para>🔒 Refusing a feature whose descriptor has no <c>SendsOpsNotice</c> is the point: an
    /// address stored against a switch that mails nobody would be a setting that governs nothing —
    /// the §326bx defect this page has twice had removed. The caller reports the refusal rather
    /// than silently accepting a value that would never be read.</para>
    ///
    /// <para>The address is trimmed but deliberately NOT otherwise validated here: the page does
    /// that (an <c>input type="email"</c>), and a service that silently dropped a malformed value
    /// would leave him looking at a box that appears to have saved nothing.</para>
    /// </remarks>
    public async Task<bool> SetNotificationRecipientAsync(
        int eventId, string featureKey, string? email, string? byEmail,
        CancellationToken ct = default)
    {
        var descriptor = FeatureCatalog.Find(featureKey);
        if (descriptor is null || !descriptor.SendsOpsNotice) return false;

        var row = await UpsertRowAsync(eventId, featureKey, descriptor, ct);
        row.NotificationRecipientEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        Stamp(row, byEmail);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// PAUSE or RESUME all background jobs for an edition — the Organizer Settings
    /// master switch (operator 2026-06-23). Upserts the reserved
    /// <see cref="FeatureGateService.JobsPausedKey"/> row (<c>Enabled == true ⇒
    /// paused</c>). It is NOT a catalog feature, so it intentionally bypasses the
    /// catalog validation in <see cref="SetEnabledAsync"/>. Returns the new state.
    /// </summary>
    public async Task<bool> SetJobsPausedAsync(
        int eventId, bool paused, string? byEmail, CancellationToken ct = default)
    {
        var row = await _db.FeatureSettings.FirstOrDefaultAsync(
            f => f.EventId == eventId && f.FeatureKey == FeatureGateService.JobsPausedKey, ct);
        if (row is null)
        {
            row = new FeatureSetting
            {
                EventId = eventId,
                FeatureKey = FeatureGateService.JobsPausedKey,
                ReleasedToRing = Rings.Default,
            };
            _db.FeatureSettings.Add(row);
        }

        row.Enabled = paused;
        Stamp(row, byEmail);
        await _db.SaveChangesAsync(ct);
        return paused;
    }

    /// <summary>
    /// §340-H — set or CLEAR the organizer's per-edition override for outbound writes to
    /// third-party systems (operator 2026-07-26: "i can as organizer control this on the
    /// settings page"). Reserved non-catalog key, same shape as
    /// <see cref="SetJobsPausedAsync"/>.
    ///
    /// <para><b>Three states, and the third is the important one.</b>
    /// <paramref name="allow"/> <c>true</c> ⇒ force writes ON for this edition;
    /// <c>false</c> ⇒ force them OFF; <b><c>null</c> ⇒ DELETE the row and inherit the
    /// ENVIRONMENT default</b> (dev false / prod true). Without the null case an organizer
    /// who toggled once could never get back to "whatever this environment says", and DEV's
    /// out-of-the-box safety would depend on remembering to toggle it back.</para>
    /// </summary>
    public async Task SetExternalWritesOverrideAsync(
        int eventId, bool? allow, string? byEmail, CancellationToken ct = default)
    {
        var row = await _db.FeatureSettings.FirstOrDefaultAsync(
            f => f.EventId == eventId
                 && f.FeatureKey == Integrations.ExternalWriteGuard.OverrideKey, ct);

        if (allow is null)
        {
            if (row is not null)
            {
                _db.FeatureSettings.Remove(row);
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        if (row is null)
        {
            row = new FeatureSetting
            {
                EventId = eventId,
                FeatureKey = Integrations.ExternalWriteGuard.OverrideKey,
                ReleasedToRing = Rings.Default,
            };
            _db.FeatureSettings.Add(row);
        }

        row.Enabled = allow.Value;
        Stamp(row, byEmail);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §340-H — the organizer's current override, or null when none is set (⇒ the edition
    /// inherits the environment default). Used by the Settings page to show all three
    /// states honestly rather than collapsing "inherit" into a bare on/off.
    /// </summary>
    public async Task<bool?> GetExternalWritesOverrideAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.FeatureSettings
            .Where(f => f.EventId == eventId
                        && f.FeatureKey == Integrations.ExternalWriteGuard.OverrideKey)
            .Select(f => (bool?)f.Enabled)
            .FirstOrDefaultAsync(ct);

    private async Task<FeatureSetting> UpsertRowAsync(
        int eventId, string featureKey, FeatureDescriptor descriptor, CancellationToken ct)
    {
        var row = await _db.FeatureSettings
            .FirstOrDefaultAsync(f => f.EventId == eventId && f.FeatureKey == featureKey, ct);
        if (row is null)
        {
            row = new FeatureSetting
            {
                EventId = eventId,
                FeatureKey = featureKey,
                Enabled = descriptor.DefaultEnabled,
            };
            _db.FeatureSettings.Add(row);
        }
        return row;
    }

    private void Stamp(FeatureSetting row, string? byEmail)
    {
        row.UpdatedAt = _clock.GetUtcNow();
        row.LastUpdatedByEmail = string.IsNullOrWhiteSpace(byEmail) ? null : byEmail.Trim();
    }

    /// <summary>
    /// Dependency warnings for the current state (REQUIREMENTS §23 cross-refs):
    /// for each ENABLED advanced feature whose dependency is currently OFF, return
    /// a (feature, missing-dependency) pair so the GUI can warn that the feature is
    /// effectively inert until its prerequisite is enabled.
    /// </summary>
    public async Task<IReadOnlyList<(FeatureDescriptor Feature, FeatureDescriptor Missing)>>
        GetUnmetDependenciesAsync(int eventId, CancellationToken ct = default)
    {
        var states = await GetAllAsync(eventId, ct);
        var byKey = states.ToDictionary(s => s.Key, s => s.Enabled);

        var result = new List<(FeatureDescriptor, FeatureDescriptor)>();
        foreach (var s in states)
        {
            if (!s.Enabled) continue;
            foreach (var depKey in s.Descriptor.DependsOn)
            {
                var dep = FeatureCatalog.Find(depKey);
                if (dep is null) continue;
                if (byKey.TryGetValue(depKey, out var depOn) && !depOn)
                {
                    result.Add((s.Descriptor, dep));
                }
            }
        }
        return result;
    }
}
