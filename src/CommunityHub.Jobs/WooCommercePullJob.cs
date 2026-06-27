using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Timer that triggers the sponsor-order pull every 30 minutes. All real work
/// lives in <see cref="SponsorOrderPullService"/> in Core so the same engine can
/// also be invoked from the CommunityHub.OneShot CLI for local DEV verification
/// without deploying the Function App. The pull covers sponsor orders + the
/// sponsor-contact sync in one job, gated by the 'sponsor-order-pull' feature flag.
/// </summary>
public sealed class WooCommercePullJob
{
    private readonly SponsorOrderPullService _service;
    private readonly SponsorZohoProvisionService _provision;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<WooCommercePullJob> _log;

    public WooCommercePullJob(
        SponsorOrderPullService service,
        SponsorZohoProvisionService provision,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        EngineAlertSender alerts,
        ILogger<WooCommercePullJob> log)
    {
        _service = service;
        _provision = provision;
        _db = db;
        _gate = gate;
        _audit = audit;
        _alerts = alerts;
        _log = log;
    }

    /// <summary>Every 15 minutes (operator 2026-06-25: tightened from 30 so new
    /// sponsors/exhibitors reconcile to Zoho sooner).</summary>
    [Function("WooCommercePullJob")]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        // GATE (REQUIREMENTS §23): the sponsor-order pull is an advanced feature,
        // off by default. The pull engine is fleet-wide (not edition-scoped), so it
        // runs only while at least one active edition has 'sponsor-order-pull'
        // enabled; when every active edition has it off the job no-ops.
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var anyEnabled = false;
        foreach (var id in activeEventIds)
        {
            if (await _gate.IsFeatureEnabledAsync("sponsor-order-pull", id, ct))
            {
                anyEnabled = true;
                break;
            }
        }
        if (!anyEnabled)
        {
            _log.LogInformation(
                "WooCommercePullJob: feature 'sponsor-order-pull' disabled for all active editions, skipped.");
            return;
        }

        var result = await _service.RunAsync(ct);
        if (!result.RanToCompletion)
        {
            _log.LogWarning(
                "WooCommercePullJob: skipped ({Reason}).", result.SkipReason);
        }

