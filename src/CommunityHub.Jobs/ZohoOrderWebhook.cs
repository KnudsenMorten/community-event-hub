using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// REAL-TIME leg of the authoritative one-way Zoho→CEH mirror (REQUIREMENTS §128): the
/// FIRST HTTP-triggered Function in this worker (every other job is a TimerTrigger). Zoho
/// Backstage POSTs here on Event Order / Attendee changes (Create / Update / Cancel /
/// Delete / Reassign); the handler validates a shared secret and parses the changed ORDER id.
///
/// <para><b>§233 BURST PROTECTION (operator 2026-07-07):</b> the webhook does NOT call the
/// Zoho API anymore. It only ENQUEUES a <see cref="ZohoOrderSyncRequest"/> (deduped per
/// still-pending order) and acks with 200 — the once-per-minute
/// <see cref="ZohoWebhookDrainJob"/> coalesces everything queued in the window into ONE
/// Zoho pull + per-order incremental reconciles. A burst (e.g. 5 orders in 2 minutes, or
/// one order every 2 seconds) therefore costs at most one Zoho pull per minute instead of
/// one full pull per webhook. The 10-minute <see cref="AttendeeBackstageSyncJob"/> stays on
/// as the drift safety-net for missed/duplicate webhooks.</para>
///
/// <para>CEH NEVER writes/deletes anything in Zoho — strictly read-then-mirror.</para>
///
/// <para>Always returns a definite HTTP status so Zoho's retry behaviour is predictable:
/// <c>401</c> bad/absent secret; <c>200</c> queued OR a deliberate no-op (disabled /
/// paused / feature off / no order id — the periodic reconcile will catch drift);
/// <c>503</c> transient (no active event yet) so Zoho retries.</para>
///
/// EXEMPT from <see cref="JobsPauseMiddleware"/> (which short-circuits with no HTTP
/// response) — the pause is enforced INSIDE the handler so a paused edition still returns a
/// clean 200 instead of a host 500.
/// </summary>
public sealed class ZohoOrderWebhook
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly FeatureGateService _gate;
    private readonly ILogger<ZohoOrderWebhook> _log;

    public ZohoOrderWebhook(
        CommunityHubDbContext db, ZohoOptions options,
        FeatureGateService gate, ILogger<ZohoOrderWebhook> log)
    {
        _db = db; _options = options;
        _gate = gate; _log = log;
    }

    [Function("ZohoOrderWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "zoho/order-webhook")]
        HttpRequestData req,
        CancellationToken ct)
    {
        // ---- 1. AUTHORIZE (shared secret; no Zoho HMAC exists, §128) -------------
        var providedQuery = GetQueryValue(req.Url.Query, _options.WebhookSecretQueryParam);
        string? providedHeader = null;
        if (req.Headers.TryGetValues(_options.WebhookSecretHeader, out var hv))
            providedHeader = hv.FirstOrDefault();

        if (!ZohoWebhook.IsAuthorized(_options.WebhookSecret, providedHeader, providedQuery))
        {
            _log.LogWarning("ZohoOrderWebhook: rejected — missing/invalid shared secret.");
            return await Text(req, HttpStatusCode.Unauthorized, "unauthorized");
        }

        // ---- 2. Read body + parse the changed order id --------------------------
        var body = await new StreamReader(req.Body).ReadToEndAsync(ct);
        var parsed = ZohoWebhook.ParsePayload(body);

        // ---- 3. No-op gates (return 200 so Zoho doesn't retry a deliberate skip) -
        if (!_options.Enabled || !_options.WebhookEnabled)
            return await Text(req, HttpStatusCode.OK, "webhook disabled — no-op (periodic reconcile active)");

        var eventId = await _db.Events.Where(e => e.IsActive)
            .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
            return await Text(req, HttpStatusCode.ServiceUnavailable, "no active event");
        var ev = eventId.Value;

        // Honour the org-admin "pause all jobs" switch (this fn is pause-middleware-exempt).
        if (await _gate.AreJobsPausedAsync(ev, ct))
            return await Text(req, HttpStatusCode.OK, "jobs paused — no-op");
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", ev, ct))
            return await Text(req, HttpStatusCode.OK, "attendee-reconcile feature off — no-op");

        // 🔒 §707.13 — THE RECEIVER'S OWN SWITCH, visible in /Organizer/Settings. Operator
        // 2026-07-30: *"ok, then we need the receiver in the portal as well (settings)"*.
        //
        // Until now the only control was the `Zoho__WebhookEnabled` app setting checked above —
        // invisible on every page and changeable only by a deploy — so the Settings/Jobs pages
        // showed the DRAIN switch and said nothing about whether anything was still being
        // accepted and queued. Turning this off stops queueing at the door; leaving it on keeps
        // events for replay when real-time sync is switched back on.
        if (!await _gate.IsFeatureEnabledAsync(FeatureCatalog.WebhookReceiverKey, ev, ct))
            return await Text(req, HttpStatusCode.OK, "webhook receiver switched off — no-op (periodic reconcile active)");

        if (string.IsNullOrWhiteSpace(parsed.OrderId))
        {
            _log.LogInformation("ZohoOrderWebhook: no order id in payload (action={Action}); "
                + "leaving it to the periodic reconcile.", parsed.Action);
            return await Text(req, HttpStatusCode.OK, "no order id in payload — periodic reconcile will catch drift");
        }
        var orderId = parsed.OrderId!;

        // ---- 4. §233: ENQUEUE + ACK — never call the Zoho API from the webhook ----
        // Dedupe: one PENDING request per (edition, order). A repeat webhook for an order
        // already queued only refreshes the cancel hint / timestamp — the drain job's next
        // run covers both. Bursts therefore coalesce to one queue row per order and at
        // most ONE Zoho pull per minute, however fast the webhooks arrive.
        var pending = await _db.ZohoOrderSyncRequests.FirstOrDefaultAsync(
            r => r.EventId == ev && r.OrderId == orderId && r.ProcessedAt == null, ct);
        var now = DateTimeOffset.UtcNow;
        if (pending is null)
        {
            _db.ZohoOrderSyncRequests.Add(new ZohoOrderSyncRequest
            {
                EventId = ev,
                OrderId = orderId,
                CancelHint = parsed.IsCancellation,
                RequestedAt = now,
            });
        }
        else
        {
            pending.CancelHint = pending.CancelHint || parsed.IsCancellation;
            pending.RequestedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        // --- Record the last-webhook-received stamp on the edition's sync marker (§132).
        //     Lets the Sync-Health dashboard show that the real-time push leg is alive,
        //     distinct from the full-sync's LastSuccessAt. ---
        await RecordWebhookStampAsync(ev, ct);

        _log.LogInformation(
            "ZohoOrderWebhook: order {Order} (action={Action}) queued for the coalesced "
            + "drain (§233) — {Mode}.",
            orderId, parsed.Action, pending is null ? "new request" : "merged into pending request");

        return await Text(req, HttpStatusCode.OK,
            $"ok: order {orderId} queued for coalesced reconcile (drain runs every minute)");
    }

    /// <summary>Upsert the edition's attendee-backstage <see cref="SyncRun"/> marker with the
    /// time this webhook was processed (§132). LastSuccessAt stays owned by the hourly
    /// full-sync; if no marker exists yet (a push arrived before any full run), one is created
    /// with both stamps set to now — a processed webhook IS a successful single-order pull.</summary>
    private async Task RecordWebhookStampAsync(int eventId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var marker = await _db.SyncRuns.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.Key == SyncRun.AttendeeBackstageKey, ct);
        if (marker is null)
        {
            marker = new SyncRun
            {
                EventId = eventId,
                Key = SyncRun.AttendeeBackstageKey,
                CreatedAt = now,
                LastSuccessAt = now,
            };
            _db.SyncRuns.Add(marker);
        }
        marker.LastWebhookAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode code, string message)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteStringAsync(message);
        return resp;
    }

    /// <summary>Read one query-string parameter (URL-decoded) from a raw "?a=b&amp;c=d" string,
    /// without taking a System.Web dependency. Case-insensitive on the key.</summary>
    private static string? GetQueryValue(string? rawQuery, string key)
    {
        if (string.IsNullOrEmpty(rawQuery)) return null;
        var q = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase)) continue;
            return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
