using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Settings;

/// <summary>
/// The single gate the web app, the Functions jobs and the sync services all
/// call to decide whether an advanced capability runs (REQUIREMENTS §23). Pure
/// and unit-testable: constructor-injected <see cref="CommunityHubDbContext"/>,
/// SQL-translatable queries, no ambient state.
///
/// Resolution order for <see cref="IsFeatureEnabledAsync"/>:
///   1. a persisted per-edition <see cref="FeatureSetting"/> kill switch (organizer choice), else
///   2. the immutable <see cref="FeatureCatalog"/> default (advanced ⇒ OFF).
///
/// A disabled feature is INERT — the caller no-ops and performs no work/sends.
/// Core capabilities are never passed here; they are not gated.
/// </summary>
public sealed class FeatureGateService
{
    private readonly CommunityHubDbContext _db;

    public FeatureGateService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Reserved (NON-catalog) per-edition key for the org-admin "pause all
    /// background jobs" master switch (operator 2026-06-23). Stored as a
    /// <see cref="FeatureSetting"/> row whose <see cref="FeatureSetting.Enabled"/>
    /// means PAUSED (<c>true ⇒ paused</c>); a MISSING row means NOT paused, so the
    /// safe default is jobs run. Deliberately not a <see cref="FeatureCatalog"/>
    /// feature — it is a single operational kill switch, not a rollout-staged
    /// capability, so it bypasses the catalog grid.
    /// </summary>
    public const string JobsPausedKey = "jobs-paused";

    /// <summary>
    /// True when an organizer has PAUSED all background jobs for the edition via the
    /// Settings master switch. The Functions worker checks this before EVERY job
    /// runs (see JobsPauseMiddleware); a missing flag ⇒ not paused (default behaviour
    /// unchanged). Resume takes effect on each job's next tick (the flag is re-read).
    /// </summary>
    public async Task<bool> AreJobsPausedAsync(int eventId, CancellationToken ct = default)
    {
        var paused = await _db.FeatureSettings
            .Where(f => f.EventId == eventId && f.FeatureKey == JobsPausedKey)
            .Select(f => (bool?)f.Enabled)
            .FirstOrDefaultAsync(ct);
        return paused ?? false;
    }

    /// <summary>
    /// Is <paramref name="featureKey"/> enabled for <paramref name="eventId"/>?
    /// Reads the persisted kill switch first, then the catalog default. An unknown
    /// key (not a registered feature) returns the catalog fallback (true), so a
    /// non-feature call never silently swallows work.
    /// </summary>
    public async Task<bool> IsFeatureEnabledAsync(
        string featureKey, int eventId, CancellationToken ct = default)
    {
        // SQL-translatable: a single keyed lookup, nullable bool projection so a
        // missing row is distinguishable from a stored false.
        var stored = await _db.FeatureSettings
            .Where(f => f.EventId == eventId && f.FeatureKey == featureKey)
            .Select(f => (bool?)f.Enabled)
            .FirstOrDefaultAsync(ct);

        return stored ?? FeatureCatalog.DefaultEnabled(featureKey);
    }

    /// <summary>
    /// The RELEASE RING a feature is currently released to for an edition
    /// (REQUIREMENTS §23a group-ring model). Resolution, in order:
    ///   1. the per-feature ring OVERRIDE (<see cref="FeatureSetting.ReleasedToRingOverride"/>)
    ///      — the "special ring" exception, if set; else
    ///   2. the EFFECTIVE GROUP's ring (<see cref="FeatureGroupSetting"/> for the
    ///      feature's group, where the group = per-edition
    ///      <see cref="FeatureSetting.GroupOverride"/> ?? catalog home group), if set; else
    ///   3. the catalog descriptor default for the feature (<see cref="FeatureCatalog.DefaultReleasedToRing"/>).
    /// An unknown key falls open to <see cref="Ring.Broad"/>. Back-compat: existing
    /// per-feature rings are migrated into the override, so behaviour is unchanged.
    /// </summary>
    public async Task<Ring> GetReleasedRingAsync(
        string featureKey, int eventId, CancellationToken ct = default)
    {
        // One keyed lookup for the feature row's override + group override.
        var row = await _db.FeatureSettings
            .Where(f => f.EventId == eventId && f.FeatureKey == featureKey)
            .Select(f => new { f.ReleasedToRingOverride, f.GroupOverride })
            .FirstOrDefaultAsync(ct);

        // 1. Per-feature override wins.
        if (row?.ReleasedToRingOverride is Ring ovr) return ovr;

        // 2. The effective group's ring (group override, else catalog home group).
        var catalogGroup = FeatureCatalog.Find(featureKey)?.Group;
        var effGroup = row?.GroupOverride ?? catalogGroup;
        if (effGroup is FeatureGroup g)
        {
            var groupRing = await _db.FeatureGroupSettings
                .Where(x => x.EventId == eventId && x.Group == g)
                .Select(x => (Ring?)x.ReleasedToRing)
                .FirstOrDefaultAsync(ct);
            if (groupRing is Ring gr) return gr;
        }

        // 3. The feature's catalog default (Broad for an unknown key).
        return FeatureCatalog.DefaultReleasedToRing(featureKey);
    }