        // Named Engine event (REQUIREMENTS §24). Fleet-wide pull — record under the
        // active edition (CEH runs one active edition at a time). Only audit a run
        // that did something (or failed) — idle 30-min polls would flood the trail.
        var changed = result.OrdersFetched + result.TasksCreated
            + result.ContactsCreated + result.ContactsUpdated;
        if (changed > 0 || !result.RanToCompletion)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventIds.FirstOrDefault(),
                Category = AuditCategory.Engine,
                Action = "sponsor-order-pull",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = result.RanToCompletion ? AuditOutcome.Success : AuditOutcome.Failure,
                Summary = result.RanToCompletion
                    ? $"Sponsor order pull: {result.OrdersFetched} orders, {result.TasksCreated} tasks, "
                        + $"contacts +{result.ContactsCreated}/~{result.ContactsUpdated}"
                    : $"Sponsor order pull skipped: {result.SkipReason}",
            }, ct);

        // STAGE 4b — after the pull, create/link Zoho sponsor + exhibitor records from
        // webshop data (replaces the legacy PowerShell sync). Per-edition gated by
        // 'sponsor-zoho-provision' (off by default) so it activates only when the
        // operator is ready to retire the script.
        foreach (var id in activeEventIds)
        {
            if (!await _gate.IsFeatureEnabledAsync("sponsor-zoho-provision", id, ct)) continue;
            try
            {
                var pr = await _provision.ProvisionAsync(id, ct);
                if (!pr.Enabled) continue;
                var did = pr.SponsorsCreated + pr.SponsorsLinked + pr.ExhibitorsCreated + pr.ExhibitorsRequested + pr.ExhibitorsLinked + pr.Skipped;
                _log.LogInformation(
                    "Zoho provision (event {Event}): created {C}, linked {L}, exhibitor-created {EC}, exhibitor-requests {E}, exhibitor-linked {EL}, skipped {S}.",
                    id, pr.SponsorsCreated, pr.SponsorsLinked, pr.ExhibitorsCreated, pr.ExhibitorsRequested, pr.ExhibitorsLinked, pr.Skipped);
                if (did > 0 || pr.Notes.Count > 0)
                    await _audit.RecordAsync(new AuditEntry
                    {
                        EventId = id,
                        Category = AuditCategory.Engine,
                        Action = "sponsor-zoho-provision",
                        ActorEmail = "system",
                        Source = AuditSource.Job,
                        Outcome = pr.Skipped > 0 ? AuditOutcome.Failure : AuditOutcome.Success,
                        Summary = $"Zoho provision: {pr.SponsorsCreated} sponsor(s) created, {pr.SponsorsLinked} linked, "
                            + $"{pr.ExhibitorsCreated} exhibitor(s) created, {pr.ExhibitorsRequested} exhibitor request(s), {pr.ExhibitorsLinked} exhibitor(s) linked, {pr.Skipped} skipped."
                            + (pr.Notes.Count > 0 ? " " + string.Join("; ", pr.Notes.Take(8)) : ""),
                    }, ct);

                // NEW-RECORD NOTIFICATION (operator 2026-06-25): email when the engine
                // CREATES sponsors/exhibitors so the operator knows it happened (creation
                // fires once per record, so this never spams the steady-state runs).
                if (pr.SponsorsCreated > 0 || pr.ExhibitorsCreated > 0 || pr.ExhibitorsRequested > 0)
                    await SendCreatedNotificationAsync(id, pr, ct);

                // DRIFT ALERT (operator 2026-06-25): the goal is Zoho == webshop orders.
                // When the webshop has sponsors/exhibitors the engine could NOT create or
                // link in Zoho (pr.Skipped > 0), email the developer so it never fails
                // silently again ("I have no way of knowing it otherwise").
                if (pr.Skipped > 0)
                    await SendDriftAlertAsync(id, pr, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Zoho provision failed for event {Event}.", id);
                var failLabel = await EventLabelAsync(id, ct);
                await _alerts.AlertAsync(
                    $"[ELDK27] Sponsor→Zoho provision FAILED ({failLabel})",
                    $"<p>The sponsor/exhibitor provision engine threw an exception for {System.Net.WebUtility.HtmlEncode(failLabel)}.</p>"
                    + $"<pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre>", ct, throttleKey: $"provision-fail:{id}");
            }
        }
    }

    /// <summary>Email the operator when the engine created new sponsor/exhibitor records.</summary>
    private async Task SendCreatedNotificationAsync(int eventId, SponsorZohoProvisionService.ProvisionResult pr, CancellationToken ct)
    {
        string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var created = pr.Notes.Where(n => n.Contains("created in Zoho", StringComparison.OrdinalIgnoreCase)).ToList();
        var items = string.Join("", created.Select(n => $"<li>{Enc(n)}</li>"));
        var label = await EventLabelAsync(eventId, ct);
        var html =
            $"<p>The sponsor→Zoho engine created new records for {Enc(label)}:</p>"
            + $"<ul><li><b>{pr.SponsorsCreated}</b> sponsor(s) created, {pr.SponsorsLinked} linked</li>"
            + $"<li><b>{pr.ExhibitorsCreated}</b> exhibitor(s) created, {pr.ExhibitorsRequested} request(s), {pr.ExhibitorsLinked} linked</li></ul>"
            + (items.Length > 0 ? $"<p><b>Records:</b></p><ul>{items}</ul>" : "");
        // No throttle: creation fires once per record, so this never floods.
        await _alerts.AlertAsync($"[ELDK27] New sponsor/exhibitor records created in Zoho ({label})", html, ct);
    }

    /// <summary>Email the developer the list of webshop sponsors/exhibitors that are NOT in
    /// Zoho (the engine couldn't create/link them) — the reconcile-drift alert.</summary>
    private async Task SendDriftAlertAsync(int eventId, SponsorZohoProvisionService.ProvisionResult pr, CancellationToken ct)
    {
        string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var items = string.Join("", pr.Notes.Select(n => $"<li>{Enc(n)}</li>"));
        var label = await EventLabelAsync(eventId, ct);
        var html =
            $"<p><b>{pr.Skipped}</b> webshop sponsor/exhibitor record(s) could NOT be reconciled to Zoho Backstage "
            + $"({Enc(label)}). The webshop order shows them, but they are missing from Zoho and the engine "
            + "could not create them (most likely Zoho's \"email is the key\" rule — the contact email is already "
            + "in use, so a create returns success with no record).</p>"
            + $"<p>Created {pr.SponsorsCreated} sponsor(s), linked {pr.SponsorsLinked}; "
            + $"created {pr.ExhibitorsCreated} exhibitor(s), linked {pr.ExhibitorsLinked}.</p>"
            + (items.Length > 0 ? $"<p><b>Details:</b></p><ul>{items}</ul>" : "")
            + "<p>Next run is in ≤15 min. Check the Function logs (ZohoClient CreateSponsor/CreateExhibitor) "
            + "for the exact Zoho response body.</p>";
        await _alerts.AlertAsync(
            $"[ELDK27] Sponsor/exhibitor NOT in Zoho — {pr.Skipped} need attention ({label})",
            html, ct, throttleKey: $"sponsor-drift:{eventId}");
    }

    /// <summary>Human label for an event in engine/ops emails — the edition Code
    /// (e.g. "ELDK27"), falling back to DisplayName, then "event {id}" (§108:
    /// never leak the raw numeric event id in operator-facing mail).</summary>
    private async Task<string> EventLabelAsync(int eventId, CancellationToken ct)
    {
        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return $"event {eventId}";
        if (!string.IsNullOrWhiteSpace(ev.Code)) return ev.Code;
        if (!string.IsNullOrWhiteSpace(ev.DisplayName)) return ev.DisplayName;
        return $"event {eventId}";
    }
}
