using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §340-H — the master switch for outbound WRITES to third-party systems (Zoho Backstage,
/// e-conomic, LinkedIn, SharePoint).
///
/// <para>Operator requirements (2026-07-26), all three satisfied here:
/// <b>(a)</b> controllable per ENVIRONMENT (dev vs prod) — the
/// <c>Integrations:AllowExternalWrites</c> app setting, emitted explicitly by both bicep
/// modules; <b>(b)</b> controllable by an ORGANIZER on the Settings page — the per-edition
/// override below; <b>(c)</b> composed so DEV is safe out of the box while prod writes.</para>
///
/// <para><b>Why this exists even though several gates already did.</b> Before §340-H, DEV's
/// inability to write to external systems was EMERGENT rather than designed — four unrelated
/// mechanisms covering different subsets, with real gaps. The worst: the Zoho <b>session</b>
/// and <b>speaker</b> pushes were gated only by per-edition FEATURE FLAGS. A feature flag is
/// an EDITION concept, not an ENVIRONMENT one, so nothing about being "the DEV app" stopped
/// them, and enabling <c>backstage-speaker-sync</c> in DEV would push real speakers into the
/// single real Backstage tenant.</para>
///
/// <para><b>Deliberately NOT built on <see cref="TestModeOptions"/>.</b> TestMode is a
/// sponsor/exhibitor <i>test-data</i> concept — test sponsor name, test coordinator address —
/// and its <c>Enabled</c> default must stay <c>true</c> for its own reasons. Overloading it
/// with "block every external write" would make one boolean answer two questions, which is
/// exactly the §333 root cause.</para>
/// </summary>
public sealed class ExternalWriteOptions
{
    public const string SectionName = "Integrations";

    /// <summary>
    /// The ENVIRONMENT default: may this host write to third-party systems?
    ///
    /// <para><b>Defaults to FALSE — a host nobody configured must not reach a third
    /// party.</b> Both bicep modules (web + functions) emit it explicitly — prod
    /// <c>true</c>, dev <c>false</c> — so this default only ever governs an unconfigured
    /// host, where "write nothing" is the only defensible answer.</para>
    ///
    /// <para>⚠️ <b>Deploy order matters in PROD.</b> Because the default is false, the app
    /// setting must exist BEFORE the code that reads it deploys, or prod stops writing until
    /// it does. That failure is safe — writes are skipped and LOGGED, nothing is corrupted —
    /// and self-heals when the setting lands.</para>
    /// </summary>
    public bool AllowExternalWrites { get; set; } = false;
}

/// <summary>
/// The single seam every outbound third-party WRITE consults. Reads are never gated:
/// pulling data INTO CEH changes nothing outside it, and blocking reads would break DEV
/// rather than protect it.
/// </summary>
public interface IExternalWriteGuard
{
    /// <summary>
    /// Ask permission for one write. True ⇒ proceed. When blocked it returns false and
    /// LOGS the refusal naming the system and operation — a blocked write must never be a
    /// silent absence (the §335 lesson: "the only symptom is that nothing happens").
    /// </summary>
    Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default);

    /// <summary>The effective posture, for the Settings page and the startup banner.</summary>
    Task<bool> IsAllowedAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IExternalWriteGuard"/>
public sealed class ExternalWriteGuard : IExternalWriteGuard
{
    /// <summary>
    /// Reserved NON-catalog per-edition key holding the organizer's override, stored as a
    /// <see cref="FeatureSetting"/> row. Same shape and rationale as
    /// <c>FeatureGateService.JobsPausedKey</c>: an operational switch, not a rollout-staged
    /// capability, so it deliberately bypasses the <c>FeatureCatalog</c> grid.
    ///
    /// <para>A MISSING row means "no override" — fall back to the environment default.
    /// That is what keeps DEV safe out of the box while still letting the operator
    /// "specifically enable" it.</para>
    /// </summary>
    public const string OverrideKey = "external-writes";

    private readonly CommunityHubDbContext _db;
    private readonly ExternalWriteOptions _options;
    private readonly ILogger<ExternalWriteGuard>? _log;

    // Resolved at most once per scope: a write loop must not issue a DB query per row.
    private bool? _cached;

    public ExternalWriteGuard(
        CommunityHubDbContext db,
        ExternalWriteOptions options,
        ILogger<ExternalWriteGuard>? log = null)
    {
        _db = db;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Effective posture = the organizer's per-edition override if one exists, else the
    /// environment default.
    ///
    /// <para><b>The override can turn writes ON in DEV as well as OFF in prod, and that is
    /// intended</b> — it is the operator's "except if i specifically enable so it can do
    /// this". DEV is protected by the environment DEFAULT, not by making the switch
    /// unreachable; the Settings page states plainly that enabling it in DEV writes to the
    /// real third-party systems.</para>
    ///
    /// <para>Fails CLOSED on a DB error: if the override cannot be read we do not guess,
    /// and "do not write to a third party" is the safe guess anyway.</para>
    /// </summary>
    public async Task<bool> IsAllowedAsync(CancellationToken ct = default)
    {
        if (_cached is bool c) return c;

        bool effective;
        try
        {
            var eventId = await _db.Events
                .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);

            bool? over = eventId is null ? null : await _db.FeatureSettings
                .Where(f => f.EventId == eventId.Value && f.FeatureKey == OverrideKey)
                .Select(f => (bool?)f.Enabled)
                .FirstOrDefaultAsync(ct);

            // 🔒 §612 — THE ENVIRONMENT IS A CEILING, NOT A DEFAULT. DO NOT RESTORE `over ?? …`.
            //
            // Operator 2026-07-28: *"in dev we want to enforce BLOCK WRITES !!!"*, and his reason
            // is concrete — *"as i will then have 2 set of everything in zoho"*: a DEV run creating
            // real sessions/speakers/sponsors alongside the live ones.
            //
            // This USED to read `over ?? _options.AllowExternalWrites`, so an organizer pressing
            // "Allow writes" on the DEV Settings page would have ENABLED real third-party writes
            // from DEV — one click, silently. The damage (duplicate Zoho records) is manual to undo
            // and can e-mail real people (§326bx invited 19 speakers prematurely).
            //
            // The override may now only ever RESTRICT: on a host whose config blocks writes, no
            // override can widen it. Same shape as the §566 e-mail ceiling —
            // effective = MIN(environment, override) — so the rule is consistent system-wide.
            effective = _options.AllowExternalWrites && (over ?? true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "External write guard: could not read the per-edition override; "
                + "failing CLOSED (no external writes).");
            effective = false;
        }

        _cached = effective;
        return effective;
    }

    public async Task<bool> AllowAsync(
        string system, string operation, CancellationToken ct = default)
    {
        if (await IsAllowedAsync(ct)) return true;

        // Warning, not Information: in a real environment this firing means either the
        // switch is correctly off (DEV — expected, and you want to SEE it while testing) or
        // prod is missing its app setting (a real problem). Both deserve to be visible.
        _log?.LogWarning(
            "External write BLOCKED — {System}.{Operation} was NOT performed. "
            + "Integrations:AllowExternalWrites is false for this host and no organizer "
            + "override enables it. Expected in DEV; in PROD it means the app setting is missing.",
            system, operation);
        return false;
    }

    /// <summary>
    /// The one-line startup banner both hosts emit, so the posture is visible in the log
    /// without anyone reading config. An operator should never have to GUESS whether the app
    /// in front of them can write to Zoho. Reports the ENVIRONMENT default — a per-edition
    /// override may change it later, which the Settings page shows.
    /// </summary>
    public static string StartupBanner(bool envDefault) =>
        envDefault
            ? "External writes: ALLOWED by environment config — this host CAN write to Zoho "
              + "Backstage, e-conomic, LinkedIn and SharePoint (an organizer override may still turn it off)."
            // §612 — the wording had to change with the rule. It previously ended "unless an
            // organizer explicitly overrides it on the Settings page", which was true and is exactly
            // the hole the operator wanted closed. A blocked host is now blocked FULL STOP.
            : "External writes: BLOCKED by environment config (Integrations:AllowExternalWrites=false) "
              + "— no outbound writes to Zoho Backstage, e-conomic, LinkedIn or SharePoint. "
              + "This is ENFORCED: no organizer override can enable writes on this host.";
}

/// <summary>
/// Always-allow guard for unit tests and for constructions that predate §340-H, so wiring
/// the guard in did not have to touch every existing test or call site. Never registered in DI.
/// </summary>
public sealed class AllowAllExternalWrites : IExternalWriteGuard
{
    public Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> IsAllowedAsync(CancellationToken ct = default) => Task.FromResult(true);
}