    /// <summary>
    /// Is <paramref name="featureKey"/> ACTIVE for a resource whose effective ring
    /// is <paramref name="effectiveRing"/>? The ring-aware gate the GUI, the
    /// engines AND the schedulers/jobs all call: active iff the feature is
    /// <see cref="IsFeatureEnabledAsync">enabled (not killed)</see> AND the
    /// resource's effective ring is at or below the feature's released ring
    /// (<c>effectiveRing &lt;= ReleasedToRing</c>). Identical in dev and prod.
    ///
    /// Defaults make this backwards-compatible: with nothing ring-assigned every
    /// resource is <see cref="Ring.Broad"/> and every feature is released to
    /// <see cref="Ring.Broad"/>, so the ring test always passes and behaviour
    /// reduces to the plain on/off gate.
    /// </summary>
    public async Task<bool> IsFeatureActiveForRingAsync(
        string featureKey, int eventId, Ring effectiveRing, CancellationToken ct = default)
    {
        if (!await IsFeatureEnabledAsync(featureKey, eventId, ct)) return false;
        // ENGINE + ENGINEQUEUED features (plumbing/dependency — pulls, syncs,
        // transport, queue-fed posters/appliers) are never ring-scoped: active for
        // everyone once enabled (GA). Only RING-SCOPED surfaces — QUEUE (org-admin
        // staging, gated per organizer/target) and USER-IMPACT (email/task/GUI a
        // person experiences) — scope by ring. An unknown key (null descriptor) is
        // treated as not ring-scoped (fail-open), matching the Broad ring default.
        if (FeatureCatalog.Find(featureKey)?.IsRingScoped != true) return true;
        var releasedTo = await GetReleasedRingAsync(featureKey, eventId, ct);
        return Rings.IsActiveForRing(effectiveRing, releasedTo);
    }

    /// <summary>
    /// RING-ONLY scope check for QUEUE commits/assigns (REQUIREMENTS §23a category 3):
    /// is the TARGET participant within <paramref name="featureKey"/>'s released ring?
    /// Unlike <see cref="IsFeatureActiveForRingAsync"/> this does NOT consult the kill
    /// switch — the on/off/availability gate for a queue feature is enforced at its
    /// TILE, so an ALREADY-SHIPPED queue feature (released ring defaults Broad, but
    /// catalog default-enabled is OFF) is never accidentally blocked here. With the
    /// Broad default every target is in scope (no behaviour change); lower the released
    /// ring in <c>/Organizer/Settings</c> to ring-TEST and out-of-ring targets return
    /// false so the organizer can hold them until the ring is promoted.
    /// </summary>
    public async Task<bool> IsTargetInReleasedRingAsync(
        string featureKey, int eventId, int participantId,
        RingResolver rings, CancellationToken ct = default)
    {
        var releasedTo = await GetReleasedRingAsync(featureKey, eventId, ct);
        var effective = await rings.GetEffectiveRingAsync(participantId, ct);
        return Rings.IsActiveForRing(effective, releasedTo);
    }

    /// <summary>
    /// Convenience overload that resolves the participant's effective ring via
    /// <see cref="RingResolver"/> first, then applies
    /// <see cref="IsFeatureActiveForRingAsync(string,int,Ring,CancellationToken)"/>.
    /// Used by the GUI ("does the logged-in user see this feature?") and by jobs
    /// iterating per-resource ("does this person's ring let the feature run?").
    /// </summary>
    public async Task<bool> IsFeatureActiveForParticipantAsync(
        string featureKey, int eventId, int participantId,
        RingResolver rings, CancellationToken ct = default)
    {
        var effective = await rings.GetEffectiveRingAsync(participantId, ct);
        return await IsFeatureActiveForRingAsync(featureKey, eventId, effective, ct);
    }

    /// <summary>
    /// Combined helper for the common "both must be on" case — e.g. a digest send
    /// needs BOTH its own feature AND the global outbound-email switch. Returns
    /// true only when every key resolves enabled. The web + jobs call this so the
    /// dependency-on-email rule is enforced identically everywhere.
    /// </summary>
    public async Task<bool> AreAllEnabledAsync(
        int eventId, CancellationToken ct, params string[] featureKeys)
    {
        foreach (var key in featureKeys)
        {
            if (!await IsFeatureEnabledAsync(key, eventId, ct)) return false;
        }
        return true;
    }

    /// <summary>
    /// Convenience for "is outbound email allowed at all for this edition" — the
    /// persisted global email kill switch. Every send path that is edition-scoped
    /// consults this; the config-level <c>EmailOptions.KillSwitch</c> is the
    /// process-wide hard stop on top (see <c>BrevoEmailSender</c>).
    /// </summary>
    public Task<bool> IsOutboundEmailEnabledAsync(int eventId, CancellationToken ct = default) =>
        IsFeatureEnabledAsync(FeatureCatalog.OutboundEmailKey, eventId, ct);
}
