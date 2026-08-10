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

    /// <summary>
    /// 🔴 §1037 — THE PER-SYSTEM CEILING. <c>Integrations:ExternalWrites:{System}</c>, e.g.
    /// <c>Integrations:ExternalWrites:Zoho = false</c>.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-10, stating the DEV policy in full: <i>"dev must newer WRITE to zoho,
    /// but it is allowed to read from zoho. dev is allowed to readwrite to erp. dev is allowed to
    /// read from webshop. dev is allowed to have test webpages so i can verify signage. dev is not
    /// allowed to publish on linkedin. dev is allowed to import from sessionize. dev is allowed to
    /// write to sharepoint (as it has its own separate path). as a general rule, importing into ceh
    /// is 100% fine - but sending data out from dev is controlled"</i>.</para>
    ///
    /// <para>🔑 <b>His general rule was already this class's rule</b> — §340-H put it in as many
    /// words: <i>"Reads are never gated: pulling data INTO CEH changes nothing outside it"</i>. What
    /// was missing is that the ceiling was ONE boolean for every system at once, so DEV could not be
    /// "no Zoho writes, but yes to ERP and SharePoint" — it was all or nothing, and the answer had to
    /// be nothing.</para>
    ///
    /// <para>🔒 <b>A system NOT listed falls back to <see cref="AllowExternalWrites"/>.</b> That
    /// keeps PROD (which sets it true) writing everywhere without enumerating systems, and keeps an
    /// unconfigured host at "write nothing" — the only defensible default. ⚠️ It also means a NEW
    /// integration is governed the moment it calls the guard, rather than silently unlisted.</para>
    /// </remarks>
    public Dictionary<string, bool> ExternalWrites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// §1037 — the system names the guard is called with, and the config key each one binds to.
/// </summary>
/// <remarks>
/// ⚠️ <b>These strings are the ones the CALL SITES really pass</b> — listed from the code, never
/// guessed (§767 shipped a sweep that matched nothing for four production runs because a convention
/// was assumed). A rename on either side unbinds the policy silently, so
/// <c>ExternalWriteSystemNameTests</c> pins the mapping against the live call sites.
/// </remarks>
public static class ExternalSystems
{
    public const string Zoho = "Zoho Backstage";
    public const string Erp = "e-conomic";
    public const string LinkedIn = "LinkedIn";
    public const string SharePoint = "SharePoint";

    /// <summary>
    /// 🔴 §1041 — the WEBSHOP (Company Manager / WordPress on the public site).
    /// </summary>
    /// <remarks>
    /// Added after a DEV run reported <b>53 billing updates against the LIVE webshop</b>. The cause
    /// was not a misconfiguration: <c>CompanyManagerClient</c> had never been wired to the guard at
    /// all, so its three write methods were reachable from any host holding the credentials — and
    /// DEV and PROD share one Company Manager. ⚠️ §340-H's list (Zoho, e-conomic, LinkedIn,
    /// SharePoint) simply did not include the webshop, and nothing failed when it was missed.
    /// </remarks>
    public const string Webshop = "Webshop";

    /// <summary>
    /// The settings key for a system name. 🔑 An ALIAS, not a slug of the display name: nobody
    /// should have to write <c>Integrations:ExternalWrites:Zoho Backstage</c> (a key with a space)
    /// or guess that e-conomic's hyphen survives.
    /// </summary>
    public static string ConfigKeyFor(string system) => system switch
    {
        Zoho => "Zoho",
        Erp => "Erp",
        LinkedIn => "LinkedIn",
        SharePoint => "SharePoint",
        Webshop => "Webshop",
        // An unrecognised system keeps its own name as the key, so a new integration is
        // configurable the day it is added rather than silently falling through for ever.
        _ => system,
    };
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
    public async Task<bool> IsAllowedAsync(CancellationToken ct = default) =>
        _options.AllowExternalWrites && await OrganizerOverrideAllowsAsync(ct);

    /// <summary>
    /// §1037 — the ORGANIZER half of the decision, on its own: <c>true</c> unless an organizer has
    /// switched external writes off for this edition.
    /// </summary>
    /// <remarks>
    /// <para>Split out because the ENVIRONMENT half is now per-system (§1037) while this half is
    /// not: an organizer turning writes off means "stop writing anywhere", never "stop writing to
    /// ERP but keep writing to SharePoint".</para>
    ///
    /// <para>🔒 §612 still holds and is what this split must not break: the override may only ever
    /// RESTRICT. It is ANDed with the per-system ceiling in <see cref="AllowAsync"/>, so no click on
    /// a DEV Settings page can widen what that host's config permits.</para>
    ///
    /// <para>Fails CLOSED on a DB error: if the override cannot be read we do not guess, and "do not
    /// write to a third party" is the safe guess anyway.</para>
    /// </remarks>
    private async Task<bool> OrganizerOverrideAllowsAsync(CancellationToken ct = default)
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
            // §1037 — the ENVIRONMENT half moved to `EnvironmentCeilingFor`, which is per-system.
            // This returns only the organizer's answer; `AllowAsync` ANDs the two, so §612's
            // "the environment is a ceiling, not a default" is unchanged.
            effective = over ?? true;
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

    /// <summary>
    /// §1037 — the ENVIRONMENT ceiling for ONE system: its explicit
    /// <c>Integrations:ExternalWrites:{key}</c> setting, else the host-wide default.
    /// </summary>
    private bool EnvironmentCeilingFor(string system) =>
        _options.ExternalWrites.TryGetValue(ExternalSystems.ConfigKeyFor(system), out var perSystem)
            ? perSystem
            : _options.AllowExternalWrites;

    public async Task<bool> AllowAsync(
        string system, string operation, CancellationToken ct = default)
    {
        // 🔴 §1037 — TWO CEILINGS, AND BOTH MUST SAY YES.
        //
        // The per-system setting is what lets DEV be what he actually asked for — no Zoho writes,
        // but read/write to ERP and writes to SharePoint — instead of the all-or-nothing switch
        // that forced DEV to "nothing". The organizer override still only ever RESTRICTS (§612):
        // no click on the Settings page can widen what this host's config permits.
        if (!EnvironmentCeilingFor(system))
        {
            _log?.LogWarning(
                "External write BLOCKED — {System}.{Operation} was NOT performed. This host's "
                + "policy for {System} is OFF (Integrations:ExternalWrites:{Key}, or the "
                + "Integrations:AllowExternalWrites default). Expected in DEV for Zoho and "
                + "LinkedIn; in PROD it means an app setting is missing.",
                system, operation, system, ExternalSystems.ConfigKeyFor(system));
            return false;
        }

        if (await OrganizerOverrideAllowsAsync(ct)) return true;

        // Warning, not Information: in a real environment this firing means either the
        // switch is correctly off (DEV — expected, and you want to SEE it while testing) or
        // prod is missing its app setting (a real problem). Both deserve to be visible.
        _log?.LogWarning(
            "External write BLOCKED — {System}.{Operation} was NOT performed: an organizer has "
            + "turned external writes off for this edition on the Settings page.",
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
        StartupBanner(new ExternalWriteOptions { AllowExternalWrites = envDefault });

    /// <summary>
    /// §1037 — the banner now names EACH system, because one word can no longer describe the host.
    /// </summary>
    /// <remarks>
    /// ⚠️ The old banner said "no outbound writes to Zoho Backstage, e-conomic, LinkedIn or
    /// SharePoint" whenever the default was off. With per-system ceilings that sentence would be a
    /// LIE on DEV — which is allowed to write to e-conomic and SharePoint — and a banner an operator
    /// cannot trust is worse than none, because its whole job is to answer "can the app in front of
    /// me write to Zoho" without reading config.
    /// </remarks>
    public static string StartupBanner(ExternalWriteOptions options)
    {
        string[] systems =
        [
            ExternalSystems.Zoho, ExternalSystems.Erp,
            ExternalSystems.LinkedIn, ExternalSystems.SharePoint,
            ExternalSystems.Webshop,
        ];

        var parts = systems.Select(s =>
        {
            var key = ExternalSystems.ConfigKeyFor(s);
            var allowed = options.ExternalWrites.TryGetValue(key, out var v)
                ? v
                : options.AllowExternalWrites;
            return $"{s}={(allowed ? "ALLOWED" : "BLOCKED")}";
        });

        return "External writes by system — " + string.Join(", ", parts)
            + ". Reads are never gated (importing into CEH changes nothing outside it). "
            + "An organizer override can only RESTRICT this further, never widen it (§612).";
    }
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
